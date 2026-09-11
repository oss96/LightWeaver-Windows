using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace LightWeaver.Player;

/// <summary>
/// Hosts the child HWND that mpv renders into (via the wid option). mpv owns this
/// window's swapchain entirely — WPF never composites video (see TECHNICAL.md).
/// </summary>
public sealed partial class MpvPlayerHost : HwndHost
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;

    // LwStorm0 (#07080D) as a COLORREF, which is 0x00BBGGRR - NOT RGB.
    private const int VoidColorRef = 0x000D0807;

    private const string VideoClassName = "LightWeaverVideoHost";
    private static bool _classRegistered;

    public nint VideoHwnd { get; private set; }

    /// <summary>Raised once the child HWND exists — safe to hand to mpv as wid.</summary>
    public event Action<nint>? HwndReady;

    public MpvPlayerHost()
    {
        Focusable = false;
    }

    /// <summary>
    /// Registers the video host's own window class, whose only reason to exist is a
    /// <c>hbrBackground</c> of LwStorm0.
    ///
    /// This used to create the child from the stock <c>"static"</c> class, and the window then
    /// flashed WHITE as playback started — reported by the user as "the screen is white and the
    /// loading indicator cannot be seen", which it could not be: the dots are near-white on
    /// #F3F3F3.
    ///
    /// Measured 2026-08-03 by bursting DXGI frames from the Play click, 30 ms apart, over the
    /// video area. Unfixed: the first frame reads 216–217 mean luminance and every later frame
    /// 2.8 — so the flash is SHORT (one frame at 30 ms granularity, under ~60 ms on a warm
    /// server title), not the whole load. It is brief because the class brush erases once at
    /// creation and nothing invalidates the window afterwards; mpv then attaches and presents
    /// black. What makes it matter rather than a curiosity is WHERE it lands: the captured frame
    /// shows the loading overlay already composed — title, badges, caption — over white, with
    /// the dots invisible. Fixed: 0 bright frames of 45.
    ///
    /// (An earlier revision of this comment claimed about 810 ms. That could not be reproduced
    /// and the figure is retracted; the duration depends on how quickly mpv attaches its
    /// swapchain, so a cold start may hold the white far longer than measured here.)
    ///
    /// A "static" control has no
    /// background brush of its own, so <c>DefWindowProc</c> erases it with the brush from
    /// <c>WM_CTLCOLORSTATIC</c>; WPF's host does not answer that message, and the fallback is
    /// COLOR_WINDOW — the SYSTEM window colour, #F3F3F3 under a light Windows theme. The app's
    /// dark theme is WPF-side and never reaches a raw HWND.
    ///
    /// It has to be the CLASS brush, not a <c>WM_ERASEBKGND</c> override: the window is created
    /// WS_VISIBLE, so it erases itself inside <see cref="BuildWindowCore"/> — before HwndHost
    /// has installed the subclass that routes messages to <c>WndProc</c> — and nothing
    /// invalidates it afterwards. An override compiles, runs never, and leaves the flash exactly
    /// as it was.
    ///
    /// <c>lpfnWndProc</c> is DefWindowProcW's own address rather than a managed delegate, so
    /// there is no callback whose lifetime has to outlive every window of this class.
    /// </summary>
    private static void EnsureClassRegistered()
    {
        if (_classRegistered)
            return;

        var user32 = GetModuleHandleW("user32.dll");
        var defWindowProc = GetProcAddress(user32, "DefWindowProcW");
        if (defWindowProc == nint.Zero)
            throw new InvalidOperationException("Could not resolve DefWindowProcW");

        // Process-lifetime allocation: a registered class outlives every window built from it.
        var classNamePtr = Marshal.StringToHGlobalUni(VideoClassName);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = defWindowProc,
            hInstance = GetModuleHandleW(null),
            hbrBackground = CreateSolidBrush(VoidColorRef),
            lpszClassName = classNamePtr,
        };

        if (RegisterClassExW(ref wc) == 0)
        {
            var err = Marshal.GetLastWin32Error();
            // Benign: a previous host in this process already registered it.
            if (err != ERROR_CLASS_ALREADY_EXISTS)
                throw new InvalidOperationException($"RegisterClassExW failed ({err})");
        }

        _classRegistered = true;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureClassRegistered();

        VideoHwnd = CreateWindowExW(
            0, VideoClassName, null,
            WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
            0, 0, 1, 1,
            hwndParent.Handle, nint.Zero, nint.Zero, nint.Zero);

        if (VideoHwnd == nint.Zero)
            throw new InvalidOperationException("Failed to create video child window");

        HwndReady?.Invoke(VideoHwnd);
        return new HandleRef(this, VideoHwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DestroyWindow(hwnd.Handle);
        VideoHwnd = nint.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(
        int exStyle, string className, string? windowName, int style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32", SetLastError = true)]
    private static partial ushort RegisterClassExW(ref WNDCLASSEXW wndClass);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("kernel32", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? moduleName);

    [LibraryImport("kernel32", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetProcAddress(nint module, string procName);

    [LibraryImport("gdi32")]
    private static partial nint CreateSolidBrush(int colorRef);
}
