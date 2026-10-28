namespace LightWeaver.Player;

/// <summary>When a player control's tooltip may open: for keyboard focus, or after the pointer has
/// really moved since the controls last appeared. Controls that appear under a resting pointer (the
/// bar showing again after a grow, a pause from a remote or a shortcut) or take focus in code open
/// none.</summary>
public static class PlayerTooltips
{
    public static bool Opens(bool keyboardFocus, bool pointerMovedSinceShown) => keyboardFocus || pointerMovedSinceShown;

    /// <summary>Whether a pointer event at <paramref name="now"/> is a real move from
    /// <paramref name="last"/>, rather than one XAML raises for a resting pointer when the content
    /// under it changes. An unknown position counts as no move.</summary>
    public static bool Moved((int X, int Y)? last, (int X, int Y)? now) => now is not null && now != last;
}
