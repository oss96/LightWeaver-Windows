namespace LightWeaver.Jellyfin;

/// <summary>What an inbound <c>Playstate</c> or <c>GeneralCommand</c> asks the player to do.
/// <see cref="Noop"/> covers everything this client does not act on — an unknown name, a command
/// whose argument is missing or unreadable, and the two Play commands that are deliberately not
/// advertised.</summary>
public enum RemoteActionKind
{
    Noop,
    Pause,
    Unpause,
    PlayPause,
    Stop,
    Seek,
    NextTrack,
    PreviousTrack,
    FastForward,
    Rewind,
    SetVolume,
    VolumeUp,
    VolumeDown,
    Mute,
    Unmute,
    ToggleMute,
    ToggleFullscreen,
    DisplayMessage,
    PlayNext,
    SetAudioStreamIndex,
    SetSubtitleStreamIndex,
}

/// <summary>Which remote queue operation a <c>Play</c> frame asks for.</summary>
public enum RemotePlayKind
{
    /// <summary>An unrecognised, absent or unsupported <c>PlayCommand</c>.</summary>
    Noop,
    PlayNow,
    PlayNext,
    PlayLast,
}

/// <summary>What a remote <c>SetAudioStreamIndex</c> / <c>SetSubtitleStreamIndex</c> resolved to
/// against the source that is playing.</summary>
public enum RemoteStreamKind
{
    /// <summary>Jellyfin's "no subtitles". There is no stream row for it, and it means the same
    /// thing in every source.</summary>
    Off,
    /// <summary>The index named a stream of the playing source; <c>Choice</c> carries it.</summary>
    Stream,
    /// <summary>Nothing in the playing source's list has that index.</summary>
    Unknown,
    /// <summary>The projection in hand describes a DIFFERENT source than the one playing, so the
    /// index cannot be resolved at all. Its own outcome rather than <see cref="Unknown"/> because
    /// the cause is structural and the log has to say so: nothing is missing from the server's
    /// answer, it is an answer about the wrong file.</summary>
    SourceMismatch,
}

/// <summary>A resolved stream-index command.</summary>
public sealed record RemoteStreamTarget(RemoteStreamKind Kind, MediaStreamChoice? Choice = null);

/// <summary>A resolved command: the thing to do plus whatever the argument bag carried for it.
/// <see cref="Number"/> is seconds for <see cref="RemoteActionKind.Seek"/>, a 0-100 level for
/// <see cref="RemoteActionKind.SetVolume"/>, and a Jellyfin stream index for the two
/// <c>Set*StreamIndex</c> commands.</summary>
public sealed record RemoteAction(RemoteActionKind Kind, double Number = 0,
    string? Header = null, string? Text = null)
{
    public static readonly RemoteAction Noop = new(RemoteActionKind.Noop);
}

/// <summary>Turns a remote message into the action it names, and nothing else. Pure and static so
/// the mapping can be asserted without a window, a player or a socket — the handlers in
/// <c>MainWindow.RemoteControl</c> are then a switch with no parsing left in it.</summary>
public static class RemoteCommand
{
    /// <summary>A transport command. Jellyfin's <c>Play</c> and <c>Unpause</c> are the same
    /// request; only the latter is current, but servers and plugins still send both.</summary>
    public static RemoteAction Resolve(PlaystateRequestMessage? message) => message?.Command switch
    {
        "Pause" => new RemoteAction(RemoteActionKind.Pause),
        "Unpause" or "Play" => new RemoteAction(RemoteActionKind.Unpause),
        "PlayPause" => new RemoteAction(RemoteActionKind.PlayPause),
        "Stop" => new RemoteAction(RemoteActionKind.Stop),
        // Ticks to seconds here rather than at the call site: it is the one unit conversion in the
        // whole path, and the thing a wrong seek would be blamed on.
        "Seek" => message.SeekPositionTicks is { } ticks
            ? new RemoteAction(RemoteActionKind.Seek, ticks / (double)TimeSpan.TicksPerSecond)
            : RemoteAction.Noop,
        "NextTrack" => new RemoteAction(RemoteActionKind.NextTrack),
        "PreviousTrack" => new RemoteAction(RemoteActionKind.PreviousTrack),
        "FastForward" => new RemoteAction(RemoteActionKind.FastForward),
        "Rewind" => new RemoteAction(RemoteActionKind.Rewind),
        _ => RemoteAction.Noop,
    };

    /// <summary>A general command. Every numeric argument arrives as a JSON string and a
    /// missing or unparseable one resolves to <see cref="RemoteAction.Noop"/> rather than to a
    /// default: a <c>SetVolume</c> with no <c>Volume</c> key silently becoming
    /// <c>SetVolume(0)</c> would be a remote mute nobody asked for.</summary>
    public static RemoteAction Resolve(GeneralCommandMessage? message) => message?.Name switch
    {
        "SetVolume" => message.IntArgument("Volume") is { } volume
            ? new RemoteAction(RemoteActionKind.SetVolume, Math.Clamp(volume, 0, 100))
            : RemoteAction.Noop,
        "VolumeUp" => new RemoteAction(RemoteActionKind.VolumeUp),
        "VolumeDown" => new RemoteAction(RemoteActionKind.VolumeDown),
        "Mute" => new RemoteAction(RemoteActionKind.Mute),
        "Unmute" => new RemoteAction(RemoteActionKind.Unmute),
        "ToggleMute" => new RemoteAction(RemoteActionKind.ToggleMute),
        "ToggleFullscreen" => new RemoteAction(RemoteActionKind.ToggleFullscreen),
        "PlayNext" => new RemoteAction(RemoteActionKind.PlayNext),
        "DisplayMessage" => new RemoteAction(RemoteActionKind.DisplayMessage, 0,
            message.Argument("Header"), message.Argument("Text")),
        "SetAudioStreamIndex" => message.IntArgument("Index") is { } audio
            ? new RemoteAction(RemoteActionKind.SetAudioStreamIndex, audio)
            : RemoteAction.Noop,
        "SetSubtitleStreamIndex" => message.IntArgument("Index") is { } subtitle
            ? new RemoteAction(RemoteActionKind.SetSubtitleStreamIndex, subtitle)
            : RemoteAction.Noop,
        _ => RemoteAction.Noop,
    };

