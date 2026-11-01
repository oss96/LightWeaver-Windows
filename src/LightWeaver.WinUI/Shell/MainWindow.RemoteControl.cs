using LightWeaver.Imaging;
using LightWeaver.Jellyfin;
using LightWeaver.Library;
using LightWeaver.Localization;
using LightWeaver.Player;
using LightWeaver.ViewModels;
using LightWeaver.WinUI.Player;
using Microsoft.UI.Windowing;

namespace LightWeaver.WinUI.Shell;

/// <summary>The live session socket in v2, as in the WPF app's MainWindow.RemoteControl: other
/// Jellyfin clients drive playback ("Play on", transport, volume, tracks, messages), and the
/// server's library pushes drop the affected browse caches, so the next page shown is fresh. The message shapes and the
/// command mapping are shared (<see cref="RemoteCommand"/>); this marshals, guards and calls what
/// the in-app control would.</summary>
public sealed partial class MainWindow
{
    private bool _closing;

    /// <summary>Logs every message on the socket's thread, then acts through the dispatcher
    /// (never a blocking hop: disposing the socket joins that thread from the UI thread).</summary>
    private void AttachRemoteControl(LiveSessionService live)
    {
        live.PlayRequested += (key, message) =>
        {
            LogRemote("play", key, FormattableString.Invariant($"items={message.ItemIds?.Count ?? 0} command={RemoteToken(message.PlayCommand)}"));
            DispatcherQueue.TryEnqueue(() => OnRemotePlay(key, message));
        };
        live.PlaystateRequested += (key, message) =>
        {
            LogRemote("playstate", key, $"command={RemoteToken(message.Command)}");
            DispatcherQueue.TryEnqueue(() => OnRemotePlaystate(key, message));
        };
        live.GeneralCommandReceived += (key, message) =>
        {
            LogRemote("general", key, $"command={RemoteToken(message.Name)}");
            DispatcherQueue.TryEnqueue(() => OnRemoteGeneralCommand(key, message));
        };
        live.LibraryChanged += (key, message) =>
        {
            LogRemoteDetail("library", key, FormattableString.Invariant($"items={message.AffectedItemIds().Count} folders={message.AffectedFolderIds().Count}"));
            DispatcherQueue.TryEnqueue(() => OnRemoteLibraryChanged(key, message));
        };
        live.AuthenticationRejected += key => LogRemote("rejected", key, "outcome=stopped");
    }

    // ---- Transport ------------------------------------------------------------------

    private void OnRemotePlaystate(string profileKey, PlaystateRequestMessage message)
    {
        var action = RemoteCommand.Resolve(message);
        if (action.Kind == RemoteActionKind.Noop) { RemoteNoop("playstate", profileKey, "unsupported_command"); return; }
        if (RemotePlayer("playstate", profileKey) is not { } playback) return;
        var vm = playback.ViewModel;
        switch (action.Kind)
        {
            case RemoteActionKind.Pause: vm.IsPaused = true; break;
            case RemoteActionKind.Unpause: vm.IsPaused = false; break;
            case RemoteActionKind.PlayPause: vm.TogglePauseCommand.Execute(null); break;
            case RemoteActionKind.Stop: playback.Stop(); break;
            case RemoteActionKind.Seek: vm.Seek(action.Number); break;
            case RemoteActionKind.NextTrack: vm.GoNext(); break;
            case RemoteActionKind.PreviousTrack: vm.GoPrevious(); break;
            case RemoteActionKind.FastForward: vm.JumpForwardLarge(); break;
            case RemoteActionKind.Rewind: vm.JumpBackLarge(); break;
        }
    }

