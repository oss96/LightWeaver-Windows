using LightWeaver.Downloads;
using LightWeaver.Home;
using LightWeaver.Jellyfin;
using LightWeaver.Library;
using LightWeaver.Localization;
using LightWeaver.WinUI.Controls;
using LightWeaver.WinUI.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LightWeaver.WinUI.Pages;

/// <summary>Home rows update in place and remain available from the profile's cache offline.</summary>
public sealed partial class HomePage : Grid, INavPage
{
    private const int RowSize = 24;
    private const double RowGap = 28;
    private static readonly TimeSpan SkeletonDelay = TimeSpan.FromMilliseconds(150);
    private readonly UserDataOverrides _overrides = new();
    private readonly HomeRow _continue;
    private readonly HomeRow _nextUp;
    private readonly HomeRow _recent;
    private readonly HomeRow _favorites;
    private readonly CardRow _downloads;
    private readonly Dictionary<Guid, string> _downloadLines = [];
    private List<Guid> _downloadIds = [];
    private bool _downloadsQueued;
    private readonly StackPanel _empty;
    private readonly TextBlock _emptyTitle;
    private readonly TextBlock _emptyText;
    private readonly Button _emptyButton;
    private readonly StackPanel _rows;
    private int _load;
    private bool _loadedOnce;
    private HomeSnapshot? _shown;
    private readonly string _profileKey;
    private readonly ItemRemovals _removals;
    private bool _loading;
    private bool _skeletonsReady;
    private HashSet<CardRow> _hidden = [];
    private CardScale _scale = CardScale.None;

