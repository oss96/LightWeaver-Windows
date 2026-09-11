using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LightWeaver.Player;

/// <summary>
/// System Media Transport Controls for the Win32 window (Phase 5 M6): media keys,
/// the volume-overlay media card, and Windows' media session list. WPF has no
/// GetForCurrentView — the SMTC instance comes from the interop factory keyed on
/// the window handle. All events are re-dispatched to the UI thread.
/// </summary>
public sealed class SystemMediaControls
{
    // IID of Windows.Media.ISystemMediaTransportControls (the class default interface).
    private static readonly Guid SmtcIid = new("99FA3FF4-1742-42A6-902E-087D41F965EC");

    [ComImport, Guid("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISystemMediaTransportControlsInterop
    {
        nint GetIids();          // IInspectable slots — never called, padding only
        nint GetRuntimeClassName();
        nint GetTrustLevel();
        nint GetForWindow(nint appWindow, ref Guid riid);
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string src, int length, out nint hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(nint classId, ref Guid iid, out nint factory);

    private readonly SystemMediaTransportControls _smtc;
    private readonly Dispatcher _dispatcher;

    public event Action? PlayPressed;
    public event Action? PausePressed;
    public event Action? StopPressed;
    public event Action? NextPressed;
    public event Action? PreviousPressed;

    private SystemMediaControls(SystemMediaTransportControls smtc, Dispatcher dispatcher)
    {
        _smtc = smtc;
        _dispatcher = dispatcher;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsStopEnabled = true;
        _smtc.IsNextEnabled = false;
        _smtc.IsPreviousEnabled = false;
        _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
        _smtc.ButtonPressed += OnButtonPressed;
    }

    /// <summary>Null when SMTC is unavailable (server OS, broken WinRT) — the app
    /// must work without it.</summary>
    public static SystemMediaControls? TryCreate(nint hwnd, Dispatcher dispatcher)
    {
        try
        {
            var className = "Windows.Media.SystemMediaTransportControls";
            Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var hstring));
            nint factoryPtr;
            try
            {
                var interopIid = typeof(ISystemMediaTransportControlsInterop).GUID;
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, ref interopIid, out factoryPtr));
            }
            finally
            {
                WindowsDeleteString(hstring);
            }
            var interop = (ISystemMediaTransportControlsInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            Marshal.Release(factoryPtr);
            var iid = SmtcIid;
            var abi = interop.GetForWindow(hwnd, ref iid);
            var smtc = WinRT.MarshalInterface<SystemMediaTransportControls>.FromAbi(abi);
            Marshal.Release(abi);
            return new SystemMediaControls(smtc, dispatcher);
        }
        catch
        {
            return null;
        }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender,
        SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        var handler = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => PlayPressed,
            SystemMediaTransportControlsButton.Pause => PausePressed,
            SystemMediaTransportControlsButton.Stop => StopPressed,
            SystemMediaTransportControlsButton.Next => NextPressed,
            SystemMediaTransportControlsButton.Previous => PreviousPressed,
            _ => null,
        };
        if (handler is not null)
            _dispatcher.BeginInvoke(handler);
    }

    /// <summary>Playing / paused / stopped session state (drives media-key routing).</summary>
    public void SetPlaybackState(bool playing)
        => Try(() => _smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused);

    public void SetStopped() => Try(() => _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped);

    public void SetNextPreviousEnabled(bool next, bool previous) => Try(() =>
    {
        _smtc.IsNextEnabled = next;
        _smtc.IsPreviousEnabled = previous;
    });

    /// <summary>Fills the media card: title/subtitle + thumbnail (a local file from
    /// the image disk cache — SMTC can't send our auth header to the server).</summary>
    public async Task UpdateDisplayAsync(string title, string? subtitle, string? thumbUrl)
    {
        try
        {
            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Video;
            updater.VideoProperties.Title = title;
            updater.VideoProperties.Subtitle = subtitle ?? "";
            updater.Thumbnail = null;
            if (thumbUrl is { Length: > 0 }
                && await Imaging.ImageCache.GetFileAsync(thumbUrl).ConfigureAwait(false) is { } path
                && File.Exists(path))
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(file);
            }
            updater.Update();
        }
        catch
        {
            // cosmetic — never disturb playback
        }
    }

    public void ClearDisplay() => Try(() =>
    {
        _smtc.DisplayUpdater.ClearAll();
        _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
    });

    /// <summary>Timeline for the OS seek/progress display (coarse; fed every ~5 s).</summary>
    public void UpdateTimeline(double positionSeconds, double durationSeconds) => Try(() =>
    {
        var props = new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            MinSeekTime = TimeSpan.Zero,
            Position = TimeSpan.FromSeconds(Math.Max(0, positionSeconds)),
            MaxSeekTime = TimeSpan.FromSeconds(Math.Max(0, durationSeconds)),
            EndTime = TimeSpan.FromSeconds(Math.Max(0, durationSeconds)),
        };
        _smtc.UpdateTimelineProperties(props);
    });

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // SMTC hiccups are cosmetic
        }
    }
}
