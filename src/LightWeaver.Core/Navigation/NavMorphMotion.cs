using LightWeaver.Player;

namespace LightWeaver.Navigation;

/// <summary>A horizontal stretch of the top bar, in DIPs from the bar's leading edge.</summary>
public readonly record struct BarSpan(double Left, double Width)
{
    public double Right => Left + Width;
}

/// <summary>The top bar's nav morph (design 01): the Home | Libraries | Playlists group changes width
/// over 240 ms, cubic ease-out, and the search field in the star column beside it gives up or takes
/// back the room.</summary>
public static class NavMorphMotion
{
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(240);

    /// <summary>Keyframes per morph: one every 6 ms or so of the 240 ms.</summary>
    public const int Samples = 40;

    /// <summary>The group's width at <paramref name="t"/> (0 to 1) of the time.</summary>
    public static double WidthAt(double from, double to, double t) => t >= 1 ? to : from + (to - from) * LayerMotion.Ease(t);

    /// <summary>Where the search field sits when the group is <paramref name="nav"/> wide, from its
    /// column slot <paramref name="slot"/> measured while the group was <paramref name="navAtSlot"/>
    /// wide: the column starts later and is narrower by the group's growth, and the field, between
    /// <paramref name="min"/> and <paramref name="max"/> wide, is centred in it, or starts at its
    /// leading edge when the column is narrower than <paramref name="min"/>.</summary>
    public static BarSpan SearchSpan(BarSpan slot, double navAtSlot, double nav, double min, double max)
    {
        var column = SlotAt(slot, navAtSlot, nav);
        var field = Math.Clamp(column.Width, min, Math.Max(min, max));
        return new BarSpan(column.Left + Math.Max(0, (column.Width - field) / 2), field);
    }

    /// <summary>The search field's column slot when the group is <paramref name="nav"/> wide, from
    /// <paramref name="slot"/> measured while it was <paramref name="navAtSlot"/> wide.</summary>
    public static BarSpan SlotAt(BarSpan slot, double navAtSlot, double nav)
    {
        var growth = nav - navAtSlot;
        return new BarSpan(slot.Left + growth, Math.Max(0, slot.Width - growth));
    }

    /// <summary>Whether a slot measured while the group was <paramref name="navAtMeasured"/> wide is
    /// not where <paramref name="slot"/> puts it, by half a pixel or more: the bar was resized or
    /// moved, and a morph under way has to take the new slot.</summary>
    public static bool SlotMoved(BarSpan slot, double navAtSlot, BarSpan measured, double navAtMeasured)
    {
        var expected = SlotAt(slot, navAtSlot, navAtMeasured);
        return Math.Abs(expected.Left - measured.Left) >= 0.5 || Math.Abs(expected.Width - measured.Width) >= 0.5;
    }
}
