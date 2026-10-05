using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LightWeaver;
using LightWeaver.Jellyfin;
using LightWeaver.Player;
using LightWeaver.ViewModels;
using LightWeaver.Views;

/// <summary>A command from the two-client controller. <c>Id</c> correlates the reply.</summary>
internal sealed record TwoClientCommand(int Id, string Op, string? Arg);

/// <summary>A worker's answer. Every reply carries a snapshot taken AFTER the command ran, on the
/// worker's UI thread. Id 0 is the unsolicited hello sent once the window is up.</summary>
internal sealed record TwoClientReply(int Id, bool Ok, string? Error, string? Result, TwoClientSnapshot? Snapshot);

/// <summary>What one client looks like from the inside. <c>Position</c> is a synchronous mpv read
/// and <c>CapturedUtcTicks</c> the instant straight after it, so two workers' positions can be
/// compared at a common instant. <c>Queue</c> is the player view model's queue rows, the current
/// one marked with a leading <c>*</c>.</summary>
internal sealed record TwoClientSnapshot(
    string Role, int Pid, string AppRoot, string DeviceId, string? SessionKey, Guid UserId, string State,
    int MainWindows, bool InGroup, bool MembershipPending, Guid? GroupId, string? GroupName,
    IReadOnlyList<string> Participants, string? GroupState, Guid? PlaylistItemId, Guid? PlayingItemId,
    bool HasPlayer, bool Loading, bool Paused, bool HasPosition, double Position, double Duration,
    int LoadGeneration, string? Path, long CapturedUtcTicks, IReadOnlyList<string>? Queue = null);

/// <summary>What one client's windows DISPLAY, read off the visual tree: the status beside each
/// SyncPlay icon and the icon's automation name, the overlay's title block and queue badge.
/// <see cref="Status"/> is the label that is actually on screen — the overlay's during playback,
/// the shell's otherwise.</summary>
internal sealed record TwoClientUi(
    string AppState, bool OverlayVisible, string ShellStatus, bool ShellStatusVisible, string ShellButtonName,
    string OverlayStatus, bool OverlayStatusVisible, string OverlayButtonName, string Title, string Subtitle,
    string QueueBadge, bool QueueButtonVisible)
{
    public string Status => OverlayVisible
        ? OverlayStatusVisible ? OverlayStatus : ""
        : ShellStatusVisible ? ShellStatus : "";
}

/// <summary>The SyncPlay flyout as opened from the icon on screen: which host, whether it shows a
/// current group, and the texts it renders.</summary>
internal sealed record TwoClientFlyout(string Host, bool Joined, string GroupName, string GroupState,
    IReadOnlyList<string> Participants, string Status, string? Capture, TwoClientStacking? Stacking = null);

/// <summary>Where an open popup really is on the desktop, read from the window manager just before
/// its capture: its HWND is visible and lies on the captured area; walking the z-order up from the
/// host window (the overlay, or the shell for the shell's flyout) reaches it, so it is above the
/// host; and the top-level window at its centre is the popup itself, so nothing covers it.</summary>
internal sealed record TwoClientStacking(bool Visible, bool AboveHost, bool Unobscured, string Detail);

/// <summary>One row of the overlay's queue popup as rendered: its number, its text, whether the
/// current-entry check mark is shown and whether it offers a remove button.</summary>
internal sealed record TwoClientQueueRow(string Position, string Display, bool Current, bool Removable);

internal sealed record TwoClientQueue(IReadOnlyList<TwoClientQueueRow> Rows, string? Capture,
    TwoClientStacking? Stacking = null);

