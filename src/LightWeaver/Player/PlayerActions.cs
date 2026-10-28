using LightWeaver.Localization;
using LightWeaver.ViewModels;

namespace LightWeaver.Player;

/// <summary>Every rebindable action (Phase 7 M16). Keys, overlay buttons, and
/// SMTC all funnel through <see cref="PlayerActionDispatcher"/> so behavior stays
/// single-sourced.</summary>
public enum PlayerAction
{
    TogglePause,
    SeekForward,
    SeekBack,
    SeekForwardLarge,
    SeekBackLarge,
    FrameStepForward,
    FrameStepBack,
    VolumeUp,
    VolumeDown,
    ToggleMute,
    CycleAudio,
    CycleSubtitle,
    CycleVideo,
    QueueNext,
    QueuePrev,
    NextChapter,
    PrevChapter,
    SkipSegment,
    SpeedUp,
    SpeedDown,
    ToggleFullscreen,
    OpenStream,
    SubtitleEarlier,
    SubtitleLater,
}

/// <summary>
/// Maps <see cref="PlayerAction"/>s onto the existing PlayerViewModel calls and resolves
/// pressed keys against the user's binding map. Bindings serialize as
/// "[Ctrl+][Alt+][Shift+]KeyName" (WPF <see cref="Key"/> enum names, e.g. "Space",
/// "OemPlus", "Ctrl+Up"); an empty value means unbound.
/// </summary>
public static partial class PlayerActionDispatcher
{
    /// <summary>The spec default map (M16 realignment): Space pause · Left/Right seek ·
    /// Ctrl+Left/Right large seek · . / , frame step · Up/Down volume · M mute · A audio ·
    /// S subtitle · V video · F11 fullscreen. Skip-segment moved to C (S now cycles subtitles);
    /// queue/chapter/speed keys keep their shipped keys. Ctrl+L opens Stream from URL while browsing.
    ///
    /// Two of these deliberately duplicate a chord <see cref="AppShortcuts.Chrome"/> also
    /// registers — Space and F11. Both registrations are real and both stay, so rebinding either
    /// action ADDS a key rather than moving one; see the <c>DuplicateOf</c> entries there.
    /// </summary>
    public static IReadOnlyDictionary<PlayerAction, string> Defaults { get; } = new Dictionary<PlayerAction, string>
    {
        [PlayerAction.TogglePause] = "Space",
        [PlayerAction.SeekForward] = "Right",
        [PlayerAction.SeekBack] = "Left",
        // The first shipped defaults that are a chord rather than a bare key: the large seek sits
        // on the same arrows as the plain one, so Ctrl is what distinguishes the two sizes.
        [PlayerAction.SeekForwardLarge] = "Ctrl+Right",
        [PlayerAction.SeekBackLarge] = "Ctrl+Left",
        // mpv's own frame-step keys, kept as-is so muscle memory transfers. Bare ',' cannot collide
        // with the Ctrl+',' that opens Settings: TryResolve matches the exact chord.
        [PlayerAction.FrameStepForward] = "OemPeriod",
        [PlayerAction.FrameStepBack] = "OemComma",
        [PlayerAction.VolumeUp] = "Up",
        [PlayerAction.VolumeDown] = "Down",
        [PlayerAction.ToggleMute] = "M",
        [PlayerAction.CycleAudio] = "A",
        [PlayerAction.CycleSubtitle] = "S",
        [PlayerAction.CycleVideo] = "V",
        [PlayerAction.QueueNext] = "N",
        [PlayerAction.QueuePrev] = "P",
        [PlayerAction.NextChapter] = "OemCloseBrackets",
        [PlayerAction.PrevChapter] = "OemOpenBrackets",
        [PlayerAction.SkipSegment] = "C",
        [PlayerAction.SpeedUp] = "OemPlus",
        [PlayerAction.SpeedDown] = "OemMinus",
        // Matches the chrome F11 that already existed, so nothing changes until the user rebinds it.
        [PlayerAction.ToggleFullscreen] = "F11",
        [PlayerAction.OpenStream] = "Ctrl+L",
        // mpv's own subtitle-delay keys; [ and ] already step chapters.
        [PlayerAction.SubtitleEarlier] = "Z",
        [PlayerAction.SubtitleLater] = "X",
    };

