using LightWeaver.Localization;

namespace LightWeaver.Details;

/// <summary>Names for streaming quality caps: the plain name with the bitrate beside it
/// (High · 1080p · 20 Mbps), or Original quality.</summary>
public static class QualityNames
{
    /// <summary>The name of a cap in Mbps; 0 is the original file.</summary>
    public static string Of(int mbps) => mbps <= 0 ? L.Text("Quality.Original") : L.Format("Quality.Named", Simple(mbps), Exact(mbps));

    public static string Simple(int mbps) => mbps switch
    {
        <= 0 => L.Text("Quality.Original"),
        >= 30 => L.Text("Quality.VeryHigh"),
        >= 15 => L.Text("Quality.High"),
        >= 8 => L.Text("Quality.Good"),
        >= 3 => L.Text("Quality.Low"),
        _ => L.Text("Quality.DataSaver"),
    };

    public static string Exact(int mbps) => mbps <= 0 ? L.Text("Quality.Original") : L.Format("Quality.Mbps", mbps);

    /// <summary>The name of a quality menu tier; null is the Settings default, shown with the cap
    /// Settings sets (<paramref name="settingsMbps"/>), worded as the Settings page words it.</summary>
    public static string Tier(int? mbps, int settingsMbps)
        => mbps is { } cap ? Of(cap)
            : L.Format("Quality.Named", L.Text("Player.QualityDefault"), settingsMbps > 0 ? Exact(settingsMbps) : L.Text("Settings.Unlimited"));
}
