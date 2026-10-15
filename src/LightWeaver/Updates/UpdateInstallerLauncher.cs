using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace LightWeaver.Updates;

public interface IUpdateInstallerLauncher
{
    bool Launch(string installerPath, bool silent, bool relaunch);
}

/// <summary>Separated so argument policy can be tested without executing a package.</summary>
public sealed class UpdateInstallerLauncher : IUpdateInstallerLauncher
{
    public bool Launch(string installerPath, bool silent, bool relaunch)
    {
        var arguments = Arguments(silent, relaunch);
#if DEBUG
        if (Environment.GetEnvironmentVariable("LIGHTWEAVER_UPDATE_LAUNCH_RECORD") is { Length: > 0 } recordPath)
        {
            var directory = Path.GetDirectoryName(recordPath);
            if (directory is { Length: > 0 }) Directory.CreateDirectory(directory);
            File.AppendAllText(recordPath, JsonSerializer.Serialize(new
            {
                InstallerPath = installerPath,
                Silent = silent,
                Arguments = arguments,
            }) + Environment.NewLine);
            Detail($"event=launcher mode=debug_record silent={Flag(silent)} relaunch={Flag(silent && relaunch)}");
            return true;
        }
#endif
        // mode and argument policy only: the installer path is never recorded.
        Detail($"event=launcher mode=process silent={Flag(silent)} relaunch={Flag(silent && relaunch)}");
        Process.Start(new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
            Arguments = arguments,
        });
        return true;
    }

    // /RELAUNCH is LightWeaver.iss's own switch. Only a silent install needs it: the interactive
    // wizard already ends on a finish page with a launch checkbox.
    private static string Arguments(bool silent, bool relaunch) => !silent ? string.Empty
        : relaunch ? "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH"
        : "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS";

    private static string Flag(bool value) => value ? "true" : "false";

    private static void Detail(string record) => Diagnostics.AppLog.Detail("updates", record);
}
