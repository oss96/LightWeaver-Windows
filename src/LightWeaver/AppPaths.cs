using System.IO;

namespace LightWeaver;

/// <summary>
/// Single source of truth for the per-user app-data root.
///
/// Debug builds live in a separate <c>LightWeaver-Debug</c> folder so running the
/// dev build (and the verify-skill test suite, which always drives the Debug exe)
/// never touches the installed release build's credentials, settings, window state,
/// or image cache. Release builds use the canonical <c>LightWeaver</c> folder.
/// Selected automatically at compile time — no configuration.
/// </summary>
public static class AppPaths
{
#if DEBUG
    /// <summary>Folder name under %LOCALAPPDATA%.</summary>
    public const string FolderName = "LightWeaver-Debug";

    /// <summary>True when this build runs in the isolated debug environment.</summary>
    public const bool IsDebugEnvironment = true;
#else
    public const string FolderName = "LightWeaver";
    public const bool IsDebugEnvironment = false;
#endif

    /// <summary>%LOCALAPPDATA%\LightWeaver (release) or …\LightWeaver-Debug (debug).</summary>
    public static string Root { get; } = ResolveRoot();

    private static string ResolveRoot()
    {
#if DEBUG
        // Separate worktrees can run on the same desktop. Opt-in fixture storage keeps
        // their settings, downloads and device identities independent. Release ignores it.
        if (Environment.GetEnvironmentVariable("LIGHTWEAVER_TEST_APPDATA_ROOT") is { Length: > 0 } root)
        {
            if (!Path.IsPathFullyQualified(root))
                throw new InvalidOperationException("The test app-data directory must be an absolute path.");
            return Path.GetFullPath(root);
        }
#endif
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);
    }
}
