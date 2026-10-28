using LightWeaver.Jellyfin;
using LightWeaver.WinUI.Pages;
using LightWeaver.WinUI.Search;
using Microsoft.UI.Xaml;

namespace LightWeaver.WinUI.Shell;

public sealed partial class MainWindow
{
    private readonly List<(string? Profile, WeakReference<FrameworkElement> Page)> _removalPages = [];

    private void TrackRemovalPage(FrameworkElement page)
    {
        _removalPages.RemoveAll(entry => !entry.Page.TryGetTarget(out _));
        if (_removalPages.Any(entry => entry.Page.TryGetTarget(out var kept) && ReferenceEquals(kept, page))) return;
        _removalPages.Add((CacheProfileKey, new(page)));
    }

    private void RemoveItems(string profileKey, IReadOnlySet<Guid> ids)
    {
        foreach (var entry in _removalPages.ToArray())
        {
            if (entry.Profile != profileKey || !entry.Page.TryGetTarget(out var page)) continue;
            switch (page)
            {
                case HomePage home: home.RemoveItems(ids); break;
                case LibraryPage library: library.RemoveItems(ids); break;
                case DetailPage detail: detail.RemoveItems(ids); break;
                case SectionPage section: section.RemoveItems(ids); break;
                case SearchPage search: search.RemoveItems(ids); break;
                case AdvancedSearchPage search: search.RemoveItems(ids); break;
                case PlaylistPage playlist: playlist.RemoveItems(ids); break;
                case PlaylistsPage playlists: playlists.RemoveItems(ids); break;
                case PersonPage person: person.RemoveItems(ids); break;
                case CastPage cast: cast.RemoveItems(ids); break;
            }
        }
        if (_playback?.PlaybackProfileKey == profileKey) _playback.RemoveItems(ids);
        if (profileKey != Model.ActiveSessionKey) return;
        _libraries?.RemoveAll(item => ids.Contains(item.Id));
        _playlists?.RemoveAll(item => ids.Contains(item.Id));
        Bar.CloseMorph(returnFocus: false);
    }
}
