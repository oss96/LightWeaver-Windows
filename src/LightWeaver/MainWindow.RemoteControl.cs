using System.Windows;
using LightWeaver.Jellyfin;
using LightWeaver.Player;
using LightWeaver.ViewModels;

namespace LightWeaver;

/// <summary>
/// What the app DOES with the live session socket: the only file that knows both the socket and
/// the player. The transport, the message shapes and the command-to-action mapping are all
/// elsewhere (<see cref="SessionSocket"/>, <see cref="SessionMessageReader"/>,
/// <see cref="RemoteCommand"/>), so nothing here parses anything — it marshals, guards, and calls
/// the method the in-app control would have called.
/// </summary>
public partial class MainWindow
{
    /// <summary>Above this many rows a <c>UserDataChanged</c> batch stops being fanned out item by
    /// item. A library scan or a "mark this season watched" produces hundreds, and one GET each is
    /// a burst against the server for a repaint a single reload already covers.</summary>
    private const int MaxUserDataFanOut = 50;

    /// <summary>Records every inbound message on the receive loop, then acts on it through the
    /// dispatcher. <see cref="System.Windows.Threading.Dispatcher.BeginInvoke(Delegate)"/> and
    /// never <c>Invoke</c>: <see cref="SessionSocket.Dispose"/> joins that loop for up to a second
    /// and the join runs on the UI thread at logout and at exit, so a loop blocked on the
    /// dispatcher would deadlock both.
    ///
    /// <para>The log line stays on the loop thread on purpose. It is the record that a command
    /// ARRIVED, and a window that is busy, closing or gone must not be able to lose it.</para></summary>
    private void AttachRemoteControl()
    {
        _live.PlayRequested += (key, message) =>
        {
            LogRemote("play", key, FormattableString.Invariant(
                $"items={message.ItemIds?.Count ?? 0} command={RemoteToken(message.PlayCommand)} start_ticks={message.StartPositionTicks ?? 0}"));
            Dispatcher.BeginInvoke(() => OnRemotePlay(key, message));
        };
        _live.PlaystateRequested += (key, message) =>
        {
            LogRemote("playstate", key, FormattableString.Invariant(
                $"command={RemoteToken(message.Command)} seek_ticks={message.SeekPositionTicks ?? 0}"));
            Dispatcher.BeginInvoke(() => OnRemotePlaystate(key, message));
        };
        _live.GeneralCommandReceived += (key, message) =>
        {
            LogRemote("general", key, $"command={RemoteToken(message.Name)}");
            Dispatcher.BeginInvoke(() => OnRemoteGeneralCommand(key, message));
        };
        _live.LibraryChanged += (key, message) =>
        {
            LogRemoteDetail("library", key, FormattableString.Invariant(
                $"items={message.AffectedItemIds().Count} folders={message.AffectedFolderIds().Count}"));
            Dispatcher.BeginInvoke(() => OnRemoteLibraryChanged(key, message));
        };
        _live.UserDataChanged += (key, message) =>
        {
            LogRemoteDetail("user_data", key, FormattableString.Invariant(
                $"entries={message.UserDataList?.Count ?? 0}"));
            Dispatcher.BeginInvoke(() => OnRemoteUserDataChanged(key, message));
        };
        // Log and stop. Nothing here may mint or re-authorise a token: the socket has already
        // given up for good, and the warm-session revalidation path owns expiry and the sign-in
        // prompt. No dispatcher hop, because this touches nothing but the log.
        _live.AuthenticationRejected += key => LogRemote("rejected", key, "outcome=stopped");
    }

    // ---- Transport ------------------------------------------------------------------

