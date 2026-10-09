using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LightWeaver.Settings;

/// <summary>When the Up Next card appears. A credits marker always wins; the lead time is the
/// fallback for an episode without one. The global lead applies unless the show being LEFT has its
/// own, which also holds for a playlist that continues into a different show. The countdown and
/// the auto-play toggle are global only.</summary>
public static class UpNextPreferences
{
    public const int MinimumCountdownSeconds = 3;
    public const int MaximumCountdownSeconds = 120;
    public const int MinimumLeadSeconds = 5;
    public const int MaximumLeadSeconds = 300;
    public const int DefaultLeadSeconds = 30;

    /// <summary>The lead times offered as segments; anything else is a custom value.</summary>
    public static readonly IReadOnlyList<int> LeadPresets = [15, 30, 60];

    public static int ClampLead(int seconds) => Math.Clamp(seconds, MinimumLeadSeconds, MaximumLeadSeconds);

    public static bool IsValidLead(int seconds) => seconds is >= MinimumLeadSeconds and <= MaximumLeadSeconds;

    public static bool TryGetShowKey(string? serverUrl, Guid userId, Guid? seriesId, out string key)
    {
        key = "";
        if (userId == Guid.Empty || seriesId is null || seriesId == Guid.Empty
            || !Uri.TryCreate(serverUrl, UriKind.Absolute, out var server)
            || server.Scheme is not ("http" or "https") || server.UserInfo.Length > 0
            || server.Query.Length > 0 || server.Fragment.Length > 0)
            return false;

        // Uri canonicalizes scheme/host/default ports; path casing remains significant.
        // Hash the URL so delimiter-bearing base paths cannot collide with the identifiers.
        var canonicalServer = server.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var serverHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalServer)));
        key = $"{serverHash}:{userId:N}:{seriesId.Value:N}";
        return true;
    }

    /// <summary>The show's own lead, or null when it inherits the global one. Clamped on read so a
    /// value set in memory or edited by hand cannot leave the supported range.</summary>
    public static int? GetShowLead(AppSettings settings, string key)
        => settings.ShowUpNextLeadSeconds.TryGetValue(key, out var seconds) ? ClampLead(seconds) : null;

    /// <summary>Sets the show's lead, or removes it (back to inheriting) with null. In memory only;
    /// the caller saves.</summary>
    public static void SetShowLead(AppSettings settings, string key, int? seconds)
    {
        if (seconds is { } value) settings.ShowUpNextLeadSeconds[key] = ClampLead(value);
        else settings.ShowUpNextLeadSeconds.Remove(key);
    }

    public static int ResolveLeadSeconds(AppSettings settings, string? serverUrl, Guid userId, Guid? seriesId)
        => TryGetShowKey(serverUrl, userId, seriesId, out var key) && GetShowLead(settings, key) is { } show
            ? show : settings.UpNextLeadSeconds;
}

/// <summary>Reads the per-show lead map leniently: an entry that is not a whole number is dropped
/// rather than failing the whole settings file, which would reset every other setting. Values are
/// clamped to the lead range. Writes are the default object form.</summary>
public sealed class ShowLeadSecondsConverter : JsonConverter<Dictionary<string, int>>
{
    public override Dictionary<string, int> Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        var map = new Dictionary<string, int>();
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return map;
        }
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var key = reader.GetString()!;
            reader.Read();
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var seconds))
                map[key] = UpNextPreferences.ClampLead((int)Math.Clamp(seconds, int.MinValue, int.MaxValue));
            else
                reader.Skip();
        }
        return map;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, int> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (key, seconds) in value)
            writer.WriteNumber(key, seconds);
        writer.WriteEndObject();
    }
}
