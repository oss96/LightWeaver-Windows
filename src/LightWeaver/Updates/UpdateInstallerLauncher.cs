using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace LightWeaver.Updates;

public interface IUpdateInstallerLauncher
{
    bool Launch(string installerPath, bool silent);
}

/// <summary>Separated so argument policy can be tested without executing a package.</summary>
public sealed class UpdateInstallerLauncher : IUpdateInstallerLauncher
{
    public bool Launch(string installerPath, bool silent)
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("LIGHTWEAVER_UPDATE_LAUNCH_RECORD") is { Length: > 0 } recordPath)
        {
            var directory = Path.GetDirectoryName(recordPath);
            if (directory is { Length: > 0 }) Directory.CreateDirectory(directory);
            File.AppendAllText(recordPath, JsonSerializer.Serialize(new
            {
                InstallerPath = installerPath,
                Silent = silent,
                Arguments = silent ? "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS" : string.Empty,
            }) + Environment.NewLine);
            Detail($"event=launcher mode=debug_record silent={(silent ? "true" : "false")}");
            return true;
        }
#endif
        // mode and argument policy only: the installer path is never recorded.
        Detail($"event=launcher mode=process silent={(silent ? "true" : "false")}");
        Process.Start(new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
            Arguments = silent ? "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS" : string.Empty,
        });
        return true;
    }

    private static void Detail(string record) => Diagnostics.AppLog.Detail("updates", record);
}