    private void OnRemotePlaystate(string profileKey, PlaystateRequestMessage message)
    {
        var action = RemoteCommand.Resolve(message);
        if (action.Kind == RemoteActionKind.Noop)
        {
            RemoteNoop("playstate", profileKey, "unsupported_command");
            return;
        }
        if (!RemotePlayerReady("playstate", profileKey))
            return;
        switch (action.Kind)
        {
            case RemoteActionKind.Pause: _playerViewModel.IsPaused = true; break;
            case RemoteActionKind.Unpause: _playerViewModel.IsPaused = false; break;
            case RemoteActionKind.PlayPause: _playerViewModel.TogglePauseCommand.Execute(null); break;
            case RemoteActionKind.Stop: RequestStop("remote"); break;
            case RemoteActionKind.Seek: _playerViewModel.Seek(action.Number); break;
            case RemoteActionKind.NextTrack: _playerViewModel.GoNext(); break;
            case RemoteActionKind.PreviousTrack: _playerViewModel.GoPrevious(); break;
            case RemoteActionKind.FastForward: _playerViewModel.JumpForwardLarge(); break;
            case RemoteActionKind.Rewind: _playerViewModel.JumpBackLarge(); break;
        }
    }

    private void OnRemoteGeneralCommand(string profileKey, GeneralCommandMessage message)
    {
        var action = RemoteCommand.Resolve(message);
        if (action.Kind == RemoteActionKind.Noop)
        {
            RemoteNoop("general", profileKey, "unsupported_command");
            return;
        }
        // Ahead of the playing guard: a toast is shell chrome, and a dashboard message sent to a
        // client that happens to be browsing should still arrive.
        if (action.Kind == RemoteActionKind.DisplayMessage)
        {
            ShowRemoteMessage(profileKey, action);
            return;
        }
        if (!RemotePlayerReady("general", profileKey))
            return;
        switch (action.Kind)
        {
            case RemoteActionKind.SetVolume: _playerViewModel.Volume = action.Number; break;
            case RemoteActionKind.VolumeUp:
                PlayerActionDispatcher.Dispatch(PlayerAction.VolumeUp, _playerViewModel); break;
            case RemoteActionKind.VolumeDown:
                PlayerActionDispatcher.Dispatch(PlayerAction.VolumeDown, _playerViewModel); break;
            case RemoteActionKind.Mute: SetRemoteMute(true); break;
            case RemoteActionKind.Unmute: SetRemoteMute(false); break;
            case RemoteActionKind.ToggleMute: SetRemoteMute(!_playerViewModel.IsMuted); break;
            case RemoteActionKind.ToggleFullscreen: _playerViewModel.RequestFullscreen(); break;
            case RemoteActionKind.PlayNext: _playerViewModel.GoNext(); break;
            case RemoteActionKind.SetAudioStreamIndex:
                _ = ApplyRemoteStreamIndexAsync(profileKey, audio: true, (int)action.Number); break;
            case RemoteActionKind.SetSubtitleStreamIndex:
                _ = ApplyRemoteStreamIndexAsync(profileKey, audio: false, (int)action.Number); break;
        }
    }

    /// <summary>mpv owns the mute flag and reports it back asynchronously, so an ABSOLUTE Mute or
    /// Unmute has to be compared against the current state rather than toggled. Toggling blindly
    /// turns a second Mute from the dashboard into an unmute, which is the one thing the person
    /// pressing it cannot have meant.</summary>
    private void SetRemoteMute(bool muted)
    {
        if (_playerViewModel.IsMuted != muted)
            _playerViewModel.ToggleMute();
    }