    private void OnRemoteGeneralCommand(string profileKey, GeneralCommandMessage message)
    {
        var action = RemoteCommand.Resolve(message);
        if (action.Kind == RemoteActionKind.Noop) { RemoteNoop("general", profileKey, "unsupported_command"); return; }
        if (action.Kind == RemoteActionKind.DisplayMessage)
        {
            var text = RemoteCommand.MessageText(action.Header, action.Text);
            if (text.Length == 0) { RemoteNoop("general", profileKey, "empty_message"); return; }
            var server = Model.FindSessionByKey(profileKey)?.ServerUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : profileKey;
            ToastHost.Show(ToastKind.Info, L.Format("MainWindowRemoteControl.ServerMessage", server, text));
            return;
        }
        if (RemotePlayer("general", profileKey) is not { } playback) return;
        var vm = playback.ViewModel;
        switch (action.Kind)
        {
            case RemoteActionKind.SetVolume: vm.Volume = action.Number; break;
            case RemoteActionKind.VolumeUp: PlayerActionDispatcher.Dispatch(PlayerAction.VolumeUp, vm); break;
            case RemoteActionKind.VolumeDown: PlayerActionDispatcher.Dispatch(PlayerAction.VolumeDown, vm); break;
            case RemoteActionKind.Mute: if (!vm.IsMuted) vm.ToggleMute(); break;
            case RemoteActionKind.Unmute: if (vm.IsMuted) vm.ToggleMute(); break;
            case RemoteActionKind.ToggleMute: vm.ToggleMute(); break;
            case RemoteActionKind.ToggleFullscreen: vm.RequestFullscreen(); break;
            case RemoteActionKind.PlayNext: vm.GoNext(); break;
            case RemoteActionKind.SetAudioStreamIndex: _ = ApplyRemoteStreamAsync(profileKey, playback, audio: true, (int)action.Number); break;
            case RemoteActionKind.SetSubtitleStreamIndex: _ = ApplyRemoteStreamAsync(profileKey, playback, audio: false, (int)action.Number); break;
        }
    }

    /// <summary>A remote audio or subtitle pick, by Jellyfin stream index of the source that plays.
    /// Direct play switches the mpv track; a transcode or a stream mpv does not list is declined
    /// (the player's quality and version flyouts renegotiate instead).</summary>
    private async Task ApplyRemoteStreamAsync(string profileKey, PlaybackController playback, bool audio, int index)
    {
        if (playback.Current is not { } item || playback.IsLocal || playback.PlaybackSession is not { } session)
        {
            RemoteNoop("general", profileKey, "local_or_idle");
            return;
        }
        MediaSourceStreams? streams = null;
        try
        {
            streams = await session.GetMediaStreamsAsync(item.Id);
        }
        catch (Exception ex)
        {
            LogRemoteDetail("general", profileKey, $"outcome=failure reason=streams error={ex.GetType().Name}");
        }
        if (_closing || playback.Current?.Id != item.Id) { RemoteNoop("general", profileKey, "stale"); return; }
        var target = RemoteCommand.ResolveStream(streams, playback.Decision?.MediaSourceId, audio, index);
        if (target.Kind is RemoteStreamKind.Unknown or RemoteStreamKind.SourceMismatch)
        {
            RemoteNoop("general", profileKey, target.Kind == RemoteStreamKind.Unknown ? "unknown_stream" : "source_mismatch");
            return;
        }
        var vm = playback.ViewModel;
        if (playback.Decision is { IsTranscode: true }) { RemoteNoop("general", profileKey, "transcode"); return; }
        if (target.Kind == RemoteStreamKind.Off)
        {
            if (vm.SubtitleTracks.FirstOrDefault(t => t.Id < 0) is { } none) vm.SelectSubtitle(none);
            return;
        }
        if (target.Choice is not { IsExternal: false, TypeOrdinal: >= 0 } choice) { RemoteNoop("general", profileKey, "not_embedded"); return; }
        var options = audio ? vm.AudioTracks : vm.SubtitleTracks;
        if (options.FirstOrDefault(t => t.Id == choice.TypeOrdinal + 1) is not { } option) { RemoteNoop("general", profileKey, "no_track"); return; }
        if (audio) vm.SelectAudio(option);
        else vm.SelectSubtitle(option);
        LogRemote("general", profileKey, FormattableString.Invariant($"outcome=success kind={(audio ? "audio" : "subtitle")} index={index}"));
    }

