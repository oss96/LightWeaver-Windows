using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace TranslationEditor;

/// <summary>The editor's per-user session secret. It is stored in the user's local app data, which
/// other Windows accounts cannot read, and survives restarts so earlier review links keep working.
/// The browser receives it in the URL fragment, keeps it in its per-origin storage and sends it as
/// <see cref="HeaderName"/>; a cookie would also reach any other server on 127.0.0.1, whatever its
/// port.</summary>
public sealed class EditorSession
{
    public const string HeaderName = "X-Translation-Editor-Session";
    public const string FragmentName = "session";
    private const int TokenBytes = 32;

    public EditorSession(string path) => Token = LoadOrCreate(path);

    public string Token { get; }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightWeaver-TranslationEditor", "session-token");

    public bool Matches(string? candidate) => candidate is { Length: > 0 }
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(Token));

    private static string LoadOrCreate(string path)
    {
        if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } stored && IsWellFormed(stored))
            return stored;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        File.WriteAllText(path, token);
        return token;
    }

    private static bool IsWellFormed(string token)
        => Base64Url.IsValid(token, out var length) && length == TokenBytes;
}