    private void ShowRemoteMessage(string profileKey, RemoteAction action)
    {
        if (_windowClosing)
        {
            RemoteNoop("general", profileKey, "closing");
            return;
        }
        // Both halves are optional and the server sends either alone. Joined rather than stacked:
        // the toast is one line of text.
        var parts = new[] { action.Header, action.Text }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim());
        var text = string.Join(" — ", parts);
        if (text.Length == 0)
        {
            RemoteNoop("general", profileKey, "empty_message");
            return;
        }
        ShowToast(text);
    }

    // ---- Remote track selection -----------------------------------------------------

    /// <summary>Applies a remote <c>SetAudioStreamIndex</c> / <c>SetSubtitleStreamIndex</c>. The
    /// index is Jellyfin's and belongs to ONE media source, so it is resolved against the source
    /// that is playing — not the item's default, which is the only one the stream projection can
    /// describe. When those are not the same file the command is refused rather than guessed at;
    /// <see cref="RemoteCommand.ResolveStream"/> carries that reasoning.
    ///
    /// <para>Direct play keeps every embedded stream in the file, so the pick is an mpv track
    /// change and needs no renegotiation — the mapping is the exact inverse of the one
    /// <see cref="CaptureQualityTrackSnapshot(IReadOnlyList{MpvTrack}, PlaybackDecision?,
    /// MediaSourceStreams?, QualityTrackSnapshot?, bool?)"/> uses, because mpv numbers each type
    /// from 1 in file order. It goes through the view model rather than the player so the
    /// AudioChosen / SubtitleChosen bookkeeping still runs.</para>
    ///
    /// <para>Anything else — a transcode, an external stream, a track mpv never exposed — needs
    /// the server to produce a different stream, which is what the version and quality switch
    /// already do.</para></summary>
    private async Task ApplyRemoteStreamIndexAsync(string profileKey, bool audio, int index)
    {
        if (_playingItem is not { } item)
        {
            RemoteNoop("general", profileKey, "not_playing");
            return;
        }
        // A completed download plays from disk with no server behind it, so there is no stream
        // list to resolve against and nothing to renegotiate. Named here rather than left to
        // ApplyStreamChangeAsync, whose first guard returns before it has logged anything.
        if (_playingLocal)
        {
            RemoteNoop("general", profileKey, "local_playback");
            return;
        }
        var jf = _playbackJf ?? _app.Jellyfin;
        MediaSourceStreams? streams = null;
        try
        {
            streams = await jf.GetMediaStreamsAsync(item.Id);
        }
        catch (Exception ex)
        {
            // By type: an SDK failure can carry the request URL.
            LogRemoteDetail("general", profileKey, $"outcome=failure reason=streams error={ex.GetType().Name}");
        }
        if (_windowClosing || _app.State != AppState.Playing || _playingItem?.Id != item.Id)
        {
            RemoteNoop("general", profileKey, "stale");
            return;
        }

        // Against the PLAYING source, never the item's default — GetMediaStreamsAsync answers for
        // the latter. See RemoteCommand.ResolveStream for what a mismatch costs.
        var target = RemoteCommand.ResolveStream(streams, _playbackDecision?.MediaSourceId,
            audio, index);
        if (target.Kind == RemoteStreamKind.Unknown)
        {
            RemoteNoop("general", profileKey, "unknown_stream");
            return;
        }
        if (target.Kind == RemoteStreamKind.SourceMismatch)
        {
            // Named apart from unknown_stream on purpose: this is the one outcome a reader would
            // otherwise blame on the server. The command was fine, the projection describes the
            // item's default source, and the file playing is a different one.
            RemoteNoop("general", profileKey, "source_mismatch");
            return;
        }
        var off = target.Kind == RemoteStreamKind.Off;
        var choice = target.Choice;

        if (_playbackDecision is { IsTranscode: false }
            && TrySelectRemoteTrack(audio, off, choice))
        {
            LogRemote("general", profileKey, FormattableString.Invariant(
                $"outcome=success mode=track kind={(audio ? "audio" : "subtitle")} index={index}"));
            return;
        }

        var pick = audio
            ? new ForcedStreamPick(choice, null, SubtitleOff: false)
            : new ForcedStreamPick(null, off ? null : choice, off);
        var applied = await ApplyStreamChangeAsync(
            new QualityRequest(_overrideMaxBitrateMbps, _overrideForceTranscode),
            newMediaSourceId: null,
            off ? "no subtitles" : choice!.Display,
            audio ? "audio track" : "subtitles",
            (outcome, sequence) => RemoteStreamDetail(outcome, audio, index, sequence),
            pick);
        // Every command records an outcome. ApplyStreamChangeAsync writes its own verbose detail
        // line for the outcomes it reaches, but it also has guards that return before the first
        // one — so without this a command could end in silence.
        if (applied)
            LogRemote("general", profileKey, FormattableString.Invariant(
                $"outcome=success mode=negotiate kind={(audio ? "audio" : "subtitle")} index={index}"));
        else
            RemoteNoop("general", profileKey, "not_applied");
    }

    /// <summary>Selects the mpv track the source stream maps onto, or false when there is none to
    /// select. False also means "renegotiate": an external stream has no embedded ordinal, and a
    /// track the file has but mpv did not list cannot be asked for by id.</summary>
    private bool TrySelectRemoteTrack(bool audio, bool off, MediaStreamChoice? choice)
    {
        if (off)
        {
            if (_playerViewModel.SubtitleTracks.FirstOrDefault(track => track.Id < 0) is not { } none)
                return false;
            _playerViewModel.SelectSubtitle(none);
            return true;
        }
        if (choice is not { IsExternal: false, TypeOrdinal: >= 0 })
            return false;
        var options = audio ? _playerViewModel.AudioTracks : _playerViewModel.SubtitleTracks;
        if (options.FirstOrDefault(track => track.Id == choice.TypeOrdinal + 1) is not { } option)
            return false;
        if (audio)
            _playerViewModel.SelectAudio(option);
        else
            _playerViewModel.SelectSubtitle(option);
        return true;
    }

    private static void RemoteStreamDetail(string outcome, bool audio, int index, int requestSequence)
    {
        if (!Diagnostics.AppLog.Verbose)
            return;
        Diagnostics.AppLog.Detail("player", FormattableString.Invariant(
            $"event=remote_stream_change outcome={outcome} request={requestSequence} kind={(audio ? "audio" : "subtitle")} index={index}"));
    }

    // ---- Play ------------------------------------------------------------------------

    private void OnRemotePlay(string profileKey, PlayRequestMessage message)
    {
        var kind = RemoteCommand.ResolvePlay(message.PlayCommand);
        if (kind == RemotePlayKind.Noop)
        {
            // PlayInstantMix and PlayShuffle land here. Neither is advertised, so a server has no
            // reason to send one.
            RemoteNoop("play", profileKey, "unsupported_command");
            return;
        }
        if (_windowClosing)
        {
            RemoteNoop("play", profileKey, "closing");
            return;
        }
        if (message.ItemIds is not { Count: > 0 })
        {
            RemoteNoop("play", profileKey, "no_items");
            return;
        }
        // The profile that was cast to, not the active one: a backgrounded profile stays a valid
        // "play on" target, and its items must be fetched and played against its own session.
        if (_app.FindSessionByKey(profileKey) is not { IsConnected: true } jf)
        {
            RemoteNoop("play", profileKey, "no_session");
            return;
        }
        _ = StartRemotePlayAsync(profileKey, kind, message, jf);
    }

    private async Task StartRemotePlayAsync(string profileKey, RemotePlayKind kind,
        PlayRequestMessage message, JellyfinService jf)
    {
        var items = new List<MediaItem>();
        foreach (var id in message.ItemIds!)
        {
            try
            {
                if (await jf.GetItemAsync(id) is { IsPlayable: true } item)
                    items.Add(item);
            }
            catch (Exception ex)
            {
                LogRemoteDetail("play", profileKey, $"outcome=failure reason=lookup error={ex.GetType().Name}");
            }
        }
        if (_windowClosing)
        {
            RemoteNoop("play", profileKey, "closing");
            return;
        }
        if (items.Count == 0)
        {
            RemoteNoop("play", profileKey, "no_playable_items");
            return;
        }
        // Against _playbackJf rather than the ACTIVE session, because those come apart in both
        // directions: a cast from a backgrounded profile plays under that profile's session while
        // another is active, and the queue belongs to whichever one the playback does.
        kind = RemoteCommand.ResolvePlayTarget(kind,
            playing: _app.State == AppState.Playing && _playingItem is not null,
            ownsPlayback: ReferenceEquals(jf, _playbackJf ?? _app.Jellyfin));
        if (kind == RemotePlayKind.Noop)
        {
            RemoteNoop("play", profileKey, "not_playback_session");
            return;
        }

        switch (kind)
        {
            case RemotePlayKind.PlayNow:
                var start = Math.Clamp(message.StartIndex ?? 0, 0, items.Count - 1);
                if (SubmitSyncPlayQueue(items, start, message.StartPositionTicks ?? 0, jf)) return;
                _app.Queue.Set(items, start);
                ActivateForRemotePlay();
                PlayItem(items[start], message.StartPositionTicks ?? 0, fromQueue: true, session: jf);
                break;
            case RemotePlayKind.PlayNext:
                if (_syncPlay.IsInGroup) { ShowToast("Leave the group to append remote queue items."); return; }
                SeedQueueFromPlayingItem();
                _app.Queue.InsertNext(items);
                ShowToast(items.Count == 1 ? $"Playing next: {items[0].Name}" : $"Queued {items.Count} items to play next");
                break;
            case RemotePlayKind.PlayLast:
                if (_syncPlay.IsInGroup) { ShowToast("Leave the group to append remote queue items."); return; }
                SeedQueueFromPlayingItem();
                _app.Queue.Append(items);
                ShowToast(items.Count == 1 ? $"Added to queue: {items[0].Name}" : $"Added {items.Count} items to the queue");
                break;
        }
        LogRemote("play", profileKey, FormattableString.Invariant(
            $"outcome=success command={kind} items={items.Count}"));
    }

    /// <summary>Gives a remote "play next" / "play last" something to queue onto. A single item
    /// started from the detail view clears the queue (<c>PlayItem</c> with fromQueue false), so
    /// without this the queue is empty while a film is playing and the inserted entry would become
    /// the whole queue with CurrentIndex on it — i.e. it would start immediately and stop the
    /// film. Seeding with the playing item first makes "next" mean what it says.</summary>
    private void SeedQueueFromPlayingItem()
    {
        if (!_app.Queue.IsActive && _playingItem is { } playing)
            _app.Queue.Set([playing], 0);
    }

    /// <summary>A remote PlayNow makes this the screen the user is looking at. That is the point
    /// of being a cast target and it is what every other Jellyfin client does — a minimised window
    /// that starts playing where nobody can see it is indistinguishable from one that ignored the
    /// command. Restore before activate: <see cref="Window.Activate"/> on a minimised window
    /// leaves it minimised, and SC_RESTORE is what returns a maximised window to maximised rather
    /// than to normal.</summary>
    private void ActivateForRemotePlay()
    {
        if (WindowState == WindowState.Minimized)
            SystemCommands.RestoreWindow(this);
        Activate();
    }

    // ---- Library and user data --------------------------------------------------------

    private void OnRemoteLibraryChanged(string profileKey, LibraryUpdateMessage message)
    {
        if (_windowClosing || message.IsEmpty)
            return;
        _ = InvalidateBrowseCachesAsync(profileKey, message);
    }

    /// <summary>The cached-items-that-were-deleted fix: drop this profile's cached browse pages
    /// for everything the batch touched, THEN let the live views reload. The order is the whole
    /// point — a view that reloads first just re-reads the entry that is about to be deleted.</summary>
    private async Task InvalidateBrowseCachesAsync(string profileKey, LibraryUpdateMessage message)
    {
        // Before any delete. A prefetch in flight for this profile holds the folder it is filling
        // in memory and writes it back when it finishes, which would restore the entry being
        // dropped and leave the deleted items on screen anyway.
        //
        // The price, stated because it is not obvious: a server-side library SCAN emits one of
        // these roughly every 30 s, so for as long as a scan runs every batch cancels this
        // profile's prefetch and — in the blanket case below — wipes its entries again. Browse
        // caching effectively stops until the scan finishes. That is the intended order of
        // preference: during the one window when the library is genuinely churning, a cold browse
        // page is cheaper than a page of items the server no longer has.
        BrowsePrefetcher.CancelProfile(profileKey);

        var folders = message.AffectedFolderIds();
        var items = message.AffectedItemIds();
        // A batch that names items but no folder cannot say WHICH page went stale, and the entry
        // holding the deleted item is one nothing else will ever invalidate. Correct beats cheap
        // here: this is the case the fix exists for.
        var blanket = folders.Count == 0
            && (message.ItemsRemoved is { Count: > 0 } || message.ItemsAdded is { Count: > 0 });
        var serverUrl = _app.FindSessionByKey(profileKey)?.ServerUrl;

        // Off the dispatcher: these are file deletes, and the blanket case is one per cached
        // folder.
        await Task.Run(async () =>
        {
            if (blanket)
                await BrowseFolderCache.InvalidateProfileAsync(profileKey).ConfigureAwait(false);
            else
                foreach (var folderId in folders)
                    BrowseFolderCache.Invalidate(profileKey, folderId);
            // The per-item projections are 1 h entries with no invalidation path of their own, and
            // their keys are computable from the item id — so dropping them costs two deletes per
            // changed item and saves an hour of a stale version list on the detail view.
            if (serverUrl is { Length: > 0 })
                foreach (var itemId in items)
                {
                    Imaging.MetadataCache.Remove($"streams:{serverUrl}:{itemId:N}");
                    Imaging.MetadataCache.Remove($"versions:{serverUrl}:{itemId:N}");
                }
        });

        // Only the active profile has live views; a backgrounded one rebuilds from the cache it
        // just lost when it is switched back to.
        if (_windowClosing || profileKey != _app.ActiveSessionKey)
            return;
        _app.NotifyLibraryChanged(folders);
    }

    private void OnRemoteUserDataChanged(string profileKey, UserDataChangeMessage message)
    {
        if (_windowClosing)
            return;
        if (profileKey != _app.ActiveSessionKey)
        {
            // The fan-out lands in the ACTIVE shell's views, so a backgrounded profile's batch
            // would repaint another server's cards with ids that mean nothing there.
            RemoteNoop("user_data", profileKey, "not_active");
            return;
        }
        if (_app.FindSessionByKey(profileKey) is not { IsConnected: true } jf)
        {
            RemoteNoop("user_data", profileKey, "no_session");
            return;
        }
        // The server pushes a user's data changes to every session it has, including ones signed
        // in as somebody else. Another account's watched state is not this one's.
        if (!Guid.TryParse(message.UserId, out var userId) || userId != jf.UserId)
        {
            RemoteNoop("user_data", profileKey, "other_user");
            return;
        }
        if (message.UserDataList is not { Count: > 0 } entries)
            return;
        if (entries.Count > MaxUserDataFanOut)
        {
            LogRemoteDetail("user_data", profileKey,
                FormattableString.Invariant($"outcome=bulk entries={entries.Count}"));
            _app.NotifyLibraryChanged([]);
            return;
        }
        _ = FanOutUserDataAsync(profileKey, jf, entries);
    }

    private async Task FanOutUserDataAsync(string profileKey, JellyfinService jf,
        IReadOnlyList<UserItemDataMessage> entries)
    {
        foreach (var entry in entries)
        {
            if (_windowClosing || profileKey != _app.ActiveSessionKey)
                return;
            if (entry.ItemId is not { } id || id == Guid.Empty)
                continue;
            // Our own progress report comes back as a change every five seconds. Applying it would
            // repaint the card being watched with a position this app already knows, and Home
            // treats a resume update as a reason to move the item out of Continue Watching.
            //
            // What it costs, since the rest of this file admits its trade-offs: a GENUINE change
            // to the playing item is swallowed with it — marking it watched or favourite from
            // another device will not repaint here until something else reloads the card. That is
            // one item, only while it is playing, against a repaint storm every five seconds.
            if (_reporter is { IsActive: true } && _playingItem?.Id == id)
                continue;
            try
            {
                if (await jf.GetItemAsync(id) is { } fresh
                    && !_windowClosing && profileKey == _app.ActiveSessionKey)
                    _app.NotifyItemUserDataChanged(fresh);
            }
            catch (Exception ex)
            {
                LogRemoteDetail("user_data", profileKey, $"outcome=failure error={ex.GetType().Name}");
            }
        }
    }

    // ---- Shared guards ----------------------------------------------------------------

    private bool RemotePlayerReady(string kind, string profileKey)
    {
        if (_windowClosing)
        {
            RemoteNoop(kind, profileKey, "closing");
            return false;
        }
        if (_app.State != AppState.Playing)
        {
            RemoteNoop(kind, profileKey, "not_playing");
            return false;
        }
        return true;
    }

    private static void RemoteNoop(string kind, string profileKey, string reason)
        => LogRemote(kind, profileKey, $"outcome=noop reason={reason}");
}
