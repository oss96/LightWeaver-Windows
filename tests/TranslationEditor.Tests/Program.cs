using System.Diagnostics;
using System.Text.Json;
using LightWeaver.Localization;
using Microsoft.AspNetCore.Http;
using TranslationEditor;

var passed = 0;
var failed = 0;
await Check("format placeholders can change order", () => Assert(TranslationRules.Validate("{0}: {1:N0}", "{1:N0} für {0}") is null));
await Check("format placeholders retain count and format", () =>
{
    Assert(TranslationRules.Validate("{0} {0}", "{0}") is not null);
    Assert(TranslationRules.Validate("{0:N0}", "{0:N1}") is not null);
    Assert(TranslationRules.Validate("{0}", "{0") is not null);
    Assert(TranslationRules.Validate("{0}", "kein Wert") is not null);
});
await Check("literal braces and equivalent alignment remain valid", () =>
{
    Assert(TranslationRules.Validate("Use {braces}", "Nutze {Klammern}") is null);
    Assert(TranslationRules.Validate("Code {{name}}: {0, 8:N0}", "Kode {{Name}}: {0,8:N0}") is null);
    Assert(TranslationRules.Validate("Plain text", "Text {0}") is not null);
    Assert(TranslationRules.Validate("Plain text", "Text {0") is not null);
});
await Check("empty values clear cells and oversized values fail", () =>
{
    Assert(TranslationRules.Validate("{0}", " ") is null);
    Assert(TranslationRules.Validate("Source", new string('a', 16001)) is not null);
});
await Check("language validation prevents traversal and invalid names", () =>
{
    foreach (var invalid in new[] { "../de", "en", "", "de.json", "de/DE", "not-a-language", "../../outside" })
        Assert(!CatalogJson.TryLanguage(invalid, out _), invalid);
    Assert(CatalogJson.TryLanguage("de-DE", out var culture) && culture.Name == "de-DE");
});
await Check("create adds a canonical language and rejects duplicates", async () =>
{
    using var fixture = new Fixture();
    await fixture.Store.Create("de");
    Assert(fixture.Store.Read("de")?.Language == "de");
    await Throws<EditConflictException>(() => fixture.Store.Create("DE"));
    await Throws<ArgumentException>(() => fixture.Store.Create("../outside"));
    Assert(!File.Exists(Path.Combine(fixture.Root, "outside.json")));
});
await Check("patch saves retain filtered hidden cells and other languages", async () =>
{
    using var fixture = new Fixture();
    await fixture.Store.Create("de"); await fixture.Store.Create("fr");
    await fixture.Save("de", ("plain", "Alt"), ("hidden", "Verborgen"));
    await fixture.Save("fr", ("plain", "Français"));
    await fixture.Save("de", ("plain", "Neu"));
    var file = fixture.Store.Read("de")!;
    Assert(file.Strings["plain"] == "Neu" && file.Strings["hidden"] == "Verborgen");
    Assert(fixture.Store.Read("fr")!.Strings["plain"] == "Français");
    Assert(TranslationRules.IsCurrent(file, "plain", fixture.Catalog.Strings["plain"]));
});
await Check("invalid placeholders reject the whole patch atomically", async () =>
{
    using var fixture = new Fixture(); await fixture.Store.Create("de");
    var before = fixture.LocaleText("de");
    await Throws<ArgumentException>(() => fixture.Save("de", ("plain", "Neu"), ("format", "ohne Platzhalter")));
    Assert(fixture.LocaleText("de") == before);
});
await Check("stale language revision cannot overwrite newer content", async () =>
{
    using var fixture = new Fixture(); await fixture.Store.Create("de");
    var stale = fixture.Revision("de");
    await fixture.Save("de", ("plain", "Neu"));
    await Throws<EditConflictException>(() => fixture.Store.Save("de", new SaveRequest(stale, new() { ["plain"] = new("Alt", CatalogJson.Hash("Source")) })));
    Assert(fixture.Store.Read("de")!.Strings["plain"] == "Neu");
});
await Check("source changes reject old browser source hashes", async () =>
{
    using var fixture = new Fixture(); await fixture.Store.Create("de");
    var hash = CatalogJson.Hash(fixture.Catalog.Strings["plain"].Text);
    fixture.Catalog.Strings["plain"] = fixture.Catalog.Strings["plain"] with { Text = "Changed source" }; fixture.WriteCatalog();
    await Throws<EditConflictException>(() => fixture.Store.Save("de", new(fixture.Revision("de"), new() { ["plain"] = new("Deutsch", hash) })));
    Assert(fixture.Store.Read("de")!.Strings.Count == 0);
});
await Check("unknown keys and malformed requests fail without writes", async () =>
{
    using var fixture = new Fixture(); await fixture.Store.Create("de"); var revision = fixture.Revision("de");
    await Throws<ArgumentException>(() => fixture.Store.Save("de", new(revision, new() { ["unknown"] = new("Hallo", "hash") })));
    await Throws<ArgumentException>(() => fixture.Store.Save("de", new(revision, null!)));
    await Throws<ArgumentException>(() => fixture.Store.Save("de", new(null!, new())));
    await Throws<ArgumentException>(() => fixture.Store.Save("de", null!));
    Assert(fixture.Revision("de") == revision);
});
await Check("clearing a translation removes text and its source stamp", async () =>
{
    using var fixture = new Fixture(); await fixture.Store.Create("de"); await fixture.Save("de", ("plain", "Deutsch"));
    await fixture.Save("de", ("plain", "  "));
    var file = fixture.Store.Read("de")!;
    Assert(!file.Strings.ContainsKey("plain") && !file.SourceHashes.ContainsKey("plain"));
});
await Check("reviewing unchanged text updates its English version", async () =>
{
    using var fixture = new Fixture(); await fixture.Store.Create("de"); await fixture.Save("de", ("plain", "Deutsch"));
    fixture.Catalog.Strings["plain"] = fixture.Catalog.Strings["plain"] with { Text = "Source changed" }; fixture.WriteCatalog();
    Assert(!TranslationRules.IsCurrent(fixture.Store.Read("de")!, "plain", fixture.Catalog.Strings["plain"]));
    Assert(fixture.Workspace().GetProperty("locales")[0].GetProperty("stale")[0].GetString() == "plain");
    await fixture.Save("de", ("plain", "Deutsch"));
    Assert(TranslationRules.IsCurrent(fixture.Store.Read("de")!, "plain", fixture.Catalog.Strings["plain"]));
});
await Check("malformed locale data stays unchanged and reports a schema error", async () =>
{
    using var fixture = new Fixture(); await fixture.Store.Create("de");
    File.WriteAllText(fixture.LocalePath("de"), "{\"language\":\"de\",\"strings\":null,\"sourceHashes\":{}}");
    var before = fixture.LocaleText("de");
    await Throws<InvalidDataException>(() => Task.FromResult(fixture.Store.Workspace()));
    await Throws<InvalidDataException>(() => fixture.Save("de", ("plain", "Deutsch")));
    Assert(fixture.LocaleText("de") == before);
});
await Check("malformed catalog schemas fail safely", async () =>
{
    using var fixture = new Fixture();
    File.WriteAllText(fixture.CatalogPath, "{\"version\":1,\"sourceLanguage\":\"en\",\"strings\":{\"key\":null}}");
    await Throws<InvalidDataException>(() => Task.FromResult(fixture.Store.Catalog()));
});
await Check("separate editor instances cannot overwrite the same revision", async () =>
{
    using var fixture = new Fixture(); await fixture.Store.Create("de");
    var revision = fixture.Revision("de"); var second = new CatalogStore(fixture.Root);
    var jobs = new[] { fixture.Store, second }.Select((store, index) => Task.Run(async () =>
    {
        try { await store.Save("de", new(revision, new() { ["plain"] = new("Edit " + index, CatalogJson.Hash("Source")) })); return true; }
        catch (EditConflictException) { return false; }
    })).ToArray();
    var results = await Task.WhenAll(jobs);
    Assert(results.Count(result => result) == 1);
    Assert(!Directory.GetFiles(Path.GetDirectoryName(fixture.LocalePath("de"))!, "*.tmp").Any());
});
await Check("held cross-process write lock rejects a second writer", async () =>
{
    using var fixture = new Fixture(); await fixture.Store.Create("de");
    using var held = new FileStream(fixture.LocalePath("de") + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    await Throws<EditConflictException>(() => fixture.Save("de", ("plain", "Hallo")));
});
await Check("workspace revisions and source hashes match returned data", async () =>
{
    using var fixture = new Fixture(); await fixture.Store.Create("de");
    var workspace = fixture.Workspace();
    Assert(workspace.GetProperty("locales")[0].GetProperty("revision").GetString() == fixture.Revision("de"));
    Assert(workspace.GetProperty("sourceHashes").GetProperty("plain").GetString() == CatalogJson.Hash("Source"));
});
await Check("changed filter includes text, metadata, translations and deleted locales", async () =>
{
    using var fixture = new Fixture(); await fixture.Store.Create("de"); await fixture.Save("de", ("hidden", "Verborgen"));
    await fixture.Git("init", "--quiet"); await fixture.Git("add", "localization");
    await fixture.Git("-c", "user.name=Translation Tests", "-c", "user.email=tests@example.invalid", "commit", "--quiet", "-m", "Fixture baseline");
    var baseline = (await fixture.Git("rev-parse", "HEAD")).Trim();
    fixture.Catalog.Strings["plain"] = fixture.Catalog.Strings["plain"] with { Text = "Edited" };
    fixture.Catalog.Strings["format"] = fixture.Catalog.Strings["format"] with { Context = "New usage" };
    fixture.Catalog.Strings["new"] = new("Added", "Fixture", ["src/LightWeaver/New.cs"]); fixture.WriteCatalog();
    File.Delete(fixture.LocalePath("de"));
    var changed = await fixture.Store.Changed(baseline);
    Assert(changed.Order().SequenceEqual(new[] { "format", "hidden", "new", "plain" }.Order()), string.Join(',', changed));
    Assert((await fixture.Store.Changed("working-tree")).Order().SequenceEqual(changed.Order()));
    await fixture.Store.Create("fr"); await fixture.Save("fr", ("unchanged", "Traduit"));
    Assert((await fixture.Store.Changed(baseline)).Contains("unchanged"));
});
await Check("changed filter reads Unicode Git output independently of console encoding", async () =>
{
    using var fixture = new Fixture();
    fixture.Catalog.Strings["unicode"] = new("Downloads \u00b7 ready", "Fixture", ["src/LightWeaver/View.cs"]);
    fixture.WriteCatalog();
    await fixture.Store.Create("de"); await fixture.Save("de", ("unicode", "Gr\u00f6\u00dfe \u00b7 bereit"));
    await fixture.Store.Create("ar"); await fixture.Save("ar", ("unicode", "\u062c\u0627\u0647\u0632 \u0644\u0644\u062a\u0634\u063a\u064a\u0644"));
    // Git stores UTF-8 bytes; default JSON escaping would hide the decoding failure.
    var rawUnicode = new JsonSerializerOptions(CatalogJson.Options)
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    File.WriteAllText(fixture.CatalogPath, JsonSerializer.Serialize(fixture.Catalog, rawUnicode));
    foreach (var code in new[] { "de", "ar" })
        File.WriteAllText(fixture.LocalePath(code), JsonSerializer.Serialize(fixture.Store.Read(code), rawUnicode));
    foreach (var path in new[] { fixture.CatalogPath, fixture.LocalePath("de"), fixture.LocalePath("ar") })
        Assert(File.ReadAllText(path).Any(c => c > 127), "Git baseline must contain raw Unicode characters.");
    await fixture.Git("init", "--quiet"); await fixture.Git("add", "localization");
    await fixture.Git("-c", "user.name=Translation Tests", "-c", "user.email=tests@example.invalid", "commit", "--quiet", "-m", "Unicode baseline");
    var baseline = (await fixture.Git("rev-parse", "HEAD")).Trim();
    var originalEncoding = Console.OutputEncoding;
    try
    {
        Console.OutputEncoding = System.Text.Encoding.ASCII;
        Assert((await fixture.Store.Changed(baseline)).Length == 0, "Unchanged Unicode strings must remain outside the changed filter.");
    }
    finally { Console.OutputEncoding = originalEncoding; }
});
await Check("changed filter rejects command injection and missing revisions", async () =>
{
    using var fixture = new Fixture(); await fixture.Git("init", "--quiet");
    foreach (var baseline in new[] { "--help", "HEAD;echo bad", "../../file", "1234567", "" })
        await Throws<ArgumentException>(() => fixture.Store.Changed(baseline));
});
await Check("HTTP write guard rejects cross-origin forms and spoofed hosts", () =>
{
    var context = new DefaultHttpContext(); var request = context.Request;
    request.Host = new HostString("127.0.0.1", 5386); request.Method = "PUT"; request.ContentType = "application/json";
    Assert(!RequestSecurity.Allows(request));
    request.Headers["X-Translation-Editor"] = "1"; Assert(RequestSecurity.Allows(request));
    request.Headers.Origin = "https://attacker.invalid"; Assert(!RequestSecurity.Allows(request));
    request.Headers.Origin = "http://127.0.0.1:5386"; Assert(RequestSecurity.Allows(request));
    request.ContentType = "application/x-www-form-urlencoded"; Assert(!RequestSecurity.Allows(request));
    request.Method = "GET"; request.Host = new HostString("attacker.invalid"); Assert(!RequestSecurity.Allows(request));
    request.Host = new HostString("localhost", 5386); Assert(RequestSecurity.Allows(request));
});
Console.WriteLine($"Translation editor: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

async Task Check(string name, Delegate action)
{
    try
    {
        if (action is Action sync) sync(); else if (action is Func<Task> asyncAction) await asyncAction(); else throw new InvalidOperationException("Unknown check type.");
        passed++; Console.WriteLine("PASS " + name);
    }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new InvalidOperationException(message); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}

sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "LightWeaver-TranslationEditor-" + Guid.NewGuid().ToString("N"));
    public StringCatalog Catalog { get; } = new()
    {
        Strings = new(StringComparer.Ordinal)
        {
            ["plain"] = new("Source", "Fixture", ["src/LightWeaver/View.cs"]),
            ["hidden"] = new("Hidden source", "Fixture", ["src/LightWeaver/Hidden.cs"]),
            ["format"] = new("{0}: {1:N0}", "Format fixture", ["src/LightWeaver/View.cs"]),
            ["unchanged"] = new("Stable", "Fixture", ["src/LightWeaver/View.cs"]),
        }
    };
    public CatalogStore Store { get; }
    public string CatalogPath => Path.Combine(Root, "localization", "catalog.json");
    public Fixture() { Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!); WriteCatalog(); Store = new(Root); }
    public void WriteCatalog() => File.WriteAllText(CatalogPath, JsonSerializer.Serialize(Catalog, CatalogJson.Options));
    public string LocalePath(string code) => Path.Combine(Root, "localization", "locales", code + ".json");
    public string LocaleText(string code) => File.ReadAllText(LocalePath(code));
    public string Revision(string code) => CatalogJson.Hash(LocaleText(code));
    public JsonElement Workspace() => JsonSerializer.SerializeToElement(Store.Workspace(), CatalogJson.Options);
    public Task<object> Save(string code, params (string Key, string Text)[] edits)
        => Store.Save(code, new(Revision(code), edits.ToDictionary(edit => edit.Key, edit => new TranslationEdit(edit.Text, CatalogJson.Hash(Catalog.Strings[edit.Key].Text)))));
    public async Task<string> Git(params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in args) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("Fixture Git failed: " + await error);
        await error; return await output;
    }
    public void Dispose()
    {
        var absolute = Path.GetFullPath(Root);
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LightWeaver-TranslationEditor-"));
        if (!absolute.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup path.");
        // Git marks object files read-only on Windows.
        foreach (var path in Directory.GetFiles(absolute, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(absolute, recursive: true);
    }
}
