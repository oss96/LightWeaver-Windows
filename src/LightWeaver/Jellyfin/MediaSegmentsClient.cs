using System.Text.Json;
using Jellyfin.Sdk.Generated.Models;

namespace LightWeaver.Jellyfin;

public enum SkipSegmentKind
{
    Intro,
    Credits,
    Recap,
}

/// <summary>A skippable region of the current item, in seconds. The prompt window is
/// when the Skip button shows; Start..End is what gets skipped.</summary>
public sealed record SkipSegment(
    SkipSegmentKind Kind,
    double StartSeconds,
    double EndSeconds,
    double PromptStartSeconds,
    double PromptEndSeconds)
{
    public string Label => Kind switch
    {
        SkipSegmentKind.Credits => "Skip Credits",
        SkipSegmentKind.Recap => "Skip Recap",
        _ => "Skip Intro",
    };
}

/// <summary>
/// Fetches skippable segments with a triple fallback — native media segments API
/// (needs a provider plugin server-side), the Intro Skipper plugin's own endpoint,
/// then chapter-name heuristics. All failures collapse to an empty list: skip
/// functionality must never disturb playback.
/// </summary>
public static class MediaSegmentsClient
{
    public static async Task<IReadOnlyList<SkipSegment>> GetSegmentsAsync(JellyfinService jf, MediaItem item)
    {
        var native = await FromNativeApiAsync(jf, item.Id).ConfigureAwait(false);
        if (native.Count > 0)
            return native;

        if (item.Type == BaseItemDto_Type.Episode)
        {
            var plugin = await FromIntroSkipperPluginAsync(jf, item.Id).ConfigureAwait(false);
            if (plugin.Count > 0)
                return plugin;
        }

        return FromChapterNames(item);
    }

    private static async Task<IReadOnlyList<SkipSegment>> FromNativeApiAsync(JellyfinService jf, Guid itemId)
    {
        try
        {
            var result = await jf.Client.MediaSegments[itemId].GetAsync(cfg =>
                cfg.QueryParameters.IncludeSegmentTypes =
                    [MediaSegmentType.Intro, MediaSegmentType.Outro, MediaSegmentType.Recap]).ConfigureAwait(false);

            return result?.Items?
                .Where(s => s.StartTicks is not null && s.EndTicks is not null)
                .Select(s =>
                {
                    var start = s.StartTicks!.Value / 10_000_000.0;
                    var end = s.EndTicks!.Value / 10_000_000.0;
                    var kind = s.Type switch
                    {
                        MediaSegmentDto_Type.Outro => SkipSegmentKind.Credits,
                        MediaSegmentDto_Type.Recap => SkipSegmentKind.Recap,
                        _ => SkipSegmentKind.Intro,
                    };
                    // Native segments carry no prompt window — prompt for the whole segment.
                    return new SkipSegment(kind, start, end, start, end);
                })
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static async Task<IReadOnlyList<SkipSegment>> FromIntroSkipperPluginAsync(JellyfinService jf, Guid itemId)
    {
        var json = await jf.GetRawAsync($"/Episode/{itemId}/IntroSkipperSegments").ConfigureAwait(false);
        if (json is null)
            return [];
        try
        {
            var segments = new List<SkipSegment>();
            using var doc = JsonDocument.Parse(json);
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var kind = entry.Name switch
                {
                    "Introduction" => SkipSegmentKind.Intro,
                    "Credits" => SkipSegmentKind.Credits,
                    _ => (SkipSegmentKind?)null,
                };
                if (kind is null)
                    continue;
                var seg = entry.Value;
                double Get(string name) => seg.TryGetProperty(name, out var v) ? v.GetDouble() : 0;
                var start = Get("IntroStart");
                var end = Get("IntroEnd");
                if (end <= start)
                    continue;
                var promptStart = Get("ShowSkipPromptAt");
                var promptEnd = Get("HideSkipPromptAt");
                if (promptEnd <= promptStart)
                {
                    promptStart = start;
                    promptEnd = end;
                }
                segments.Add(new SkipSegment(kind.Value, start, end, promptStart, promptEnd));
            }
            return segments;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<SkipSegment> FromChapterNames(MediaItem item)
    {
        if (item.Chapters.Count == 0 || item.RuntimeTicks is not { } runtimeTicks)
            return [];
        var duration = runtimeTicks / 10_000_000.0;
        var segments = new List<SkipSegment>();
        for (var i = 0; i < item.Chapters.Count; i++)
        {
            var name = item.Chapters[i].Name?.Trim() ?? "";
            SkipSegmentKind kind;
            if (name.Equals("Intro", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Introduction", StringComparison.OrdinalIgnoreCase))
                kind = SkipSegmentKind.Intro;
            else if (name.Equals("Credits", StringComparison.OrdinalIgnoreCase)
                || name.Equals("End Credits", StringComparison.OrdinalIgnoreCase))
                kind = SkipSegmentKind.Credits;
            else
                continue;

            var start = item.Chapters[i].StartSeconds;
            var end = i + 1 < item.Chapters.Count ? item.Chapters[i + 1].StartSeconds : duration;
            if (end > start)
                segments.Add(new SkipSegment(kind, start, end, start, end));
        }
        return segments;
    }
}
