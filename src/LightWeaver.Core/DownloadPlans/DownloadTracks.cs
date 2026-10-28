using LightWeaver.Jellyfin;
using LightWeaver.Localization;
using LightWeaver.Player;

namespace LightWeaver.Downloads;

public sealed record TrackChoice(DownloadTrack? Track, bool All = false)
{
    public string Label(bool audio) => Track is { } track
        ? track.Display + (audio && track.IsDefault ? L.Text("Downloads.DefaultTrack") : "")
        : !All ? L.Text("Downloads.None")
        : audio ? L.Text("Downloads.AllAudioLanguages") : L.Text("Downloads.AllSubtitles");
}

public sealed record TrackPicks(int Audio, int Subtitle, bool DefaultAudio, IReadOnlyList<bool> AudioAllowed, IReadOnlyList<bool> SubtitleAllowed);

public enum SubtitleStart
{
    Unchanged,

    Off,

    Track,
}

public sealed record DownloadTrackStart(int? AudioId, SubtitleStart Subtitle, int? SubtitleId = null);

public static class DownloadTracks
{
    public static bool HasChoice(DownloadOption original) => original.AudioTracks.Count > 1 || original.SubtitleTracks.Count > 0;

    public static IReadOnlyList<TrackChoice> AudioChoices(DownloadOption original)
        => [new(null, All: true), .. original.AudioTracks.Select(t => new TrackChoice(t))];

    public static IReadOnlyList<TrackChoice> SubtitleChoices(DownloadOption original)
        => [new(null), new(null, All: true), .. original.SubtitleTracks.Select(t => new TrackChoice(t))];

    public static int DefaultAudio(DownloadOption original, string? preferredLanguage)
        => DownloadLanguages.DefaultAudio(original, preferredLanguage) is { } track ? IndexOf(original.AudioTracks, track) + 1 : 0;

    public static int DefaultSubtitle(DownloadOption original, string? preferredLanguage)
    {
        var index = original.SubtitleTracks.ToList().FindIndex(t => Languages.Matches(t.Language, preferredLanguage));
        return index >= 0 ? index + 2 : 0;
    }

