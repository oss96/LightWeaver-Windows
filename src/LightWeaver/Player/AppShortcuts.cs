using LightWeaver.Localization;
using System.Windows.Input;
using LightWeaver.Settings;

namespace LightWeaver.Player;

/// <summary>Display grouping for the shortcuts overlay (Phase 10 M9).</summary>
public enum ShortcutGroup
{
    Playback,
    AudioSubtitles,
    General,
    Mouse,
}

/// <summary>
/// One app-chrome chord. These are NOT rebindable — <see cref="PlayerAction"/> covers that
/// population — but they are the ones that used to be chord literals inside eight lambdas in
/// <c>MainWindow</c>'s constructor with no table behind them, which is the drift the shortcuts
/// overlay had to close: the ctor now builds its <see cref="KeyBinding"/>s by iterating
/// <see cref="AppShortcuts.Chrome"/>, so a chord cannot appear in the panel and not in the app,
/// or the reverse.
/// </summary>
/// <param name="Id">Stable identity the window switches on to supply the handler. The handlers
/// stay in <c>MainWindow</c> because their state guards ("only while playing", "fullscreen else
/// search") read private window state.</param>
/// <param name="Registered">False for a chord the window handles itself rather than through an
/// <see cref="InputBinding"/>. Only <see cref="ShowShortcuts"/> is such a case, and its reason is
/// on that field.</param>
/// <param name="DuplicateOf">Set when a rebindable <see cref="PlayerAction"/> already lists this
/// same function, so the panel shows one row while the app keeps both registrations.</param>
public sealed record ChromeShortcut(
    string Id,
    Key Key,
    ModifierKeys Modifiers,
    string Function,
    ShortcutGroup Group,
    bool Registered = true,
    PlayerAction? DuplicateOf = null);

/// <summary>One row as the overlay renders it: a function and the keys that invoke it.</summary>
/// <param name="Bindable">True for the rebindable player actions — the ones Settings can change.
/// Display-only today; kept because "which of these can I change?" is the obvious next question a
/// reader of the panel has.</param>
public sealed record ShortcutRow(ShortcutGroup Group, string Function, string Keys, bool Bindable);

/// <summary>
/// The single source for every keyboard affordance the app has, and the thing the shortcuts
/// overlay renders (Phase 10 M9). Three populations feed it, and only one of them existed as a
/// table before this milestone:
///
/// 1. The rebindable <see cref="PlayerAction"/>s — already enumerable, already carrying display
///    names and a user-overlaid chord map (<see cref="PlayerActionDispatcher"/>). Settings builds
///    its rebinding rows from the identical call, so the panel follows a rebind for free.
/// 2. <see cref="Chrome"/> — the app-chrome chords, new here.
/// 3. <see cref="Unbound"/> — affordances with no chord at all (mouse buttons, the wheel, and
///    Enter on an armed Up Next). Declared literally, because there is nothing to derive them
///    from; a shortcut the user can press belongs in the panel whether or not a KeyBinding backs
///    it.
/// </summary>
public static class AppShortcuts
{
    public const string OpenFile = "OpenFile";
    public const string OpenSettings = "OpenSettings";
    public const string SwitchServer = "SwitchServer";
    public const string MiniPlayer = "MiniPlayer";
    public const string TogglePause = "TogglePause";
    public const string Fullscreen = "Fullscreen";
    public const string Escape = "Escape";
    public const string BrowseBack = "BrowseBack";
    public const string ZoomIn = "ZoomIn";
    public const string ZoomOut = "ZoomOut";
    public const string ZoomReset = "ZoomReset";

    /// <summary>
    /// The '?' that opens the panel. <see cref="ChromeShortcut.Registered"/> is false on purpose:
    /// a window <see cref="KeyBinding"/> fires even while the search pill holds focus and would
    /// eat the character, so <c>MainWindow</c> matches it on text input with a focus guard
    /// instead. Layout matters here — '?' is Shift+/ on a US keyboard and Shift+ß on a German one,
    /// which are different virtual keys — so the match is on the typed character and the
    /// <see cref="ChromeShortcut.Key"/> below is only what the panel prints.
    /// </summary>
    public static ChromeShortcut ShowShortcuts => new(
        "ShowShortcuts", Key.OemQuestion, ModifierKeys.Shift,
        L.Text("AppShortcuts.KeyboardShortcutsThisPanel.463485e7"), ShortcutGroup.General, Registered: false);

