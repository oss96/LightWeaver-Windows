using System.Globalization;
using System.Text;
using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Localization;

namespace LightWeaver.Details;

/// <summary>One label and value line of the media info view.</summary>
public sealed record MediaInfoLine(string Label, string Value);

/// <summary>A titled block of the media info view: the file's facts or one stream.</summary>
public sealed record MediaInfoSection(string Title, IReadOnlyList<MediaInfoLine> Lines);

/// <summary>One version (media source) of a title: its file block, then a block per stream.</summary>
public sealed record MediaInfoVersion(string Name, IReadOnlyList<MediaInfoSection> Sections);

/// <summary>The technical media info of a title's files as Jellyfin reports it: container, size
/// and bitrate, then every video, audio and subtitle stream of each version.</summary>
public static class MediaInfoModel
{
    public static IReadOnlyList<MediaInfoVersion> Build(IReadOnlyList<MediaSourceInfo> sources)
    {
        var versions = new List<MediaInfoVersion>();
        foreach (var source in sources)
        {
            var name = source.Name is { Length: > 0 } n ? n : L.Format("MediaInfo.Version", versions.Count + 1);
            var streams = source.MediaStreams ?? [];
            var sections = new List<MediaInfoSection> { new(L.Text("MediaInfo.File"), FileLines(source)) };
            sections.AddRange(StreamSections(streams, MediaStream_Type.Video, L.Text("MediaInfo.Video"), n => L.Format("MediaInfo.VideoNumbered", n), VideoLines));
            sections.AddRange(StreamSections(streams, MediaStream_Type.Audio, L.Text("MediaInfo.Audio"), n => L.Format("MediaInfo.AudioNumbered", n), AudioLines));
            sections.AddRange(StreamSections(streams, MediaStream_Type.Subtitle, L.Text("MediaInfo.Subtitle"), n => L.Format("MediaInfo.SubtitleNumbered", n), SubtitleLines));
            versions.Add(new MediaInfoVersion(name, [.. sections.Where(s => s.Lines.Count > 0)]));
        }
        return versions;
    }

    /// <summary>The view as plain text for the clipboard; version names only when there are several.</summary>
    public static string AsText(string title, IReadOnlyList<MediaInfoVersion> versions)
    {
        var text = new StringBuilder(title).AppendLine();
        foreach (var version in versions)
        {
            if (versions.Count > 1) text.AppendLine().AppendLine(version.Name);
            foreach (var section in version.Sections)
            {
                text.AppendLine().AppendLine(section.Title);
                foreach (var line in section.Lines) text.Append("  ").Append(line.Label).Append(": ").AppendLine(line.Value);
            }
        }
        return text.ToString();
    }

    /// <summary>"7.54 GB", or "700 MB" under a gigabyte.</summary>
    public static string Size(long bytes)
        => bytes >= 1L << 30 ? Invariant($"{bytes / (double)(1L << 30):0.00} GB") : Invariant($"{Math.Max(1, bytes >> 20)} MB");

    /// <summary>"12.4 Mbps", or "640 kbps" under a megabit.</summary>
    public static string Bitrate(int bitsPerSecond)
        => bitsPerSecond >= 1_000_000 ? Invariant($"{bitsPerSecond / 1_000_000.0:0.0} Mbps") : Invariant($"{bitsPerSecond / 1000} kbps");

    /// <summary>The range type as people know it: SDR, HDR10, HDR10+, HLG, Dolby Vision and its fallbacks.</summary>
    public static string? RangeType(string? rangeType) => rangeType?.ToUpperInvariant() switch
    {
        null or "" or "UNKNOWN" => null,
        "HDR10PLUS" => "HDR10+",
        "DOVI" => "Dolby Vision",
        "DOVIWITHHDR10" => "Dolby Vision / HDR10",
        "DOVIWITHHDR10PLUS" => "Dolby Vision / HDR10+",
        "DOVIWITHHLG" => "Dolby Vision / HLG",
        "DOVIWITHSDR" => "Dolby Vision / SDR",
        "DOVIWITHEL" => "Dolby Vision (EL)",
        "DOVIWITHELHDR10PLUS" => "Dolby Vision (EL) / HDR10+",
        "DOVIINVALID" => "Dolby Vision (invalid)",
        _ => rangeType,
    };

    private static List<MediaInfoLine> FileLines(MediaSourceInfo source)
    {
        var lines = new List<MediaInfoLine>();
        Add(lines, L.Text("MediaInfo.Container"), source.Container?.ToUpperInvariant());
        Add(lines, L.Text("MediaInfo.Path"), source.Path);
        Add(lines, L.Text("MediaInfo.Size"), source.Size is > 0 ? Size(source.Size.Value) : null);
        Add(lines, L.Text("MediaInfo.Bitrate"), source.Bitrate is > 0 ? Bitrate(source.Bitrate.Value) : null);
        return lines;
    }

    private static IEnumerable<MediaInfoSection> StreamSections(List<MediaStream> streams, MediaStream_Type type, string single, Func<int, string> numbered,
        Func<MediaStream, List<MediaInfoLine>> lines)
    {
        var ofType = streams.Where(s => s.Type == type).ToList();
        return ofType.Select((stream, i) => new MediaInfoSection(ofType.Count == 1 ? single : numbered(i + 1), lines(stream)));
    }

