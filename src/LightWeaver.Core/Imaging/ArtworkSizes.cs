namespace LightWeaver.Imaging;

/// <summary>The widths artwork is requested at, in physical pixels. A card asks for the step at or
/// above the width it shows, so its address (and the disk and memory cache entry behind it) stays
/// the same while the window, the poster size or the display scale moves within a step, and the
/// image is never upscaled. Steps grow by a fifth to a half, so a request is at most half again as
/// wide as needed, and cover posters, wide cards and backdrops at 100 to 200 % scale.</summary>
public static class ArtworkSizes
{
    /// <summary>The ladder, narrowest first; the last step is also the widest request.</summary>
    public static IReadOnlyList<int> Steps { get; } = [160, 240, 320, 400, 480, 640, 800, 960, 1280, 1600, 1920];

    /// <summary>The narrowest step at least <paramref name="width"/> wide, or the widest step.</summary>
    public static int Step(int width)
    {
        foreach (var step in Steps)
            if (step >= width) return step;
        return Steps[^1];
    }

    /// <summary>The step after <paramref name="step"/>, or null past the widest or off the ladder.</summary>
    public static int? Next(int step)
    {
        for (var i = 0; i < Steps.Count - 1; i++)
            if (Steps[i] == step) return Steps[i + 1];
        return null;
    }
}