    // ---- Play ------------------------------------------------------------------------

    private void OnRemotePlay(string profileKey, PlayRequestMessage message)
    {
        var kind = RemoteCommand.ResolvePlay(message.PlayCommand);
        if (kind == RemotePlayKind.Noop) { RemoteNoop("play", profileKey, "unsupported_command"); return; }
        if (_closing) { RemoteNoop("play", profileKey, "closing"); return; }
        if (message.ItemIds is not { Count: > 0 }) { RemoteNoop("play", profileKey, "no_items"); return; }
        if (Model.FindSessionByKey(profileKey) is not { IsConnected: true } session) { RemoteNoop("play", profileKey, "no_session"); return; }
        _ = StartRemotePlayAsync(profileKey, kind, RemoteCommand.LimitPlayRequest(message), session);
    }

    private async Task StartRemotePlayAsync(string profileKey, RemotePlayKind kind, PlayRequestMessage message, JellyfinService session)
    {
        var items = new List<MediaItem>();
        foreach (var id in message.ItemIds!)
        {
            try
            {
                if (await session.GetItemAsync(id) is { IsPlayable: true } item) items.Add(item);
            }
            catch (Exception ex)
            {
                LogRemoteDetail("play", profileKey, $"outcome=failure reason=lookup error={ex.GetType().Name}");
            }
        }
        if (_closing) { RemoteNoop("play", profileKey, "closing"); return; }
        if (items.Count == 0) { RemoteNoop("play", profileKey, "no_playable_items"); return; }
        var playback = Playback();
        kind = RemoteCommand.ResolvePlayTarget(kind, playing: playback.IsPlaying,
            ownsPlayback: ReferenceEquals(session, playback.PlaybackSession ?? Model.Jellyfin));
        if (kind == RemotePlayKind.Noop) { RemoteNoop("play", profileKey, "not_playback_session"); return; }
        var grouped = _sync?.Client.IsInGroup == true;
        switch (kind)
        {
            case RemotePlayKind.PlayNow:
                ActivateForRemotePlay();
                playback.PlayRemote(items, Math.Clamp(message.StartIndex ?? 0, 0, items.Count - 1), message.StartPositionTicks ?? 0, session);
                break;
            case RemotePlayKind.PlayNext when grouped:
            case RemotePlayKind.PlayLast when grouped:
                ToastHost.Show(ToastKind.Info, L.Text("MainWindowRemoteControl.LeaveTheGroupToAppendRemoteQueue.314d7ddd"));
                return;
            case RemotePlayKind.PlayNext:
                SeedQueue(playback);
                Model.Queue.InsertNext(items);
                ToastHost.Show(ToastKind.Info, items.Count == 1 ? L.Format("MainWindowRemoteControl.PlayingNext.77e3707a", items[0].Name) : L.Format("MainWindowRemoteControl.QueuedItemsToPlayNext.096175d6", items.Count));
                break;
            case RemotePlayKind.PlayLast:
                SeedQueue(playback);
                Model.Queue.Append(items);
                ToastHost.Show(ToastKind.Info, items.Count == 1 ? L.Format("MainWindowRemoteControl.AddedToQueue.0caee923", items[0].Name) : L.Format("MainWindowRemoteControl.AddedItemsToTheQueue.07df3474", items.Count));
                break;
        }
        LogRemote("play", profileKey, FormattableString.Invariant($"outcome=success command={kind} items={items.Count}"));
    }

    /// <summary>"Play next" onto a lone title queues after it instead of replacing it.</summary>
    private void SeedQueue(PlaybackController playback)
    {
        if (!Model.Queue.IsActive && playback.Current is { } playing) Model.Queue.Set([playing], 0);
    }