    public HomePage(IAppShell shell)
    {
        Shell = shell;
        _profileKey = shell.CacheProfileKey;
        _removals = ItemRemovals.ForProfile(_profileKey);
        _continue = new HomeRow(shell, HomeSection.ContinueWatching, _overrides, new ItemMenuOptions(RemoveFromContinueWatching));
        _nextUp = new HomeRow(shell, HomeSection.NextUp, _overrides);
        _recent = new HomeRow(shell, HomeSection.RecentlyAdded, _overrides);
        _favorites = new HomeRow(shell, HomeSection.Favorites, _overrides);
        _downloads = new CardRow(shell, "HomeRow", "Downloads", () => L.Text("Home.Downloads"), shell.OpenDownloads, CardKind.Wide,
            Ui.Resource<double>("LwHomeWideCardWidth"), _overrides)
        {
            CardTitle = HomeSections.Title,
            CardSubtitle = item => _downloadLines.GetValueOrDefault(item.Id, ""),
            Open = item => { if (shell.Connectivity.IsOffline) shell.Play(item); else shell.OpenItem(item); },
        };
        var rows = _rows = new StackPanel { Spacing = RowGap, Padding = new Thickness(24, 18, 24, 28) };
        foreach (var row in Rows) { row.Visibility = Visibility.Collapsed; rows.Children.Add(row); }
        InitializeSections();
        var content = new Grid { Children = { rows, _customize } };
        var scroller = new ScrollViewer { Content = content, HorizontalScrollMode = ScrollMode.Disabled, IsTabStop = false };
        AutomationProperties.SetAutomationId(scroller, "HomeScroller");
        Children.Add(scroller);

        _emptyTitle = new TextBlock { FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetAutomationId(_emptyTitle, "HomeEmptyTitle");
        _emptyText = new TextBlock { FontSize = 13, Foreground = Ui.Resource<Brush>("LwText2Brush"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Center };
        _emptyButton = new Button { Style = Ui.Resource<Style>("LwAccentButton"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetAutomationId(_emptyButton, "HomeEmptyBrowse");
        _emptyButton.Click += (_, _) => shell.OpenAdvancedSearch();
        _empty = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 90), Visibility = Visibility.Collapsed };
        _empty.Children.Add(new FontIcon { FontFamily = Ui.Resource<FontFamily>("LwIconFont"), FontSize = 44, Glyph = Theme.Icons.VideoLibrary, Foreground = Ui.Resource<Brush>("LwText3Brush") });
        _empty.Children.Add(_emptyTitle);
        _empty.Children.Add(_emptyText);
        _empty.Children.Add(_emptyButton);
        Children.Add(_empty);

        ApplyTexts();
        if (HomeSnapshot.Last(_profileKey) is { } last) Show(last);
        ShowDownloads();
        LiveText.Observe(this, (_, _) => DispatcherQueue.TryEnqueue(ApplyTexts));
        Loaded += (_, _) =>
        {
            shell.Downloads.DownloadsChanged += OnDownloadsChanged;
            ShowDownloads();
            if (!_loadedOnce && !_loading) _ = LoadAsync();
        };
        Unloaded += (_, _) => shell.Downloads.DownloadsChanged -= OnDownloadsChanged;
        SizeChanged += (_, e) => ApplyScale(e.NewSize.Width);
        RegisterPropertyChangedCallback(FlowDirectionProperty, (_, _) => PlaceCustomizeButton());
        Unloaded += (_, _) => _sectionsFlyout?.Hide();
        PlaceCustomizeButton();
    }

    public IAppShell Shell { get; }

    private IEnumerable<CardRow> Rows => [_continue, _nextUp, _recent, _favorites, _downloads];

    /// <summary>Grows the cards, captions, headings and the gaps between rows with the window
    /// (<see cref="CardScale"/>), in steps while the window is dragged.</summary>
    private void ApplyScale(double width)
    {
        var scale = CardRow.ScaleFor(width);
        if (!_scale.MovesTo(scale, CardRow.MaxScale)) return;
        _scale = scale;
        _rows.Spacing = scale.Space(RowGap);
        foreach (var row in Rows) row.ApplyScale(scale);
    }

    public void OnShown()
    {
        ApplyVisibility();
        if (_loadedOnce && !_loading) _ = LoadAsync();
    }

    /// <summary>The rows in the order Settings › Home gives them; turned-off rows stay hidden.</summary>
    private void ApplyLayout()
    {
        var layout = Settings.HomeLayoutStore.Load(_profileKey);
        var ordered = new List<CardRow>();
        _hidden = [];
        foreach (var pref in layout)
        {
            if (RowFor(pref.Id) is not { } row) continue;
            ordered.Add(row);
            if (!pref.Enabled) _hidden.Add(row);
        }
        foreach (var row in Rows.Where(r => !ordered.Contains(r))) ordered.Add(row);
        for (var i = 0; i < ordered.Count; i++)
            if (!ReferenceEquals(_rows.Children[i], ordered[i]))
            {
                _rows.Children.Remove(ordered[i]);
                _rows.Children.Insert(i, ordered[i]);
            }
    }

    private CardRow? RowFor(Settings.HomeSectionId id) => id switch
    {
        Settings.HomeSectionId.Resume => _continue,
        Settings.HomeSectionId.NextUp => _nextUp,
        Settings.HomeSectionId.Latest => _recent,
        Settings.HomeSectionId.Favorites => _favorites,
        Settings.HomeSectionId.Downloads => _downloads,
        _ => null,
    };

    public void Reload() => _ = LoadAsync();

    public DateTime? CachedAtUtc => _shown?.SavedAtUtc;

    public void LoadCached() { if (_shown is null && !_loading) _ = LoadAsync(); }

    public void RemoveItems(IReadOnlySet<Guid> ids)
    {
        if (_shown is { } snapshot) Show(snapshot.Without(ids));
    }

    private async Task LoadAsync()
    {
        var load = ++_load;
        _loading = true;
        try
        {
            var jellyfin = Shell.Model.Jellyfin;
            var profileKey = _profileKey;
            if (_shown is null)
            {
                _ = Task.Delay(SkeletonDelay).ContinueWith(_ =>
                {
                    if (_loading && load == _load && _shown is null)
                    {
                        _skeletonsReady = true;
                        ApplyVisibility();
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());
                var started = System.Diagnostics.Stopwatch.StartNew();
                var cached = await HomeSnapshot.ReadAsync(profileKey);
                if (load != _load) return;
                Diagnostics.AppLog.Detail("home", $"event=snapshot outcome={(cached is null ? "miss" : "hit")} elapsed_ms={started.ElapsedMilliseconds}");
                if (cached is not null && _shown is null)
                {
                    Show(cached);
                    Shell.CachedDataShown(this);
                }
            }

            if (Shell.Connectivity.IsOffline || !jellyfin.IsConnected || profileKey != Shell.CacheProfileKey) return;

            var fetching = System.Diagnostics.Stopwatch.StartNew();
            var readAt = _removals.Mark();
            List<MediaItem> resume, next, latest, favorites;
            Dictionary<Guid, int> unplayed;
            try
            {
                var resumeTask = jellyfin.GetResumeItemsAsync(RowSize);
                var nextTask = jellyfin.GetNextUpAsync(RowSize);
                var librariesTask = jellyfin.GetLibrariesAsync();
                var favoritesTask = jellyfin.GetFavoritesAsync(RowSize);
                await Task.WhenAll(resumeTask, nextTask, librariesTask, favoritesTask);
                (resume, next, favorites) = (resumeTask.Result, nextTask.Result, favoritesTask.Result);
                var latestTask = jellyfin.GetLatestAsync(HomeSections.LatestLibraries(librariesTask.Result), RowSize);
                var seriesIds = next.Select(i => i.SeriesId).OfType<Guid>().Distinct().ToList();
                var seriesTask = seriesIds.Count > 0 ? jellyfin.GetItemsByIdsAsync(seriesIds, CancellationToken.None) : Task.FromResult(new List<MediaItem>());
                await Task.WhenAll(latestTask, seriesTask);
                latest = [.. HomeSections.Newest(latestTask.Result, RowSize)];
                unplayed = seriesTask.Result.Where(s => s.UnplayedItemCount is not null).ToDictionary(s => s.Id, s => s.UnplayedItemCount!.Value);
            }
            catch (Exception ex)
            {
                if (load == _load) Shell.ReportFailure(ex);
                return;
            }
            if (load != _load || profileKey != Shell.CacheProfileKey) return;
            _loadedOnce = true;
            var fresh = new HomeSnapshot(resume, next, latest, favorites, unplayed) { SavedAtUtc = DateTime.UtcNow, ReadAt = readAt }.Filter(_removals, readAt);
            var previous = _shown;
            var changed = !fresh.SameAs(previous);
            if (changed) Show(fresh);
            else
            {
                _shown = fresh;
                ApplyVisibility();
            }
            Diagnostics.AppLog.Detail("home", $"event=refresh outcome={(changed ? "changed" : "unchanged")} elapsed_ms={fetching.ElapsedMilliseconds}");
            _ = HomeSnapshot.StoreAsync(profileKey, fresh, previous, readAt);
        }
        finally { if (load == _load) _loading = false; }
    }

    /// <summary>Puts the rows on screen; each row keeps the cards of items it already shows.</summary>
    private void Show(HomeSnapshot snapshot)
    {
        snapshot = snapshot.Filter(_removals, snapshot.ReadAt);
        _shown = snapshot;
        _continue.SetItems(snapshot.Resume);
        _nextUp.SetItems(snapshot.NextUp, item => HomeSections.EpisodesAfter(item, snapshot.Unplayed));
        _recent.SetItems(snapshot.Latest);
        _favorites.SetItems(snapshot.Favorites);
        ApplyVisibility();
    }

    private void OnDownloadsChanged()
    {
        if (_downloadsQueued) return;
        _downloadsQueued = true;
        DispatcherQueue.TryEnqueue(ShowDownloads);
    }

    /// <summary>Fills the Downloads row from the downloads on this device, online or offline: the
    /// server is the profile's, since an offline start opens Home without a session.</summary>
    private void ShowDownloads()
    {
        _downloadsQueued = false;
        var downloads = Shell.Downloads.Snapshot();
        var items = DownloadsModel.HomeItems(downloads, Settings.HomeLayoutStore.ServerOf(_profileKey));
        if (items.Select(i => i.Id).SequenceEqual(_downloadIds)) return;
        _downloadIds = [.. items.Select(i => i.Id)];
        _downloadLines.Clear();
        foreach (var download in downloads) _downloadLines[download.ItemId] = download.Subtitle ?? "";
        _downloads.SetItems(items);
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        ApplyLayout();
        foreach (var row in Rows)
        {
            var visible = HomeSections.IsRowVisible(!_hidden.Contains(row), !row.IsEmpty, ReferenceEquals(row, _downloads) || _shown is not null, _skeletonsReady);
            if (visible && !ReferenceEquals(row, _downloads) && _shown is null && row.Visibility != Visibility.Visible) row.ShowSkeleton();
            row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
        var ready = _shown is not null || Rows.Where(r => !_hidden.Contains(r)).All(r => ReferenceEquals(r, _downloads));
        _empty.Visibility = ready && Rows.All(r => r.Visibility == Visibility.Collapsed) ? Visibility.Visible : Visibility.Collapsed;
        PlaceCustomizeButton();
        ApplyEmptyTexts();
    }

    /// <summary>Takes the item off Continue Watching at once, with Undo in a toast (design 03, note 4).</summary>
    private async void RemoveFromContinueWatching(MediaItem item)
    {
        var jellyfin = Shell.Model.Jellyfin;
        var index = _continue.Remove(item);
        if (!await jellyfin.SetResumePositionAsync(item.Id, 0))
        {
            _continue.Insert(index, item);
            ToastHost.Show(ToastKind.Error, L.Text("Home.RemoveFailed"));
            return;
        }
        ToastHost.Show(ToastKind.Success, L.Text("Home.RemovedFromContinueWatching"), L.Text("Toast.Undo"), async () =>
        {
            if (await jellyfin.SetResumePositionAsync(item.Id, item.ResumePositionTicks)) _continue.Insert(index, item);
        });
    }

    private void ApplyTexts()
    {
        foreach (var row in Rows) row.ApplyTexts();
        ApplySectionTexts();
        ApplyEmptyTexts();
    }
}
