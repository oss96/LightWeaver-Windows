using LightWeaver.Localization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LightWeaver.Jellyfin;

namespace LightWeaver.Downloads;

public sealed record DownloadTrack(int Index, int TypeOrdinal, string Display,
    string? Language, string? Title, string? Codec, bool IsExternal, bool IsText, bool IsDefault)
{
    public override string ToString() => Display;
}

public sealed record DownloadSelection(DownloadOption Quality, DownloadTrack? Audio = null,
    bool AllSubtitles = false, DownloadTrack? Subtitle = null)
{
    public long PlaybackPreferenceTimestampMs { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public bool UseDefaultAudio { get; init; }
    public string? PreferredAudioLanguage { get; init; }
}

public sealed record DownloadSubtitleFile(int StreamIndex, string Codec, string FilePath,
    string? Title, string? Language, bool IsDefault);

public static class DownloadLanguages
{
    public static DownloadTrack? DefaultAudio(DownloadOption quality, string? preferredLanguage)
        => quality.AudioTracks.FirstOrDefault(t => !t.IsExternal && Player.Languages.Matches(t.Language, preferredLanguage))
            ?? quality.AudioTracks.FirstOrDefault(t => !t.IsExternal && t.IsDefault)
            ?? quality.AudioTracks.FirstOrDefault(t => !t.IsExternal);

    public static DownloadOption EffectiveQuality(DownloadSelection selection)
    {
        if (selection.Audio?.IsExternal == true
            || selection.Audio is null && selection.Quality.AudioTracks.Any(t => t.IsExternal))
            throw new InvalidOperationException(L.Text("Downloads.ExternalAudioUnavailable"));
        var subtitles = SelectedSubtitles(selection);
        foreach (var track in subtitles.Where(t => t.IsExternal))
            _ = SubtitleExtension(track.Codec);
        var needsOriginal = selection.Audio is null && selection.Quality.AudioTracks.Count > 1
            || subtitles.Any(t => !t.IsExternal && !CanExtractText(t));
        if (!needsOriginal || selection.Quality.IsOriginal)
            return selection.Quality;
        return selection.Quality.OriginalOption
            ?? throw new InvalidOperationException(L.Text("Downloads.OriginalRequiredLanguages"));
    }

    public static DownloadSelection? ResolveForEpisode(DownloadSelection selection,
        IReadOnlyList<DownloadOption> episodeOptions)
    {
        var quality = episodeOptions.FirstOrDefault(o => o.Resolution == selection.Quality.Resolution);
        if (quality is null)
            return null;
        var audio = selection.UseDefaultAudio ? DefaultAudio(quality, selection.PreferredAudioLanguage)
            : selection.Audio is null ? null : Match(selection.Audio, quality.AudioTracks);
        var subtitle = selection.Subtitle is null ? null : Match(selection.Subtitle, quality.SubtitleTracks);
        if (!selection.UseDefaultAudio && selection.Audio is not null && audio is null
            || !selection.AllSubtitles && selection.Subtitle is not null && subtitle is null)
            return null;
        var resolved = selection with { Quality = quality, Audio = audio, Subtitle = subtitle };
        try { return EffectiveQuality(resolved) == quality ? resolved : null; }
        catch (InvalidOperationException) { return null; }
    }

    public static DownloadItem ApplyToItem(DownloadSelection selection, DownloadItem item)
    {
        var quality = EffectiveQuality(selection);
        var subtitles = SelectedSubtitles(selection);
        var identity = string.Join("|", quality.MediaSourceId, quality.Resolution,
            quality.MaxWidth, quality.VideoBitRate, selection.Audio?.Index.ToString() ?? "all",
            selection.AllSubtitles ? "all" : selection.Subtitle?.Index.ToString() ?? "none",
            string.Join(",", subtitles.Select(t => $"{t.Index}:{t.Codec}:{t.IsExternal}")));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..12]
            .ToLowerInvariant();
        var extension = quality.IsOriginal ? quality.Container ?? "mkv" : "mp4";
        extension = extension.Split(',')[0];
        extension = new string(extension.Where(char.IsAsciiLetterOrDigit).Take(12).ToArray());
        if (extension.Length == 0)
            extension = "mkv";
        var path = Path.Combine(Path.GetDirectoryName(item.FilePath)!,
            $"{Path.GetFileNameWithoutExtension(item.FilePath)}-{fingerprint}.{extension}");
        var sidecars = subtitles.Where(t => t.IsExternal || !quality.IsOriginal).Select(t =>
            new DownloadSubtitleFile(t.Index, t.Codec ?? "vtt",
                $"{Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path))}.sub-{t.Index}.{SubtitleExtension(t.Codec)}",
                t.Title ?? t.Display, t.Language, t.IsDefault)).ToArray();
        var selectedSidecar = !selection.AllSubtitles && selection.Subtitle is { } selected
            && sidecars.Any(s => s.StreamIndex == selected.Index) ? selected.Index : (int?)null;
        return item with
        {
            FilePath = path,
            MediaSourceId = quality.MediaSourceId,
            Container = quality.Container,
            Resolution = quality.Resolution,
            MaxWidth = quality.MaxWidth,
            VideoBitRate = quality.VideoBitRate,
            TotalBytes = quality.IsOriginal ? quality.EstimatedSizeBytes ?? -1 : -1,
            EstimatedTotalBytes = quality.IsOriginal ? -1 : quality.EstimatedSizeBytes ?? -1,
            DownloadLanguagesSelected = true,
            PlaybackPreferenceTimestampMs = selection.PlaybackPreferenceTimestampMs,
            AudioStreamIndex = quality.IsOriginal ? null : selection.Audio?.Index
                ?? quality.AudioTracks.FirstOrDefault(t => t.IsDefault)?.Index
                ?? quality.AudioTracks.FirstOrDefault()?.Index,
            PreferredAudioOrdinal = quality.IsOriginal ? selection.Audio?.TypeOrdinal : 0,
            PreferredSubtitleOrdinal = selection.AllSubtitles ? null
                : selection.Subtitle is null || selectedSidecar is not null ? -1 : selection.Subtitle.TypeOrdinal,
            PreferredSubtitleFileIndex = selectedSidecar,
            LanguageSummary = FormatSummary(selection.Audio, selection.AllSubtitles, selection.Subtitle,
                quality.AudioTracks.Count, quality.SubtitleTracks.Count, quality.IsOriginal),
            SubtitleFiles = sidecars,
            AudioTracks = quality.AudioTracks,
            SubtitleTracks = quality.SubtitleTracks,
        };
    }

    public static string FormatSummary(DownloadItem item)
    {
        if (!item.DownloadLanguagesSelected)
            return item.LanguageSummary ?? L.Text("Downloads.LegacyLanguages");
        var audio = item.IsTranscoded
            ? item.AudioTracks.FirstOrDefault(t => t.Index == item.AudioStreamIndex)
            : item.AudioTracks.FirstOrDefault(t => t.TypeOrdinal == item.PreferredAudioOrdinal);
        var subtitle = item.PreferredSubtitleFileIndex is { } fileIndex
            ? item.SubtitleTracks.FirstOrDefault(t => t.Index == fileIndex)
            : item.PreferredSubtitleOrdinal is >= 0
                ? item.SubtitleTracks.FirstOrDefault(t => t.TypeOrdinal == item.PreferredSubtitleOrdinal) : null;
        return FormatSummary(audio, item.PreferredSubtitleOrdinal is null, subtitle,
            item.AudioTracks.Count, item.SubtitleTracks.Count, !item.IsTranscoded);
    }

    private static string FormatSummary(DownloadTrack? audio, bool allSubtitles, DownloadTrack? subtitle,
        int audioCount, int subtitleCount, bool original)
        => L.Format("Downloads.LanguageSummary",
            audio?.Display ?? (audioCount == 0 ? L.Text("Downloads.NoAudioTracks") : L.Text("Downloads.AllLanguages")),
            allSubtitles ? subtitleCount == 0 ? L.Text("Downloads.NoSubtitleTracks") : L.Text("Downloads.AllLanguages")
                : subtitle?.Display ?? L.Text("Downloads.None"))
            + (original ? L.Text("Downloads.OriginalTrackPreferences") : "");

    private static IReadOnlyList<DownloadTrack> SelectedSubtitles(DownloadSelection selection)
        => selection.AllSubtitles ? selection.Quality.SubtitleTracks
            : selection.Subtitle is { } subtitle ? [subtitle] : [];

    private static DownloadTrack? Match(DownloadTrack desired, IReadOnlyList<DownloadTrack> tracks)
    {
        var candidates = tracks.Where(t => Player.Languages.Matches(t.Language, desired.Language)
            || string.Equals(t.Language, desired.Language, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (string.IsNullOrWhiteSpace(desired.Language))
            return candidates.FirstOrDefault(t => string.Equals(t.Title, desired.Title,
                StringComparison.OrdinalIgnoreCase) && string.Equals(t.Codec, desired.Codec,
                StringComparison.OrdinalIgnoreCase) && t.IsExternal == desired.IsExternal
                && (!string.IsNullOrWhiteSpace(desired.Title) || t.TypeOrdinal == desired.TypeOrdinal));
        if (!string.IsNullOrWhiteSpace(desired.Title))
            candidates = candidates.Where(t => string.Equals(t.Title, desired.Title,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        return candidates.OrderByDescending(t => string.Equals(t.Title, desired.Title,
                StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(t => string.Equals(t.Codec, desired.Codec, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(t => t.IsDefault).FirstOrDefault();
    }

    private static bool CanExtractText(DownloadTrack track)
    {
        if (!track.IsText)
            return false;
        try { _ = SubtitleExtension(track.Codec); return true; }
        catch (InvalidOperationException) { return false; }
    }

    public static string SubtitleExtension(string? codec)
        => SubtitleRequestFormat(codec) is "pgssub" ? "sup" : SubtitleRequestFormat(codec);

    public static string SubtitleRequestFormat(string? codec) => codec?.ToLowerInvariant() switch
    {
        "ass" => "ass",
        "ssa" => "ssa",
        "srt" or "subrip" => "srt",
        "vtt" or "webvtt" => "vtt",
        "ttml" => "ttml",
        "mov_text" or "text" => "vtt",
        "pgs" or "pgssub" or "hdmv_pgs_subtitle" => "pgssub",
        _ => throw new InvalidOperationException(L.Format("Downloads.SubtitleFormatUnavailable", codec ?? L.Text("Downloads.UnknownFormat"))),
    };
}
