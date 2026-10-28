using System.Runtime.InteropServices;
using LightWeaver.Player;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace LightWeaver.WinUI.Player;

/// <summary>The video: a SwapChainPanel showing mpv's composition swapchain. The swapchain is the
/// display's size in physical pixels, so full screen shows it one buffer pixel per screen pixel, and
/// it follows display changes (spike W1). Everywhere smaller (the detail preview, the window, every
/// frame of the grow between them) the drawn picture is scaled on the compositor to where mpv would
/// draw it at the surface's size; the swapchain is never resized for a size change of the surface,
/// so nothing flickers while it changes. The same surface serves the preview and the full window, so
/// growing it never reloads the video. From a new title's request until mpv shows its first frame the
/// video is held back: artwork (or, in the preview, the page under it) stands in, and the video then
/// fades in over 120 ms, so no frame of the previous file and no black gap is seen.</summary>
internal sealed partial class VideoSurface : Grid
{
    private static readonly Duration RevealFade = new(TimeSpan.FromMilliseconds(120));
    private readonly Brush _black = new SolidColorBrush(Microsoft.UI.Colors.Black);
    private readonly SwapChainPanel _panel = new();
    private readonly Grid _video = new();
    private readonly Image _art = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly FirstFrameGate _gate = new();
    private readonly ProgressRing _ring = new() { Width = 40, Height = 40, IsActive = false, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private Storyboard? _reveal;
    private bool _seeThrough;
    private bool _waiting;
    private readonly CompositeTransform _fit = new();
    private readonly RectangleGeometry _clip = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _drawnFallback;
    private MpvPlayer? _player;
    private nint _swapChain;
    private double _scale;
    private (int Width, int Height) _buffer;
    private (int Width, int Height) _requested;
    private (int Width, int Height) _drawn;
    private double? _fittedAspect;

    public VideoSurface()
    {
        Background = _black;
        _video.Background = _black;
        Clip = _clip;
        _panel.RenderTransform = _fit;
        _video.Children.Add(_panel);
        Children.Add(_art);
        Children.Add(_video);
        _ring.Foreground = Controls.Ui.Resource<Brush>("LwAccentFgBrush");
        _ring.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        Children.Add(_ring);
        _drawnFallback = DispatcherQueue.CreateTimer();
        _drawnFallback.Interval = TimeSpan.FromMilliseconds(250);
        _drawnFallback.IsRepeating = false;
        _drawnFallback.Tick += (_, _) => MarkDrawn();
        SizeChanged += (_, e) =>
        {
            _clip.Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
            PushSize();
            Fit();
        };
        Loaded += (_, _) =>
        {
            if (XamlRoot is { } root) root.Changed += (_, _) => OnScaleMaybeChanged();
            OnScaleMaybeChanged();
        };
    }

    private double DisplayScale => XamlRoot?.RasterizationScale is > 0 and var scale ? scale : 1;

    /// <summary>The surface's size in physical pixels.</summary>
    public (int Width, int Height) Pixels
        => ((int)Math.Round(ActualWidth * DisplayScale), (int)Math.Round(ActualHeight * DisplayScale));

    /// <summary>The swapchain's size: the display's, once known; the surface's until then.</summary>
    public (int Width, int Height) BufferPixels => _buffer != default ? _buffer : Pixels;

    /// <summary>Sizes the swapchain to the display the window is on, in physical pixels.</summary>
    public void SetDisplaySize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (width, height) == _buffer) return;
        _buffer = (width, height);
        PushSize();
    }

    private void OnScaleMaybeChanged()
    {
        if (DisplayScale == _scale) return;
        _scale = DisplayScale;
        PushSize();
        ApplyScale();
        Fit();
    }

    public void Attach(MpvPlayer player)
    {
        Unhook();
        _player = player;
        player.SwapChainChanged += OnSwapChainChanged;
        player.OsdGeometryChanged += OnOsdGeometryChanged;
        player.OsdWidthChanged += OnOsdGeometryChanged;
        player.VideoStateChanged += OnVideoStateChanged;
        player.PlaybackRestarted += OnPlaybackRestarted;
        PushSize();
    }

    public void Detach()
    {
        Unhook();
        _player = null;
        if (_swapChain == 0) return;
        try
        {
            SwapChainInterop.SetSwapChain(_panel, 0);
        }
        catch (COMException)
        {
        }
        _swapChain = 0;
    }

    private void Unhook()
    {
        if (_player is null) return;
        _player.SwapChainChanged -= OnSwapChainChanged;
        _player.OsdGeometryChanged -= OnOsdGeometryChanged;
        _player.OsdWidthChanged -= OnOsdGeometryChanged;
        _player.VideoStateChanged -= OnVideoStateChanged;
        _player.PlaybackRestarted -= OnPlaybackRestarted;
    }

    /// <summary>A new title was asked for: the video stays hidden until its first frame, and
    /// <paramref name="art"/> (if any) stands in for it.</summary>
    public void Hold(ImageSource? art, Stretch stretch)
    {
        HoldForLoad();
        SetArt(art, stretch);
    }

    /// <summary>A new file is about to load: hidden until its first frame; the artwork stays.</summary>
    public void HoldForLoad()
    {
        _gate.Hold(_player?.LoadGeneration ?? 0);
        _reveal?.Stop();
        _reveal = null;
        _video.Opacity = 0;
        ShowArt();
    }

    /// <summary>The artwork shown while the video is held back; it stays until the first frame.</summary>
    public void SetArt(ImageSource? art, Stretch stretch)
    {
        _art.Source = art;
        _art.Stretch = stretch;
        ShowArt();
    }

    /// <summary>In the detail preview the page's own backdrop shows through while the video is
    /// held back; everywhere else the artwork stands in on black.</summary>
    public bool SeeThrough
    {
        get => _seeThrough;
        set
        {
            _seeThrough = value;
            Background = value ? null : _black;
            ShowArt();
        }
    }

    /// <summary>A ring spins over the black surface while the held load has nothing standing in for
    /// it (a link), until its first frame or until it is no longer waited for.</summary>
    public bool Waiting
    {
        get => _waiting;
        set
        {
            _waiting = value;
            ShowRing();
        }
    }

    private void ShowRing()
    {
        var spinning = _waiting && _gate.IsHeld;
        _ring.IsActive = spinning;
        _ring.Visibility = spinning ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A requested title will not load here after all (the SyncPlay group took the
    /// request, or mpv could not start): what plays, if anything, shows again at once.</summary>
    public void CancelHold()
    {
        if (_gate.IsHeld) Reset();
    }

    /// <summary>Playback ended: nothing is held and no artwork is kept for the next title.</summary>
    public void Reset()
    {
        _gate.Clear();
        Waiting = false;
        _reveal?.Stop();
        _reveal = null;
        _video.Opacity = 1;
        SetArt(null, Stretch.Uniform);
    }

    /// <summary>The shape of what the surface shows now, for a grow or shrink that scales it: the
    /// stand-in artwork while the video is held back, else the video fitted by its shape.</summary>
    public LayerContent Content
        => _art.Visibility == Visibility.Visible && _art.Source is Microsoft.UI.Xaml.Media.Imaging.BitmapSource { PixelWidth: > 0, PixelHeight: > 0 } art
            ? new LayerContent((double)art.PixelWidth / art.PixelHeight, _art.Stretch == Stretch.UniformToFill)
            : _player?.VideoAspect is { } aspect ? new LayerContent(aspect) : LayerContent.Stretched;

    private void ShowArt()
        => _art.Visibility = _gate.IsHeld && !_seeThrough && _art.Source is not null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>mpv has shown the first frame of the load being waited for: the fit is set for its
    /// shape first, then the video fades in over the artwork.</summary>
    private void OnPlaybackRestarted(int generation)
    {
        if (_player is not { } player || !_gate.TryRelease(generation, player.LoadGeneration)) return;
        ShowRing();
        Fit();
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = RevealFade };
        Storyboard.SetTarget(fade, _video);
        Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));
        var board = new Storyboard { Children = { fade } };
        var frames = Services.FrameLog.Start("video_reveal");
        board.Completed += (_, _) =>
        {
            frames?.Dispose();
            if (!ReferenceEquals(_reveal, board)) return;
            _reveal = null;
            board.Stop();
            SetArt(null, _art.Stretch);
        };
        _video.Opacity = 1;
        _reveal = board;
        board.Begin();
    }

    /// <summary>A new file can change the video's shape without changing the buffer's size, so the
    /// fit follows the shape as well.</summary>
    private void OnVideoStateChanged()
    {
        if (_player?.VideoAspect != _fittedAspect) Fit();
    }

    private void OnSwapChainChanged(nint swapChain)
    {
        try
        {
            SwapChainInterop.SetSwapChain(_panel, swapChain);
            _swapChain = swapChain;
            ApplyScale();
            MarkDrawn();
        }
        catch (COMException ex)
        {
            Diagnostics.AppLog.Error("player", $"event=attach_swapchain outcome=failure hr=0x{ex.HResult:X8}");
        }
        finally
        {
            Marshal.Release(swapChain);
        }
    }

    private void PushSize()
    {
        var size = BufferPixels;
        if (_player is null || size.Width <= 0 || size.Height <= 0 || size == _requested) return;
        _requested = size;
        _player.SetCompositionSize(size.Width, size.Height);
        if (_drawn == default || _player.OsdHeight <= 0) MarkDrawn();
        else _drawnFallback.Start();
    }

    /// <summary>mpv reports the size it draws at; once both sides match, the new size is on screen.</summary>
    private void OnOsdGeometryChanged()
    {
        if (_player is { OsdHeight: > 0 } player && player.OsdHeight == _requested.Height && player.OsdWidth == _requested.Width) MarkDrawn();
    }

    private void MarkDrawn()
    {
        _drawnFallback.Stop();
        if (_drawn == _requested) return;
        _drawn = _requested;
        Fit();
    }

    private void ApplyScale()
    {
        if (_swapChain == 0) return;
        SwapChainInterop.SetMatrixTransform(_swapChain, (float)(1 / DisplayScale), (float)(1 / DisplayScale));
    }

    /// <summary>Lays the panel out at the drawn buffer's size and scales it so mpv's picture lands
    /// where mpv will draw it at the surface's size: centred and fitted, by the video's shape.</summary>
    private void Fit()
    {
        _fittedAspect = _player?.VideoAspect;
        if (_drawn == default || _drawn == Pixels)
        {
            _panel.Width = _panel.Height = double.NaN;
            _panel.HorizontalAlignment = HorizontalAlignment.Stretch;
            _panel.VerticalAlignment = VerticalAlignment.Stretch;
            _fit.ScaleX = _fit.ScaleY = 1;
            _fit.TranslateX = _fit.TranslateY = 0;
            return;
        }
        var bufferWidth = _drawn.Width / DisplayScale;
        var bufferHeight = _drawn.Height / DisplayScale;
        _panel.Width = bufferWidth;
        _panel.Height = bufferHeight;
        _panel.HorizontalAlignment = HorizontalAlignment.Left;
        _panel.VerticalAlignment = VerticalAlignment.Top;
        var fit = SurfaceFit.Of(ActualWidth, ActualHeight, bufferWidth, bufferHeight, _fittedAspect);
        _fit.ScaleX = fit.ScaleX;
        _fit.ScaleY = fit.ScaleY;
        _fit.TranslateX = fit.TranslateX;
        _fit.TranslateY = fit.TranslateY;
    }
}