    /// <summary>A <c>Play</c> frame's queue command. <c>PlayInstantMix</c> and <c>PlayShuffle</c>
    /// resolve to <see cref="RemotePlayKind.Noop"/> on purpose: neither is advertised in the
    /// client's capabilities, so a server has no reason to send one.</summary>
    public static RemotePlayKind ResolvePlay(string? playCommand) => playCommand switch
    {
        "PlayNow" => RemotePlayKind.PlayNow,
        "PlayNext" => RemotePlayKind.PlayNext,
        "PlayLast" => RemotePlayKind.PlayLast,
        _ => RemotePlayKind.Noop,
    };

    /// <summary>What a resolved play command can actually do here.
    ///
    /// <para>Queueing needs something to queue onto: with nothing playing, "play next" can only
    /// mean "play", and the alternative is a queue that fills up while the screen stays on the
    /// browser.</para>
    ///
    /// <para>And it has to queue onto the RIGHT playback. A PlayNow from a backgrounded profile is
    /// a cast and stays valid — it carries its own session all the way through. A PlayNext or
    /// PlayLast cannot: there is one queue and one playback session, so the entry would be
    /// negotiated and progress-reported under whichever account is playing. The other user's watch
    /// state would go missing, this one's history would gain something nobody played, and across
    /// two servers the item id means nothing at all. Refused rather than fixed by carrying a
    /// session per queue entry: the queue is also the in-app one, and a second owner in it would
    /// have to be threaded through every advance, Up Next and episode step for a case the
    /// dashboard can express but nobody asks for.</para></summary>
    /// <param name="playing">Whether anything is playing right now. Note this is NOT "a queue is
    /// playing": a film started from the detail view clears the queue, so gating on the queue
    /// turned every dashboard "play next" during a single item into a "play now" that stopped the
    /// film it was meant to follow. Whether a queue exists to insert into is the caller's problem
    /// — it can seed one from the playing item.</param>
    /// <param name="ownsPlayback">Whether the profile the command arrived on is the one the live
    /// playback belongs to.</param>
    public static RemotePlayKind ResolvePlayTarget(RemotePlayKind kind, bool playing,
        bool ownsPlayback)
    {
        if (kind is RemotePlayKind.Noop or RemotePlayKind.PlayNow)
            return kind;
        if (!playing)
            return RemotePlayKind.PlayNow;
        return ownsPlayback ? kind : RemotePlayKind.Noop;
    }

    /// <summary>Resolves a remote stream index against the source that is PLAYING.
    ///
    /// <para>A stream index is an ordinal INSIDE one media source, and the dashboard's track list
    /// comes from the source this client is playing. The projection in hand does not have to be
    /// that source: <c>GetMediaStreamsAsync</c> answers for the item's DEFAULT one, so a movie
    /// whose 4K remux is playing — picked in the version list, or remembered by
    /// <c>MediaVersionStore</c> — is described here by the 1080p file. Reading index 3 off that
    /// list took the 1080p file's third stream and selected the remux's track of the same ordinal,
    /// which is a different track whenever the two containers order their streams differently, and
    /// logged it as a success. When the default had fewer streams the command disappeared as
    /// <see cref="RemoteStreamKind.Unknown"/> instead.</para>
    ///
    /// <para>So a mismatch resolves to nothing at all. Renegotiating the raw index against the
    /// playing source was the alternative and is not better: on direct play the server answers
    /// with the same static URL and the track never changes, and on a reload the stand-in track
    /// built from an index with no ordinal, language or title is matched by
    /// <c>RestoreQualityTracks</c> against the first row whose fields are also empty — the same
    /// guess under another name. The projection has no per-source form to ask for; until it does,
    /// the honest answer is that this cannot be resolved.</para></summary>
    /// <param name="streams">The stream projection in hand, or null when the fetch failed.</param>
    /// <param name="playingSourceId">The live decision's media source id, or null when nothing
    /// says which source is playing — in which case the projection is taken at its word, as
    /// <c>CaptureQualityTrackSnapshot</c> does.</param>
    public static RemoteStreamTarget ResolveStream(MediaSourceStreams? streams,
        string? playingSourceId, bool audio, int index)
    {
        // Jellyfin's "no subtitles" is a negative index, there is no stream row for it, and it
        // means the same thing in every source — so it survives a mismatch.
        if (!audio && index < 0)
            return new RemoteStreamTarget(RemoteStreamKind.Off);
        if (streams is null)
            return new RemoteStreamTarget(RemoteStreamKind.Unknown);
        if (playingSourceId is { Length: > 0 }
            && !string.Equals(playingSourceId, streams.SourceId, StringComparison.Ordinal))
            return new RemoteStreamTarget(RemoteStreamKind.SourceMismatch);
        var choice = (audio ? streams.Audio : streams.Subtitles)
            .FirstOrDefault(stream => stream.Index == index);
        return choice is null
            ? new RemoteStreamTarget(RemoteStreamKind.Unknown)
            : new RemoteStreamTarget(RemoteStreamKind.Stream, choice);
    }
}