    /// <summary>
    /// Whether holding the key down should keep firing the action. True only for the continuous
    /// ones — seek and volume, where repeat is the point. Everything else is discrete: one press,
    /// one action. Without this distinction key auto-repeat drove them all, so holding N walked
    /// the queue by roughly thirty items a second, each one a full <c>PlayItem</c> → negotiation
    /// POST → <c>loadfile</c> (B9); holding a cycle key spun through every track.
    /// New actions are discrete unless deliberately added here.
    ///
    /// The frame-step pair is deliberately ASYMMETRIC, and it is the B9 failure class above that
    /// makes it so. Holding '.' to walk forward is standard mpv behaviour and each step is one
    /// cheap forward decode, so it repeats. <see cref="PlayerAction.FrameStepBack"/> does not:
    /// mpv implements <c>frame-back-step</c> as a hi-res seek backwards plus a re-decode, which on
    /// long-GOP content is both expensive and inexact, so auto-repeat would flood the command
    /// queue at the key-repeat rate and stall the player — the same shape of failure as holding N.
    /// </summary>
    public static bool RepeatsWhileHeld(PlayerAction action) => action is
        PlayerAction.SeekForward or PlayerAction.SeekBack
        or PlayerAction.SeekForwardLarge or PlayerAction.SeekBackLarge
        or PlayerAction.FrameStepForward
        or PlayerAction.VolumeUp or PlayerAction.VolumeDown;

    public static string DisplayName(PlayerAction action) => action switch
    {
        PlayerAction.TogglePause => L.Text("PlayerActions.PlayPause.07245d65"),
        PlayerAction.SeekForward => L.Text("PlayerActions.SeekForward.804babac"),
        PlayerAction.SeekBack => L.Text("PlayerActions.SeekBack.ab02e67d"),
        PlayerAction.SeekForwardLarge => L.Text("PlayerActions.SeekForwardLarge.a826683b"),
        PlayerAction.SeekBackLarge => L.Text("PlayerActions.SeekBackLarge.c9d9175f"),
        PlayerAction.FrameStepForward => L.Text("PlayerActions.NextFrame.6bec0a7b"),
        PlayerAction.FrameStepBack => L.Text("PlayerActions.PreviousFrame.c980e379"),
        PlayerAction.VolumeUp => L.Text("PlayerActions.VolumeUp.d9bb5687"),
        PlayerAction.VolumeDown => L.Text("PlayerActions.VolumeDown.d7df69cb"),
        PlayerAction.ToggleMute => L.Text("PlayerActions.Mute.4b40494c"),
        PlayerAction.CycleAudio => L.Text("PlayerActions.CycleAudioTrack.79382828"),
        PlayerAction.CycleSubtitle => L.Text("PlayerActions.CycleSubtitleTrack.19c7538b"),
        PlayerAction.CycleVideo => L.Text("PlayerActions.CycleVideoTrack.c207af5f"),
        PlayerAction.QueueNext => L.Text("PlayerActions.NextInQueue.7a013309"),
        PlayerAction.QueuePrev => L.Text("PlayerActions.PreviousInQueue.994ad41d"),
        PlayerAction.NextChapter => L.Text("PlayerActions.NextChapter.a0b24056"),
        PlayerAction.PrevChapter => L.Text("PlayerActions.PreviousChapter.3e967ea5"),
        PlayerAction.SkipSegment => L.Text("PlayerActions.SkipIntroCredits.6edab871"),
        PlayerAction.SpeedUp => L.Text("PlayerActions.SpeedUp.eb352570"),
        PlayerAction.SpeedDown => L.Text("PlayerActions.SpeedDown.dc493c6c"),
        // Word for word the chrome entry's Function, so the panel prints one row and not two
        // near-identical ones when both registrations are listed after a rebind.
        PlayerAction.ToggleFullscreen => L.Text("PlayerActions.ToggleFullscreen.ac72de7e"),
        PlayerAction.OpenStream => L.Text("PlayerActions.StreamFromUrlWhileBrowsing.933b9de0"),
        PlayerAction.SubtitleEarlier => L.Text("Player.SubtitlesEarlier"),
        PlayerAction.SubtitleLater => L.Text("Player.SubtitlesLater"),
        _ => action.ToString(),
    };

