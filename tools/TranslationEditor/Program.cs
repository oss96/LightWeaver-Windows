using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using LightWeaver.Localization;
using TranslationEditor;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
// This developer tool writes translation files. It always stays on loopback.
builder.WebHost.ConfigureKestrel(options => { options.Listen(IPAddress.Loopback, 5386); options.Limits.MaxRequestBodySize = 2 * 1024 * 1024; });
var repository = FindRepository();
var store = new CatalogStore(repository);
var session = new EditorSession(EditorSession.DefaultPath);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
var app = builder.Build();
app.Use(async (context, next) =>
{
    if (RequestSecurity.Refusal(context.Request, session) is { } refusal) { context.Response.StatusCode = refusal; return; }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    context.Response.Headers.CacheControl = "no-store";
    try { await next(); }
    catch (EditConflictException ex) { await Error(409, ex.Message); }
    catch (Exception ex) when (ex is ArgumentException or JsonException or BadHttpRequestException) { await Error(400, ex.Message); }
    catch (InvalidDataException ex) { await Error(500, ex.Message); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    { await Error(500, "Could not read or save translation files. Check permissions and retry."); }
    async Task Error(int status, string message)
    { context.Response.StatusCode = status; await context.Response.WriteAsJsonAsync(new { error = message }); }
});
app.MapGet("/api/workspace", () => store.Workspace());
app.MapGet("/api/languages", () => CultureInfo.GetCultures(CultureTypes.NeutralCultures | CultureTypes.SpecificCultures)
    .Where(c => CatalogJson.TryLanguage(c.Name, out _)).OrderBy(c => c.EnglishName)
    .Select(c => new { code = c.Name, name = c.NativeName, englishName = c.EnglishName }));
app.MapPost("/api/languages", async (LanguageRequest request) => await store.Create(request.Language));
app.MapPut("/api/languages/{language}", async (string language, SaveRequest request) => await store.Save(language, request));
app.MapGet("/api/changes", async (string? baseline) => new { keys = await store.Changed(baseline ?? "working-tree") });
var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.Combine(repository, "tools", "TranslationEditor", "wwwroot"));
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
app.MapGet("/", () => Results.File(Path.Combine(repository, "tools", "TranslationEditor", "wwwroot", "index.html"), "text/html"));
app.Lifetime.ApplicationStarted.Register(() =>
{
    var link = $"http://127.0.0.1:5386/#{EditorSession.FragmentName}={session.Token}";
    Console.WriteLine("Translation editor session link (opens the editor in this browser profile): " + link);
    if (args.Contains("--no-browser")) return;
    try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); }
    catch (Win32Exception) { Console.WriteLine("Could not open a browser. Open the link above."); }
});
app.Run();

static string FindRepository()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "localization", "catalog.json")) && Directory.Exists(Path.Combine(dir.FullName, "src", "LightWeaver"))) return dir.FullName;
    throw new DirectoryNotFoundException("Run this editor from the LightWeaver checkout.");
}
public sealed record LanguageRequest(string Language);
