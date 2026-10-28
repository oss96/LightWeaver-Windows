namespace LightWeaver.Details;

/// <summary>The detail page's preview size and side panel width, in DIP (<see cref="DetailModel.Frame"/>).</summary>
public readonly record struct DetailFrame(double PreviewWidth, double PreviewHeight, double SideWidth);

/// <summary>The detail page's fixed spacing, in DIP: the page padding on both sides and on top
/// and bottom together, and the gaps between the preview and the side panel and above the rows.</summary>
public readonly record struct DetailSpacing(double Horizontal, double Vertical, double ColumnGap, double RowGap);

/// <summary>Where the detail page's preview, rows and side panel sit within the page, and where the
/// overview and the season or episode grid sit within the side panel, in DIP.</summary>
public readonly record struct DetailGeometry(double PreviewX, double PreviewY, double PreviewWidth, double PreviewHeight, double RowsTop, double SideWidth,
    double OverviewTop, double GridTop)
{
    /// <summary>Movement below this is layout rounding, not a change the eye sees.</summary>
    public const double Tolerance = 0.5;

    /// <summary>Whether anything moved or changed size against <paramref name="other"/>.</summary>
    public bool MovedFrom(DetailGeometry other)
        => Far(PreviewX, other.PreviewX) || Far(PreviewY, other.PreviewY) || Far(PreviewWidth, other.PreviewWidth)
            || Far(PreviewHeight, other.PreviewHeight) || Far(RowsTop, other.RowsTop) || Far(SideWidth, other.SideWidth)
            || Far(OverviewTop, other.OverviewTop) || Far(GridTop, other.GridTop);

    private static bool Far(double a, double b) => Math.Abs(a - b) >= Tolerance;
}

/// <summary>Counts how often the detail page's layout changed after it was first shown: the first
/// sample is the layout it opened with, and every later sample that moved is one change.</summary>
public sealed class DetailSettle
{
    private DetailGeometry? _last;

    /// <summary>Layout changes seen since the first sample.</summary>
    public int Changes { get; private set; }

    /// <summary>When the last change was seen, in ms after the first sample's clock; 0 without one.</summary>
    public double SettledMs { get; private set; }

    /// <summary>Takes the geometry of one frame, <paramref name="elapsedMs"/> after the page showed.</summary>
    public void Observe(DetailGeometry geometry, double elapsedMs)
    {
        if (_last is { } last && geometry.MovedFrom(last))
        {
            Changes++;
            SettledMs = elapsedMs;
        }
        _last = geometry;
    }
}