    /// <summary>User-facing spellings, applied per chord token. This is a token map and not a
    /// chain of substring replaces because the chrome shortcuts (P10 M9) brought <c>Back</c> into
    /// the table: replacing that as a substring would have turned <c>OemBackslash</c> into
    /// "OemBackspaceslash".</summary>
    private static Dictionary<string, string> ChordSpellings => new(StringComparer.OrdinalIgnoreCase)
    {
        ["OemOpenBrackets"] = "[",
        ["OemCloseBrackets"] = "]",
        ["OemPlus"] = "+",
        ["OemMinus"] = "-",
        ["OemComma"] = ",",
        ["OemPeriod"] = ".",
        ["OemQuestion"] = "?",
        ["D0"] = "0",
        ["Escape"] = L.Text("PlayerActions.Esc.abeb1d0a"),
        ["Back"] = L.Text("PlayerActions.Backspace.46280906"),
        ["Ctrl"] = L.Text("PlayerActions.Ctrl.4294439e"),
        ["Shift"] = L.Text("PlayerActions.Shift.0f1fc6ac"),
        ["Alt"] = L.Text("PlayerActions.Alt.9be36c95"),
        ["Right"] = L.Text("PlayerActions.Right.e9a2228f"),
        ["Left"] = L.Text("PlayerActions.Left.0795e838"),
        ["Up"] = L.Text("PlayerActions.Up.f09e69a7"),
        ["Down"] = L.Text("PlayerActions.Down.073d0e08"),
        ["Space"] = L.Text("PlayerActions.Space.42b61e2c"),
        ["Enter"] = L.Text("PlayerActions.Enter.e761ccbf"),
        ["Tab"] = L.Text("PlayerActions.Tab.aea77d7d"),
        ["Delete"] = L.Text("PlayerActions.Delete.d6cb7244"),
        ["Home"] = L.Text("PlayerActions.Home.47fcf378"),
        ["End"] = L.Text("PlayerActions.End.31f0df28"),
        ["PageUp"] = L.Text("PlayerActions.Pageup.8883ccb6"),
        ["PageDown"] = L.Text("PlayerActions.Pagedown.36cb25c7"),
        ["Insert"] = L.Text("PlayerActions.Insert.129db0a7"),
        ["Backspace"] = L.Text("PlayerActions.Backspace.46280906"),
        // Alias spellings, for chords saved before ChordOf canonicalized them.
        ["Oem4"] = "[",
        ["Oem6"] = "]",
    };

    /// <summary>User-facing chord text ("Ctrl+Right", "[", "+", "(unbound)").</summary>
    public static string DisplayChord(string? chord)
    {
        if (string.IsNullOrWhiteSpace(chord))
            return L.Text("PlayerActions.Unbound.7f895535");
        var parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("+", parts.Select(p =>
            ChordSpellings.TryGetValue(p.Trim(), out var spelled) ? spelled : p.Trim()));
    }

    /// <summary>The effective map: shipped defaults overlaid with the user's saved
    /// entries (unknown action names ignored; missing actions backfilled).</summary>
    public static Dictionary<PlayerAction, string> Resolve(IDictionary<string, string>? saved)
    {
        var map = new Dictionary<PlayerAction, string>(Defaults);
        if (saved is not null)
            foreach (var (name, chord) in saved)
                if (Enum.TryParse<PlayerAction>(name, out var action))
                    map[action] = chord;
        return map;
    }

