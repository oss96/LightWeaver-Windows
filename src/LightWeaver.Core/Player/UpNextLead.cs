using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Jellyfin;
using LightWeaver.Settings;

namespace LightWeaver.Player;

/// <summary>Resolves Up Next timing for the episode being left.</summary>
public static class UpNextLead
{
    public static int For(AppSettings settings, string? serverUrl, Guid userId, MediaItem? leaving)
        => UpNextPreferences.ResolveLeadSeconds(settings, serverUrl, userId, leaving?.Type == BaseItemDto_Type.Episode ? leaving.SeriesId : null);
}
