using System.Windows.Input;
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
}

/// <summary>
/// Maps <see cref="PlayerAction"/>s onto the existing PlayerViewModel calls and resolves
/// pressed keys against the user's binding map. Bindings serialize as
/// "[Ctrl+][Alt+][Shift+]KeyName" (WPF <see cref="Key"/> enum names, e.g. "Space",
/// "OemPlus", "Ctrl+Up"); an empty value means unbound.
/// </summary>
public static class PlayerActionDispatcher
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
    };

    /// <summary>
    /// Whether holding the key down should keep firing the action. True only for the continuous
    /// ones — seek and volume, where repeat is the point. Everything else is discrete: one press,
    /// one action. Without this distinction key auto-repeat drove them all, so holding N walked
    /// the queue by roughly thirty items a second, each one a full <c>PlayItem</c> → negotiation
    /// POST → <c>loadfile</c> (BUGS.md B9); holding a cycle key spun through every track.
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
        PlayerAction.TogglePause => "Play / pause",
        PlayerAction.SeekForward => "Seek forward",
        PlayerAction.SeekBack => "Seek back",
        PlayerAction.SeekForwardLarge => "Seek forward (large)",
        PlayerAction.SeekBackLarge => "Seek back (large)",
        PlayerAction.FrameStepForward => "Next frame",
        PlayerAction.FrameStepBack => "Previous frame",
        PlayerAction.VolumeUp => "Volume up",
        PlayerAction.VolumeDown => "Volume down",
        PlayerAction.ToggleMute => "Mute",
        PlayerAction.CycleAudio => "Cycle audio track",
        PlayerAction.CycleSubtitle => "Cycle subtitle track",
        PlayerAction.CycleVideo => "Cycle video track",
        PlayerAction.QueueNext => "Next in queue",
        PlayerAction.QueuePrev => "Previous in queue",
        PlayerAction.NextChapter => "Next chapter",
        PlayerAction.PrevChapter => "Previous chapter",
        PlayerAction.SkipSegment => "Skip intro/credits",
        PlayerAction.SpeedUp => "Speed up",
        PlayerAction.SpeedDown => "Speed down",
        // Word for word the chrome entry's Function, so the panel prints one row and not two
        // near-identical ones when both registrations are listed after a rebind.
        PlayerAction.ToggleFullscreen => "Toggle fullscreen",
        PlayerAction.OpenStream => "Stream from URL (while browsing)",
        _ => action.ToString(),
    };

    /// <summary>User-facing spellings, applied per chord token. This is a token map and not a
    /// chain of substring replaces because the chrome shortcuts (P10 M9) brought <c>Back</c> into
    /// the table: replacing that as a substring would have turned <c>OemBackslash</c> into
    /// "OemBackspaceslash".</summary>
    private static readonly Dictionary<string, string> ChordSpellings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OemOpenBrackets"] = "[",
        ["OemCloseBrackets"] = "]",
        ["OemPlus"] = "+",
        ["OemMinus"] = "-",
        ["OemComma"] = ",",
        ["OemPeriod"] = ".",
        ["OemQuestion"] = "?",
        ["Escape"] = "Esc",
        ["Back"] = "Backspace",
        // Alias spellings, for chords saved before ChordOf canonicalized them.
        ["Oem4"] = "[",
        ["Oem6"] = "]",
    };

    /// <summary>User-facing chord text ("Ctrl+Right", "[", "+", "(unbound)").</summary>
    public static string DisplayChord(string? chord)
    {
        if (string.IsNullOrWhiteSpace(chord))
            return "(unbound)";
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

    /// <summary>
    /// WPF's <see cref="Key"/> enum has aliased members, and <c>ToString()</c> returns whichever
    /// name was declared first for a value. That is not symmetric across the pairs we use:
    /// <c>Key.OemOpenBrackets</c> (149) stringifies as "OemOpenBrackets", but
    /// <c>Key.OemCloseBrackets</c> (151) stringifies as **"Oem6"**.
    ///
    /// It silently broke NextChapter: the default is spelled "OemCloseBrackets", a real ']'
    /// press produced the chord "Oem6", and the string comparison in <see cref="TryResolve"/>
    /// never matched — so ']' did nothing while '[' worked. Found by the Phase 9 M1
    /// every-shortcut sweep, which is exactly the kind of thing it was written to catch.
    ///
    /// Canonicalizing here keeps newly captured chords readable in settings.json and matching
    /// the shipped defaults; <see cref="ChordEquals"/> additionally compares by parsed Key so
    /// chords already saved in the alias spelling keep working.
    /// </summary>
    private static readonly Dictionary<Key, string> CanonicalKeyNames = new()
    {
        [Key.OemOpenBrackets] = "OemOpenBrackets",
        [Key.OemCloseBrackets] = "OemCloseBrackets",
        [Key.OemPipe] = "OemPipe",
        [Key.OemSemicolon] = "OemSemicolon",
        [Key.OemQuestion] = "OemQuestion",
        [Key.OemTilde] = "OemTilde",
        [Key.OemQuotes] = "OemQuotes",
        [Key.OemBackslash] = "OemBackslash",
    };

    public static string ChordOf(Key key, ModifierKeys modifiers)
    {
        // Numpad aliases fold onto their main-row twins so "+"/"-" keep working.
        if (key == Key.Add) key = Key.OemPlus;
        if (key == Key.Subtract) key = Key.OemMinus;
        var s = CanonicalKeyNames.TryGetValue(key, out var canonical) ? canonical : key.ToString();
        if (modifiers.HasFlag(ModifierKeys.Shift)) s = "Shift+" + s;
        if (modifiers.HasFlag(ModifierKeys.Alt)) s = "Alt+" + s;
        if (modifiers.HasFlag(ModifierKeys.Control)) s = "Ctrl+" + s;
        return s;
    }

    /// <summary>Splits a serialized chord ("Ctrl+Shift+OemPlus") into its key and modifiers.
    /// Returns false for unbound/unparseable values.</summary>
    public static bool TryParseChord(string? chord, out Key key, out ModifierKeys modifiers)
    {
        key = Key.None;
        modifiers = ModifierKeys.None;
        if (string.IsNullOrWhiteSpace(chord))
            return false;

        foreach (var raw in chord.Split('+'))
        {
            var part = raw.Trim();
            if (part.Length == 0)
                continue;
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase))
                modifiers |= ModifierKeys.Control;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                modifiers |= ModifierKeys.Alt;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                modifiers |= ModifierKeys.Shift;
            else if (Enum.TryParse<Key>(part, ignoreCase: true, out var parsed))
                key = parsed;
            else
                return false;
        }
        return key != Key.None;
    }

    /// <summary>Chord equality that survives the Key-enum alias spellings ("Oem6" ==
    /// "OemCloseBrackets"). Falls back to the literal comparison so anything unparseable
    /// still behaves as before.</summary>
    public static bool ChordEquals(string? a, string? b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;
        return TryParseChord(a, out var keyA, out var modsA)
            && TryParseChord(b, out var keyB, out var modsB)
            && keyA == keyB && modsA == modsB;
    }

    public static bool TryResolve(Key key, ModifierKeys modifiers,
        IDictionary<string, string>? saved, out PlayerAction action)
    {
        var chord = ChordOf(key, modifiers);
        foreach (var (a, c) in Resolve(saved))
            if (ChordEquals(c, chord))
            {
                action = a;
                return true;
            }
        action = default;
        return false;
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
