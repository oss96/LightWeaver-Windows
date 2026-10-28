using System.Text.Json;
using System.Text.Json.Serialization;

namespace LightWeaver.Diagnostics;

/// <summary>Frame-interval statistics of one animation or page switch: one line of the opt-in
/// frame log (<c>LIGHTWEAVER_FRAME_LOG</c>). Times are milliseconds, rounded to 0.1. The image
/// counts, added after the timing fields, say how many queued artwork applications landed while it
/// ran, on how many frames, and the most on one frame. A recording that watched a page settle adds
/// how often its layout changed after it was shown and when the last change came; other lines
/// leave both out.</summary>
public sealed record FrameStats(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("duration_ms")] double DurationMs,
    [property: JsonPropertyName("frames")] int Frames,
    [property: JsonPropertyName("mean_ms")] double MeanMs,
    [property: JsonPropertyName("p50_ms")] double P50Ms,
    [property: JsonPropertyName("p95_ms")] double P95Ms,
    [property: JsonPropertyName("max_ms")] double MaxMs,
    [property: JsonPropertyName("over_16_7_ms")] int Over60HzBudget,
    [property: JsonPropertyName("over_33_3_ms")] int Over30HzBudget,
    [property: JsonPropertyName("images")] int Images = 0,
    [property: JsonPropertyName("image_frames")] int ImageFrames = 0,
    [property: JsonPropertyName("images_max_per_frame")] int MaxImagesPerFrame = 0,
    [property: JsonPropertyName("layout_changes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? LayoutChanges = null,
    [property: JsonPropertyName("layout_settled_ms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? LayoutSettledMs = null)
{
    /// <summary>A frame longer than this missed a 60 Hz refresh.</summary>
    public const double Budget60Hz = 16.7;

    /// <summary>A frame longer than this missed a 30 Hz refresh.</summary>
    public const double Budget30Hz = 33.3;

    /// <summary>Summarises the intervals between consecutive frames; percentiles are nearest-rank.
    /// <paramref name="imagesPerFrame"/> holds the artwork applications of each frame, and
    /// <paramref name="layout"/> the layout changes a settling page made.</summary>
    public static FrameStats From(string name, double durationMs, IReadOnlyList<double> intervalsMs, IReadOnlyList<int>? imagesPerFrame = null,
        (int Changes, double SettledMs)? layout = null)
        => Summarise(name, durationMs, intervalsMs, imagesPerFrame) with
        {
            LayoutChanges = layout?.Changes,
            LayoutSettledMs = layout is { } settle ? Round(settle.SettledMs) : null,
        };

    private static FrameStats Summarise(string name, double durationMs, IReadOnlyList<double> intervalsMs, IReadOnlyList<int>? imagesPerFrame)
    {
        var images = imagesPerFrame ?? [];
        var (total, frames, most) = (images.Sum(), images.Count(n => n > 0), images.Count == 0 ? 0 : images.Max());
        if (intervalsMs.Count == 0) return new FrameStats(name, Round(durationMs), 0, 0, 0, 0, 0, 0, 0, total, frames, most);
        var sorted = intervalsMs.Order().ToArray();
        return new FrameStats(
            name,
            Round(durationMs),
            sorted.Length,
            Round(sorted.Average()),
            Round(Percentile(sorted, 50)),
            Round(Percentile(sorted, 95)),
            Round(sorted[^1]),
            sorted.Count(ms => ms > Budget60Hz),
            sorted.Count(ms => ms > Budget30Hz),
            total,
            frames,
            most);
    }

    /// <summary>The single-line JSON form the frame log appends.</summary>
    public string ToJson() => JsonSerializer.Serialize(this);

    private static double Percentile(double[] sorted, int percent)
        => sorted[Math.Clamp((int)Math.Ceiling(percent / 100.0 * sorted.Length) - 1, 0, sorted.Length - 1)];

    private static double Round(double ms) => Math.Round(ms, 1, MidpointRounding.AwayFromZero);
}
