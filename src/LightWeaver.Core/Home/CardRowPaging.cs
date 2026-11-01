namespace LightWeaver.Home;

/// <summary>Tracks a row's requested page until its matching scroll operation finishes.</summary>
public sealed class CardRowPaging
{
    private int _correlationId = -1;
    public double? Target { get; private set; }

    public double? WheelDestination(double offset, double maximum, int delta, bool horizontal, bool rightToLeft)
    {
        if (maximum <= 0 || delta == 0) return null;
        var direction = horizontal ? (rightToLeft ? -1 : 1) : -1;
        return Math.Clamp((Target ?? offset) + (double)delta * direction, 0, maximum);
    }

    public double Destination(double offset, double viewport, double maximum, double cardWidth, double gap, int direction)
    {
        var stride = cardWidth + gap;
        var perPage = Math.Max(1, Math.Floor((viewport + gap) / stride));
        var first = Math.Round(Math.Clamp(Target ?? offset, 0, maximum) / stride);
        return Math.Clamp((first + direction * perPage) * stride, 0, maximum);
    }

    public void Track(int correlationId, double target)
    {
        _correlationId = correlationId;
        Target = correlationId < 0 ? null : target;
    }

    public void Complete(int correlationId)
    {
        if (correlationId == _correlationId) Reset();
    }

    public void Reset()
    {
        _correlationId = -1;
        Target = null;
    }
}
