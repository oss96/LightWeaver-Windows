using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;

namespace LightWeaver.WinUI.Player;

/// <summary>Raw-vtable COM calls for attaching mpv's composition swapchain (spike W1). No
/// [ComImport], so it does not depend on built-in COM interop, which CsWinRT apps and trimming do
/// not guarantee.</summary>
internal static unsafe class SwapChainInterop
{
    // microsoft.ui.xaml.media.dxinterop.h
    private static readonly Guid IidSwapChainPanelNative = new("63aad0b8-7c24-40ff-85a8-640d944cc325");
    // dxgi1_3.h
    private static readonly Guid IidDxgiSwapChain2 = new("a8be2ac4-199f-4946-b331-79599fb98de7");

    // IDXGISwapChain2::SetMatrixTransform: IUnknown 0-2, IDXGIObject 3-6, IDXGIDeviceSubObject 7,
    // IDXGISwapChain 8-17, IDXGISwapChain1 18-28, IDXGISwapChain2 29-35.
    private const int SlotSetMatrixTransform = 34;

    /// <summary>ISwapChainPanelNative::SetSwapChain. UI thread only; 0 detaches. The panel keeps its
    /// own reference.</summary>
    public static void SetSwapChain(SwapChainPanel panel, nint swapChain)
    {
        var unknown = WinRT.MarshalInspectable<SwapChainPanel>.FromManaged(panel);
        try
        {
            var iid = IidSwapChainPanelNative;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var native));
            try
            {
                var fn = (delegate* unmanaged[Stdcall]<nint, nint, int>)(*(nint**)native)[3];
                Marshal.ThrowExceptionForHR(fn(native, swapChain));
            }
            finally
            {
                Marshal.Release(native);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>IDXGISwapChain2::SetMatrixTransform. A SwapChainPanel shows one swapchain pixel per
    /// DIP; a transform of 1/CompositionScale maps buffer pixels 1:1 to physical pixels.</summary>
    public static int SetMatrixTransform(nint swapChain, float sx, float sy)
    {
        var iid = IidDxgiSwapChain2;
        var hr = Marshal.QueryInterface(swapChain, in iid, out var swapChain2);
        if (hr < 0) return hr;
        try
        {
            var matrix = stackalloc float[6] { sx, 0, 0, sy, 0, 0 };
            var fn = (delegate* unmanaged[Stdcall]<nint, float*, int>)(*(nint**)swapChain2)[SlotSetMatrixTransform];
            return fn(swapChain2, matrix);
        }
        finally
        {
            Marshal.Release(swapChain2);
        }
    }
}
