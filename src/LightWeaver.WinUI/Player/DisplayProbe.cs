using System.Globalization;
using System.Runtime.InteropServices;
using LightWeaver.Player;

namespace LightWeaver.WinUI.Player;

/// <summary>What mpv's window mode reads from the window's monitor (d3d11_helpers.c,
/// mp_dxgi_output_desc_from_hwnd), done here because composition mode has no window:
/// IDXGIOutput6::GetDesc1 of the output whose HMONITOR holds the app window (spike W1).</summary>
internal static unsafe partial class DisplayProbe
{
    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IidOutput6 = new("068346e8-aaec-4b84-add7-137f513f77a1");

    // IDXGIFactory1: EnumAdapters1 = 12. IDXGIAdapter: EnumOutputs = 7. IDXGIOutput6: GetDesc1 = 27.
    private const int SlotEnumAdapters1 = 12, SlotEnumOutputs = 7, SlotGetDesc1 = 27;
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint ColorSpaceRgbFullG2084P2020 = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct OutputDesc1
    {
        public fixed char DeviceName[32];
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public nint Monitor;
        public uint BitsPerColor;
        public uint ColorSpace;
        public fixed float Primaries[8];
        public float MinLuminance, MaxLuminance, MaxFullFrameLuminance;
    }

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(in Guid riid, out nint factory);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    /// <summary>The display under the window; SDR when the probe fails.</summary>
    public static DisplayTarget ForWindow(nint hwnd)
    {
        try
        {
            var monitor = MonitorFromWindow(hwnd, 2);
            Marshal.ThrowExceptionForHR(CreateDXGIFactory1(in IidFactory1, out var factory));
            try
            {
                for (uint a = 0; ; a++)
                {
                    nint adapter;
                    var hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)factory)[SlotEnumAdapters1])(factory, a, &adapter);
                    if (hr == DxgiErrorNotFound) break;
                    Marshal.ThrowExceptionForHR(hr);
                    try
                    {
                        if (FindOutput(adapter, monitor) is { } target) return target;
                    }
                    finally
                    {
                        Marshal.Release(adapter);
                    }
                }
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            Diagnostics.AppLog.Error("player", $"event=display_probe outcome=failure error={ex.GetType().Name}");
        }
        return DisplayTarget.Sdr;
    }

    private static DisplayTarget? FindOutput(nint adapter, nint monitor)
    {
        for (uint o = 0; ; o++)
        {
            nint output;
            var hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)adapter)[SlotEnumOutputs])(adapter, o, &output);
            if (hr == DxgiErrorNotFound) return null;
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                var iid = IidOutput6;
                if (Marshal.QueryInterface(output, in iid, out var output6) < 0) continue;
                try
                {
                    OutputDesc1 d;
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, OutputDesc1*, int>)(*(nint**)output6)[SlotGetDesc1])(output6, &d));
                    if (d.Monitor != monitor) continue;
                    var primaries = new string[8];
                    for (var i = 0; i < 8; i++) primaries[i] = d.Primaries[i].ToString("0.0000", CultureInfo.InvariantCulture);
                    return new DisplayTarget(d.ColorSpace == ColorSpaceRgbFullG2084P2020, d.BitsPerColor > 8, d.MaxLuminance, 0, d.MinLuminance, string.Join(",", primaries));
                }
                finally
                {
                    Marshal.Release(output6);
                }
            }
            finally
            {
                Marshal.Release(output);
            }
        }
    }
}