/// <summary>
/// One of the two clients in <c>--sync-play-two-client</c>: its own process, its own app-data
/// root and so its own persisted device id, ONE production <see cref="MainWindow"/> under a plain
/// application carrying the production resources (<see cref="UiFixtureApplication"/>), and a
/// named pipe to the controller.
///
/// <para>Every command goes through an entry point the product itself uses: sign-in is
/// <see cref="AppViewModel.LoginAsync"/>; create, list, join and leave are the methods the SyncPlay
/// flyout is constructed with; starting an episode is the detail view's Play handler, which queues
/// the rest of the series; transport is the player view model's toggle and seek, which is where the
/// overlay, the keys and the remote all arrive. Next, Previous and Back are the overlay's own
/// buttons, and the queue's jump and remove are its queue popup, each pressed through the control's
/// automation peer — the click the UI fixtures make. Nothing here reaches past those into the group
/// binding — no ApplyJoin, ApplyQueue, OnSyncPlayActionRequired, ArmSyncPlayLoad or
/// MpvPlayer.LoadFile — so whatever the player does is the product's answer to the server's frames.
/// Private members are READ for the snapshot only, with two exceptions that are not SyncPlay at
/// all: the detail view's Play handler, and the overlay's <c>ShowControls</c> (with its idle timer
/// stopped), which is what a mouse move does — called so a capture shows the controls and a button
/// is pressed while it is on screen, as a user's would be.</para>
///
/// <para>The window's CONTENT is sized to exactly <see cref="CaptureWidth"/> x
/// <see cref="CaptureHeight"/> device pixels and copied off the screen, as the UI fixture's screen
/// captures are, so a capture is what is really displayed: mpv's own video window, the shell, the
/// overlay and any open popup. A worker captures only as the foreground, active application, taken
/// BEFORE it opens a popup it is going to capture, because WPF raises a popup above other windows
/// only while its application is active. A worker that cannot take the foreground fails the
/// capture with what holds it, rather than copying whatever is on screen.</para>
///
/// <para>Guest-only, like the two modes before it: it opens a real window.</para>
/// </summary>
internal static class SyncPlayTwoClientWorker
{
    public const string Mode = "--sync-play-two-client-worker";

    /// <summary>Sent to the synthetic server only, which accepts any password.</summary>
    public const string Password = "synthetic-two-client-password";

