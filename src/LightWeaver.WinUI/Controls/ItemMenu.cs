using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Details;
using LightWeaver.Jellyfin;
using LightWeaver.Library;
using LightWeaver.Localization;
using LightWeaver.WinUI.Shell;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LightWeaver.WinUI.Controls;

/// <summary>Entries a page adds to the item menu: Continue Watching's Remove, and a playlist's
/// Rename and Delete on All playlists.</summary>
internal sealed record ItemMenuOptions(Action<MediaItem>? RemoveFromContinueWatching = null, Action<MediaItem>? Rename = null, Action<MediaItem>? Delete = null);

/// <summary>The item context menu (design 03, 3a): opened by a card's ⋯, a right-click or
/// Shift+F10. Play or resume (Shuffle for a show or a season), watched state, favourites, series and info.</summary>
internal static class ItemMenu
{
    public static MenuFlyout Build(IAppShell shell, JellyfinService session, MediaItem item, (bool Played, bool Favorite) state,
        Func<UserDataField, bool, Func<Task<bool>>, Task> change, ItemMenuOptions? options = null, PlayOrigin? origin = null)
    {
        var menu = FastUi.Flyout(new MenuFlyout());
        AutomationProperties.SetAutomationId(menu, "ItemMenu");
        if (item.Type == BaseItemDto_Type.Playlist)
        {
            AddPlaylistEntries(menu, shell, session, item, options, origin);
            return menu;
        }

        if (item.IsPlayable)
        {
            if (item.ResumePositionTicks > 0 && !state.Played)
            {
                var resume = Entry("ItemMenuResume", L.Format("ItemMenu.Resume", Clock(TimeSpan.FromTicks(item.ResumePositionTicks))), Theme.Icons.Play,
                    () => shell.Play(item, TimeSpan.FromTicks(item.ResumePositionTicks), origin));
                resume.KeyboardAcceleratorTextOverride = L.Text("ItemMenu.EnterKey");
                menu.Items.Add(resume);
                menu.Items.Add(Entry("ItemMenuPlayFromStart", L.Text("ItemMenu.PlayFromBeginning"), Theme.Icons.Refresh, () => shell.Play(item, TimeSpan.Zero, origin)));
            }
            else
            {
                var play = Entry("ItemMenuPlay", L.Text("ItemMenu.Play"), Theme.Icons.Play, () => shell.Play(item, TimeSpan.Zero, origin));
                play.KeyboardAcceleratorTextOverride = L.Text("ItemMenu.EnterKey");
                menu.Items.Add(play);
            }
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        else if (item.Type is BaseItemDto_Type.Series or BaseItemDto_Type.Season)
        {
            menu.Items.Add(Entry("ItemMenuShuffle", L.Text("Playlist.Shuffle"), Theme.Icons.Shuffle, () => _ = ShuffleAsync(shell, session, item, origin)));
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        menu.Items.Add(Entry("ItemMenuPlayed", state.Played ? L.Text("ItemMenu.MarkUnplayed") : L.Text("ItemMenu.MarkPlayed"), Theme.Icons.Check, async () =>
        {
            var target = !state.Played;
            await change(UserDataField.Played, target, () => session.SetPlayedAsync(item.Id, target));
        }));
        if (options?.RemoveFromContinueWatching is { } remove)
            menu.Items.Add(Entry("ItemMenuRemoveResume", L.Text("ItemMenu.RemoveFromContinueWatching"), Theme.Icons.EyeHidden, () => remove(item)));
        menu.Items.Add(Entry("ItemMenuFavorite", state.Favorite ? L.Text("Card.RemoveFavorite") : L.Text("Card.AddFavorite"),
            state.Favorite ? Theme.Icons.HeartFilled : Theme.Icons.Heart, async () =>
            {
                var target = !state.Favorite;
                await change(UserDataField.Favorite, target, () => session.SetFavoriteAsync(item.Id, target));
            }));

        if (item.IsPlayable || item.Type is BaseItemDto_Type.Series or BaseItemDto_Type.Season or BaseItemDto_Type.BoxSet)
            menu.Items.Add(AddToPlaylist(shell, item));
        if (item.IsPlayable && shell.Downloads.IsTracked(item.Id))
            menu.Items.Add(Entry("ItemMenuDeleteDownload", L.Text("ItemMenu.DeleteDownload"), Theme.Icons.Delete, () => shell.DeleteDownload(item)));
        else if (item.IsPlayable)
            menu.Items.Add(Entry("ItemMenuDownload", L.Text("ItemMenu.Download"), Theme.Icons.Download, () => _ = shell.DownloadAsync(item)));
        else if (item.Type is BaseItemDto_Type.Series or BaseItemDto_Type.Season)
            menu.Items.Add(Entry("ItemMenuDownloadAll", L.Text("ItemMenu.DownloadAll"), Theme.Icons.Download, () => _ = shell.DownloadAsync(item)));

        if (item.IsPlayable)
            menu.Items.Add(Entry("ItemMenuCopyStreamUrl", L.Text("MainWindow.CopyStreamUrl.d7188f29"), Theme.Icons.Copy, () => CopyStreamUrl(session, item)));

        menu.Items.Add(new MenuFlyoutSeparator());
        if (item.Type == BaseItemDto_Type.Episode && item.SeriesId is { } seriesId)
        {
            menu.Items.Add(Entry("ItemMenuSeries", L.Text("ItemMenu.GoToSeries"), Theme.Icons.Tv,
                () => shell.OpenItem(new MediaItem { Id = seriesId, Name = item.SeriesName ?? "", Type = BaseItemDto_Type.Series })));
        }
        var info = Entry("ItemMenuInfo", L.Text("ItemMenu.Info"), Theme.Icons.Info, () => shell.ShowInfo(item));
        info.KeyboardAcceleratorTextOverride = L.Text("ItemMenu.InfoShortcut");
        menu.Items.Add(info);
        return menu;
    }

    /// <summary>"Add to playlist": New playlist… and the known playlists.</summary>
    private static MenuFlyoutSubItem AddToPlaylist(IAppShell shell, MediaItem item)
    {
        var sub = new MenuFlyoutSubItem { Text = L.Text("ItemMenu.AddToPlaylist"), Icon = new FontIcon { FontFamily = Ui.Resource<FontFamily>("LwIconFont"), Glyph = Theme.Icons.PlaylistAdd } };
        AutomationProperties.SetAutomationId(sub, "ItemMenuAddToPlaylist");
        sub.Items.Add(Entry("ItemMenuNewPlaylist", L.Text("ItemMenu.NewPlaylist"), Theme.Icons.Add, () => _ = shell.AddToPlaylistAsync(null, [item])));
        if (shell.KnownPlaylists.Count > 0) sub.Items.Add(new MenuFlyoutSeparator());
        foreach (var playlist in shell.KnownPlaylists)
        {
            var entry = Entry("ItemMenuPlaylist", playlist.Name, Theme.Icons.List, () => _ = shell.AddToPlaylistAsync(playlist, [item]));
            sub.Items.Add(entry);
        }
        return sub;
    }

    /// <summary>A playlist card's menu (design 06, 6a): Play, Shuffle, Rename, Delete.</summary>
    private static void AddPlaylistEntries(MenuFlyout menu, IAppShell shell, JellyfinService session, MediaItem playlist, ItemMenuOptions? options, PlayOrigin? origin)
    {
        menu.Items.Add(Entry("ItemMenuPlay", L.Text("ItemMenu.Play"), Theme.Icons.Play, async () =>
        {
            try { shell.PlayQueue(await session.GetPlaylistItemsAsync(playlist.Id), 0, origin); }
            catch (Exception ex) { shell.ReportFailure(ex); }
        }));
        menu.Items.Add(Entry("ItemMenuShuffle", L.Text("Playlist.Shuffle"), Theme.Icons.Shuffle, async () =>
        {
            try { shell.PlayQueue(Playlists.PlaylistModel.Shuffled(await session.GetPlaylistItemsAsync(playlist.Id), Random.Shared), 0, origin); }
            catch (Exception ex) { shell.ReportFailure(ex); }
        }));
        if (options?.Rename is { } rename || options?.Delete is not null) menu.Items.Add(new MenuFlyoutSeparator());
        if (options?.Rename is { } renamePlaylist) menu.Items.Add(Entry("ItemMenuRename", L.Text("Playlist.Rename"), Theme.Icons.Edit, () => renamePlaylist(playlist)));
        if (options?.Delete is { } delete) menu.Items.Add(Entry("ItemMenuDelete", L.Text("Playlist.Delete"), Theme.Icons.Delete, () => delete(playlist)));
    }

    /// <summary>Plays a show's or a season's unwatched episodes in random order as the queue
    /// (<see cref="DetailModel.ShuffleQueue"/>); a toast says when there is nothing to play. A lookup
    /// that a later Shuffle or card Play, a play, a stop or a session switch overtakes plays nothing.</summary>
    public static async Task ShuffleAsync(IAppShell shell, JellyfinService session, MediaItem item, PlayOrigin? origin = null)
    {
        var request = shell.PlayRequests.Claim();
        var intent = shell.PlaybackIntent;
        var active = shell.Model.Jellyfin;
        var removals = ItemRemovals.ForProfile(shell.CacheProfileKey);
        var readAt = removals.Mark();
        try
        {
            IReadOnlyList<MediaItem> episodes = item.Type == BaseItemDto_Type.Series ? await session.GetSeriesEpisodesAsync(item.Id)
                : (item.SeriesId ?? item.ParentId) is { } seriesId ? await session.GetEpisodesAsync(seriesId, item.Id) : [];
            if (!shell.PlayRequests.IsLatest(request) || shell.PlaybackIntent != intent || !ReferenceEquals(shell.Model.Jellyfin, active)) return;
            var queue = DetailModel.ShuffleQueue(removals.Filter(episodes, readAt), Random.Shared);
            if (queue.Count == 0) Shell.ToastHost.Show(Shell.ToastKind.Info, L.Text("MainWindow.NothingPlayableInHere.380074e4"));
            else shell.PlayQueue(queue, 0, origin);
        }
        catch (Exception ex)
        {
            shell.ReportFailure(ex);
        }
    }

    /// <summary>A playback position as people read it: 42:10, or 1:02:10 past an hour.</summary>
    public static string Clock(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    private static MenuFlyoutItem Entry(string automationId, string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { FontFamily = Ui.Resource<FontFamily>("LwIconFont"), Glyph = glyph } };
        AutomationProperties.SetAutomationId(item, automationId);
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>A stream URL that carries its own authorization, for another player.</summary>
    private static void CopyStreamUrl(JellyfinService session, MediaItem item)
    {
        if (session.GetShareableStreamUrl(item.Id) is not { } url)
        {
            Shell.ToastHost.Show(Shell.ToastKind.Error, L.Text("MainWindow.NotConnectedToAServer.be7bffc6"));
            return;
        }
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(url);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        Shell.ToastHost.Show(Shell.ToastKind.Success, L.Text("MainWindow.StreamUrlCopied.b94f2cd2"));
    }
}