    /// <summary>The app-chrome chords, in panel order. <c>MainWindow</c>'s ctor registers every
    /// entry whose <see cref="ChromeShortcut.Registered"/> is true.</summary>
    public static IReadOnlyList<ChromeShortcut> Chrome =>
    [
        // Space is deliberately in two places: this guarded InputBinding, and
        // PlayerAction.TogglePause's default chord. Both registrations are real, so both stay —
        // DuplicateOf keeps the panel from printing "Play / pause" twice.
        new(TogglePause, Key.Space, ModifierKeys.None, L.Text("AppShortcuts.PlayPause.0592b946"),
            ShortcutGroup.Playback, DuplicateOf: PlayerAction.TogglePause),
        // F11 is the second dual registration, on the same terms as Space above: the chrome binding
        // stays unconditional, so rebinding ToggleFullscreen to (say) F gives the user F11 AND F,
        // and the panel then correctly prints both rows.
        //
        // Some OTHER action on F11 (or Space) would mean different things depending on which
        // window has focus: the chrome InputBinding wins in the main window, while the other
        // action resolves first in the overlay (HandleOverlayKey runs HandlePlayerKey before
        // retrying our own chords). The capture flow refuses it (OwnerOf), as it refuses every
        // chord this table registers.
        //
        // One population still meets it on the very first launch: a user whose settings.json
        // ALREADY put some other action on F11 before ToggleFullscreen existed. The backfill gives
        // them two actions on one chord, TryResolve's first match wins by enum order, and the panel
        // still prints "Toggle fullscreen  F11" — so for them the panel is wrong on day one. Settings
        // flags that row and the user rebinds it; nothing rewrites their file behind their back.
        new(Fullscreen, Key.F11, ModifierKeys.None, L.Text("AppShortcuts.ToggleFullscreen.b46f7946"), ShortcutGroup.General,
            DuplicateOf: PlayerAction.ToggleFullscreen),
        new(MiniPlayer, Key.M, ModifierKeys.Control, L.Text("AppShortcuts.ToggleMiniPlayer.c5a8790b"), ShortcutGroup.General),
        new(Escape, Key.Escape, ModifierKeys.None, L.Text("AppShortcuts.ClosePanelLeaveFullscreenOrClearSearch.6f167aee"),
            ShortcutGroup.General),
        new(BrowseBack, Key.Back, ModifierKeys.None, L.Text("AppShortcuts.Back.54e480b1"), ShortcutGroup.General),
        new(OpenFile, Key.O, ModifierKeys.Control, L.Text("AppShortcuts.OpenFile.a046add4"), ShortcutGroup.General),
        new(OpenSettings, Key.OemComma, ModifierKeys.Control, L.Text("AppShortcuts.Settings.d0fd4f98"), ShortcutGroup.General),
        new(SwitchServer, Key.U, ModifierKeys.Control, L.Text("AppShortcuts.SwitchServer.18623bcb"), ShortcutGroup.General),
        new(ZoomIn, Key.OemPlus, ModifierKeys.Control, L.Text("AppShortcuts.ZoomIn.4c1e7b2a"), ShortcutGroup.General),
        new(ZoomOut, Key.OemMinus, ModifierKeys.Control, L.Text("AppShortcuts.ZoomOut.9d3f60e5"), ShortcutGroup.General),
        new(ZoomReset, Key.D0, ModifierKeys.Control, L.Text("AppShortcuts.ResetZoom.e27a5c81"), ShortcutGroup.General),
        ShowShortcuts,
    ];

    /// <summary>
    /// Extra keys for a <see cref="Chrome"/> entry: registered as bindings under the same id, never
    /// printed, because the panel row already names the function. The numpad keys are their own
    /// virtual keys, and on a US layout '+' is Shift+'=', so Ctrl+'+' arrives with Shift held —
    /// without that alias the key printed on the keycap would not zoom in.
    /// </summary>
    public static IReadOnlyList<ChromeShortcut> ChromeAliases =>
    [
        new(ZoomIn, Key.Add, ModifierKeys.Control, "", ShortcutGroup.General),
        new(ZoomIn, Key.OemPlus, ModifierKeys.Control | ModifierKeys.Shift, "", ShortcutGroup.General),
        new(ZoomOut, Key.Subtract, ModifierKeys.Control, "", ShortcutGroup.General),
        new(ZoomReset, Key.NumPad0, ModifierKeys.Control, "", ShortcutGroup.General),
    ];

    /// <summary>True for any zoom chord, listed or alias. The overlay forwards these even while a
    /// panel or flyout owns the keyboard, because zooming is how a user gets to read it.</summary>
    public static bool IsZoomChord(Key key, ModifierKeys modifiers)
        => Chrome.Concat(ChromeAliases).Any(c => c.Id is ZoomIn or ZoomOut or ZoomReset
            && c.Key == key && c.Modifiers == modifiers);

    /// <summary>
    /// The app shortcut that already owns <paramref name="chord"/>, as the panel lists it (an
    /// alias answers with its listed entry), or null when <paramref name="action"/> may have it.
    /// In the main window an app shortcut's <see cref="KeyBinding"/> takes the key before any
    /// player binding sees it, and over the player the zoom chords do too, so a player action
    /// on one of these keys would do nothing there. Space and F11 stay open to the player action
    /// they duplicate (<see cref="ChromeShortcut.DuplicateOf"/>): both registrations do the same
    /// thing. Settings refuses a capture this answers for, and flags a saved binding it answers
    /// for, which only a hand-edited or pre-update settings.json can hold.
    /// </summary>
    public static ChromeShortcut? OwnerOf(string? chord, PlayerAction action)
    {
        if (string.IsNullOrWhiteSpace(chord))
            return null;
        var owner = Chrome.Concat(ChromeAliases).FirstOrDefault(c => c.Registered && c.DuplicateOf != action
            && PlayerActionDispatcher.ChordEquals(chord, PlayerActionDispatcher.ChordOf(c.Key, c.Modifiers)));
        return owner is null ? null : Chrome.First(c => c.Id == owner.Id);
    }

