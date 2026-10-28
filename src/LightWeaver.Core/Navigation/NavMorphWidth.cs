namespace LightWeaver.Navigation;

/// <summary>How wide the Home | Libraries | Playlists group opens into the nav morph (design 01).</summary>
public static class NavMorphWidth
{
    /// <summary>Room for <paramref name="items"/> items of <paramref name="itemWidth"/> plus the
    /// group's <paramref name="chrome"/>, at most <paramref name="room"/> (the group's width plus what
    /// the search field can give up), and never narrower than the <paramref name="closed"/> group as
    /// measured, whose labels may be wider than the morph's items.</summary>
    public static double Open(int items, double itemWidth, double chrome, double room, double closed)
        => Math.Max(closed, Math.Min(items * itemWidth + chrome, room));
}