    /// <summary>The same key under another name: WPF's <c>Key</c> aliases ("Oem6" for "]") and the
    /// numpad twins of + and -, so chords saved under either spelling compare equal.</summary>
    private static readonly Dictionary<string, string> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Oem1"] = "OemSemicolon",
        ["Oem2"] = "OemQuestion",
        ["Oem3"] = "OemTilde",
        ["Oem4"] = "OemOpenBrackets",
        ["Oem5"] = "OemPipe",
        ["Oem6"] = "OemCloseBrackets",
        ["Oem7"] = "OemQuotes",
        ["Oem102"] = "OemBackslash",
        ["Return"] = "Enter",
        ["Prior"] = "PageUp",
        ["Next"] = "PageDown",
        ["Capital"] = "CapsLock",
        ["Add"] = "OemPlus",
        ["Subtract"] = "OemMinus",
    };

    /// <summary>Chord equality by name, for hosts without WPF's <c>Key</c> enum: modifiers in any
    /// order, key aliases folded. Unbound never equals anything.</summary>
    public static bool SameChord(string? a, string? b)
    {
        static (bool, bool, bool, string)? Parse(string? chord)
        {
            if (string.IsNullOrWhiteSpace(chord)) return null;
            bool ctrl = false, alt = false, shift = false;
            var key = "";
            foreach (var raw in chord.Split('+', StringSplitOptions.RemoveEmptyEntries))
            {
                var part = raw.Trim();
                if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) ctrl = true;
                else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) alt = true;
                else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) shift = true;
                else key = KeyAliases.TryGetValue(part, out var canonical) ? canonical : part;
            }
            return key.Length == 0 ? null : (ctrl, alt, shift, key.ToUpperInvariant());
        }
        return Parse(a) is { } x && Parse(b) is { } y && x == y;
    }

    public static void Dispatch(PlayerAction action, PlayerViewModel vm)
    {
        switch (action)
        {
            case PlayerAction.TogglePause: vm.TogglePauseCommand.Execute(null); break;
            case PlayerAction.SeekForward: vm.JumpForward(); break;
            case PlayerAction.SeekBack: vm.JumpBack(); break;
            case PlayerAction.SeekForwardLarge: vm.JumpForwardLarge(); break;
            case PlayerAction.SeekBackLarge: vm.JumpBackLarge(); break;
            case PlayerAction.FrameStepForward: vm.FrameStepForward(); break;
            case PlayerAction.FrameStepBack: vm.FrameStepBack(); break;
            case PlayerAction.VolumeUp: vm.Volume = Math.Min(100, vm.Volume + 5); break;
            case PlayerAction.VolumeDown: vm.Volume = Math.Max(0, vm.Volume - 5); break;
            case PlayerAction.ToggleMute: vm.ToggleMute(); break;
            case PlayerAction.CycleAudio: vm.CycleAudioTrack(); break;
            case PlayerAction.CycleSubtitle: vm.CycleSubtitleTrack(); break;
            case PlayerAction.CycleVideo: vm.CycleVideoTrack(); break;
            // P10: N/P run the same merged next/previous as the OSD pair, so a key and a
            // button mean the same thing (queue entry, then armed Up Next, then series order).
            // The enum names stay QueueNext/QueuePrev because saved key mappings persist by name.
            case PlayerAction.QueueNext: vm.GoNext(); break;
            case PlayerAction.QueuePrev: vm.GoPrevious(); break;
            case PlayerAction.NextChapter: vm.NextChapter(); break;
            case PlayerAction.PrevChapter: vm.PreviousChapter(); break;
            case PlayerAction.SkipSegment: vm.SkipActiveSegment(); break;
            case PlayerAction.SpeedUp: vm.IncreaseSpeed(); break;
            case PlayerAction.SpeedDown: vm.DecreaseSpeed(); break;
            // Fullscreen is a WINDOW operation and this method only has the view-model, so the
            // request is raised as an event and MainWindow does the work. That is the codebase's
            // established bridge for exactly this (QueuePlayRequested, EpisodeStepRequested,
            // SubtitleBasePositionChanged) and it keeps every caller of Dispatch — keys today,
            // SMTC or an OSD button tomorrow — working without its own special case.
            case PlayerAction.ToggleFullscreen: vm.RequestFullscreen(); break;
            case PlayerAction.OpenStream: vm.RequestOpenStream(); break;
            case PlayerAction.SubtitleEarlier: vm.AdjustSubtitleDelay(-0.1); break;
            case PlayerAction.SubtitleLater: vm.AdjustSubtitleDelay(0.1); break;
            // Every member of the enum is mapped above, so this arm is unreachable today — and
            // that is exactly why it is here. A new PlayerAction that reaches a binding before it
            // reaches this switch is dispatched, does nothing, and looks identical in the log to a
            // key that was never pressed: the [shortcut] event=invoke record is written by the
            // caller before the action is dispatched, so without this the intent would be recorded
            // and the silence would not.
            default:
                if (Diagnostics.AppLog.Verbose)
                    Diagnostics.AppLog.Detail("player",
                        $"event=action action={ActionToken(action)} outcome=noop reason=unmapped_action");
                break;
        }
    }

    /// <summary>`SeekForward` → `seek_forward`. The log vocabulary is lower_snake everywhere, and
    /// an enum name formatted straight into a record would be the one value a grep for
    /// `action=seek_forward` silently misses.</summary>
    private static string ActionToken(PlayerAction action)
    {
        var name = action.ToString();
        var token = new System.Text.StringBuilder(name.Length + 4);
        foreach (var c in name)
        {
            if (char.IsUpper(c) && token.Length > 0)
                token.Append('_');
            token.Append(char.ToLowerInvariant(c));
        }
        return token.Length == 0 ? "unknown" : token.ToString();
    }
}
