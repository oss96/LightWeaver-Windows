using Windows.System;

namespace LightWeaver.WinUI.Player;

/// <summary>Chords in the shared settings spelling ("Ctrl+Right", "OemPeriod"): the names of WPF's
/// key enum, so a binding saved by either app means the same key in the other.</summary>
internal static class KeyChords
{
    public static bool IsZoomChord(string? chord) => LightWeaver.Theme.UiZoom.IsZoomChord(chord);

    private static readonly Dictionary<int, string> Oem = new()
    {
        [0xBA] = "OemSemicolon",
        [0xBB] = "OemPlus",
        [0xBC] = "OemComma",
        [0xBD] = "OemMinus",
        [0xBE] = "OemPeriod",
        [0xBF] = "OemQuestion",
        [0xC0] = "OemTilde",
        [0xDB] = "OemOpenBrackets",
        [0xDC] = "OemPipe",
        [0xDD] = "OemCloseBrackets",
        [0xDE] = "OemQuotes",
        [0xE2] = "OemBackslash",
    };

    /// <summary>The chord for a key press; null for a modifier on its own or a key with no name.</summary>
    public static string? Of(VirtualKey key, bool ctrl, bool alt, bool shift)
    {
        if (Name(key) is not { } name) return null;
        if (shift) name = "Shift+" + name;
        if (alt) name = "Alt+" + name;
        if (ctrl) name = "Ctrl+" + name;
        return name;
    }

    private static string? Name(VirtualKey key)
    {
        var code = (int)key;
        if (key is >= VirtualKey.A and <= VirtualKey.Z) return key.ToString();
        if (key is >= VirtualKey.Number0 and <= VirtualKey.Number9) return "D" + (code - (int)VirtualKey.Number0);
        if (key is >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9) return "NumPad" + (code - (int)VirtualKey.NumberPad0);
        if (key is >= VirtualKey.F1 and <= VirtualKey.F24) return key.ToString();
        if (Oem.TryGetValue(code, out var oem)) return oem;
        return key switch
        {
            VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down => key.ToString(),
            VirtualKey.Space or VirtualKey.Tab or VirtualKey.Home or VirtualKey.End or VirtualKey.Insert or VirtualKey.Delete => key.ToString(),
            VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Escape or VirtualKey.Enter => key.ToString(),
            VirtualKey.Back => "Back",
            VirtualKey.Add => "OemPlus",
            VirtualKey.Subtract => "OemMinus",
            VirtualKey.Multiply => "Multiply",
            VirtualKey.Divide => "Divide",
            VirtualKey.Decimal => "Decimal",
            _ => null,
        };
    }
}
