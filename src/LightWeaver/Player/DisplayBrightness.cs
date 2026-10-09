using System.Runtime.InteropServices;

namespace LightWeaver.Player;

/// <summary>Queries reported HDR peak luminance for the monitor containing a window.
/// This is display metadata (EDID or an override), not a brightness measurement.</summary>
internal static unsafe class DisplayBrightness
{
    internal sealed record DisplayInfo(nint Monitor, bool HdrEnabled, int? PeakNits);

    public static int? PeakNitsForWindow(nint window) => ReadForWindow(window)?.PeakNits;

    public static DisplayInfo? ReadForWindow(nint window)
    {
        if (window == 0) return null;
        var monitor = MonitorFromWindow(window, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (monitor == 0) return null;
        nint factory = 0;
        try
        {
            var factoryId = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
            if (CreateDXGIFactory1(in factoryId, out factory) < 0) return null;
            var enumAdapters = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vtable(factory)[12];
            for (uint index = 0; index < 64; index++)
            {
                nint adapter = 0;
                try
                {
                    if (enumAdapters(factory, index, &adapter) < 0) break;
                    var enumOutputs = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vtable(adapter)[7];
                    for (uint outputIndex = 0; outputIndex < 64; outputIndex++)
                    {
                        nint output = 0, output6 = 0;
                        try
                        {
                            if (enumOutputs(adapter, outputIndex, &output) < 0) break;
                            var outputId = new Guid("068346e8-aaec-4b84-add7-137f513f77a1"); // IDXGIOutput6
                            if (Marshal.QueryInterface(output, in outputId, out output6) < 0) continue;
                            // GetDesc1 follows IDXGIOutput through IDXGIOutput5 (slots 0..26).
                            var getDesc = (delegate* unmanaged[Stdcall]<nint, OutputDesc1*, int>)Vtable(output6)[27];
                            OutputDesc1 desc = default;
                            if (getDesc(output6, &desc) >= 0 && desc.Monitor == monitor && desc.AttachedToDesktop != 0)
                                return new DisplayInfo(desc.Monitor, desc.ColorSpace == 12,
                                    ValidPeakNits(desc.ColorSpace, desc.MaxLuminance));
                        }
                        finally
                        {
                            if (output6 != 0) Marshal.Release(output6);
                            if (output != 0) Marshal.Release(output);
                        }
                    }
                }
                finally { if (adapter != 0) Marshal.Release(adapter); }
            }
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            // Keep the manual value if the driver cannot supply a usable HDR description.
        }
        finally { if (factory != 0) Marshal.Release(factory); }
        return null;
    }

    internal static int? ValidPeakNits(uint colorSpace, float luminance)
        // DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020. SDR outputs may report 80 nits
        // as a fallback; that is not an HDR panel's peak and must not replace the setting.
        => colorSpace == 12 && float.IsFinite(luminance) && luminance is >= 1 and <= 10_000
            ? (int)Math.Round(luminance) : null;

    private static nint* Vtable(nint instance) => *(nint**)instance;

    [StructLayout(LayoutKind.Sequential)]
    private struct OutputDesc1
    {
        public fixed char DeviceName[32];
        public fixed int DesktopCoordinates[4];
        public int AttachedToDesktop;
        public uint Rotation;
        public nint Monitor;
        public uint BitsPerColor;
        public uint ColorSpace;
        public fixed float Chromaticity[8];
        public float MinLuminance;
        public float MaxLuminance;
        public float MaxFullFrameLuminance;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid riid, out nint factory);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromWindow(nint window, uint flags);
}
