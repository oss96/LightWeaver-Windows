using System.Windows.Input;

namespace LightWeaver.Player;

/// <summary>The WPF side of the chord map: chords from and to WPF's <see cref="Key"/> enum.</summary>
public static partial class PlayerActionDispatcher
{
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

    /// <summary>The map after binding <paramref name="chord"/> to <paramref name="action"/>: the
    /// chord moves here and any other action holding the same keystroke becomes unbound. The
    /// match is <see cref="ChordEquals"/>, not text, so a binding saved in an alias spelling
    /// ("Oem6", "Shift+Ctrl+X") is found too; a textual match left both actions on one key, and
    /// the dispatcher then fired whichever came first. Every other entry is returned unchanged.
    /// </summary>
    public static Dictionary<PlayerAction, string> Rebind(IDictionary<string, string>? saved,
        PlayerAction action, string chord)
    {
        var map = Resolve(saved);
        foreach (var (other, c) in map.ToList())
            if (other != action && ChordEquals(c, chord))
                map[other] = "";
        map[action] = chord;
        return map;
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
}
