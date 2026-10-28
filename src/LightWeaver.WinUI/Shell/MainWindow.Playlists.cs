using LightWeaver.Jellyfin;
using LightWeaver.Localization;
using LightWeaver.Playlists;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace LightWeaver.WinUI.Shell;

/// <summary>Playlists for every page (design 06): the cached list for "Add to playlist", the name
/// dialog, adding items and the play-queue hand-off.</summary>
public sealed partial class MainWindow
{
    public IReadOnlyList<MediaItem> KnownPlaylists => _playlists ?? [];

    public async Task RefreshPlaylistsAsync()
    {
        try
        {
            var session = Model.Jellyfin;
            var profile = CacheProfileKey;
            var removals = ItemRemovals.ForProfile(profile);
            var readAt = removals.Mark();
            var playlists = removals.Filter(await session.GetPlaylistsAsync(), readAt);
            if (profile == CacheProfileKey && ReferenceEquals(session, Model.Jellyfin) && removals.Mark() == readAt) _playlists = playlists;
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    /// <summary>The New playlist and Rename dialog; null when cancelled.</summary>
    public async Task<string?> AskPlaylistNameAsync(string title, string action, string? current = null)
    {
        var box = new TextBox { Text = current ?? "", PlaceholderText = L.Text("Playlist.NamePlaceholder"), MinWidth = 320 };
        AutomationProperties.SetAutomationId(box, "PlaylistNameBox");
        AutomationProperties.SetName(box, L.Text("Playlist.Name"));
        var dialog = new Controls.LwDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = box,
            PrimaryButtonText = action,
            CloseButtonText = L.Text("Playlist.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = PlaylistModel.CleanName(box.Text) is not null,
        };
        AutomationProperties.SetAutomationId(dialog, "PlaylistNameDialog");
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = PlaylistModel.CleanName(box.Text) is not null;
        dialog.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? PlaylistModel.CleanName(box.Text) : null;
    }

    /// <summary>Adds items to a playlist, or to a new one named in the dialog when
    /// <paramref name="playlist"/> is null; returns the playlist's id, or null when nothing was added.</summary>
    public async Task<Guid?> AddToPlaylistAsync(MediaItem? playlist, IReadOnlyList<MediaItem> items)
    {
        try
        {
            Guid? id;
            string name;
            if (playlist is null)
            {
                if (await AskPlaylistNameAsync(L.Text("Playlist.NewTitle"), L.Text("Playlist.Create")) is not { } newName) return null;
                name = newName;
                id = await Model.Jellyfin.CreatePlaylistAsync(name, [.. items.Select(i => i.Id)]);
                if (id is null) throw new InvalidOperationException("the server returned no playlist id");
            }
            else
            {
                name = playlist.Name;
                id = playlist.Id;
                await Model.Jellyfin.AddToPlaylistAsync(playlist.Id, [.. items.Select(i => i.Id)]);
            }
            ToastHost.Show(ToastKind.Success, items.Count == 1 ? L.Format("Playlist.AddedOne", items[0].Name, name) : L.Format("Playlist.AddedMany", items.Count, name));
            await RefreshPlaylistsAsync();
            return id;
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("playlists", $"event=add outcome=failure error={ex.GetType().Name}");
            ToastHost.Show(ToastKind.Error, L.Text("Playlist.AddFailed"));
            return null;
        }
    }
}
