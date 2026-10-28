using LightWeaver.Jellyfin;
using LightWeaver.ViewModels;

namespace LightWeaver.Player;

/// <summary>A subtitle preference resolved by identity across files.</summary>
public sealed record SubtitleCarryOver(bool Off, string? Lang, string? Title, bool Forced)
{
    public static SubtitleCarryOver From(SubtitleSelectionRequest request) => new(request.Off, request.Lang, request.Title, request.Forced);

    /// <summary>Matches language, forced flag and title before falling back to language or title.</summary>
    public MpvTrack? Match(IReadOnlyList<MpvTrack> subtitles)
    {
        if (Off) return null;
        var hasLang = Lang is { Length: > 0 };
        var hasTitle = Title is { Length: > 0 };
        bool SameTitle(MpvTrack t) => string.Equals(t.Title, Title, StringComparison.OrdinalIgnoreCase);
        return (hasLang && hasTitle ? subtitles.FirstOrDefault(t => Languages.Matches(t.Lang, Lang) && t.Forced == Forced && SameTitle(t)) : null)
            ?? (hasLang ? subtitles.FirstOrDefault(t => Languages.Matches(t.Lang, Lang) && t.Forced == Forced) ?? subtitles.FirstOrDefault(t => Languages.Matches(t.Lang, Lang)) : null)
            ?? (hasTitle ? subtitles.FirstOrDefault(SameTitle) : null);
    }

    public MediaStreamChoice? MatchSource(IReadOnlyList<MediaStreamChoice> subtitles)
    {
        var match = Match(subtitles.Select(s => new MpvTrack(s.Index, "sub", s.Title, s.Language, false, s.IsDefault, s.IsForced)).ToList());
        return match is null ? null : subtitles.First(s => s.Index == match.Id);
    }

    public static MpvTrack? MatchExact(SubtitleSelectionRequest request, int generation, IReadOnlyList<MpvTrack> tracks)
        => request.Generation != generation || request.Off ? null : tracks.FirstOrDefault(t => t.Type == "sub" && t.Id == request.TrackId
            && string.Equals(t.Lang, request.Lang, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.Title, request.Title, StringComparison.OrdinalIgnoreCase) && t.Forced == request.Forced);
}
