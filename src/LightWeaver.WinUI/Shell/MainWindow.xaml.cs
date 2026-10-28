using LightWeaver.Localization;
using LightWeaver.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.UI;

namespace LightWeaver.WinUI.Shell;

/// <summary>The one window. The top bar extends into the title bar; its controls are marked as
/// pass-through so the rest of the bar drags the window, and the system caption buttons keep
/// snap layouts. The caption buttons are the system's Tall height (48 px), not the bar's 56:
/// the title bar API offers no custom height.</summary>
public sealed partial class MainWindow : Window
{
    private const int MinWidthDip = 900, MinHeightDip = 600;

    public MainWindow(AppViewModel app)
    {
        Model = app;
        ApplySavedZoom();
        InitializeComponent();
#if DEBUG
        Title += " (Debug)";
#endif
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "LightWeaver.ico"));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(BarZoom);
        var titleBar = AppWindow.TitleBar;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        // Dark caption buttons whatever the system theme (design: white glyphs, rgba(255,255,255,.06) hover).
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = Colors.White;
        titleBar.ButtonInactiveForegroundColor = Color.FromArgb(0x8B, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonHoverForegroundColor = Colors.White;
        titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x0B, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonPressedForegroundColor = Colors.White;

        ApplyLaunchBounds();
        // Crossing onto a screen with another scale rescales the window (WM_DPICHANGED), so the
        // requested bounds are applied once more after the first activation.
        Activated += ReapplyBoundsOnce;

        Bar.RegionsChanged += (_, _) => UpdateDragRegions();
        ApplyFlowDirection();
        L.Source.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyFlowDirection);
        InitializeNavigation();
        InitializeFiles();
        InitializeUpdates();
        InitializeZoom();
        Root.Loaded += (_, _) =>
        {
            Root.XamlRoot.Changed += (_, _) => { UpdateMinimumSize(); UpdateDragRegions(); };
            UpdateMinimumSize();
            UpdateDragRegions();
        };
        FastUi.Attach(this);
    }

    public AppViewModel Model { get; }

    /// <summary>Arabic and other right-to-left languages mirror the whole window.</summary>
    private void ApplyFlowDirection()
    {
        var direction = L.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        if (Root.FlowDirection == direction) return;
        Root.FlowDirection = direction;
        Bar.CaptionInset = Bar.CaptionInset;
        _lastRegions = "";
        UpdateDragRegions();
    }

    public TopBar TopBar => Bar;

    /// <summary>Puts <paramref name="page"/> on screen. Home goes into its own host and stays in the
    /// tree, collapsed, while other pages show, for as long as the history keeps it: XAML lets go of
    /// the decoded artwork of an image that leaves the tree and decodes it again when it comes back,
    /// so Home shown again would draw its cards blank and fill them in over the next frames.</summary>
    public void Show(FrameworkElement page)
    {
        TrackRemovalPage(page);
        _lastWalkMs = 0;
        HoldFocusForPage();
        var home = page is Pages.HomePage;
        if (home) HomeHost.Content = page;
        else if (HomeHost.Content is FrameworkElement kept && !_nav.Holds(kept)) HomeHost.Content = null;
        PageHost.Content = home ? null : page;
        HomeHost.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
        if (page.IsLoaded) Walk(page);
        else page.Loaded += WalkOnce;

        void WalkOnce(object sender, RoutedEventArgs e)
        {
            var element = (FrameworkElement)sender;
            element.Loaded -= WalkOnce;
            Walk(element);
        }
    }

    private long _lastWalkMs;

    private void Walk(FrameworkElement page)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        FastUi.Walk(page);
        _lastWalkMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private string _lastRegions = "";

    /// <summary>The bar's controls take clicks and the rest of it drags the window; in full screen
    /// the whole window takes clicks, so nothing over the video drags or restores it. The bar keeps
    /// the caption buttons' width through full screen, which has none, and through the frames after
    /// it in which the title bar still reports none, so it is laid out for the window it returns to.</summary>
    private void UpdateDragRegions()
    {
        if (Bar.XamlRoot is null) return;
        var scale = Bar.XamlRoot.RasterizationScale;
        var inset = AppWindow.TitleBar.RightInset / scale / BarZoom.Factor;
        if (LightWeaver.Player.WindowChange.TakesCaptionInset(IsOsFullscreen, inset) && Math.Abs(Bar.CaptionInset - inset) > 0.5)
        {
            // The bar re-lays out for the new inset; read the rects after that pass.
            Bar.CaptionInset = inset;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateDragRegions);
            return;
        }
        var rects = IsOsFullscreen
            ? [new RectInt32(0, 0, AppWindow.Size.Width, AppWindow.Size.Height)]
            : Bar.InteractiveRects()
                .Select(r => new RectInt32((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height)))
                .ToArray();
        var key = string.Join(";", rects.Select(r => $"{r.X},{r.Y},{r.Width},{r.Height}"));
        if (key == _lastRegions) return;
        _lastRegions = key;
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, rects);
    }

    /// <summary>The design's minimum window, 900 x 600 DIP at the zoom (<see cref="MinimumWindow"/>);
    /// the presenter takes physical pixels. With <paramref name="grow"/> a restored window smaller
    /// than a raised minimum grows to it.</summary>
    private void UpdateMinimumSize(bool grow = false)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var scale = Root.XamlRoot?.RasterizationScale ?? Scale();
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var (width, height) = MinimumWindow(scale, work);
        presenter.PreferredMinimumWidth = width;
        presenter.PreferredMinimumHeight = height;
        if (grow) GrowToMinimum(presenter, width, height, work);
    }

    private void ReapplyBoundsOnce(object sender, WindowActivatedEventArgs e)
    {
        Activated -= ReapplyBoundsOnce;
        if (Environment.GetEnvironmentVariable("LIGHTWEAVER_WINDOW_RECT") is not null) ApplyLaunchBounds();
    }

    /// <summary>LIGHTWEAVER_WINDOW_RECT="x,y,w,h" (physical pixels) places the window, so test
    /// runs can keep it on a given screen; otherwise where it was last time, or the design's
    /// 1440 x 900 frame, kept inside the screen's work area.</summary>
    private void ApplyLaunchBounds()
    {
        var spec = Environment.GetEnvironmentVariable("LIGHTWEAVER_WINDOW_RECT")?.Split(',');
        if (spec is { Length: 4 } && spec.All(s => int.TryParse(s, out _)))
        {
            var v = spec.Select(int.Parse).ToArray();
            AppWindow.MoveAndResize(new RectInt32(v[0], v[1], v[2], v[3]));
            return;
        }
        if (RestorePlacement()) return;
        var scale = Scale();
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var width = Math.Min((int)(1440 * scale), work.Width);
        var height = Math.Min((int)(900 * scale), work.Height);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
    }

    private double Scale() => GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
