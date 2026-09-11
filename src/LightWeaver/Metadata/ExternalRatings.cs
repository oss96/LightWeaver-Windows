using System.Text.Json.Serialization;

namespace LightWeaver.Metadata;

/// <summary>Third-party critic scores for a title. Any field may be null when the
/// provider doesn't carry that score. Used only for display on the detail view.</summary>
public sealed record ExternalRatings(string? Imdb, string? RottenTomatoes, string? Metacritic)
{
    /// <summary>True when at least one score is present (nothing renders otherwise).
    /// Computed, so it is kept out of the cached JSON — it is ignored on read anyway.</summary>
    [JsonIgnore]
    public bool HasAny => Imdb is not null || RottenTomatoes is not null || Metacritic is not null;
}
