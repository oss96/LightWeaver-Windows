namespace LightWeaver.Downloads;

/// <summary>Fresh resume data gets a short opportunity to arrive, never a transport timeout.
/// The same saved metadata works offline and for index entries from older app versions.</summary>
internal static class DownloadPlayback
{
    internal static bool UseSavedSubtitlePreference(DownloadItem download, long latestPlayerChoiceTimestampMs)
        => download.DownloadLanguagesSelected && latestPlayerChoiceTimestampMs <= download.PlaybackPreferenceTimestampMs
            && (download.PreferredSubtitleOrdinal is not null || download.PreferredSubtitleFileIndex is not null);

    internal static (Jellyfin.MediaStreamChoice? Audio, Jellyfin.MediaStreamChoice? Subtitle, bool SubtitleOff)
        FallbackTracks(DownloadItem download, IReadOnlyList<Player.MpvTrack> tracks, bool fileLoaded,
            bool? explicitSubtitleOff = null)
    {
        var audio = tracks.Where(t => t.Type == "audio").ToList();
        var subtitles = tracks.Where(t => t.Type == "sub").ToList();
        var currentAudio = audio.FirstOrDefault(t => t.Selected);
        DownloadTrack? sourceAudio = download.IsTranscoded
            ? download.AudioTracks.FirstOrDefault(t => t.Index == download.AudioStreamIndex)
            : download.AudioTracks.FirstOrDefault(t => !t.IsExternal
                && t.TypeOrdinal == (fileLoaded && currentAudio is not null ? audio.IndexOf(currentAudio) : download.PreferredAudioOrdinal));
        sourceAudio ??= download.AudioTracks.FirstOrDefault(t => t.IsDefault) ?? download.AudioTracks.FirstOrDefault();
        var currentSubtitle = subtitles.FirstOrDefault(t => t.Selected);
        DownloadTrack? sourceSubtitle = null;
        if (fileLoaded && currentSubtitle is not null)
        {
            var sidecar = download.SubtitleFiles.FirstOrDefault(s => string.Equals(s.FilePath,
                currentSubtitle.ExternalFilename, StringComparison.OrdinalIgnoreCase));
            sourceSubtitle = sidecar is not null
                ? download.SubtitleTracks.FirstOrDefault(t => t.Index == sidecar.StreamIndex)
                : download.SubtitleTracks.FirstOrDefault(t => !t.IsExternal && t.TypeOrdinal
                    == subtitles.Where(t => t.ExternalFilename is null).ToList().IndexOf(currentSubtitle));
        }
        else if (explicitSubtitleOff != true)
        {
            sourceSubtitle = download.PreferredSubtitleFileIndex is { } index
                ? download.SubtitleTracks.FirstOrDefault(t => t.Index == index)
                : download.SubtitleTracks.FirstOrDefault(t => !t.IsExternal && t.TypeOrdinal == download.PreferredSubtitleOrdinal);
        }
        static Jellyfin.MediaStreamChoice? Choice(DownloadTrack? track) => track is null ? null
            : new(track.Index, track.TypeOrdinal, track.Display, track.Language, track.Title, track.IsDefault, track.IsExternal);
        return (Choice(sourceAudio), explicitSubtitleOff == true ? null : Choice(sourceSubtitle),
            explicitSubtitleOff == true || currentSubtitle is null
                && download.PreferredSubtitleFileIndex is null && download.PreferredSubtitleOrdinal == -1);
    }

    internal static Player.MpvTrack? PreferredSubtitleTrack(DownloadItem download,
        IReadOnlyList<Player.MpvTrack> tracks)
    {
        if (download.PreferredSubtitleFileIndex is { } streamIndex)
        {
            var file = download.SubtitleFiles.FirstOrDefault(s => s.StreamIndex == streamIndex);
            return file is null ? null : tracks.FirstOrDefault(t => t.Type == "sub"
                && string.Equals(t.ExternalFilename, file.FilePath, StringComparison.OrdinalIgnoreCase));
        }
        return download.PreferredSubtitleOrdinal is >= 0
            ? tracks.Where(t => t.Type == "sub" && t.ExternalFilename is null)
                .ElementAtOrDefault(download.PreferredSubtitleOrdinal.Value)
            : null;
    }

    internal static readonly TimeSpan MetadataBudget = TimeSpan.FromMilliseconds(750);

    internal static async Task<Jellyfin.MediaItem> ResolveMetadataAsync(DownloadItem download,
        Func<CancellationToken, Task<Jellyfin.MediaItem?>>? fetch)
    {
        if (fetch is not null)
        {
            using var timeout = new CancellationTokenSource(MetadataBudget);
            try
            {
                var fresh = await fetch(timeout.Token).WaitAsync(timeout.Token);
                if (fresh is not null) return fresh;
            }
            catch
            {
                // An unreachable server cannot prevent playback of bytes already downloaded.
            }
        }
        return download.PlaybackMetadata ?? new Jellyfin.MediaItem
        {
            Id = download.ItemId, Name = download.Title,
            Type = global::Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Movie,
            RuntimeTicks = download.RuntimeTicks,
        };
    }
}
