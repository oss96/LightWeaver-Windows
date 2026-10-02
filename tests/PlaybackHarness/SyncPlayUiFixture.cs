using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LightWeaver;
using LightWeaver.Jellyfin;
using LightWeaver.ViewModels;
using LightWeaver.Views;

// Guest-only: opens the real shell and overlay flyouts, invoking named controls.
// Server state is synthetic here; protocol and asynchronous load races have separate legs.
internal static class SyncPlayUiFixture
{
    private static int _failures;
    private static int _assertions;
    private static readonly string Output = Path.Combine(Path.GetTempPath(), "lightweaver-tests");

    public static int Run()
    {
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_MUTE", "1");
        Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_AUDIO", "1");
        Environment.SetEnvironmentVariable("LIGHTWEAVER_UPDATE_DEFER_START", "1");
        var app = UiFixtureApplication.Create();
        MainWindow? main = null;
        SyncPlayFlyout? dialog = null;
        try
        {
            var icons = new Typeface((FontFamily)app.FindResource("LwFontIcon"), FontStyles.Normal,
                FontWeights.Normal, FontStretches.Normal);
            Check(icons.TryGetGlyphTypeface(out var glyphs)
                && glyphs.CharacterToGlyphMap.ContainsKey(((string)app.FindResource("IconCheck"))[0])
                && glyphs.CharacterToGlyphMap.ContainsKey(((string)app.FindResource("IconClose"))[0]),
                "harness resolves bundled current-item and remove icon glyphs");
            main = new MainWindow { Width = 700, Height = 480 };
            main.Show();
            main.Activate();
            Pump();
            Require(app.Windows.OfType<MainWindow>().Count() == 1,
                "fixture owns exactly one production MainWindow");
            var vm = Field<PlayerViewModel>(main, "_playerViewModel");
            var overlay = Field<OverlayWindow>(main, "_overlay");
            var model = Field<AppViewModel>(main, "_app");
            model.LeavePlayback();
            Pump();
            Check(Element<ToggleButton>(main, "SyncPlayButton").IsVisible, "shell exposes SyncPlay icon at 700x480");
            Check(Element<TextBlock>(main, "SyncPlayButtonLabel").Visibility == Visibility.Collapsed,
                "inactive shell shows only the icon");
            Click(main, "SyncPlayButton");
            var popup = Element<Popup>(main, "SyncPlayPopup");
            Check(popup.IsOpen && !popup.StaysOpen && popup.Child is SyncPlayFlyout
                && popup.PlacementTarget == Element<ToggleButton>(main, "SyncPlayButton"),
                "shell icon opens anchored production flyout with outside dismissal");
            Check(RightAligned(popup, Element<ToggleButton>(main, "SyncPlayButton")),
                "shell dropdown right edge aligns with icon and opens into host");
            Click(main, "SyncPlayButton");
            Check(!popup.IsOpen, "shell icon toggles flyout closed");
            Click(main, "SyncPlayButton");
            CaptureScreen(main, popup, "shell-open-before-native");
            ClickNative(main, Element<ToggleButton>(main, "SyncPlayButton"));
            Require(!popup.IsOpen, "native shell icon click closes flyout before any keyboard activation");
            Element<ToggleButton>(main, "SyncPlayButton").Focus();
            SendKey(Element<ToggleButton>(main, "SyncPlayButton"), Key.Space);
            Require(popup.IsOpen, "focused shell icon opens flyout with Space");
            Escape((FrameworkElement)popup.Child);
            SendKey(Element<ToggleButton>(main, "SyncPlayButton"), Key.Enter);
            Require(popup.IsOpen, "Escape returns shell icon focus and Enter reopens flyout");
            Require(BindingOperations.IsDataBound(Element<ToggleButton>(main, "SyncPlayButton"), ToggleButton.IsCheckedProperty),
                "shell keyboard activation preserves trigger binding");
            ClickNative(main, Element<ToggleButton>(main, "SyncPlayButton"));
            Require(!popup.IsOpen, "native second click on shell icon closes flyout without reopening");

            var listCompletion = new TaskCompletionSource<IReadOnlyList<SyncPlayGroupSummary>>();
            var listMode = 0;
            var lateList = new TaskCompletionSource<IReadOnlyList<SyncPlayGroupSummary>>();
            var selected = Guid.NewGuid();
            var joined = Guid.Empty;
            var created = "";
            var leaves = 0;
            var groupName = "Friday film club with a deliberately long group name";
            IReadOnlyList<SyncPlayGroupSummary> groups =
            [new(selected, groupName, ["Alex", "Morgan", "Sam with a long display name"]),
             new(Guid.NewGuid(), "Another group", ["Taylor"])];
            Func<SyncPlayFlyout> factory = () => new SyncPlayFlyout(vm,
                () => listMode switch
                {
                    0 => listCompletion.Task,
                    3 => lateList.Task,
                    1 => Task.FromException<IReadOnlyList<SyncPlayGroupSummary>>(new InvalidOperationException()),
                    _ => Task.FromResult(groups),
                },
                id => { joined = id; SetPending(vm, true); return Task.FromResult(true); },
                () => { leaves++; SetPending(vm, false); SetGroup(vm, null); return Task.CompletedTask; },
                name => { created = name; return Task.FromResult(true); });
            typeof(MainWindow).GetField("_syncPlayFlyoutFactory", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(main, factory);
            Click(main, "SyncPlayButton");
            dialog = (SyncPlayFlyout)popup.Child;
            Pump();
            Check(Text(dialog, "SyncPlayStatus").Contains("Looking"), "loading status is visible");
            Check(!Element<Button>(dialog, "RefreshButton").IsEnabled, "loading blocks duplicate refresh");
            Check(dialog.IsKeyboardFocusWithin && !Element<TextBox>(dialog, "GroupNameBox").IsEnabled,
                "slow initial list keeps keyboard in flyout while entry is disabled");
            Capture(dialog, "loading");
            listCompletion.SetResult([]);
            Pump();
            Check(Text(dialog, "SyncPlayStatus").Contains("No groups"), "empty result is distinct from failure");
            Check(Element<TextBox>(dialog, "GroupNameBox").IsKeyboardFocusWithin,
                "delayed initial list completion focuses newly enabled group-name entry");
            Capture(dialog, "empty");
            listMode = 1;
            Click(dialog, "SyncPlayRefresh");
            Pump();
            Check(Text(dialog, "SyncPlayStatus").Contains("Could not load"), "read failure offers retry");
            Capture(dialog, "error");
            listMode = 2;
            Click(dialog, "SyncPlayRefresh");
            Pump();
            var groupList = Element<ListBox>(dialog, "GroupsList");
            Check(groupList.Items.Count == 2, "refresh recovers and shows available groups");
            AssertOwned(dialog, groupList);
            groupList.SelectedIndex = 0;
            Check(Element<Button>(dialog, "JoinButton").IsEnabled, "selection enables join");
            Capture(dialog, "available");
            Click(dialog, "SyncPlayJoin");
            Check(joined == selected, "join sends selected group identity");
            Check(vm.SyncPlayStatus == "Joining\u2026", "unjoined pending status says Joining");
            Check(Text(dialog, "SyncPlayStatus").Contains("Waiting"), "join waits for server membership");
            Check(Element<Button>(dialog, "CancelJoinButton").IsVisible, "pending join can be cancelled");
            Check(Element<FrameworkElement>(dialog, "CreatePanel").Visibility == Visibility.Collapsed,
                "pending membership reserves picker space by hiding create options");
            var selectedRow = (ListBoxItem)groupList.ItemContainerGenerator.ContainerFromIndex(0);
            Check(selectedRow.ActualHeight <= groupList.ActualHeight,
                "pending membership retains room for a complete selected group row");
            Capture(dialog, "pending");
            Click(dialog, "SyncPlayCancelJoin");
            Check(leaves == 1 && !vm.SyncPlayPending, "cancel invokes leave callback");
            var nameBox = Element<TextBox>(dialog, "GroupNameBox");
            AssertOwned(dialog, nameBox);
            nameBox.Text = " New group ";
            Click(dialog, "SyncPlayCreate");
            Check(created == "New group", "create sends trimmed group name");
            SetGroup(vm, new(groupName, groups[0].Participants, "Paused"));
            Pump();
            Check(Text(dialog, "SyncPlayGroupName") == groupName, "joined group name is rendered");
            SetPending(vm, true);
            Check(vm.SyncPlayStatus == "Syncing\u2026 \u00b7 3", "joined pending transport status says Syncing with participant count");
            SetPending(vm, false);
            Check(vm.SyncPlayStatus == "Paused \u00b7 3", "clearing transport pending restores actual group state");
            Check(Text(dialog, "SyncPlayParticipants").Contains("Morgan"), "participants are rendered");
            Check(!groupList.IsVisible, "joined state hides group picker");
            Capture(dialog, "joined");
            CaptureScreen(main, popup, "shell-flyout");
            dialog.Width = 300;
            dialog.Height = 360;
            Pump();
            var leaveButton = Element<Button>(dialog, "LeaveButton");
            var closeButton = Element<Button>(dialog, "SyncPlayClose");
            Check(leaveButton.TranslatePoint(new Point(0, leaveButton.ActualHeight), dialog).Y
                <= closeButton.TranslatePoint(new Point(), dialog).Y,
                "minimum flyout keeps Leave group above Close");
            Check(Element<ScrollViewer>(dialog, "ParticipantsScroll").ViewportHeight
                >= Element<TextBlock>(dialog, "ParticipantsText").FontSize,
                "minimum flyout keeps a readable participant viewport");
            Capture(dialog, "joined-360");
            var longestName = string.Join(" ", Enumerable.Repeat("Watch together", 8))[..100];
            SetGroup(vm, new(longestName, Enumerable.Range(1, 20).Select(i => $"Participant {i}").ToArray(), "Paused"));
            Pump();
            var details = Element<ScrollViewer>(dialog, "GroupDetailsScroll");
            var participants = Element<ScrollViewer>(dialog, "ParticipantsScroll");
            Check(leaveButton.TranslatePoint(new Point(0, leaveButton.ActualHeight), dialog).Y
                <= closeButton.TranslatePoint(new Point(), dialog).Y,
                "maximum group name keeps Leave above Close at narrow flyout size");
            Check(details.ViewportHeight >= Element<TextBlock>(dialog, "ParticipantsText").FontSize
                && participants.ScrollableHeight > 0,
                "long group details and participant list remain scrollable");
            Capture(dialog, "joined-long-360-top");
            details.ScrollToEnd();
            participants.ScrollToEnd();
            Pump();
            Capture(dialog, "joined-long-360");
            Capture(main, "shell-700");
            Click(dialog, "SyncPlayLeave");
            Check(leaves == 2 && vm.SyncPlay is null, "leave invokes callback and restores picker");
            Click(dialog, "SyncPlayClose");
            Check(!popup.IsOpen, "flyout Close dismisses production popup");
            Check(Element<ToggleButton>(main, "SyncPlayButton").IsKeyboardFocusWithin, "Close returns focus to shell icon");
            dialog = null;
            Click(main, "SyncPlayButton");
            dialog = (SyncPlayFlyout)popup.Child;
            Escape(dialog);
            Check(!popup.IsOpen && Element<ToggleButton>(main, "SyncPlayButton").IsKeyboardFocusWithin,
                "Escape dismisses shell flyout and returns focus");
            Click(main, "SyncPlayButton");
            dialog = (SyncPlayFlyout)popup.Child;
            ClickOutside(main, Element<ToggleButton>(main, "SyncPlayButton"));
            Check(!popup.IsOpen, "outside mouse click dismisses shell flyout");
            Click(main, "SyncPlayButton");
            dialog = (SyncPlayFlyout)popup.Child;
            Check(Element<TextBox>(dialog, "GroupNameBox").IsKeyboardFocusWithin,
                "reopened inactive flyout focuses group-name entry");
            var textBox = Element<TextBox>(dialog, "GroupNameBox");
            var composition = new TextComposition(InputManager.Current, textBox, "?");
            textBox.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                { RoutedEvent = TextCompositionManager.TextInputEvent });
            Pump();
            Check(!Element<ShortcutsOverlay>(main, "Shortcuts").IsOpen,
                "group-name entry does not open shortcut panel");

            Click(dialog, "SyncPlayClose");
            listMode = 3;
            Click(main, "SyncPlayButton");
            var abandoned = (SyncPlayFlyout)popup.Child;
            popup.IsOpen = false;
            listMode = 2;
            // Reopen before pumping Popup's deferred Closed event.
            Element<ToggleButton>(main, "SyncPlayButton").SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Pump();
            dialog = (SyncPlayFlyout)popup.Child;
            lateList.SetResult([]);
            Pump();
            SetPending(vm, true);
            Check(Text(abandoned, "SyncPlayStatus").Contains("Looking")
                && Text(dialog, "SyncPlayStatus").Contains("Waiting"),
                "closed async content ignores late completion and membership changes after immediate reopen");
            SetPending(vm, false);
            Check(Element<TextBox>(dialog, "GroupNameBox").IsKeyboardFocusWithin,
                "late closed-content completion does not steal focus from replacement entry");

            SetGroup(vm, new(groupName, groups[0].Participants, "Playing"));
            vm.SetNowPlaying("A film with friends", "SyncPlay guest UI fixture");
            vm.SetQualityState(null, false, 20);
            var entry = Guid.NewGuid();
            SetQueue(vm, [new(0, "Current episode", true, Guid.NewGuid()), new(1, "Next episode", false, entry)]);
            model.EnterPlayback();
            overlay.SetBoundsFromOwner(main.Left, main.Top, 700, 480);
            overlay.Show();
            Pump();
            Check(Element<ToggleButton>(overlay, "SyncPlayButton").IsVisible, "playback exposes group pill");
            Check(!Element<FrameworkElement>(overlay, "QualityTierControl").IsEnabled, "group disables quality changes");
            Check(!Element<FrameworkElement>(overlay, "VersionSection").IsEnabled, "group disables version changes");
            Check(!Element<Button>(overlay, "SpeedButton").IsEnabled, "group disables speed changes");
            Click(overlay, "OverlaySyncPlayButton");
            Pump();
            Check(!popup.IsOpen, "playback transition closes shell flyout");
            var overlayPopup = Element<Popup>(overlay, "SyncPlayPopup");
            Check(overlayPopup.IsOpen && overlayPopup.Child is SyncPlayFlyout
                && overlayPopup.PlacementTarget == Element<ToggleButton>(overlay, "SyncPlayButton"),
                "playback icon opens anchored overlay flyout");
            Check(RightAligned(overlayPopup, Element<ToggleButton>(overlay, "SyncPlayButton")),
                "overlay dropdown right edge aligns with icon and opens into host");
            Check(Text(overlay, "SyncPlayButtonLabel") == "Playing · 3", "playback shows compact state left of icon");
            var status = Element<TextBlock>(overlay, "SyncPlayButtonLabel");
            var icon = Element<ToggleButton>(overlay, "SyncPlayButton");
            Check(status.TranslatePoint(new Point(status.ActualWidth, 0), overlay).X
                <= icon.TranslatePoint(new Point(), overlay).X, "status is positioned to the left of SyncPlay icon");
            Check(((IEnumerable<Popup>)typeof(OverlayWindow).GetProperty("Flyouts", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(overlay)!).Contains(overlayPopup), "SyncPlay participates in central flyout lifecycle");
            typeof(OverlayWindow).GetMethod("HideControls", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(overlay, null);
            Check(Field<FrameworkElement>(overlay, "ControlsPanel").Opacity > 0,
                "open SyncPlay flyout keeps overlay controls awake");
            var bridged = 0;
            overlay.KeyPressed += _ => bridged++;
            SendKey(overlay, Key.Space);
            Check(bridged == 0, "open SyncPlay flyout suppresses player shortcut forwarding");
            Capture(overlayPopup.Child, "overlay-joined");
            CaptureScreen(overlay, overlayPopup, "overlay-flyout");
            Escape((FrameworkElement)overlayPopup.Child);
            Check(!overlayPopup.IsOpen && icon.IsKeyboardFocusWithin,
                "Escape dismisses overlay flyout and returns focus to icon");
            Click(overlay, "OverlaySyncPlayButton");
            Click(overlay, "OverlaySyncPlayButton");
            Check(!overlayPopup.IsOpen, "overlay icon toggles flyout closed");
            Click(overlay, "OverlaySyncPlayButton");
            CaptureScreen(overlay, overlayPopup, "overlay-open-before-native");
            ClickNative(overlay, icon);
            Require(!overlayPopup.IsOpen, "native overlay icon click closes flyout before keyboard activation");
            icon.Focus();
            SendKey(icon, Key.Space);
            Require(overlayPopup.IsOpen && bridged == 0,
                "focused overlay icon opens flyout with Space without forwarding player pause");
            Escape((FrameworkElement)overlayPopup.Child);
            SendKey(icon, Key.Enter);
            Require(overlayPopup.IsOpen && bridged == 0,
                "Escape returns overlay icon focus and Enter reopens without player shortcut forwarding");
            Require(BindingOperations.IsDataBound(icon, ToggleButton.IsCheckedProperty),
                "overlay keyboard activation preserves trigger binding");
            ClickNative(overlay, icon);
            Require(!overlayPopup.IsOpen, "native second click on overlay icon closes flyout without reopening");
            Click(overlay, "OverlaySyncPlayButton");
            ClickOutside(overlay, icon);
            Check(!overlayPopup.IsOpen, "outside mouse click dismisses overlay flyout");
            Click(overlay, "OverlaySyncPlayButton");
            overlay.Hide();
            Check(!overlayPopup.IsOpen, "hiding overlay closes SyncPlay flyout");
            overlay.Show();
            Pump();
            Click(overlay, "QueueButton");
            Pump();
            var queue = Element<ListBox>(overlay, "QueueList");
            Check(queue.Items.Count == 2, "playback queue renders group entries");
            var requested = Guid.Empty;
            vm.TransportGate = request => { requested = request.PlaylistItemId ?? Guid.Empty; return false; };
            Capture(overlay, "overlay");
            Capture(Element<Popup>(overlay, "QueuePopup").Child, "queue");
            var queuePopup = Element<Popup>(overlay, "QueuePopup");
            if (Window.GetWindow(queuePopup.PlacementTarget) != overlay || !queuePopup.IsOpen)
                throw new InvalidOperationException("Queue popup is not owned by the tested overlay");
            queue.SelectedIndex = 1;
            Check(requested == entry, "queue selection routes group playlist identity");
            overlay.SetMiniMode(true);
            overlay.SetMiniMode(false);
            vm.Volume = 35;
            Pump();
            Capture(overlay, "restored-volume");
            SetGroup(vm, null);
            Check(Element<FrameworkElement>(overlay, "QualityTierControl").IsEnabled, "leaving restores quality control");
            overlay.Hide();
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"FAIL: SyncPlay UI fixture {ex.GetType().Name}: {ex.Message}");
        }
        finally { dialog?.Dispose(); main?.Close(); app.Shutdown(); }
        Console.WriteLine($"RESULT: {(_failures == 0 ? "PASS" : "FAIL")} ({_assertions} sync play UI assertions, {_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static T Field<T>(object owner, string name) where T : class
        => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static T Element<T>(FrameworkElement root, string name) where T : class
        => (T)(root.FindName(name) ?? FindById(root, name) ?? throw new InvalidOperationException($"Missing {name}"));
    private static DependencyObject? FindById(DependencyObject root, string id)
    {
        if (System.Windows.Automation.AutomationProperties.GetAutomationId(root) == id) return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindById(VisualTreeHelper.GetChild(root, i), id) is { } child) return child;
        return null;
    }
    private static string Text(FrameworkElement root, string id) => Element<TextBlock>(root, id).Text;
    private static void Click(FrameworkElement root, string id)
    {
        var button = Element<ButtonBase>(root, id);
        AssertOwned(root, button);
        if (!button.IsVisible || !button.IsEnabled) throw new InvalidOperationException($"Button unavailable: {id}");
        // Invoke the named control's automation peer; no focus or coordinate injection.
        var peer = UIElementAutomationPeer.CreatePeerForElement(button);
        if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) invoke.Invoke();
        else if (peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle) toggle.Toggle();
        else throw new InvalidOperationException($"Button has no Invoke or Toggle provider: {id}");
        Pump();
    }
    private static void AssertOwned(FrameworkElement root, FrameworkElement target)
    {
        var owner = root as Window ?? Window.GetWindow(root);
        if (root is SyncPlayFlyout)
        {
            if (!root.IsVisible || PresentationSource.FromVisual(root) is null
                || PresentationSource.FromVisual(root) != PresentationSource.FromVisual(target))
                throw new InvalidOperationException("Input target does not belong to the tested flyout");
        }
        else if (owner is null || !owner.IsVisible || Window.GetWindow(target) != owner)
            throw new InvalidOperationException("Input target does not belong to the tested window");
    }
    private static void Escape(FrameworkElement root) => SendKey(root, Key.Escape);
    private static void SendKey(FrameworkElement root, Key key)
    {
        var source = PresentationSource.FromVisual(root) ?? throw new InvalidOperationException("No input source");
        root.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Pump();
    }
    private static void ClickOutside(Window host, FrameworkElement anchor)
        => ClickPoint(host, anchor.PointToScreen(new Point(-50, 20)));
    private static void ClickNative(Window host, FrameworkElement anchor)
    {
        AssertOwned(host, anchor);
        ClickPoint(host, anchor.PointToScreen(new Point(anchor.ActualWidth / 2, anchor.ActualHeight / 2)));
    }
    private static void ClickPoint(Window host, Point point)
    {
        var target = GetAncestor(WindowFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y }), 2);
        GetWindowThreadProcessId(target, out var targetPid);
        GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundPid);
        var hostHwnd = new WindowInteropHelper(host).Handle;
        if (target != hostHwnd || targetPid != Environment.ProcessId || foregroundPid != Environment.ProcessId)
        {
            GetWindowRect(hostHwnd, out var rect);
            GetWindowRect(target, out var targetRect);
            var popup = Element<Popup>(host, "SyncPlayPopup");
            var popupHwnd = popup.Child is Visual visual && PresentationSource.FromVisual(visual) is HwndSource source
                ? source.Handle : nint.Zero;
            GetWindowRect(popupHwnd, out var popupRect);
            var overlayHwnd = host is MainWindow main ? new WindowInteropHelper(Field<OverlayWindow>(main, "_overlay")).Handle : hostHwnd;
            GetWindowRect(overlayHwnd, out var overlayRect);
            var targetClass = new System.Text.StringBuilder(128);
            GetClassName(target, targetClass, targetClass.Capacity);
            throw new InvalidOperationException($"Native click ownership failed: targetIsHost={target == hostHwnd} "
                + $"targetPid={targetPid} foregroundPid={foregroundPid} fixturePid={Environment.ProcessId} "
                + $"hostIsActive={host.IsActive} point={point.X:F0},{point.Y:F0} "
                + $"hostRect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom} "
                + $"targetClass={targetClass} targetRect={targetRect.Left},{targetRect.Top},{targetRect.Right},{targetRect.Bottom} "
                + $"targetIsPopup={target == popupHwnd} popupPlacement={popup.Placement} "
                + $"popupRect={popupRect.Left},{popupRect.Top},{popupRect.Right},{popupRect.Bottom} "
                + $"targetIsOverlay={target == overlayHwnd} overlayRect={overlayRect.Left},{overlayRect.Top},{overlayRect.Right},{overlayRect.Bottom}");
        }
        var x = (int)((point.X - GetSystemMetrics(76)) * 65535.0 / (GetSystemMetrics(78) - 1));
        var y = (int)((point.Y - GetSystemMetrics(77)) * 65535.0 / (GetSystemMetrics(79) - 1));
        const uint moveAbsolute = 0x0001 | 0x8000 | 0x4000;
        NativeInput[] inputs =
        [new() { Mouse = new() { X = x, Y = y, Flags = moveAbsolute } },
         new() { Mouse = new() { X = x, Y = y, Flags = moveAbsolute | 0x0002 } },
         new() { Mouse = new() { X = x, Y = y, Flags = moveAbsolute | 0x0004 } }];
        if (SendInput(3, inputs, Marshal.SizeOf<NativeInput>()) != 3)
            throw new InvalidOperationException("Native click batch was not delivered");
        Pump();
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public NativeMouse Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMouse
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public nint Extra;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, System.Text.StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int pid);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);

    private static void SetGroup(PlayerViewModel vm, SyncPlayInfo? info)
        => typeof(PlayerViewModel).GetMethod("SetSyncPlay", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, [info]);
    private static void SetQueue(PlayerViewModel vm, IReadOnlyList<QueueRow> rows)
        => typeof(PlayerViewModel).GetMethod("SetSyncPlayQueue", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, [rows]);
    private static void SetPending(PlayerViewModel vm, bool value)
        => typeof(PlayerViewModel).GetProperty(nameof(PlayerViewModel.SyncPlayPending))!.SetValue(vm, value);
    private static void Require(bool condition, string description)
    {
        Check(condition, description);
        if (!condition) throw new InvalidOperationException(description);
    }
    private static void Check(bool condition, string description)
    {
        _assertions++;
        if (!condition) _failures++;
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: {description}");
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
    private static bool RightAligned(Popup popup, FrameworkElement trigger)
        => popup.Child is Visual visual && PresentationSource.FromVisual(visual) is HwndSource source
            && GetWindowRect(source.Handle, out var rect)
            && Math.Abs(rect.Right - trigger.PointToScreen(new Point(trigger.ActualWidth, 0)).X) <= 2;

    private static void CaptureScreen(Window host, Popup popup, string state)
    {
        var hostHwnd = new WindowInteropHelper(host).Handle;
        if (!host.IsVisible || !popup.IsOpen || popup.Child is not FrameworkElement { IsVisible: true } content
            || PresentationSource.FromVisual(content) is not HwndSource popupSource)
            throw new InvalidOperationException("Screen capture requires visible tested host and flyout");
        GetWindowThreadProcessId(hostHwnd, out var hostPid);
        GetWindowThreadProcessId(popupSource.Handle, out var popupPid);
        GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundPid);
        if (hostPid != Environment.ProcessId || popupPid != Environment.ProcessId || foregroundPid != Environment.ProcessId
            || !GetWindowRect(hostHwnd, out var hostRect) || !GetWindowRect(popupSource.Handle, out var popupRect))
            throw new InvalidOperationException("Screen capture windows or foreground are not owned by test process");
        var x = Math.Max(GetSystemMetrics(76), Math.Min(hostRect.Left, popupRect.Left) - 12);
        var y = Math.Max(GetSystemMetrics(77), Math.Min(hostRect.Top, popupRect.Top) - 12);
        var right = Math.Min(GetSystemMetrics(76) + GetSystemMetrics(78), Math.Max(hostRect.Right, popupRect.Right) + 12);
        var bottom = Math.Min(GetSystemMetrics(77) + GetSystemMetrics(79), Math.Max(hostRect.Bottom, popupRect.Bottom) + 12);
        using var bitmap = new System.Drawing.Bitmap(right - x, bottom - y);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(x, y, 0, 0, bitmap.Size, System.Drawing.CopyPixelOperation.SourceCopy);
        Directory.CreateDirectory(Output);
        var path = Path.Combine(Output, $"syncplay-ui-{state}.png");
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"CAPTURE: {path}");
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);

    private static void Capture(Visual visual, string state)
    {
        if (visual is not FrameworkElement element) throw new InvalidOperationException("Capture requires element");
        element.UpdateLayout();
        var image = new RenderTargetBitmap(Math.Max(1, (int)element.ActualWidth), Math.Max(1, (int)element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(Output);
        var path = Path.Combine(Output, $"syncplay-ui-{state}.png");
        using var output = File.Create(path);
        encoder.Save(output);
        Console.WriteLine($"CAPTURE: {path}");
    }
}
