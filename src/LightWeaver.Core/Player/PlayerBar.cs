namespace LightWeaver.Player;

/// <summary>How the player bar's button row fits (design 13): one row, the transport group at the
/// start and the track, quality, speed, info and full-screen group at the end.</summary>
public static class PlayerBar
{
    /// <summary>Whether Audio and Subtitles fold into one Tracks button (design 13c): when the row,
    /// with both of them, is wider than <paramref name="available"/>. <paramref name="oneRow"/> is
    /// the row's width as it is now, folded or not; folding saves <paramref name="saved"/>, so a
    /// folded row is measured as if unfolded and the choice does not flip back and forth.</summary>
    public static bool FoldsTracks(double available, double oneRow, bool folded, double saved)
        => !double.IsInfinity(available) && (folded ? oneRow + saved : oneRow) > available;
}