    /// <summary>Affordances with no chord: nothing to derive, so they are written out.</summary>
    private static IReadOnlyList<ShortcutRow> Unbound =>
    [
        new(ShortcutGroup.Playback, L.Text("AppShortcuts.PlayTheNextEpisodeNowWhileUp.fd98dec8"), L.Text("AppShortcuts.Enter.fae48132"), false),
        new(ShortcutGroup.Mouse, L.Text("AppShortcuts.BackForward.f6075622"), L.Text("AppShortcuts.Mouse4Mouse5.53098a35"), false),
        new(ShortcutGroup.Mouse, L.Text("AppShortcuts.VolumeOverThePlayer.3e1534cd"), L.Text("AppShortcuts.Wheel.a7db831a"), false),
        new(ShortcutGroup.Mouse, L.Text("AppShortcuts.ScrollARowSideways.065b160a"), L.Text("AppShortcuts.ShiftWheel.afa7860e"), false),
        new(ShortcutGroup.Mouse, L.Text("AppShortcuts.ZoomTheInterface.b85d3f17"), L.Text("AppShortcuts.CtrlWheel.6a0e9c42"), false),
    ];

    public static string GroupTitle(ShortcutGroup group) => group switch
    {
        ShortcutGroup.Playback => L.Text("AppShortcuts.Playback.f740617d"),
        ShortcutGroup.AudioSubtitles => L.Text("AppShortcuts.AudioSubtitles.8c9a85d9"),
        ShortcutGroup.General => L.Text("AppShortcuts.General.a42bcd89"),
        ShortcutGroup.Mouse => L.Text("AppShortcuts.Mouse.af8ea09d"),
        _ => group.ToString(),
    };

    /// <summary>Which group a rebindable action belongs under. Volume and the three track
    /// cyclers read as audio/subtitle work; everything else is transport.
    ///
    /// Fullscreen is the exception that has to be listed: it is a window operation, and its chrome
    /// twin has always been under General. Without the entry the panel's row would silently move
    /// from General to Playback the moment the rebindable copy is the one that renders — cosmetic,
    /// but only visible to a human looking at the panel.</summary>
    private static ShortcutGroup GroupOf(PlayerAction action) => action switch
    {
        PlayerAction.VolumeUp or PlayerAction.VolumeDown or PlayerAction.ToggleMute
            or PlayerAction.CycleAudio or PlayerAction.CycleSubtitle or PlayerAction.CycleVideo
            => ShortcutGroup.AudioSubtitles,
        PlayerAction.ToggleFullscreen or PlayerAction.OpenStream => ShortcutGroup.General,
        _ => ShortcutGroup.Playback,
    };

    /// <summary>The panel's whole content, grouped and in display order. Called on every open so a
    /// rebind made in Settings is reflected without a relaunch.</summary>
    public static IReadOnlyList<IGrouping<ShortcutGroup, ShortcutRow>> BuildRows(AppSettings settings)
    {
        var rows = new List<ShortcutRow>();

        var map = PlayerActionDispatcher.Resolve(settings.KeyBindings);
        foreach (var action in Enum.GetValues<PlayerAction>())
        {
            // An action the user has unbound is not a shortcut, so it is not listed. Settings is
            // where an unbound action is visible and re-bindable.
            if (string.IsNullOrWhiteSpace(map[action]))
                continue;
            rows.Add(new ShortcutRow(GroupOf(action), PlayerActionDispatcher.DisplayName(action),
                PlayerActionDispatcher.DisplayChord(map[action]), Bindable: true));
        }

        foreach (var chord in Chrome)
        {
            // Suppress the duplicate only while the two really are the same keystroke. Rebinding
            // TogglePause to K does NOT stop Space from pausing — the chrome InputBinding is
            // unconditional — so at that point they are two different shortcuts for one function
            // and the panel must show both, or it would be lying about Space.
            if (chord.DuplicateOf is { } dup
                && PlayerActionDispatcher.ChordEquals(map[dup],
                    PlayerActionDispatcher.ChordOf(chord.Key, chord.Modifiers)))
                continue;
            rows.Add(new ShortcutRow(chord.Group, chord.Function, DisplayChord(chord), Bindable: false));
        }

        rows.AddRange(Unbound);

        // Grouped in enum order, rows in insertion order within a group.
        return [.. rows.GroupBy(r => r.Group).OrderBy(g => g.Key)];
    }

    /// <summary>User-facing chord text for a chrome entry, through the same speller the player
    /// actions use so "Ctrl+OemComma" prints as "Ctrl+," in both populations.</summary>
    public static string DisplayChord(ChromeShortcut shortcut)
        => PlayerActionDispatcher.DisplayChord(
            PlayerActionDispatcher.ChordOf(shortcut.Key, shortcut.Modifiers));
}
