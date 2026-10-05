using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LightWeaver.Localization;

namespace TranslationEditor;

public sealed class CatalogStore(string repository)
{
    private readonly string _catalogPath = Path.Combine(repository, "localization", "catalog.json");
    private readonly string _localeDirectory = Path.Combine(repository, "localization", "locales");
    private readonly SemaphoreSlim _writes = new(1);

    public StringCatalog Catalog()
    {
        var catalog = JsonSerializer.Deserialize<StringCatalog>(File.ReadAllText(_catalogPath), CatalogJson.Options)
            ?? throw new InvalidDataException("The string catalog is empty.");
        if (catalog.Version != 1 || catalog.SourceLanguage != "en" || catalog.Strings is null
            || catalog.Strings.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null
                || pair.Value.Text is null || pair.Value.Context is null || pair.Value.Sources is null
                || pair.Value.Sources.Any(source => source is null)))
            throw new InvalidDataException("The string catalog has an invalid schema.");
        return catalog;
    }

    private string LocalePath(string language)
    {
        if (!CatalogJson.TryLanguage(language, out var culture)) throw new ArgumentException("Choose a valid language other than source English.");
        return Path.Combine(_localeDirectory, culture.Name + ".json");
    }

    public TranslationFile? Read(string language)
    {
        var path = LocalePath(language);
        return File.Exists(path) ? ParseFile(File.ReadAllText(path), path) : null;
    }

    private static TranslationFile ParseFile(string content, string path)
    {
        var file = JsonSerializer.Deserialize<TranslationFile>(content, CatalogJson.Options)
            ?? throw new InvalidDataException("A translation file is empty.");
        var code = Path.GetFileNameWithoutExtension(path);
        if (file.Language is null || !file.Language.Equals(code, StringComparison.OrdinalIgnoreCase)
            || file.Strings is null || file.SourceHashes is null
            || file.Strings.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
            || file.SourceHashes.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
            throw new InvalidDataException("A translation file has an invalid schema or language.");
        return file;
    }

    public object Workspace()
    {
        var catalog = Catalog();
        Directory.CreateDirectory(_localeDirectory);
        var locales = Directory.GetFiles(_localeDirectory, "*.json").Order().Select(path =>
        {
            var code = Path.GetFileNameWithoutExtension(path);
            var content = File.ReadAllText(path);
            _ = LocalePath(code);
            var file = ParseFile(content, path);
            return new { code, name = CultureInfo.GetCultureInfo(code).NativeName, revision = CatalogJson.Hash(content), strings = file.Strings,
                stale = file.Strings.Keys.Where(key => catalog.Strings.TryGetValue(key, out var entry)
                    && !string.IsNullOrWhiteSpace(file.Strings[key]) && !TranslationRules.IsCurrent(file, key, entry)).ToArray() };
        }).ToArray();
        return new { catalog, locales, sourceHashes = catalog.Strings.ToDictionary(pair => pair.Key, pair => CatalogJson.Hash(pair.Value.Text)) };
    }

    public async Task<object> Create(string language)
    {
        var path = LocalePath(language);
        await _writes.WaitAsync();
        try
        {
            Directory.CreateDirectory(_localeDirectory);
            using var writeLock = Lock(path);
            if (File.Exists(path)) throw new EditConflictException("That language already has a column.");
            var code = Path.GetFileNameWithoutExtension(path);
            Write(path, new TranslationFile { Language = code });
            return new { code, name = CultureInfo.GetCultureInfo(code).NativeName, revision = Revision(path), strings = new Dictionary<string, string>(), stale = Array.Empty<string>() };
        }
        finally { _writes.Release(); }
    }

    public async Task<object> Save(string language, SaveRequest request)
    {
        var path = LocalePath(language);
        await _writes.WaitAsync();
        try
        {
            if (!Directory.Exists(_localeDirectory)) throw new ArgumentException("Add the language column first.");
            using var writeLock = Lock(path);
            if (!File.Exists(path)) throw new ArgumentException("Add the language column first.");
            if (request is null || string.IsNullOrWhiteSpace(request.Revision)) throw new ArgumentException("A revision is required.");
            if (request.Revision != Revision(path)) throw new EditConflictException("This language was changed elsewhere. Reload before saving; your unsaved text is kept in this browser.");
            var catalog = Catalog();
            var file = Read(language)!;
            // A patch updates only edited cells; filtered/hidden rows and other languages survive.
            if (request.Changes is null || request.Changes.Count > catalog.Strings.Count) throw new ArgumentException("Invalid translation changes.");
            foreach (var (key, edit) in request.Changes)
            {
                if (!catalog.Strings.TryGetValue(key, out var entry)) throw new ArgumentException("Unknown string key: " + key);
                if (edit is null || edit.Text is null || edit.SourceHash != CatalogJson.Hash(entry.Text))
                    throw new EditConflictException("The English source changed. Reload before translating this string: " + key);
                if (TranslationRules.Validate(entry.Text, edit.Text) is { } error) throw new ArgumentException(key + ": " + error);
                if (string.IsNullOrWhiteSpace(edit.Text)) { file.Strings.Remove(key); file.SourceHashes.Remove(key); }
                else { file.Strings[key] = edit.Text; file.SourceHashes[key] = edit.SourceHash; }
            }
            if (request.Revision != Revision(path)) throw new EditConflictException("This language changed during saving. Reload before saving again.");
            var latestCatalog = Catalog();
            if (request.Changes.Any(pair => !latestCatalog.Strings.TryGetValue(pair.Key, out var entry)
                || pair.Value.SourceHash != CatalogJson.Hash(entry.Text)))
                throw new EditConflictException("The English source changed during saving. Reload before saving again.");
            Write(path, file);
            return new { revision = Revision(path) };
        }
        finally { _writes.Release(); }
    }

    private static string Revision(string path) => CatalogJson.Hash(File.ReadAllText(path));
    private static FileStream Lock(string path)
    {
        try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (IOException) { throw new EditConflictException("Another editor is saving this language. Retry after it finishes."); }
    }
    private static void Write(string path, TranslationFile file)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(file, CatalogJson.Options) + Environment.NewLine);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<string[]> Changed(string baseline)
    {
        if (baseline == "working-tree") baseline = "HEAD";
        else if (!Regex.IsMatch(baseline, "^[0-9a-fA-F]{7,40}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Use working-tree or a Git commit hash for the changed filter.");
        var commit = await Git("rev-parse", "--verify", baseline + "^{commit}");
        if (commit.ExitCode != 0) throw new ArgumentException("That Git revision does not exist.");
        var id = commit.Output.Trim();
        var exists = await Git("cat-file", "-e", id + ":localization/catalog.json");
        if (exists.ExitCode != 0) return [.. Catalog().Strings.Keys];
        var previous = await Git("show", id + ":localization/catalog.json");
        if (previous.ExitCode != 0) throw new InvalidDataException("Could not read the baseline catalog.");
        var old = JsonSerializer.Deserialize<StringCatalog>(previous.Output, CatalogJson.Options)
            ?? throw new InvalidDataException("The baseline catalog is invalid.");
        if (old.Strings is null || old.Strings.Values.Any(entry => entry is null || entry.Sources is null))
            throw new InvalidDataException("The baseline catalog has an invalid schema.");
        var keys = Catalog().Strings.Where(pair => !old.Strings.TryGetValue(pair.Key, out var prior) || prior.Text != pair.Value.Text
            || prior.Context != pair.Value.Context || !prior.Sources.SequenceEqual(pair.Value.Sources))
            .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        // Include edits to translation content and review stamps as well as English source edits.
        // Compare the union so deleting a locale also marks its formerly translated keys.
        var oldFiles = await Git("ls-tree", "-r", "--name-only", id, "localization/locales/");
        if (oldFiles.ExitCode != 0) throw new InvalidDataException("Could not list baseline languages.");
        var localePaths = Directory.Exists(_localeDirectory) ? Directory.GetFiles(_localeDirectory, "*.json") : [];
        var names = localePaths.Select(Path.GetFileName).Concat(oldFiles.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Path.GetFileName(line.Trim())).Where(name => name.EndsWith(".json", StringComparison.Ordinal))).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var code = Path.GetFileNameWithoutExtension(name)!;
            var current = Read(code);
            var oldPath = "localization/locales/" + name;
            var hadFile = await Git("cat-file", "-e", id + ":" + oldPath);
            TranslationFile? before = null;
            if (hadFile.ExitCode == 0)
            {
                var content = await Git("show", id + ":" + oldPath);
                if (content.ExitCode != 0) throw new InvalidDataException("Could not read a baseline language.");
                before = ParseFile(content.Output, Path.Combine(_localeDirectory, name!));
            }
            foreach (var key in (current?.Strings.Keys.AsEnumerable() ?? []).Concat(before?.Strings.Keys.AsEnumerable() ?? []).Distinct())
                if (current?.Strings.GetValueOrDefault(key) != before?.Strings.GetValueOrDefault(key)
                    || current?.SourceHashes.GetValueOrDefault(key) != before?.SourceHashes.GetValueOrDefault(key)) keys.Add(key);
        }
        var currentCatalog = Catalog();
        return [.. keys.Where(currentCatalog.Strings.ContainsKey).Order(StringComparer.Ordinal)];
    }

    private async Task<(int ExitCode, string Output)> Git(params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = repository, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw new IOException("Git timed out."); }
        await error;
        return (process.ExitCode, await output);
    }
}

public sealed record TranslationEdit(string Text, string SourceHash);
public sealed record SaveRequest(string Revision, Dictionary<string, TranslationEdit> Changes);
public sealed class EditConflictException(string message) : Exception(message);