    // 16:9 and small enough that both windows, frames included, sit side by side on the guest's
    // 1920-pixel desktop. At 1280x720 they overlapped, and each client's captures showed a corner
    // of the other client's window.
    public const int CaptureWidth = 928;
    public const int CaptureHeight = 522;

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static int Run(string[] args)
    {
        if (args.Length != 6 || !Path.IsPathFullyQualified(args[3])
            || !double.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var left)
            || !double.TryParse(args[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var top))
        {
            Console.WriteLine("worker: expected <pipe> <role> <absolute app-data root> <left> <top>");
            return 2;
        }
        // Before anything can touch AppPaths: its root is resolved once, on first use, and the
        // device id, the credential store and the logs all live under it.
        Environment.SetEnvironmentVariable("LIGHTWEAVER_TEST_APPDATA_ROOT", args[3]);
        // Video on, so a capture shows which clip is playing, unless the controller was told the
        // guest cannot render it.
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_VIDEO",
            Environment.GetEnvironmentVariable(SyncPlayTwoClientFixture.NoVideoSwitch) == "1" ? "1" : null);
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_AUDIO", "1");
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_MUTE", "1");
        Environment.SetEnvironmentVariable("LIGHTWEAVER_UPDATE_DEFER_START", "1");
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            pipe.Connect(15_000);
        }
        catch (TimeoutException)
        {
            Console.WriteLine("worker: the controller's pipe never accepted the connection");
            return 3;
        }
        var app = UiFixtureApplication.Create();
        var session = new Session(app, pipe, args[2], left, top);
        app.Dispatcher.BeginInvoke(() => _ = session.StartAsync());
        app.Run();
        return session.ExitCode;
    }

    private sealed class Session(Application app, NamedPipeClientStream pipe, string role, double left, double top)
    {
        private readonly object _writeLock = new();
        private readonly StreamWriter _writer = new(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        private MainWindow? _window;
        private bool _closing;

        public int ExitCode { get; private set; }

        private AppViewModel Model => Field<AppViewModel>(_window!, "_app");
        private OverlayWindow Overlay => Field<OverlayWindow>(_window!, "_overlay");

        public async Task StartAsync()
        {
            try
            {
                _window = new MainWindow { Left = left, Top = top };
                _window.Show();
                await UntilAsync(() => Field<MpvPlayer?>(_window, "_player") is not null
                    && Model.State == AppState.Login, TimeSpan.FromSeconds(60),
                    "the window never reached the login state with a player attached");
                await FitContentAsync();
                StartReader();
                Write(new TwoClientReply(0, true, null, null, Snapshot()));
            }
            catch (Exception ex)
            {
                Write(new TwoClientReply(0, false, Describe(ex), null, null));
                Close(4);
            }
        }

        private void StartReader()
        {
            var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            var thread = new Thread(() =>
            {
                try
                {
                    while (reader.ReadLine() is { } line)
                    {
                        if (JsonSerializer.Deserialize<TwoClientCommand>(line) is not { } command)
                            continue;
                        app.Dispatcher.InvokeAsync(() => HandleAsync(command));
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonException)
                {
                }
                // The controller is gone. A worker it can no longer drive must not outlive it.
                app.Dispatcher.BeginInvoke(() => Close(5));
            }) { IsBackground = true, Name = "two-client pipe reader" };
            thread.Start();
        }

        private async Task HandleAsync(TwoClientCommand command)
        {
            TwoClientReply reply;
            try
            {
                var result = await ExecuteAsync(command);
                reply = new TwoClientReply(command.Id, true, null, result, Snapshot());
            }
            catch (Exception ex)
            {
                reply = new TwoClientReply(command.Id, false, Describe(ex), null, TrySnapshot());
            }
            Write(reply);
            if (command.Op == "close")
                Close(0);
        }

        private async Task<string?> ExecuteAsync(TwoClientCommand command)
        {
            var window = _window!;
            var vm = Field<PlayerViewModel>(window, "_playerViewModel");
            switch (command.Op)
            {
                case "snapshot":
                case "close":
                    return null;
                case "login":
                    // The login form's own call. Null is success; anything else is its message.
                    return await Model.LoginAsync(command.Arg!, SyncPlayTwoClientServer.UserName, Password);
                case "create":
                    return (await window.CreateSyncPlayGroupAsync(command.Arg!)).ToString();
                case "list":
                    return JsonSerializer.Serialize(await window.ListSyncPlayGroupsAsync());
                case "join":
                    return (await window.JoinSyncPlayGroupAsync(Guid.Parse(command.Arg!))).ToString();
                case "leave":
                    await window.LeaveSyncPlayGroupAsync();
                    return null;
                case "play":
                    // The detail view's Play: an episode queues itself and the rest of its series,
                    // in a group that becomes a new group queue, and the entry then loads off the
                    // server's PlayQueue like any member's would.
                    var item = await Model.Jellyfin.GetItemAsync(Guid.Parse(command.Arg!))
                        ?? throw new InvalidOperationException("the server has no such item");
                    typeof(MainWindow).GetMethod("OnDetailPlayRequested", Private)!
                        .Invoke(window, [item, 0L, null]);
                    return null;
                case "toggle-pause":
                    vm.TogglePauseCommand.Execute(null);
                    return null;
                case "seek":
                    vm.Seek(double.Parse(command.Arg!, CultureInfo.InvariantCulture));
                    return null;
                case "next":
                case "previous":
                case "back":
                    // The overlay's own buttons, pressed while they are on screen.
                    Wake();
                    await InvokeAsync(Find<ButtonBase>(Overlay, command.Op switch
                    {
                        "next" => "NextButton",
                        "previous" => "PrevButton",
                        _ => "BackButton",
                    }));
                    return null;
                case "ui":
                    return JsonSerializer.Serialize(ReadUi());
                case "capture":
                    await TakeForegroundAsync();
                    return await CaptureAsync(command.Arg!);
                case "grant-foreground":
                    // Only the foreground process may hand the foreground on, so the controller
                    // asks the other worker to, before each capture.
                    return AllowSetForegroundWindow(int.Parse(command.Arg!, CultureInfo.InvariantCulture)).ToString();
                case "flyout":
                    return JsonSerializer.Serialize(await FlyoutAsync(command.Arg));
                case "queue":
                    {
                        if (command.Arg is not null)
                            await TakeForegroundAsync();
                        var (popup, list) = await OpenQueueAsync();
                        var rows = ReadQueue(list);
                        TwoClientStacking? stacking = null;
                        string? path = null;
                        if (command.Arg is { } name)
                        {
                            // Read straight after the copy, while the popup is still open: the
                            // z-order the image was taken under.
                            path = await CaptureAsync(name);
                            stacking = Stacking(popup, Overlay);
                        }
                        await CloseQueueAsync(popup);
                        return JsonSerializer.Serialize(new TwoClientQueue(rows, path, stacking));
                    }
                case "queue-select":
                    {
                        // The popup row's selection, through its automation peer: the popup's own
                        // SelectionChanged handler is the product's jump, and it closes the popup.
                        var (popup, list) = await OpenQueueAsync();
                        var index = int.Parse(command.Arg!, CultureInfo.InvariantCulture);
                        var peer = (UIElementAutomationPeer.CreatePeerForElement(list)?.GetChildren() ?? [])
                            .OfType<ItemAutomationPeer>().FirstOrDefault(p => ReferenceEquals(p.Item, list.Items[index]))
                            ?? throw new InvalidOperationException($"queue row {index} has no automation peer");
                        if (peer.GetPattern(PatternInterface.SelectionItem) is not ISelectionItemProvider select)
                            throw new InvalidOperationException($"queue row {index} cannot be selected");
                        select.Select();
                        await Task.Delay(150);
                        await CloseQueueAsync(popup);
                        return null;
                    }
                case "queue-remove":
                    {
                        var (popup, list) = await OpenQueueAsync();
                        var index = int.Parse(command.Arg!, CultureInfo.InvariantCulture);
                        var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
                        var remove = Descendants<Button>(row).FirstOrDefault()
                            ?? throw new InvalidOperationException($"queue row {index} has no remove button");
                        await InvokeAsync(remove);
                        await CloseQueueAsync(popup);
                        return null;
                    }
                default:
                    throw new InvalidOperationException("unknown command " + command.Op);
            }
        }

        /// <summary>Sizes the window so its content is exactly the capture size in device pixels,
        /// then keeps it on the work area.</summary>
        private async Task FitContentAsync()
        {
            var window = _window!;
            var content = (FrameworkElement)window.Content;
            var dpi = VisualTreeHelper.GetDpi(window);
            for (var attempt = 0; attempt < 4; attempt++)
            {
                await Task.Delay(100);
                var width = CaptureWidth / dpi.DpiScaleX - content.ActualWidth;
                var height = CaptureHeight / dpi.DpiScaleY - content.ActualHeight;
                if (Math.Abs(width) < 0.5 && Math.Abs(height) < 0.5)
                    break;
                window.Width = window.ActualWidth + width;
                window.Height = window.ActualHeight + height;
            }
            var work = SystemParameters.WorkArea;
            window.Left = Math.Max(work.Left, Math.Min(left, work.Right - window.ActualWidth));
            window.Top = Math.Max(work.Top, Math.Min(top, work.Bottom - window.ActualHeight));
            await Task.Delay(100);
        }

        /// <summary>What a mouse move over the overlay does: stop the idle timer and show the
        /// controls, so they are on screen rather than faded.</summary>
        private void Wake()
        {
            var overlay = Overlay;
            if (!overlay.IsVisible)
                return;
            Member<DispatcherTimer>(overlay, "_idleTimer").Stop();
            typeof(OverlayWindow).GetMethod("ShowControls", Private)!.Invoke(overlay, null);
        }

        private TwoClientUi ReadUi()
        {
            var window = _window!;
            var overlay = Overlay;
            var shellLabel = Find<TextBlock>(window, "SyncPlayButtonLabel");
            var overlayLabel = Find<TextBlock>(overlay, "SyncPlayButtonLabel");
            return new TwoClientUi(Model.State.ToString(), overlay.IsVisible,
                shellLabel.Text, shellLabel.IsVisible, AutomationProperties.GetName(Find<ToggleButton>(window, "SyncPlayButton")),
                overlayLabel.Text, overlayLabel.IsVisible, AutomationProperties.GetName(Find<ToggleButton>(overlay, "SyncPlayButton")),
                Find<TextBlock>(overlay, "NowPlayingTitle").Text, Find<TextBlock>(overlay, "NowPlayingSubtitle").Text,
                // Inside the queue button's content, so only there once the overlay has been shown.
                (TryFind(overlay, "QueueBadge") as TextBlock)?.Text ?? "", Find<ButtonBase>(overlay, "QueueButton").IsVisible);
        }

        /// <summary>Copies this window's content off the screen. The caller has taken the
        /// foreground; it is required again just before the copy, so an image is only ever taken
        /// as the foreground application.</summary>
        private async Task<string> CaptureAsync(string name)
        {
            var window = _window!;
            Wake();
            // Past the controls' 150 ms fade-in and a composition pass.
            await Task.Delay(400);
            RequireForeground("its capture");
            var origin = ((FrameworkElement)window.Content).PointToScreen(new Point(0, 0));
            using var bitmap = new System.Drawing.Bitmap(CaptureWidth, CaptureHeight);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                // CAPTUREBLT, or the copy leaves out layered windows, and every WPF popup with
                // AllowsTransparency is one: the SyncPlay flyout and the queue popup were
                // absent from all the captures that were meant to show them. A raw BitBlt,
                // because Graphics.CopyFromScreen rejects SourceCopy | CaptureBlt as an
                // invalid enum value.
                var target = graphics.GetHdc();
                var screen = GetDC(0);
                try
                {
                    if (!BitBlt(target, 0, 0, CaptureWidth, CaptureHeight, screen,
                            (int)Math.Round(origin.X), (int)Math.Round(origin.Y), SrcCopy | CaptureBlt))
                        throw new System.ComponentModel.Win32Exception();
                }
                finally
                {
                    _ = ReleaseDC(0, screen);
                    graphics.ReleaseHdc(target);
                }
            }
            Directory.CreateDirectory(SyncPlayTwoClientFixture.CaptureDirectory);
            var path = Path.Combine(SyncPlayTwoClientFixture.CaptureDirectory,
                $"{SyncPlayTwoClientFixture.CapturePrefix}{name}-{role}.png");
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return path;
        }

        /// <summary>Makes this worker's main window the foreground, active window. Plain
        /// <c>SetForegroundWindow</c> first, which succeeds once the controller or the foreground
        /// worker has granted this process the right; failing that, attaching this thread to the
        /// foreground thread's input for the call, which lifts the foreground lock. Fails with what
        /// holds the foreground if neither works.</summary>
        private async Task TakeForegroundAsync()
        {
            var window = _window!;
            var hwnd = new WindowInteropHelper(window).Handle;
            for (var attempt = 0; attempt < 3 && !IsForeground(window, hwnd); attempt++)
            {
                if (!SetForegroundWindow(hwnd))
                {
                    var mine = GetCurrentThreadId();
                    var theirs = GetWindowThreadProcessId(GetForegroundWindow(), out _);
                    if (theirs != 0 && theirs != mine && AttachThreadInput(mine, theirs, true))
                    {
                        try
                        {
                            BringWindowToTop(hwnd);
                            SetForegroundWindow(hwnd);
                        }
                        finally
                        {
                            AttachThreadInput(mine, theirs, false);
                        }
                    }
                }
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                while (!IsForeground(window, hwnd) && DateTime.UtcNow < deadline)
                    await Task.Delay(50);
            }
            RequireForeground("its capture");
            // Activation repaints the window; let that land before a popup opens or a copy is taken.
            await Task.Delay(150);
        }

        private static bool IsForeground(Window window, nint hwnd) => GetForegroundWindow() == hwnd && window.IsActive;

        /// <summary>Throws unless this worker's main window is the foreground window and WPF has
        /// it active.</summary>
        private void RequireForeground(string what)
        {
            var window = _window!;
            var foreground = GetForegroundWindow();
            GetWindowThreadProcessId(foreground, out var pid);
            if (foreground != new WindowInteropHelper(window).Handle || pid != Environment.ProcessId || !window.IsActive)
                throw new InvalidOperationException($"worker {role} is not the foreground application for {what}: "
                    + $"foreground={DescribeWindow(foreground)} worker pid={Environment.ProcessId} active={window.IsActive}");
        }

        /// <summary>The window manager's answer to "is this open popup really on screen, above
        /// its host": see <see cref="TwoClientStacking"/>.</summary>
        private TwoClientStacking Stacking(Popup popup, Window host)
        {
            var popupHwnd = popup.Child is Visual visual && PresentationSource.FromVisual(visual) is HwndSource source
                ? source.Handle : nint.Zero;
            var hostHwnd = new WindowInteropHelper(host).Handle;
            var origin = ((FrameworkElement)_window!.Content).PointToScreen(new Point(0, 0));
            int left = (int)Math.Round(origin.X), top = (int)Math.Round(origin.Y);
            int right = left + CaptureWidth, bottom = top + CaptureHeight;
            var rect = default(NativeRect);
            var visible = popupHwnd != nint.Zero && IsWindowVisible(popupHwnd) && GetWindowRect(popupHwnd, out rect)
                && rect.Right > rect.Left && rect.Bottom > rect.Top
                && rect.Left < right && rect.Right > left && rect.Top < bottom && rect.Bottom > top;
            // Up the z-order from the host: reaching the popup means it is drawn above the host.
            var above = false;
            var steps = 0;
            for (var next = GetWindow(hostHwnd, GwHwndPrev); next != nint.Zero && steps < 10_000;
                 next = GetWindow(next, GwHwndPrev), steps++)
            {
                if (next == popupHwnd)
                {
                    above = true;
                    break;
                }
            }
            var centre = new NativePoint { X = (rect.Left + rect.Right) / 2, Y = (rect.Top + rect.Bottom) / 2 };
            var hit = popupHwnd == nint.Zero ? nint.Zero : GetAncestor(WindowFromPoint(centre), GaRoot);
            var detail = $"popup={DescribeWindow(popupHwnd)} rect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom} "
                + $"capture={left},{top},{right},{bottom} host={DescribeWindow(hostHwnd)} above={above} "
                + $"(walked {steps}) atCentre={DescribeWindow(hit)} foreground={DescribeWindow(GetForegroundWindow())}";
            return new TwoClientStacking(visible, above, hit != nint.Zero && hit == popupHwnd, detail);
        }

        /// <summary>Opens the flyout from the icon on screen, reads what it renders, optionally
        /// captures it open, and closes it with its own Close button.</summary>
        private async Task<TwoClientFlyout> FlyoutAsync(string? capture)
        {
            var overlay = Overlay;
            Window host = overlay.IsVisible ? overlay : _window!;
            if (capture is not null)
                await TakeForegroundAsync();
            Wake();
            var popup = Find<Popup>(host, "SyncPlayPopup");
            if (popup.IsOpen)
                throw new InvalidOperationException("the SyncPlay flyout was already open");
            await InvokeAsync(Find<ToggleButton>(host, "SyncPlayButton"));
            await UntilAsync(() => popup.IsOpen && popup.Child is SyncPlayFlyout { IsLoaded: true }, TimeSpan.FromSeconds(5),
                "the SyncPlay flyout opens from its icon");
            var flyout = (SyncPlayFlyout)popup.Child;
            // Outside a group the flyout lists the groups first; let that settle.
            await UntilAsync(() => !Find<TextBlock>(flyout, "SyncPlayStatus").Text.StartsWith("Looking", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5), "the SyncPlay flyout finishes loading");
            await Task.Delay(150);
            // Only what is on screen counts. Outside a group the group panel is collapsed: a
            // subtree never shown is never templated, so its texts cannot even be found, and one
            // shown earlier still holds the old group's text while hidden. Read them only while
            // the panel is visible, and as empty otherwise.
            var joined = Find<FrameworkElement>(flyout, "CurrentGroupPanel").IsVisible;
            string TextOf(string id) => joined && TryFind(flyout, id) is TextBlock text ? text.Text : "";
            var participants = TextOf("SyncPlayParticipants")
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            var result = new TwoClientFlyout(ReferenceEquals(host, overlay) ? "overlay" : "shell",
                joined, TextOf("SyncPlayGroupName"), TextOf("SyncPlayGroupState"),
                participants, Find<TextBlock>(flyout, "SyncPlayStatus").Text, null);
            var path = capture is null ? null : await CaptureAsync(capture);
            // Straight after the copy, while the flyout is still open: the z-order it was taken under.
            var stacking = capture is null ? null : Stacking(popup, host);
            await InvokeAsync(Find<ButtonBase>(flyout, "SyncPlayClose"));
            await UntilAsync(() => !popup.IsOpen, TimeSpan.FromSeconds(5), "the SyncPlay flyout closes");
            return result with { Capture = path, Stacking = stacking };
        }

        private async Task<(Popup Popup, ListBox List)> OpenQueueAsync()
        {
            var overlay = Overlay;
            if (!overlay.IsVisible)
                throw new InvalidOperationException("the queue lives in the overlay, which is not shown");
            Wake();
            var popup = Find<Popup>(overlay, "QueuePopup");
            if (!popup.IsOpen)
                await InvokeAsync(Find<ButtonBase>(overlay, "QueueButton"));
            var list = Find<ListBox>(overlay, "QueueList");
            await UntilAsync(() => popup.IsOpen && list.IsVisible && list.Items.Count > 0
                    && Enumerable.Range(0, list.Items.Count).All(i =>
                        list.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem { IsLoaded: true }),
                TimeSpan.FromSeconds(5), "the queue popup opens with every row realized");
            await Task.Delay(100);
            return (popup, list);
        }

        private async Task CloseQueueAsync(Popup popup)
        {
            if (popup.IsOpen)
                await InvokeAsync(Find<ButtonBase>(Overlay, "QueueButton"));
            await UntilAsync(() => !popup.IsOpen, TimeSpan.FromSeconds(5), "the queue popup closes");
        }

        /// <summary>Each row as drawn: the check-mark glyph, the "N." number and the title are the
        /// row template's three text blocks outside its remove button.</summary>
        private static List<TwoClientQueueRow> ReadQueue(ListBox list)
        {
            var check = (string)list.FindResource("IconCheck");
            var rows = new List<TwoClientQueueRow>();
            for (var i = 0; i < list.Items.Count; i++)
            {
                var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(i);
                var blocks = Descendants<TextBlock>(container)
                    .Where(t => !Ancestors(t).Any(a => a is ButtonBase)).ToList();
                var marker = blocks.FirstOrDefault(t => t.Text == check);
                var number = blocks.FirstOrDefault(t => Regex.IsMatch(t.Text, @"^\d+\.$"));
                var title = blocks.LastOrDefault(t => !ReferenceEquals(t, marker) && !ReferenceEquals(t, number));
                var remove = Descendants<Button>(container).FirstOrDefault();
                rows.Add(new TwoClientQueueRow(number?.Text ?? "", title?.Text ?? "", marker?.IsVisible == true,
                    remove?.IsVisible == true));
            }
            return rows;
        }

        private TwoClientSnapshot? TrySnapshot()
        {
            try { return Snapshot(); }
            catch (Exception) { return null; }
        }

        private TwoClientSnapshot Snapshot()
        {
            var window = _window!;
            var model = Model;
            var client = Field<SyncPlayClient>(window, "_syncPlay");
            var player = Field<MpvPlayer?>(window, "_player");
            var playing = typeof(MainWindow).GetField("_playingItem", Private)!.GetValue(window) as MediaItem;
            var hasPosition = false;
            double position = 0;
            if (player is not null)
                hasPosition = player.TryReadPosition(out position);
            // Straight after the read, so the pair describes one observation.
            var captured = DateTime.UtcNow.Ticks;
            return new TwoClientSnapshot(
                role, Environment.ProcessId, AppPaths.Root, CredentialStore.GetOrCreateDeviceId(),
                model.ActiveSessionKey, model.Jellyfin.UserId, model.State.ToString(),
                app.Windows.OfType<MainWindow>().Count(), client.IsInGroup, client.IsMembershipPending,
                client.GroupId, client.GroupName, [.. client.Participants], client.GroupState,
                client.PlaylistItemId, playing?.Id, player is not null, player?.IsLoading ?? false,
                player is null || Property(player, "pause") == "yes", hasPosition, position,
                player?.Duration ?? 0, player?.LoadGeneration ?? -1,
                player is null ? null : Property(player, "path"), captured,
                [.. Field<PlayerViewModel>(window, "_playerViewModel").QueueRows
                    .Select(r => (r.IsCurrent ? "*" : "") + r.Display)]);
        }

        private static string? Property(MpvPlayer player, string name)
        {
            try { return player.GetPropertyString(name); }
            catch (Exception) { return null; }
        }

        private void Write(TwoClientReply reply)
        {
            var line = JsonSerializer.Serialize(reply);
            lock (_writeLock)
            {
                try { _writer.WriteLine(line); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            }
        }

        private void Close(int code)
        {
            if (_closing)
                return;
            _closing = true;
            ExitCode = code;
            // Queued, so the reply above is on the wire before the window starts tearing down.
            app.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                try { _window?.Close(); }
                finally { app.Shutdown(code); }
            });
        }

        private static string Describe(Exception ex)
            => ex is TargetInvocationException { InnerException: { } inner }
                ? $"{inner.GetType().Name}: {inner.Message}" : $"{ex.GetType().Name}: {ex.Message}";

        private static async Task UntilAsync(Func<bool> condition, TimeSpan timeout, string what)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!condition())
            {
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException(what);
                await Task.Delay(50);
            }
        }
    }

    /// <summary>Presses a control through its automation peer — no focus, no coordinates — and
    /// lets the click it dispatches run.</summary>
    private static async Task InvokeAsync(ButtonBase button)
    {
        var id = AutomationProperties.GetAutomationId(button);
        if (!button.IsVisible || !button.IsEnabled)
            throw new InvalidOperationException(
                $"control {id} is not available (visible={button.IsVisible} enabled={button.IsEnabled})");
        var peer = UIElementAutomationPeer.CreatePeerForElement(button);
        if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
            invoke.Invoke();
        else if (peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle)
            toggle.Toggle();
        else
            throw new InvalidOperationException($"control {id} has no Invoke or Toggle provider");
        await Task.Delay(150);
    }

    private static T Field<T>(object target, string name)
        => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(target)!;

    private static T Member<T>(object target, string name)
        => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;

    private const int SrcCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source,
        int sourceX, int sourceY, int operation);

    private const uint GwHwndPrev = 3;
    private const uint GaRoot = 2;
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool BringWindowToTop(nint window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint to, bool on);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int processId);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int length);

    /// <summary>An HWND with its process and window class, for failure details.</summary>
    private static string DescribeWindow(nint window)
    {
        if (window == nint.Zero)
            return "none";
        GetWindowThreadProcessId(window, out var pid);
        var name = new StringBuilder(128);
        _ = GetClassName(window, name, name.Capacity);
        return $"0x{window:X}/pid {pid}/{name}";
    }

    /// <summary>A named element, or one found by automation id — the UI fixture's lookup, rooted
    /// at the window's content as well, which is all a window that has never been shown has.</summary>
    private static T Find<T>(FrameworkElement root, string name) where T : class
        => (T)(TryFind(root, name) ?? throw new InvalidOperationException($"missing {name}"));

    private static object? TryFind(FrameworkElement root, string name)
        => root.FindName(name) ?? FindById(root, name)
            ?? (root is ContentControl { Content: DependencyObject content } ? FindById(content, name) : null);

    private static DependencyObject? FindById(DependencyObject root, string id)
    {
        if (AutomationProperties.GetAutomationId(root) == id)
            return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindById(VisualTreeHelper.GetChild(root, i), id) is { } child)
                return child;
        return null;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var deeper in Descendants<T>(child))
                yield return deeper;
        }
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            yield return parent;
    }
}
