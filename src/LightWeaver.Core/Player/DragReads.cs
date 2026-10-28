namespace LightWeaver.Player;

/// <summary>Which drag a read of the dragged data belongs to. A read's result applies only while
/// its drag is still the current one, so a slow read cannot bring the drop overlay back after a
/// leave or a drop, and an older read cannot overwrite a newer one.</summary>
public sealed class DragReads
{
    private int _generation;
    private bool _active;

    /// <summary>A drag entered: starts a read and returns its generation.</summary>
    public int Begin()
    {
        _active = true;
        return ++_generation;
    }

    /// <summary>The drag left or dropped: every read still running is stale.</summary>
    public void End()
    {
        _active = false;
        _generation++;
    }

    /// <summary>Whether the read of <paramref name="generation"/> may still apply its result.</summary>
    public bool IsCurrent(int generation) => _active && generation == _generation;
}
