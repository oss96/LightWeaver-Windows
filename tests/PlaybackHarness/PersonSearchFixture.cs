using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LightWeaver.Jellyfin;
using LightWeaver.ViewModels;
using LightWeaver.Views;

internal static class PersonSearchFixture
{
    private static readonly Guid PersonId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid MovieId = Guid.Parse("22222222-3333-4444-5555-666666666666");
    private static readonly Guid UserId = Guid.Parse("33333333-4444-5555-6666-777777777777");
    private const string PersonName = "Christopher Nolan";
    private const string MovieName = "Nolan fixture movie";
    private static int _checks;
    private static int _failures;
    private static readonly object TraceLock = new();

    public static int RunUi()
    {
        if (!File.Exists(@"C:\ci\verify-runs\CURRENT"))
            throw new InvalidOperationException("Person search UI requires the approved Windows verification guest.");
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "lightweaver-tests"));
        File.WriteAllText(TracePath, "Synthetic person search fixture trace\n");
        var application = UiFixtureApplication.Create();
        using var server = new Server();
        var model = new AppViewModel();
        Window? window = null;
        try
        {
            var connection = model.Jellyfin.ReconnectAsync(new SavedCredentials(server.Origin,
                "SYNTHETIC", "synthetic-person-fixture", UserId));
            Wait(() => connection.IsCompleted, "synthetic session", 5000);
            Check(connection.GetAwaiter().GetResult(), "synthetic session connects without saved credentials");

            // A direct request is the positive control: the fixture has matching media and the
            // production service can retrieve it before any picker interaction is involved.
            var control = model.Jellyfin.AdvancedSearchAsync(new AdvancedSearchQuery
            {
                PersonIds = [PersonId],
                ItemTypes = [Jellyfin.Sdk.Generated.Models.BaseItemKind.Movie],
            }, 0, 24);
            Wait(() => control.IsCompleted, "positive server control", 5000);
            Check(control.GetAwaiter().GetResult().Items.Any(item => item.Id == MovieId),
                "production service returns a known movie for the synthetic PersonIds");

            window = new Window
            {
                Title = "LightWeaver person search fixture", Width = 1000, Height = 650,
                Background = (Brush)application.Resources["LwStorm1Brush"],
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            window.Show();
            window.Activate();
            foreach (var input in new[] { "mouse", "keyboard", "uia", "highlighted-mouse" })
                RunSelection(window, model, server, input);
        }
        catch (Exception ex)
        {
            Check(false, $"fixture completes: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            window?.Close();
            model.Jellyfin.Dispose();
            application.Shutdown();
        }
        Console.WriteLine($"RESULT: {(_failures == 0 ? "PASS" : "FAIL")} ({_checks} person search checks, {_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    private static void RunSelection(Window window, AppViewModel model, Server server, string input)
    {
        var view = new AdvancedSearchView(model);
        window.Content = view;
        window.Activate();
        var search = Element<TextBox>(view, "SearchBox");
        var person = Element<TextBox>(view, "PersonBox");
        var suggestions = Element<ListBox>(view, "PersonSuggestList");
        var popup = Element<Popup>(view, "PersonPopup");
        var results = Element<ListBox>(view, "ItemsGrid");
        var chip = Element<TextBlock>(view, "SeedChipText");
        var seedFacet = Element<StackPanel>(view, "SeedFacet");
        Wait(() => view.IsLoaded && Field<bool>(view, "_genresLoaded")
            && Field<int>(view, "_loadGeneration") > 0 && !Field<bool>(view, "_queryRunning")
            && search.IsKeyboardFocused, input + " initial prompt settled", 5000);
        Check(seedFacet.Visibility != Visibility.Visible && results.Items.Count == 0
            && Field<List<Guid>>(view, "_personIds").Count == 0,
            input + ": fresh view begins unfiltered with no person chip or results");
        Trace($"{input} initial settled generation={Field<int>(view, "_loadGeneration")} gridItems={results.Items.Count}");
        Element<ToggleButton>(view, "MoreFiltersButton").IsChecked = true;
        window.UpdateLayout();
        person.Focus();
        person.Text = PersonName;
        Wait(() => popup.IsOpen && suggestions.Items.Count == 1, input + " suggestions", 5000);
        Check(search.Text.Length == 0, input + ": main Search remains empty");
        Check(suggestions.Items[0] is PersonEntry { Name: PersonName }, input + ": typed name produces the expected suggestion");
        suggestions.UpdateLayout();
        var row = suggestions.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
            ?? throw new InvalidOperationException("Expected a realized person suggestion row.");
        person.LostKeyboardFocus += (_, args) => Trace(
            $"{input} person focus lost to {FocusPath(args.NewFocus)}; popup={popup.IsOpen}; suggestions={suggestions.Items.Count}");
        suggestions.SelectionChanged += (_, args) => Trace(
            $"{input} selection added={args.AddedItems.Count}; removed={args.RemovedItems.Count}; popup={popup.IsOpen}; generation={Field<int>(view, "_loadGeneration")}");
        Capture(window, input + "-suggestions");
        var previousRequests = server.ItemQueries.Count;
        switch (input)
        {
            case "mouse":
                ClickPopupRow(window, popup, row);
                break;
            case "highlighted-mouse":
                Key(window, person, 0x28);
                Pump(150);
                Check(suggestions.SelectedIndex == 0 && row.IsSelected && seedFacet.Visibility != Visibility.Visible
                    && server.ItemQueries.Count == previousRequests,
                    "highlighted-mouse: ArrowDown highlights the same row without applying the person");
                ClickPopupRow(window, popup, row);
                break;
            case "keyboard":
                Key(window, person, 0x28); // ArrowDown: highlights without committing.
                Pump(150);
                Check(seedFacet.Visibility != Visibility.Visible && server.ItemQueries.Count == previousRequests,
                    "keyboard: ArrowDown only highlights the person");
                Key(window, person, 0x0d);
                break;
            case "uia":
                // The realized container has a wrapper peer; SelectionItem belongs to
                // the data item's peer under the selector, as external UIA sees it.
                var listPeer = UIElementAutomationPeer.CreatePeerForElement(suggestions) as ListBoxAutomationPeer
                    ?? new ListBoxAutomationPeer(suggestions);
                var peer = listPeer.GetChildren()?.SingleOrDefault(child => child.GetName() == PersonName)
                    ?? new ListBoxItemAutomationPeer(suggestions.Items[0], listPeer);
                var selection = peer.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider
                    ?? throw new InvalidOperationException("Suggestion data peer has no SelectionItem provider.");
                // Native input arrives through the dispatcher with its synchronization
                // context. Give this UIA provider the same context for async view loading.
                window.Dispatcher.Invoke(selection.Select);
                break;
        }
        // Mouse failures still allow the keyboard/UIA controls to run. Their differential
        // results distinguish an input focus race from a backend/query failure.
        var committed = Wait(() => seedFacet.Visibility == Visibility.Visible
            && chip.Text.Contains(PersonName, StringComparison.Ordinal), input + " person chip", 2000, false);
        Check(committed, input + ": selecting the suggestion applies the named person chip");
        var loaded = Wait(() => !Field<bool>(view, "_queryRunning") && results.IsVisible
            && results.Items.Cast<MediaItem>().Any(item => item.Id == MovieId), input + " matching movie", 5000, false);
        var queries = server.ItemQueries.ToArray().Skip(previousRequests).ToArray();
        Check(queries.Length > 0 && queries.All(query => Guid.TryParse(query.PersonIds, out var id) && id == PersonId
            && string.IsNullOrEmpty(query.SearchTerm)), input + ": actual requests contain PersonIds and no SearchTerm");
        Check(loaded && results.IsVisible && results.Items.Cast<MediaItem>().Any(item => item.Id == MovieId),
            input + ": matching movie appears in the production result grid");
        Check(committed && !popup.IsOpen && person.Text.Length == 0,
            input + ": successful selection closes and clears the transient picker");
        Trace($"{input} completed committed={committed} loaded={loaded} requests={queries.Length} "
            + $"gridItems={results.Items.Count} gridVisible={results.IsVisible} generation={Field<int>(view, "_loadGeneration")} "
            + $"queryRunning={Field<bool>(view, "_queryRunning")} focus={FocusPath(Keyboard.FocusedElement)}");
        Capture(window, input + "-results");
        popup.IsOpen = false;
        if (input == "keyboard" && committed && loaded)
        {
            // Closing a new lookup must preserve the already-applied person filter.
            person.Focus();
            person.Text = PersonName;
            Wait(() => popup.IsOpen && suggestions.Items.Count == 1, "Escape suggestions", 5000);
            Key(window, person, 0x1b);
            Check(!popup.IsOpen && suggestions.Items.Count == 0 && person.IsKeyboardFocused,
                "keyboard: Escape closes suggestions and keeps the typing focus");
            Check(seedFacet.Visibility == Visibility.Visible && chip.Text.Contains(PersonName, StringComparison.Ordinal)
                && results.Items.Cast<MediaItem>().Any(item => item.Id == MovieId),
                "keyboard: Escape preserves the applied person and its results");
            person.Text = "";
            person.Text = PersonName;
            Wait(() => popup.IsOpen && suggestions.Items.Count == 1, "outside-focus suggestions", 5000);
            ClickElement(window, search, new WindowInteropHelper(window).Handle);
            Wait(() => !popup.IsOpen && suggestions.Items.Count == 0, "outside-focus dismissal", 2000);
            Check(search.IsKeyboardFocused && !popup.IsOpen && suggestions.Items.Count == 0,
                "mouse: clicking the main Search field dismisses person suggestions");
            Check(seedFacet.Visibility == Visibility.Visible && chip.Text.Contains(PersonName, StringComparison.Ordinal)
                && search.Text.Length == 0 && results.Items.Cast<MediaItem>().Any(item => item.Id == MovieId),
                "mouse: outside-focus dismissal preserves the applied person with empty Search");
        }
    }

    private static T Element<T>(AdvancedSearchView view, string name) where T : FrameworkElement
        => view.FindName(name) as T ?? throw new InvalidOperationException($"Missing {name}.");

    private static T Field<T>(AdvancedSearchView view, string name)
        => (T)(typeof(AdvancedSearchView).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(view)
            ?? throw new InvalidOperationException($"Missing field {name}."));

    private static string FocusPath(IInputElement? focus)
    {
        var entries = new List<string>();
        for (var node = focus as DependencyObject; node is not null && entries.Count < 5;
             node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            entries.Add(node.GetType().Name + (node is FrameworkElement { Name.Length: > 0 } element ? $"({element.Name})" : ""));
        return entries.Count == 0 ? "null" : string.Join(" <- ", entries);
    }

    private static string TracePath => Path.Combine(Path.GetTempPath(), "lightweaver-tests", "person-search-trace.log");

    private static void Trace(string message)
    {
        var line = $"WARN: person-search {message}";
        lock (TraceLock)
        {
            File.AppendAllText(TracePath, line + Environment.NewLine);
            Console.WriteLine(line);
        }
    }

    private static bool Wait(Func<bool> predicate, string name, int milliseconds, bool required = true)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (!predicate() && Environment.TickCount64 < deadline) Pump(25);
        if (predicate()) return true;
        if (required) throw new InvalidOperationException($"Timed out waiting for {name}.");
        return false;
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Capture(Window window, string state)
    {
        window.UpdateLayout();
        Pump(200);
        var handle = new WindowInteropHelper(window).Handle;
        AssertForeground(window);
        if (!GetWindowRect(handle, out var rect)) throw new InvalidOperationException("Cannot capture the tested window.");
        using var bitmap = new System.Drawing.Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size, System.Drawing.CopyPixelOperation.SourceCopy);
        var directory = Path.Combine(Path.GetTempPath(), "lightweaver-tests");
        Directory.CreateDirectory(directory);
        bitmap.Save(Path.Combine(directory, $"person-search-{state}.png"), System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void AssertForeground(Window window)
    {
        GetWindowThreadProcessId(new WindowInteropHelper(window).Handle, out var owner);
        GetWindowThreadProcessId(GetForegroundWindow(), out var foreground);
        if (owner != Environment.ProcessId || foreground != owner)
            throw new InvalidOperationException("Input/capture requires the tested process in the foreground.");
    }

    private static void ClickPopupRow(Window window, Popup popup, ListBoxItem row)
    {
        var popupHandle = popup.Child is Visual visual && PresentationSource.FromVisual(visual) is HwndSource source
            ? source.Handle : nint.Zero;
        ClickElement(window, row, popupHandle);
    }

    private static void ClickElement(Window window, FrameworkElement element, nint expectedHandle)
    {
        AssertForeground(window);
        var point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        var target = GetAncestor(WindowFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y }), 2);
        GetWindowThreadProcessId(target, out var owner);
        if (expectedHandle == nint.Zero || target != expectedHandle || owner != Environment.ProcessId)
            throw new InvalidOperationException("Native click target is not the fixture's expected window.");
        var x = (int)((point.X - GetSystemMetrics(76)) * 65535.0 / (GetSystemMetrics(78) - 1));
        var y = (int)((point.Y - GetSystemMetrics(77)) * 65535.0 / (GetSystemMetrics(79) - 1));
        const uint moveAbsolute = 0x0001 | 0x8000 | 0x4000;
        NativeInput[] inputs =
        [new() { Data = new() { Mouse = new() { X = x, Y = y, Flags = moveAbsolute } } },
         new() { Data = new() { Mouse = new() { X = x, Y = y, Flags = moveAbsolute | 0x0002 } } },
         new() { Data = new() { Mouse = new() { X = x, Y = y, Flags = moveAbsolute | 0x0004 } } }];
        Send(inputs);
        Pump(200);
    }

    private static void Key(Window window, TextBox field, ushort virtualKey)
    {
        AssertForeground(window);
        if (!field.IsKeyboardFocused) throw new InvalidOperationException("Native keys require focus in the tested Person box.");
        Send([new() { Type = 1, Data = new() { Keyboard = new() { VirtualKey = virtualKey } } },
              new() { Type = 1, Data = new() { Keyboard = new() { VirtualKey = virtualKey, Flags = 2 } } }]);
        Pump(100);
    }

    private static void Send(NativeInput[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) != inputs.Length)
            throw new InvalidOperationException("Native input was not delivered.");
    }

    private static void Check(bool condition, string name)
    {
        _checks++;
        if (!condition) _failures++;
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: {name}");
    }

    private sealed record ItemQuery(string? PersonIds, string? SearchTerm);

    private sealed class Server : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        internal ConcurrentQueue<ItemQuery> ItemQueries { get; } = new();
        internal string Origin { get; }

        internal Server()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            Origin = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Origin + "/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync().WaitAsync(_stop.Token); }
                    catch (Exception) when (_stop.IsCancellationRequested) { break; }
                    try { await Reply(context); }
                    catch (Exception) when (_stop.IsCancellationRequested) { }
                    finally { context.Response.Abort(); }
                }
            });
        }

        private async Task Reply(HttpListenerContext context)
        {
            var path = context.Request.Url!.AbsolutePath;
            object body = new { Items = Array.Empty<object>(), TotalRecordCount = 0 };
            if (path.Equals("/Users/Me", StringComparison.OrdinalIgnoreCase))
                body = new { Id = UserId, Name = "Synthetic fixture user" };
            else if (path.Equals("/Persons", StringComparison.OrdinalIgnoreCase))
                body = new { Items = new[] { new { Id = PersonId, Name = PersonName, Type = "Person" } }, TotalRecordCount = 1 };
            else if (path.Equals("/Items", StringComparison.OrdinalIgnoreCase))
            {
                var query = context.Request.QueryString;
                ItemQueries.Enqueue(new(query["PersonIds"], query["SearchTerm"]));
                Trace($"server Items PersonIds={query["PersonIds"] ?? "none"} SearchTermLength={query["SearchTerm"]?.Length ?? 0} "
                    + $"types={query["IncludeItemTypes"] ?? "none"}");
                if (Guid.TryParse(query["PersonIds"], out var person) && person == PersonId
                    && string.IsNullOrEmpty(query["SearchTerm"])
                    && query["IncludeItemTypes"]?.Contains("Movie", StringComparison.OrdinalIgnoreCase) == true)
                    body = new { Items = new[] { new { Id = MovieId, Name = MovieName, Type = "Movie", ProductionYear = 2026 } }, TotalRecordCount = 1 };
            }
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, _stop.Token);
            context.Response.Close();
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            _loop.GetAwaiter().GetResult();
            _stop.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] public NativeMouse Mouse;
        [FieldOffset(0)] public NativeKeyboard Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMouse
    { public int X, Y; public uint Data, Flags, Time; public nint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeKeyboard
    { public ushort VirtualKey, ScanCode; public uint Flags, Time; public nint Extra; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint handle, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
}
