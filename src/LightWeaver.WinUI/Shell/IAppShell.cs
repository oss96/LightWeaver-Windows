using LightWeaver.Details;
using LightWeaver.Downloads;
using LightWeaver.Home;
using LightWeaver.Jellyfin;
using LightWeaver.Search;
using LightWeaver.ViewModels;

namespace LightWeaver.WinUI.Shell;

/// <summary>What pages may ask of the window: navigate, report a failed server call, raise a
/// toast. Pages never reach into the window or each other.</summary>
public interface IAppShell
{
    AppViewModel Model { get; }

    /// <summary>The active profile, or the saved profile during an offline startup.</summary>
    string CacheProfileKey => Model.ActiveSessionKey ?? "";

    /// <summary>Changes with every play or stop; 0 before anything has played.</summary>
    int PlaybackIntent { get; }

    /// <summary>The order of plays that wait for a lookup, so only the newest one starts.</summary>
    LightWeaver.Player.PlayRequests PlayRequests { get; }

    /// <summary>The full player (or the mini-player) covers the page; page shortcuts stay off.</summary>
    bool PlayerCoversPage { get; }

    void GoHome();

    /// <summary>The "Home ›" breadcrumb: back to Home as it was left when it is the previous page.</summary>
    void ReturnHome();
    void OpenItem(MediaItem item);

    /// <summary>Info (Ctrl+I): the media info of a title with a file, the detail page otherwise.</summary>
    void ShowInfo(MediaItem item);

    void OpenPerson(Guid personId, string name);
    void OpenLibrary(MediaItem library);
    void OpenSection(HomeSection section);
    void OpenAdvancedSearch(AdvancedSearchFilters? seed = null);

    /// <summary>More filters from the search results: the query stays and the chip is the Type filter.</summary>
    void OpenAdvancedSearch(AdvancedSearchFilters seed, string term);

    /// <summary>The signed-in server's name, for text such as the search empty state.</summary>
    string ServerName { get; }
    void OpenPlaylists();
    void OpenPlaylist(MediaItem playlist);
    void OpenDownloads();
    void OpenSettings();
    /// <summary>Plays a title; from a card (<paramref name="from"/>) the player grows out of its artwork.</summary>
    void Play(MediaItem item, TimeSpan? start = null, PlayOrigin? from = null);

    /// <summary>Plays with what the detail page's bottom bar preselected.</summary>
    void Play(MediaItem item, PlaybackChoice choice);

    /// <summary>Plays with the detail page's picks; <paramref name="fullscreen"/> goes straight to full screen.</summary>
    void Play(MediaItem item, PlaybackChoice choice, bool fullscreen);

    /// <summary>Plays a saved download from its file, under the server it came from.</summary>
    void PlayDownload(DownloadItem download);

    /// <summary>"Cast and crew →": everyone credited on the item.</summary>
    void OpenCast(MediaItem item);

    /// <summary>The user's playlists as last loaded, for "Add to playlist".</summary>
    IReadOnlyList<MediaItem> KnownPlaylists { get; }

    Task RefreshPlaylistsAsync();

    /// <summary>The New playlist and Rename dialog; null when cancelled.</summary>
    Task<string?> AskPlaylistNameAsync(string title, string action, string? current = null);

    /// <summary>Adds items to a playlist, or to a new one when <paramref name="playlist"/> is null.</summary>
    Task<Guid?> AddToPlaylistAsync(MediaItem? playlist, IReadOnlyList<MediaItem> items);

    /// <summary>Plays a queue (a playlist, shuffled or not) from one item.</summary>
    void PlayQueue(IReadOnlyList<MediaItem> queue, int start, PlayOrigin? from = null);

    /// <summary>The download engine shared with the WPF app.</summary>
    DownloadManager Downloads { get; }

    /// <summary>Whether the server answers; the Downloads page shows its offline banner from it.</summary>
    Services.Connectivity Connectivity { get; }

    /// <summary>Queues a title, or every episode Download all takes for a season or a show.</summary>
    Task DownloadAsync(MediaItem item);

    /// <summary>Deletes a download's file.</summary>
    void DeleteDownload(MediaItem item);

    /// <summary>Opens the avatar's profile menu (Switch user, Switch server).</summary>
    void OpenProfileMenu();

    /// <summary>Signs out of this server and opens Login on it.</summary>
    void LogOut();

    /// <summary>The window, for pickers that need an owner.</summary>
    nint WindowHandle { get; }

    /// <summary>Closes the app the normal way, as the window's close button does.</summary>
    void Quit();

    /// <summary>The audio outputs mpv sees, once the player exists; null before.</summary>
    IReadOnlyList<LightWeaver.Player.MpvAudioDevice>? AudioDevices();

    /// <summary>A server request failed; an unreachable server swaps the page for the offline view.</summary>
    void ReportFailure(Exception ex);

    /// <summary>A page put data from the cache on screen, or found none for what it shows now;
    /// offline, it shows under the banner or gives way to the unreachable view.</summary>
    void CachedDataShown(INavPage page);

    /// <summary>The profile signed in without "Stay signed in": its saved token is removed when the app closes.</summary>
    void ForgetOnExit(SavedCredentials profile);
}
