using System.Runtime.InteropServices;

namespace LightWeaver.Player;

/// <summary>
/// Detects whether an NVIDIA RTX GPU drives the desktop — the RTX video filters
/// (VSR / RTX Video HDR) are NVIDIA-only and need RTX-class hardware. Detection
/// failure counts as capable: never disable a feature without evidence.
/// </summary>
internal static class GpuCapabilities
{
    /// <summary>Null when an RTX GPU is present (or detection failed), otherwise a
    /// short human-readable reason for the RTX toggles' tooltips.</summary>
    public static string? RtxUnavailableReason { get; } = Detect();

    /// <summary>The same static cause as a bounded lower_snake token, for the structural logs:
    /// <c>none</c>, <c>no_rtx_video_support</c> or <c>no_nvidia_rtx_gpu</c>. The reason above is a
    /// sentence written for a tooltip — pasting it into a <c>key=value</c> record would make the
    /// record unparseable, and the set of causes is fixed, so the token is the thing to log.
    /// Initialized after <see cref="RtxUnavailableReason"/>: static field initializers run in
    /// declaration order.</summary>
    public static string RtxUnavailableToken { get; } = RtxUnavailableReason switch
    {
        null => "none",
        "this NVIDIA GPU has no RTX video support" => "no_rtx_video_support",
        _ => "no_nvidia_rtx_gpu",
    };

    /// <summary>The desktop display adapters, comma-joined, for the crash/playback log header —
    /// a green-frame or hwdec failure is a GPU fact first, so the file has to name the hardware.
    /// "unknown" when enumeration fails; never throws.</summary>
    public static string DesktopAdapterNames { get; } = DescribeAdapters();

    private static string DescribeAdapters()
    {
        try
        {
            // EnumDisplayDevices reports one entry per attached MONITOR, so a two-monitor machine
            // named the same GPU twice in the log header. Distinct, order preserved.
            var names = EnumerateDesktopAdapters().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return names.Count > 0 ? string.Join(", ", names) : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string? Detect()
    {
        try
        {
            // Diagnostics: LIGHTWEAVER_FAKE_GPU=<adapter name> simulates the machine's
            // only display adapter (the no-RTX paths can't be tested on RTX hardware).
            var names = Environment.GetEnvironmentVariable("LIGHTWEAVER_FAKE_GPU") is { Length: > 0 } fake
                ? [fake]
                : EnumerateDesktopAdapters();
            if (names.Count == 0)
                return null;
            if (names.Any(n => Contains(n, "NVIDIA") && Contains(n, "RTX")))
                return null;
            return names.Any(n => Contains(n, "NVIDIA"))
                ? "this NVIDIA GPU has no RTX video support"
                : "no NVIDIA RTX GPU detected";
        }
        catch
        {
            return null;
        }
    }

    private static bool Contains(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static List<string> EnumerateDesktopAdapters()
    {
        const uint attachedToDesktop = 0x1;
        var names = new List<string>();
        var device = NewDevice();
        for (uint i = 0; EnumDisplayDevices(null, i, ref device, 0); i++)
        {
            if ((device.StateFlags & attachedToDesktop) != 0 && device.DeviceString.Length > 0)
                names.Add(device.DeviceString);
            device = NewDevice();
        }
        return names;
    }

    private static DISPLAY_DEVICE NewDevice() => new() { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum,
        ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);
}