    public static bool Fits(DownloadSelection selection)
    {
        try
        {
            return DownloadLanguages.EffectiveQuality(selection) == selection.Quality;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static TrackPicks Allow(DownloadOption original, DownloadOption quality, int audio, int subtitle, bool defaultAudio, string? preferredAudio)
    {
        var audioChoices = AudioChoices(original);
        var subtitleChoices = SubtitleChoices(original);
        audio = Math.Clamp(audio, 0, audioChoices.Count - 1);
        subtitle = Math.Clamp(subtitle, 0, subtitleChoices.Count - 1);
        var audioAllowed = audioChoices.Select(a => (quality.IsOriginal || a.Track is not null || quality.AudioTracks.Count == 0)
            && Fits(new DownloadSelection(quality, a.Track))).ToList();
        if (!audioAllowed[audio])
        {
            audio = DefaultAudio(original, preferredAudio);
            if (!audioAllowed[audio]) audio = audioAllowed.FindIndex(a => a);
            if (audio < 0) audio = 0;
            defaultAudio = true;
        }
        var subtitleAllowed = subtitleChoices.Select(s => Fits(new DownloadSelection(quality, audioChoices[audio].Track, s.All, s.Track))).ToList();
        if (!subtitleAllowed[subtitle]) subtitle = 0;
        return new TrackPicks(audio, subtitle, defaultAudio, audioAllowed, subtitleAllowed);
    }

    public static DownloadSelection Select(DownloadOption original, DownloadOption quality, TrackPicks picks, string? preferredAudio)
    {
        var subtitle = SubtitleChoices(original)[picks.Subtitle];
        return new DownloadSelection(quality, AudioChoices(original)[picks.Audio].Track, subtitle.All, subtitle.Track)
        {
            UseDefaultAudio = picks.DefaultAudio,
            PreferredAudioLanguage = preferredAudio,
        };
    }

    public static DownloadSelection? ForTitle(DownloadSelection chosen, DownloadOption quality)
        => DownloadLanguages.ResolveForEpisode(chosen with { Quality = quality }, [quality]);

    public static DownloadItem Build(MediaItem title, DownloadSelection selection, string serverUrl, string directory, DateTimeOffset now)
        => DownloadLanguages.ApplyToItem(selection, DownloadsModel.BuildItem(title, selection.Quality, serverUrl, directory, includeSourceId: true, now));

    public static DownloadTrackStart Playback(DownloadItem download, IReadOnlyList<MpvTrack> tracks, string? preferredAudio, string? preferredSubtitle)
    {
        var audio = tracks.Where(t => t.Type == "audio" && t.ExternalFilename is null).ToList();
        var audioId = download.PreferredAudioOrdinal is { } ordinal && download.DownloadLanguagesSelected
            ? audio.ElementAtOrDefault(ordinal)?.Id
            : audio.FirstOrDefault(t => Languages.Matches(t.Lang, preferredAudio))?.Id;
        if (download.DownloadLanguagesSelected && download.PreferredSubtitleFileIndex is not null)
            return DownloadPlayback.PreferredSubtitleTrack(download, tracks) is { } sidecar
                ? new DownloadTrackStart(audioId, SubtitleStart.Track, sidecar.Id)
                : new DownloadTrackStart(audioId, SubtitleStart.Off);
        if (download.DownloadLanguagesSelected && download.PreferredSubtitleOrdinal == -1)
            return new DownloadTrackStart(audioId, SubtitleStart.Off);
        var subtitle = download.DownloadLanguagesSelected && download.PreferredSubtitleOrdinal is >= 0
            ? DownloadPlayback.PreferredSubtitleTrack(download, tracks)
            : tracks.FirstOrDefault(t => t.Type == "sub" && Languages.Matches(t.Lang, preferredSubtitle));
        return subtitle is null ? new DownloadTrackStart(audioId, SubtitleStart.Unchanged) : new DownloadTrackStart(audioId, SubtitleStart.Track, subtitle.Id);
    }

    /// <summary>The file's tracks, with the language and title the file lacks taken from what the
    /// download saved: embedded tracks by their order, subtitle files by their path.</summary>
    public static IReadOnlyList<MpvTrack> Named(DownloadItem download, IReadOnlyList<MpvTrack> tracks)
    {
        var audio = 0;
        var subtitles = 0;
        var named = new List<MpvTrack>(tracks.Count);
        foreach (var track in tracks)
        {
            named.Add((track.Type, track.ExternalFilename) switch
            {
                ("audio", null) => Fill(track, SavedAudio(download, audio++)),
                ("sub", null) => Fill(track, SavedSubtitle(download, subtitles++)),
                ("sub", { } file) => Fill(track, SavedSidecar(download, file)),
                _ => track,
            });
        }
        return named;
    }

    private static DownloadTrack? SavedSubtitle(DownloadItem download, int ordinal)
        => download.IsTranscoded ? null : download.SubtitleTracks.FirstOrDefault(t => !t.IsExternal && t.TypeOrdinal == ordinal);

    private static DownloadTrack? SavedAudio(DownloadItem download, int ordinal)
    {
        if (!download.IsTranscoded) return download.AudioTracks.FirstOrDefault(t => !t.IsExternal && t.TypeOrdinal == ordinal);
        if (ordinal > 0) return null;
        return download.AudioTracks.FirstOrDefault(t => t.Index == download.AudioStreamIndex)
            ?? download.AudioTracks.FirstOrDefault(t => t.IsDefault) ?? download.AudioTracks.FirstOrDefault();
    }

    private static DownloadTrack? SavedSidecar(DownloadItem download, string file)
    {
        var sidecar = download.SubtitleFiles.FirstOrDefault(s => string.Equals(s.FilePath, file, StringComparison.OrdinalIgnoreCase));
        if (sidecar is null) return null;
        return download.SubtitleTracks.FirstOrDefault(t => t.Index == sidecar.StreamIndex)
            ?? new DownloadTrack(sidecar.StreamIndex, -1, sidecar.Title ?? "", sidecar.Language, sidecar.Title, sidecar.Codec, true, true, sidecar.IsDefault, sidecar.IsForced);
    }

    private static MpvTrack Fill(MpvTrack track, DownloadTrack? saved)
    {
        if (saved is null) return track;
        var language = track.Lang ?? saved.Language;
        return track with
        {
            Lang = language,
            Title = track.Title ?? saved.Title ?? (language is null && saved.Display.Length > 0 ? saved.Display : null),
            Forced = track.Forced || saved.IsForced,
        };
    }

    private static int IndexOf(IReadOnlyList<DownloadTrack> tracks, DownloadTrack track)
    {
        for (var i = 0; i < tracks.Count; i++)
            if (ReferenceEquals(tracks[i], track)) return i;
        return -1;
    }
}