    /// <summary>A remote play brings the window forward, restored first if it was minimised.</summary>
    private void ActivateForRemotePlay()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
    }

    // ---- Library and user data --------------------------------------------------------

    private void OnRemoteLibraryChanged(string profileKey, LibraryUpdateMessage message)
    {
        if (_closing || message.IsEmpty) return;
        var added = (message.ItemsAdded ?? []).Select(id => Guid.TryParse(id, out var parsed) ? parsed : Guid.Empty);
        var ledger = ItemRemovals.ForProfile(profileKey);
        ledger.Restore(added);
        if (profileKey == Model.ActiveSessionKey && message.ItemsAdded is { Count: > 0 })
        {
            _libraries = null;
            _playlists = null;
        }
        var ids = (message.ItemsRemoved ?? []).Select(id => Guid.TryParse(id, out var parsed) ? parsed : Guid.Empty);
        var removed = ledger.Remove(ids);
        if (removed.Count > 0) RemoveItems(profileKey, removed);
        _ = InvalidateBrowseCachesAsync(profileKey, message);
    }

    /// <summary>Drops this profile's cached pages for what changed, then lets the page reload; in
    /// that order, or the reload re-reads the entry about to be deleted.</summary>
    private async Task InvalidateBrowseCachesAsync(string profileKey, LibraryUpdateMessage message)
    {
        var folders = message.AffectedFolderIds();
        var items = message.AffectedItemIds();
        var blanket = folders.Count == 0 && (message.ItemsRemoved is { Count: > 0 } || message.ItemsAdded is { Count: > 0 });
        var serverUrl = Model.FindSessionByKey(profileKey)?.ServerUrl;
        await BrowsePrefetcher.BeginInvalidate(profileKey);
        try
        {
            var removed = (message.ItemsRemoved ?? []).Select(id => Guid.TryParse(id, out var parsed) ? parsed : Guid.Empty).Where(id => id != Guid.Empty).ToHashSet();
            if (removed.Count > 0)
            {
                await LightWeaver.Home.HomeSnapshot.RemoveAsync(profileKey, removed, recorded: true);
                await QueryCache.Sections.RemoveAsync(profileKey, removed, recorded: true);
                await QueryCache.Searches.RemoveAsync(profileKey, removed, recorded: true);
            }
            await Task.Run(async () =>
            {
                if (blanket) await BrowseFolderCache.InvalidateProfileAsync(profileKey).ConfigureAwait(false);
                else foreach (var folderId in folders) BrowseFolderCache.Invalidate(profileKey, folderId);
                if (serverUrl is { Length: > 0 })
                    foreach (var itemId in items)
                    {
                        MetadataCache.Remove($"streams:{serverUrl}:{itemId:N}:{L.CacheIdentity}");
                        MetadataCache.Remove($"versions:{serverUrl}:{itemId:N}:{L.CacheIdentity}");
                    }
            });
        }
        finally
        {
            BrowsePrefetcher.EndInvalidate(profileKey);
        }
        if (_closing || profileKey != Model.ActiveSessionKey) return;
        Model.NotifyLibraryChanged(folders);
    }

    // ---- Guards and log -----------------------------------------------------------------

    private PlaybackController? RemotePlayer(string kind, string profileKey)
    {
        if (_closing) { RemoteNoop(kind, profileKey, "closing"); return null; }
        if (_playback is not { IsPlaying: true } playback) { RemoteNoop(kind, profileKey, "not_playing"); return null; }
        return playback;
    }

    private static void RemoteNoop(string kind, string profileKey, string reason) => LogRemote(kind, profileKey, $"outcome=noop reason={reason}");

    private static void LogRemote(string kind, string profileKey, string detail) => Diagnostics.AppLog.Info("session", RemoteLine(kind, profileKey, detail));

    private static void LogRemoteDetail(string kind, string profileKey, string detail) => Diagnostics.AppLog.Detail("session", RemoteLine(kind, profileKey, detail));

    private static string RemoteLine(string kind, string profileKey, string detail)
        => $"event=remote kind={kind} session={Diagnostics.AppLog.ShortHash(profileKey)} {detail}";

    /// <summary>A server-chosen command name as a log token: letters and digits, at most 32.</summary>
    private static string RemoteToken(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "none";
        var token = new string([.. value.Where(char.IsAsciiLetterOrDigit).Take(32)]);
        return token.Length > 0 ? token : "other";
    }
}