    private static List<MediaInfoLine> VideoLines(MediaStream s)
    {
        var lines = new List<MediaInfoLine>();
        Add(lines, L.Text("MediaInfo.Title"), s.DisplayTitle ?? s.Title);
        Add(lines, L.Text("MediaInfo.Codec"), s.Codec?.ToUpperInvariant());
        Add(lines, L.Text("MediaInfo.Profile"), s.Profile);
        Add(lines, L.Text("MediaInfo.Level"), s.Level is > 0 ? Invariant($"{s.Level.Value}") : null);
        Add(lines, L.Text("MediaInfo.Resolution"), s is { Width: > 0, Height: > 0 } ? Invariant($"{s.Width}x{s.Height}") : null);
        Add(lines, L.Text("MediaInfo.AspectRatio"), s.AspectRatio);
        Add(lines, L.Text("MediaInfo.Interlaced"), YesNo(s.IsInterlaced));
        Add(lines, L.Text("MediaInfo.FrameRate"), (s.RealFrameRate ?? s.AverageFrameRate) is > 0 and var fps ? Invariant($"{fps:0.###} fps") : null);
        Add(lines, L.Text("MediaInfo.Bitrate"), s.BitRate is > 0 ? Bitrate(s.BitRate.Value) : null);
        Add(lines, L.Text("MediaInfo.BitDepth"), s.BitDepth is > 0 ? Invariant($"{s.BitDepth} bit") : null);
        Add(lines, L.Text("MediaInfo.VideoRange"), RangeType(s.VideoRangeType?.ToString()) ?? s.VideoRange?.ToString());
        Add(lines, L.Text("MediaInfo.DolbyVision"), s.VideoDoViTitle);
        Add(lines, L.Text("MediaInfo.ColorSpace"), s.ColorSpace);
        Add(lines, L.Text("MediaInfo.ColorTransfer"), s.ColorTransfer);
        Add(lines, L.Text("MediaInfo.ColorPrimaries"), s.ColorPrimaries);
        Add(lines, L.Text("MediaInfo.PixelFormat"), s.PixelFormat);
        return lines;
    }

    private static List<MediaInfoLine> AudioLines(MediaStream s)
    {
        var lines = new List<MediaInfoLine>();
        Add(lines, L.Text("MediaInfo.Title"), s.DisplayTitle ?? s.Title);
        Add(lines, L.Text("MediaInfo.Language"), s.Language);
        Add(lines, L.Text("MediaInfo.Codec"), s.Codec?.ToUpperInvariant());
        Add(lines, L.Text("MediaInfo.Profile"), s.Profile);
        Add(lines, L.Text("MediaInfo.ChannelLayout"), s.ChannelLayout);
        Add(lines, L.Text("MediaInfo.Channels"), s.Channels is > 0 ? s.Channels.Value.ToString(CultureInfo.InvariantCulture) : null);
        Add(lines, L.Text("MediaInfo.Bitrate"), s.BitRate is > 0 ? Bitrate(s.BitRate.Value) : null);
        Add(lines, L.Text("MediaInfo.SampleRate"), s.SampleRate is > 0 ? Invariant($"{s.SampleRate.Value / 1000.0:0.#} kHz") : null);
        Add(lines, L.Text("MediaInfo.BitDepth"), s.BitDepth is > 0 ? Invariant($"{s.BitDepth} bit") : null);
        Add(lines, L.Text("MediaInfo.SpatialAudio"), SpatialFormat(s.AudioSpatialFormat?.ToString()));
        Add(lines, L.Text("MediaInfo.Default"), YesNo(s.IsDefault));
        Add(lines, L.Text("MediaInfo.Forced"), YesNo(s.IsForced));
        Add(lines, L.Text("MediaInfo.External"), YesNo(s.IsExternal));
        return lines;
    }

    private static List<MediaInfoLine> SubtitleLines(MediaStream s)
    {
        var lines = new List<MediaInfoLine>();
        Add(lines, L.Text("MediaInfo.Title"), s.DisplayTitle ?? s.Title);
        Add(lines, L.Text("MediaInfo.Language"), s.Language);
        Add(lines, L.Text("MediaInfo.Codec"), s.Codec?.ToUpperInvariant());
        Add(lines, L.Text("MediaInfo.Default"), YesNo(s.IsDefault));
        Add(lines, L.Text("MediaInfo.Forced"), YesNo(s.IsForced));
        Add(lines, L.Text("MediaInfo.External"), YesNo(s.IsExternal));
        Add(lines, L.Text("MediaInfo.HearingImpaired"), s.IsHearingImpaired == true ? YesNo(true) : null);
        return lines;
    }

    private static string? SpatialFormat(string? format) => format?.ToUpperInvariant() switch
    {
        "DOLBYATMOS" => "Dolby Atmos",
        "DTSX" => "DTS:X",
        _ => null,
    };

    private static string? YesNo(bool? value) => value is { } v ? v ? L.Text("MediaInfo.Yes") : L.Text("MediaInfo.No") : null;

    private static void Add(List<MediaInfoLine> lines, string label, string? value)
    {
        if (value is { Length: > 0 }) lines.Add(new MediaInfoLine(label, value));
    }

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);
}
