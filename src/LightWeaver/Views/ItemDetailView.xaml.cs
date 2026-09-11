using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using LightWeaver.Jellyfin;
using LightWeaver.ViewModels;
using BaseItemType = Jellyfin.Sdk.Generated.Models.BaseItemDto_Type;

namespace LightWeaver.Views;

public partial class ItemDetailView : UserControl
{
    private readonly AppViewModel _app;
    private readonly Settings.AppSettings _settings;
    private MediaItem _item;

    /// <summary>Play was requested; resumeTicks 0 = from the beginning. Track selection
    /// now happens in the player during playback, so no preselection rides along.</summary>
    public event Action<MediaItem, long>? PlayRequested;

    /// <summary>A "More like this" / episode / series / season navigation was picked —
    /// route it like any other browse item.</summary>
    public event Action<MediaItem>? ItemSelected;

    /// <summary>A cast/crew face was clicked — open the Advanced Search Screen pre-filtered
    /// on that person (Phase 7 M5).</summary>
    public event Action<PersonEntry>? PersonSelected;

    /// <summary>A genre label was clicked — open the Advanced Search Screen pre-filtered
    /// on that genre, server-wide (Phase 7 M6).</summary>
    public event Action<string>? GenreSelected;

    /// <summary>Download this item (M20); MainWindow handles feedback, the resolution picker
    /// and enqueue. A null options list means the per-view preload failed; an empty one means
    /// the server answered successfully but exposed no downloadable video source.</summary>
    public event Func<ItemDetailView, MediaItem, IReadOnlyList<DownloadOption>?, Task>? DownloadRequested;

    /// <summary>Remove this item's completed download (M20); MainWindow confirms first.</summary>
    public event Action<MediaItem>? DownloadRemoveRequested;

    private Guid _similarLoadedFor;
    private bool _similarFetching;
    private Guid _streamsLoadedFor;
    private Guid _episodesLoadedFor;
    private Guid _episodesLoadingFor;
    private int _seasonLoadGeneration;
    private Guid _ratingsLoadedFor;
    private MediaSourceStreams? _streams;
    private CancellationTokenSource? _downloadOptionsCts;
    private Task<DownloadOptionsLoad>? _downloadOptionsTask;
    private int _downloadOptionsGeneration;
    private bool _downloadActionPending;
    private bool _downloadWaiting;
    /// <summary>The episode-list row for the item being viewed — kept selected as the
    /// "you are here" marker (garnet-lit, non-clickable).</summary>
    private MediaItem? _currentEpisodeRow;
    private readonly Dictionary<Guid, UserDataOverride> _userDataOverrides = [];
    private readonly Dictionary<int, int> _activeUserDataRequests = [];
    private int _userDataRevision;
    private int _nextUserDataRequestId;

    public ItemDetailView(AppViewModel app, Settings.AppSettings settings, MediaItem item)
    {
        InitializeComponent();
        _app = app;
        _settings = settings;
        _item = item;
        Render();
        SizeChanged += (_, _) => UpdateTitleScale();
        // IsVisibleChanged, not Loaded: on back-from-playback the same instance is
        // re-shown by a BrowseLayer visibility flip, which never re-raises Loaded.
        IsVisibleChanged += async (_, e) =>
        {
            if ((bool)e.NewValue)
            {
                StartDownloadOptionsPreload();
                await RefreshAsync();
            }
            else
            {
                CancelDownloadOptionsPreload();
            }
        };
    }

    private sealed record DownloadOptionsLoad(IReadOnlyList<DownloadOption>? Options, bool Cancelled);

    /// <summary>Starts one view-local preload each time this history frame becomes visible.
    /// Re-entering after Back gets a fresh request, but never inherits the old click intent.</summary>
    private void StartDownloadOptionsPreload()
    {
        CancelDownloadOptionsPreload();
        CreateDownloadOptionsPreload();
    }

    /// <summary>Creates the sole preload task for the current visible generation. Called on
    /// entry and, after a reported failure was discarded, by the next user click.</summary>
    private Task<DownloadOptionsLoad> CreateDownloadOptionsPreload()
    {
        var cts = new CancellationTokenSource();
        _downloadOptionsCts = cts;
        return _downloadOptionsTask = LoadDownloadOptionsAsync(cts.Token);
    }

    private async Task<DownloadOptionsLoad> LoadDownloadOptionsAsync(CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("detail",
            $"event=load outcome=start section=download-options item={_item.Id:N} type={_item.Type}");
        try
        {
            var options = await _app.Jellyfin.GetDownloadOptionsAsync(_item.Id, cancellationToken);
            Diagnostics.AppLog.Detail("detail",
                $"event=load outcome=success section=download-options elapsed_ms={started.ElapsedMilliseconds} count={options.Count} item={_item.Id:N} type={_item.Type}");
            return new(options, Cancelled: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Diagnostics.AppLog.Detail("detail",
                $"event=load outcome=cancelled section=download-options elapsed_ms={started.ElapsedMilliseconds} item={_item.Id:N} type={_item.Type}");
            return new(null, Cancelled: true);
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Detail("detail",
                $"event=load outcome=failure section=download-options elapsed_ms={started.ElapsedMilliseconds} item={_item.Id:N} type={_item.Type}", ex);
            return new(null, Cancelled: false);
        }
    }

    /// <summary>Invalidates both the current fetch and any click awaiting it. Cancellation is
    /// an intentional navigation outcome, not an empty result or a reason to retry.</summary>
    private void CancelDownloadOptionsPreload()
    {
        _downloadOptionsGeneration++;
        _downloadOptionsCts?.Cancel();
        _downloadOptionsCts?.Dispose();
        _downloadOptionsCts = null;
        _downloadOptionsTask = null;
        _downloadActionPending = false;
        _downloadWaiting = false;
        RefreshDownloadAction();
    }

    /// <summary>Forget only the failed task that this click actually reported. Reference and
    /// generation guards prevent a stale completion from clearing a newer re-entry/retry.</summary>
    private void DiscardDownloadOptionsFailure(int generation, Task<DownloadOptionsLoad> loadTask)
    {
        if (generation != _downloadOptionsGeneration || !IsVisible
            || !ReferenceEquals(_downloadOptionsTask, loadTask))
            return;
        _downloadOptionsCts?.Dispose();
        _downloadOptionsCts = null;
        _downloadOptionsTask = null;
    }

    /// <summary>Re-fetch for fresh UserData (resume position may have changed after playback)
    /// plus the once-per-item media pills, cast season list and "more like this".</summary>
    private async Task RefreshAsync()
    {
        var itemId = _item.Id;
        var profileKey = _app.ActiveSessionKey;
        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("detail",
            $"event=load outcome=start section=item item={_item.Id:N} type={_item.Type}");
        var userDataRequestId = 0;
        try
        {
            // Let an in-flight stop report land first, or our GET returns the
            // pre-stop resume position (capped so a dead server can't stall us).
            if (_app.PendingStopReport is { } pending)
            {
                await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(10)));
                _app.PendingStopReport = null;
            }
            if (profileKey != _app.ActiveSessionKey || _item.Id != itemId)
                return;
            userDataRequestId = BeginUserDataRequest();
            var requestUserDataRevision = _activeUserDataRequests[userDataRequestId];

            if (await _app.Jellyfin.GetItemAsync(itemId) is { } fresh)
            {
                if (profileKey != _app.ActiveSessionKey || _item.Id != itemId)
                    return;
                _item = ReconcileNetworkUserData([fresh], requestUserDataRevision)[0];
                Render();
                Diagnostics.AppLog.Detail("detail",
                    $"event=load outcome=success section=item elapsed_ms={started.ElapsedMilliseconds} item={_item.Id:N} type={_item.Type}");
            }
            else
            {
                Diagnostics.AppLog.Detail("detail",
                    $"event=load outcome=empty section=item elapsed_ms={started.ElapsedMilliseconds} item={_item.Id:N} type={_item.Type}");
            }
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Detail("detail",
                $"event=load outcome=failure section=item elapsed_ms={started.ElapsedMilliseconds} item={_item.Id:N} type={_item.Type}", ex);
            // stale copy is still renderable
        }
        finally
        {
            if (userDataRequestId != 0)
                EndUserDataRequest(userDataRequestId);
        }

        // Load the independent sections CONCURRENTLY (each fetched once per item, updating
        // only its own region) so a slow one — e.g. OMDB, up to a 15 s timeout — never
        // blocks the others. Previously these ran in sequence and the whole view sat empty.
        _ = LoadStreamsAsync();
        _ = LoadSeasonAsync();
        _ = LoadSimilarAsync();
        _ = LoadRatingsAsync();
    }

    /// <summary>Media streams → metadata gem pills (once per item).</summary>
    private async Task LoadStreamsAsync()
    {
        if (_streamsLoadedFor == _item.Id)
            return;
        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("detail",
            $"event=load outcome=start section=streams item={_item.Id:N} type={_item.Type}");
        try
        {
            if (await _app.Jellyfin.GetMediaStreamsAsync(_item.Id) is { } streams)
            {
                _streamsLoadedFor = _item.Id;
                _streams = streams;
                BuildMetaRow();
                Diagnostics.AppLog.Detail("detail",
                    $"event=load outcome=success section=streams elapsed_ms={started.ElapsedMilliseconds} video={streams.Video.Count} audio={streams.Audio.Count} subtitles={streams.Subtitles.Count} item={_item.Id:N} type={_item.Type}");
            }
            else
            {
                Diagnostics.AppLog.Detail("detail",
                    $"event=load outcome=empty section=streams elapsed_ms={started.ElapsedMilliseconds} item={_item.Id:N} type={_item.Type}");
            }
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Detail("detail",
                $"event=load outcome=failure section=streams elapsed_ms={started.ElapsedMilliseconds} item={_item.Id:N} type={_item.Type}", ex);
        }
    }

    /// <summary>Inline season episode list for episode details (once per item).</summary>
    private async Task LoadSeasonAsync()
    {
        if (_item.Type != BaseItemType.Episode || _episodesLoadedFor == _item.Id
            || _episodesLoadingFor == _item.Id
            || _item.SeriesId is not { } seriesId
            || (_item.SeasonId ?? _item.ParentId) is not { } seasonId)
            return;
        var itemId = _item.Id;
        var profileKey = _app.ActiveSessionKey;
        var generation = ++_seasonLoadGeneration;
        _episodesLoadingFor = itemId;
        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("detail",
            $"event=load outcome=start section=episodes item={_item.Id:N} type={_item.Type}");
        var cacheKey = $"browse:{profileKey}:detail-episodes:{seriesId:N}:{seasonId:N}";
        var cached = await Imaging.MetadataCache.ReadAsync<Imaging.BrowseCacheEntry<MediaItem>>(cacheKey);
        if (!IsCurrentSeasonLoad(itemId, profileKey, generation))
        {
            if (_episodesLoadingFor == itemId)
                _episodesLoadingFor = Guid.Empty;
            return;
        }
        if (cached is not null)
        {
            RenderSeason(ReconcileCachedUserData(cached.Items));
        }

        // Tier 3: the page around this list is already painted, so the skeleton waits out
        // the reveal gate and is cancelled outright if the episodes beat it. `done` is
        // per-invocation rather than a field - the continuation closes over it, so a load
        // that has already finished can never be re-revealed by its own stale timer.
        var done = false;
        if (EpisodesList.Items.Count == 0)
        {
            _ = Task.Delay(SkeletonFactory.RevealDelayMs).ContinueWith(_ =>
            {
                if (done || !IsCurrentSeasonLoad(itemId, profileKey, generation)) return;
                SeasonSkeleton.Content = SkeletonFactory.Rows(this, 3, "DetailEpisodesSkeleton");
                SeasonSkeleton.Visibility = Visibility.Visible;
                SeasonSection.Visibility = Visibility.Visible;
                // UpdateContentLayout is NOT optional here. Until a season renders, the episodes
                // column is pinned to width 0 so the cast grid can span full width - so making
                // the section Visible without re-running it lays the skeleton out inside a
                // zero-width column: present in the UIA tree, invisible on screen. Caught by
                // screenshot review on 2026-08-02; the UIA assertion had passed, because the
                // marker exists in the tree whatever width its column has.
                UpdateContentLayout();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        var userDataRequestId = 0;
        try
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("LIGHTWEAVER_SLOW_LOAD_MS"), out var slowMs) && slowMs > 0)
                await Task.Delay(slowMs);
            if (!IsCurrentSeasonLoad(itemId, profileKey, generation))
                return;
            userDataRequestId = BeginUserDataRequest();
            var requestUserDataRevision = _activeUserDataRequests[userDataRequestId];
            var episodes = await _app.Jellyfin.GetEpisodesAsync(seriesId, seasonId);
            if (!IsCurrentSeasonLoad(itemId, profileKey, generation))
                return;
            episodes = ReconcileNetworkUserData(episodes, requestUserDataRevision);
            _ = Imaging.MetadataCache.StoreAsync(cacheKey,
                new Imaging.BrowseCacheEntry<MediaItem>(episodes, episodes.Count, DateTime.UtcNow));
            RenderSeason(episodes);
            _episodesLoadedFor = itemId;
            Diagnostics.AppLog.Detail("detail",
                $"event=load outcome=success section=episodes elapsed_ms={started.ElapsedMilliseconds} count={episodes.Count} item={_item.Id:N} type={_item.Type}");
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Detail("detail",
                $"event=load outcome=failure section=episodes elapsed_ms={started.ElapsedMilliseconds} item={_item.Id:N} type={_item.Type}", ex);
        }
        finally
        {
            if (userDataRequestId != 0)
                EndUserDataRequest(userDataRequestId);
            if (IsCurrentSeasonLoad(itemId, profileKey, generation))
            {
                done = true;
                _episodesLoadingFor = Guid.Empty;
                SeasonSkeleton.Visibility = Visibility.Collapsed;
                SeasonSkeleton.Content = null;
                // RenderSeason settles the section on both its own paths, but it never runs if
                // the fetch threw - and the reveal may have opened the column by then. Close it
                // again rather than leaving an empty section holding the cast grid narrow.
                if (EpisodesList.Items.Count == 0)
                {
                    SeasonSection.Visibility = Visibility.Collapsed;
                    UpdateContentLayout();
                }
            }
            else if (generation == _seasonLoadGeneration && _episodesLoadingFor == itemId)
            {
                _episodesLoadingFor = Guid.Empty;
            }
        }
    }

    /// <summary>"More like this" (once per item; the endpoint tie-breaks equal scores randomly).</summary>
    private async Task LoadSimilarAsync()
    {
        // _similarLoadedFor is only stamped on SUCCESS, so it cannot guard re-entrancy on
        // its own - a second call arriving while the first is in flight would start another
        // fetch and fight over the skeleton.
        if (_similarLoadedFor == _item.Id || _similarFetching)
            return;
        _similarFetching = true;
        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("detail",
            $"event=load outcome=start section=similar item={_item.Id:N} type={_item.Type}");
        var done = false;
        _ = Task.Delay(SkeletonFactory.RevealDelayMs).ContinueWith(_ =>
        {
            if (done) return;
            // withTitle:false - the real "More like this" header is already on screen above
            // this, and a placeholder heading under a real one reads as a duplicate.
            SimilarSkeleton.Content = SkeletonFactory.Rail(this, 5, landscape: false, "DetailSimilarSkeleton", withTitle: false);
            SimilarSkeleton.Visibility = Visibility.Visible;
            SimilarSection.Visibility = Visibility.Visible;
        }, TaskScheduler.FromCurrentSynchronizationContext());
        try
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("LIGHTWEAVER_SLOW_LOAD_MS"), out var slowMs) && slowMs > 0)
                await Task.Delay(slowMs);
            var similar = await _app.Jellyfin.GetSimilarAsync(_item.Id);
            _similarLoadedFor = _item.Id;
            SimilarRow.ItemsSource = similar;
            SimilarSection.Visibility = similar.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Diagnostics.AppLog.Detail("detail",
                $"event=load outcome=success section=similar elapsed_ms={started.ElapsedMilliseconds} count={similar.Count} item={_item.Id:N} type={_item.Type}");
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Detail("detail",
                $"event=load outcome=failure section=similar elapsed_ms={started.ElapsedMilliseconds} item={_item.Id:N} type={_item.Type}", ex);
        }
        finally
        {
            done = true;
            _similarFetching = false;
            SimilarSkeleton.Visibility = Visibility.Collapsed;
            SimilarSkeleton.Content = null;
            // A skeleton shown for a section that turns out to be empty must not leave the
            // section open around nothing.
            if (_similarLoadedFor != _item.Id || SimilarRow.Items.Count == 0)
                SimilarSection.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>External critic scores (OMDB), only with a key + IMDb id (once per item).</summary>
    private async Task LoadRatingsAsync()
    {
        if (_ratingsLoadedFor == _item.Id
            || _settings.OmdbApiKey is not { Length: > 0 } key
            || _item.ImdbId is not { Length: > 0 } imdb)
            return;
        _ratingsLoadedFor = _item.Id;
        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("detail",
            $"event=load outcome=start section=ratings item={_item.Id:N} type={_item.Type}");
        try
        {
            var ratings = await Metadata.OmdbClient.GetByImdbIdAsync(imdb, key);
            RenderRatings(ratings);
            Diagnostics.AppLog.Detail("detail",
                $"event=load outcome=success section=ratings elapsed_ms={started.ElapsedMilliseconds} found={ratings is not null} item={_item.Id:N} type={_item.Type}");
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Detail("detail",
                $"event=load outcome=failure section=ratings elapsed_ms={started.ElapsedMilliseconds} item={_item.Id:N} type={_item.Type}", ex);
        }
    }

    private void Render()
    {
        TitleText.Text = _item.Name;
        UpdateTitleScale();
        RenderEyebrow();
        BuildMetaRow();
        RenderActions();

        OverviewText.Text = _item.Overview ?? "";
        RenderGenres();

        LoadCachedImage(PosterImage, PosterUrlFor(_item), PosterUrlFor);
        LoadCachedImage(BackdropImage, _item.BackdropUrl, i => i.BackdropUrl);

        PeopleRow.ItemsSource = _item.People.Take(18).ToList();
        CastSection.Visibility = _item.People.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateContentLayout();
    }

    /// <summary>M5.2: step the display title down at narrow content widths so a long
    /// (episode) title holds ~2 lines and the metadata + action row stay above the fold.</summary>
    private void UpdateTitleScale()
    {
        var narrow = ActualWidth > 0 && ActualWidth < 900;
        TitleText.FontSize = narrow ? 34 : 48;
        var lineHeight = narrow ? 38d : 52d;
        TitleText.LineHeight = lineHeight;
        // WPF TextBlock has no MaxLines; cap at 2 lines via MaxHeight + the existing
        // TextTrimming=CharacterEllipsis so an over-long title ellipsizes on line 2.
        TitleText.MaxHeight = lineHeight * 2;
    }

    /// <summary>Eyebrow above the title: for an episode the clickable Series · Season · E#
    /// (keeps DetailSeriesLink / DetailSeasonLink); for everything else the type word.</summary>
    private void RenderEyebrow()
    {
        EyebrowText.Inlines.Clear();
        if (_item.Type != BaseItemType.Episode)
        {
            EyebrowText.Text = TypeLabel(_item.Type);
            return;
        }

        AppendEyebrowSegment(_item.SeriesName, "DetailSeriesLink",
            _item.SeriesId is { } ? OpenSeries : null);
        var seasonLabel = _item.SeasonName
            ?? (_item.ParentIndexNumber is { } s ? $"Season {s}" : null);
        AppendEyebrowSegment(seasonLabel, "DetailSeasonLink",
            (_item.SeasonId ?? _item.ParentId) is { } && _item.SeriesId is { } ? OpenSeason : null);
        AppendEyebrowSegment(_item.IndexNumber is { } e ? $"E{e}" : null, null, null);
    }

    private static string TypeLabel(BaseItemType type) => type switch
    {
        BaseItemType.Movie => "MOVIE",
        BaseItemType.MusicVideo => "MUSIC VIDEO",
        BaseItemType.Audio => "AUDIO",
        BaseItemType.MusicAlbum => "ALBUM",
        BaseItemType.MusicArtist => "ARTIST",
        BaseItemType.Video => "VIDEO",
        _ => type.ToString().ToUpperInvariant(),
    };

    /// <summary>Adds a "  ·  "-separated eyebrow segment; with an action it becomes a link
    /// (inherited stormlight foreground, underline on hover).</summary>
    private void AppendEyebrowSegment(string? text, string? automationId, Action? navigate)
    {
        if (string.IsNullOrEmpty(text))
            return;
        if (EyebrowText.Inlines.Count > 0)
            EyebrowText.Inlines.Add(new Run("   ·   "));
        if (navigate is null)
        {
            EyebrowText.Inlines.Add(new Run(text));
            return;
        }
        EyebrowText.Inlines.Add(MakeLink(text, EyebrowText.Foreground, navigate, automationId));
    }

    /// <summary>Builds a hover-underlined navigation hyperlink (inherited/explicit foreground,
    /// underline on hover). Shared by the eyebrow, the season "view all" link, and the
    /// per-genre links (M6).</summary>
    private Hyperlink MakeLink(string text, Brush foreground, Action navigate, string? automationId = null)
    {
        var link = new Hyperlink(new Run(text))
        {
            Foreground = foreground,
            TextDecorations = null,
        };
        link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
        link.MouseLeave += (_, _) => link.TextDecorations = null;
        link.Click += (_, _) => navigate();
        if (automationId is not null)
            AutomationProperties.SetAutomationId(link, automationId);
        return link;
    }

    /// <summary>Renders the genre row as per-genre clickable links; a click opens the
    /// Advanced Search Screen pre-filtered on that genre, server-wide (Phase 7 M6).</summary>
    private void RenderGenres()
    {
        GenresText.Inlines.Clear();
        for (var i = 0; i < _item.Genres.Count; i++)
        {
            if (i > 0)
                GenresText.Inlines.Add(new Run("   ·   "));
            var genre = _item.Genres[i];
            GenresText.Inlines.Add(
                MakeLink(genre, GenresText.Foreground, () =>
                {
                    Diagnostics.AppLog.Detail("detail",
                        $"event=interaction action=open-genre item={_item.Id:N} type={_item.Type}");
                    GenreSelected?.Invoke(genre);
                }, "DetailGenreLink"));
        }
    }

    /// <summary>Metadata row: exact release date / year · runtime · rating box · community score,
    /// then the glowing media gem pills once the streams have loaded.</summary>
    private void BuildMetaRow()
    {
        MetaRow.Children.Clear();
        var releaseText = !string.IsNullOrEmpty(_item.PlaybackRelease)
            ? _item.PlaybackRelease
            : _item.ProductionYear?.ToString();
        AddMetaText(releaseText, (Brush)FindResource("LwText2Brush"));
        AddMetaText(_item.RuntimeDisplay, (Brush)FindResource("LwText2Brush"));
        AddRatingBox(_item.OfficialRating);
        if (_item.CommunityRating is { } r)
            AddMetaText(r.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                        (Brush)FindResource("LwCommunityScoreBrush"));
        RenderMediaPills();
    }

    private void MetaSeparator()
    {
        if (MetaRow.Children.Count == 0)
            return;
        MetaRow.Children.Add(new TextBlock
        {
            Text = "·",
            FontSize = 14,
            Foreground = (Brush)FindResource("LwMetaDotBrush"),
            Margin = new Thickness(9, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
    }

    private void AddMetaText(string? text, Brush fg)
    {
        if (string.IsNullOrEmpty(text))
            return;
        MetaSeparator();
        MetaRow.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 14,
            Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center,
        });
    }

    private void AddRatingBox(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        MetaSeparator();
        MetaRow.Children.Add(new Border
        {
            BorderBrush = (Brush)FindResource("LwRatingBoxBorderBrush"),
            BorderThickness = new Thickness(1),
            // Stays literal 4 on purpose (P10 M7): a checkbox-scale element. LwR1 is 8 and
            // would over-round a box this small - the same call as the check-square in M6.
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 0, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = (Brush)FindResource("LwText2Brush"),
            },
        });
    }

    /// <summary>Resolution / native-HDR / audio gem pills from the default media source
    /// (skipped until the streams land, e.g. never for a bare local file).</summary>
    private void RenderMediaPills()
    {
        if (_streams is not { Summary: { } summary })
            return;

        if (Field(summary, "Resolution") is { } res && ResolutionLabel(res) is { } resLabel)
            AddGemPill(resLabel, amber: false);
        if (Field(summary, "Range") is { } range && !range.Equals("SDR", StringComparison.OrdinalIgnoreCase))
            AddGemPill(range, amber: true);
        if (Field(summary, "Audio") is { Length: > 0 } audio)
            AddGemPill(audio, amber: false);
    }

    private static string? Field(List<MediaInfoField> summary, string label)
        => summary.FirstOrDefault(f => f.Label == label)?.Value;

    /// <summary>"1920x1080" → a resolution badge (4K / 1080p / 720p / SD).</summary>
    private static string? ResolutionLabel(string resolution)
    {
        var x = resolution.IndexOf('x');
        if (x <= 0 || !int.TryParse(resolution.AsSpan(0, x), out var w))
            return null;
        return w >= 3400 ? "4K" : w >= 1900 ? "1080p" : w >= 1200 ? "720p" : "SD";
    }

    private void AddGemPill(string text, bool amber)
    {
        var fill = (Brush)FindResource(amber ? "LwGemAmberFillBrush" : "LwGemBlueFillBrush");
        var border = (Brush)FindResource(amber ? "LwGemAmberBorderBrush" : "LwGemBlueBorderBrush");
        var fg = (Brush)FindResource(amber ? "LwGemAmberTextBrush" : "LwGemBlueTextBrush");
        var glow = (Effect)FindResource(amber ? "LwGlowPillAmber" : "LwGlowPillBlue");
        var pill = new Border
        {
            Height = 22,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = fill,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = (FontFamily)FindResource("LwFontMono"),
                FontSize = 10.5,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = glow,
            },
        };
        // P10 M7: this was CornerRadius(999) and rendered as an ELLIPSE, measured at 8x on
        // the detail meta row ("1080p", "DD 5.1"). Border clamps the horizontal arc to
        // width/2 and the vertical to height/2 INDEPENDENTLY, so 999 on anything wider than
        // tall is a lens, not a stadium - the same finding M5 made on LwSearchField, which
        // the XAML sweep could not reach here. Routing LwRPill (also 999) through this site
        // would have changed nothing; height/2 is the only value that binds both clamps, so
        // it goes through the same converter the XAML pill family uses. Bound rather than
        // hard-coded to 11 so a change to Height above cannot silently reintroduce the lens.
        pill.SetBinding(Border.CornerRadiusProperty, new Binding(nameof(FrameworkElement.ActualHeight))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.Self),
            Converter = (IValueConverter)FindResource("LwRFullFromHeight"),
        });
        MetaRow.Children.Add(pill);
    }

    /// <summary>Renders up to three OMDB critic-score chips beside the community score.
    /// A null/empty result leaves the row collapsed (blank key, no IMDb id, no match).</summary>
    private void RenderRatings(Metadata.ExternalRatings? ratings)
    {
        RatingsRow.Children.Clear();
        if (ratings is not { HasAny: true })
        {
            RatingsRow.Visibility = Visibility.Collapsed;
            return;
        }
        if (ratings.Imdb is { } imdb)
            // IMDb ratings are out of 10; the logo implies the scale, so drop the "/10".
            RatingsRow.Children.Add(LogoChip("imdb.png", imdb.Split('/')[0], "IMDb", "RatingChipImdb"));
        if (ratings.RottenTomatoes is { } rt)
            RatingsRow.Children.Add(LogoChip("rottentomatoes.png", rt, "Rotten Tomatoes", "RatingChipRt"));
        if (ratings.Metacritic is { } mc)
            RatingsRow.Children.Add(MetacriticChip(mc, "RatingChipMc"));
        RatingsRow.Visibility = Visibility.Visible;
    }

    /// <summary>A rating chip: the provider's logo (rounded 16px) + the score, in a subtle
    /// storm pill. The AutomationId sits on the score TextBlock (a Border/StackPanel has no
    /// UIA peer); the logo carries the provider name for accessibility.</summary>
    private FrameworkElement LogoChip(string file, string score, string provider, string automationId)
    {
        var logo = new Image
        {
            Source = new BitmapImage(new Uri($"pack://application:,,,/Theme/Logos/{file}")),
            Width = 16,
            Height = 16,
            Stretch = Stretch.UniformToFill,
        };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        AutomationProperties.SetName(logo, provider);
        // P10 M7: the radius stays literal 3 (a 16px box; any ladder step swallows it), but
        // the logo underneath was rendering SQUARE - measured at 8x, the IMDb yellow and the
        // Rotten Tomatoes red both had hard 90-degree corners while this method's own summary
        // called the logo "rounded 16px". ClipToBounds does NOT round-clip; it clips to the
        // rectangular bounds. Same defect and same fix as M6's card artwork, and the clip
        // must sit on a container - never the Image, whose ArrangeOverride reports the
        // stretch-computed size rather than the slot (see Theme/RoundedClip).
        var logoHost = new Grid();
        logoHost.Children.Add(logo);
        Theme.RoundedClip.SetRadius(logoHost, 3d);
        var logoBox = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(3),
            Child = logoHost,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var text = new TextBlock
        {
            Text = score,
            Margin = new Thickness(6, 0, 0, 0),
            FontFamily = (FontFamily)FindResource("LwFontMono"),
            FontSize = 11.5,
            Foreground = (Brush)FindResource("LwText1Brush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(text, automationId);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(logoBox);
        panel.Children.Add(text);
        return new Border
        {
            Height = 24,
            CornerRadius = (CornerRadius)FindResource("LwR1"),   // P10 M7: on the ladder
            Padding = new Thickness(5, 0, 9, 0),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)FindResource("LwStorm2Brush"),
            BorderBrush = (Brush)FindResource("LwHairlineBrush"),
            BorderThickness = new Thickness(1),
            Child = panel,
        };
    }

    /// <summary>Metacritic's signature score box: the number in a band-colored rounded
    /// square (green ≥61 / yellow 40–60 / red ≤39), matching how Metacritic shows scores.</summary>
    private FrameworkElement MetacriticChip(string score, string automationId)
    {
        var color = int.TryParse(score, out var n)
            ? (n >= 61 ? Color.FromRgb(0x66, 0xCC, 0x33)
                : n >= 40 ? Color.FromRgb(0xFF, 0xCC, 0x33)
                : Color.FromRgb(0xFF, 0x00, 0x00))
            : Color.FromRgb(0x66, 0xCC, 0x33);
        var text = new TextBlock
        {
            Text = score,
            FontFamily = (FontFamily)FindResource("LwFontMono"),
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(text, automationId);
        return new Border
        {
            MinWidth = 24,
            Height = 24,
            CornerRadius = (CornerRadius)FindResource("LwR1"),   // P10 M7: on the ladder
            Padding = new Thickness(7, 0, 7, 0),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(color),
            Child = text,
        };
    }

    private void RenderActions()
    {
        if (_item.ResumePositionTicks > 0)
        {
            var pos = TimeSpan.FromTicks(_item.ResumePositionTicks);
            var stamp = pos.TotalHours >= 1 ? pos.ToString(@"h\:mm\:ss") : pos.ToString(@"m\:ss");
            ResumeButton.Content = PlayGlyphLabel($"Resume {stamp}");
            ResumeButton.Visibility = Visibility.Visible;
            PlayButton.Content = "From start";
            PlayButton.Style = (Style)FindResource("LwButtonSecondary");
        }
        else
        {
            ResumeButton.Visibility = Visibility.Collapsed;
            PlayButton.Content = PlayGlyphLabel("Play");
            PlayButton.Style = (Style)FindResource("LwButtonPrimary");
        }

        if (_item.TrailerUrl is { Length: > 0 })
        {
            TrailerButton.Content = GlyphLabel("IconTrailer", "Trailer");
            TrailerButton.Visibility = Visibility.Visible;
        }
        else
        {
            TrailerButton.Visibility = Visibility.Collapsed;
        }

        FavoriteButton.Content = (string)FindResource(_item.IsFavorite ? "IconFavorite" : "IconFavoriteBorder");
        FavoriteButton.Foreground = _item.IsFavorite
            ? (Brush)FindResource("LwGarnetLitBrush")
            : (Brush)FindResource("LwText2Brush");
        AutomationProperties.SetName(FavoriteButton, _item.IsFavorite ? "Favorite" : "Add to favorites");
        FavoriteButton.ToolTip = _item.IsFavorite ? "Remove from favorites" : "Add to favorites";

        // M5.1: filled = watched / outline = unwatched (the same glyph-pair rule the
        // favorite heart uses), not a color-only flip.
        WatchedButton.Content = (string)FindResource(_item.Played ? "IconWatched" : "IconWatchedBorder");
        WatchedButton.Foreground = _item.Played
            ? (Brush)FindResource("LwEmeraldLitBrush")
            : (Brush)FindResource("LwText2Brush");
        AutomationProperties.SetName(WatchedButton, _item.Played ? "Watched" : "Mark watched");
        WatchedButton.ToolTip = _item.Played ? "Mark as unwatched" : "Mark as watched";

        // The overflow button appears only when the overflow menu would have something in it.
        // Same predicate the menu builds from, deliberately, so the two cannot disagree and
        // leave a button that opens an empty popup.
        MoreButton.Visibility = HasOverflowActions ? Visibility.Visible : Visibility.Collapsed;

        RefreshDownloadAction();
    }

    /// <summary>Whether the overflow menu has any entry to show. Episode navigation is all
    /// that is left in it since 2026-08-05, when the watched / favorite duplicates came out:
    /// they invoked the same two handlers as the icon buttons sitting beside the overflow
    /// button, so the menu restated two controls that were already on screen. Removing them
    /// emptied the menu for every movie, series, season and playlist, hence this gate.
    /// <para>Reduces to "an episode that knows its series": "Go to series" needs
    /// <c>SeriesId</c>, and "Go to season" needs <c>SeriesId</c> as well as a season, so
    /// <c>SeriesId</c> alone decides whether either entry can be built.</para></summary>
    private bool HasOverflowActions => _item.Type == BaseItemType.Episode && _item.SeriesId is { };

    /// <summary>Download affordance (M20): sapphire download-done once the item's local
    /// copy exists, plain download glyph otherwise. Re-rendered with the rest of the
    /// actions on every refresh (IsVisibleChanged), so completion shows on return.</summary>
    private void RefreshDownloadAction()
    {
        var downloaded = Downloads.DownloadManager.Instance?.IsDownloaded(_item.Id) == true;
        DownloadButton.IsEnabled = !_downloadActionPending;
        DownloadGlyph.Visibility = _downloadWaiting ? Visibility.Collapsed : Visibility.Visible;
        DownloadSpinner.Visibility = _downloadWaiting ? Visibility.Visible : Visibility.Collapsed;
        DownloadGlyph.Text = (string)FindResource(downloaded ? "IconDownloadDone" : "IconDownload");
        DownloadButton.Foreground = downloaded
            ? (Brush)FindResource("LwSapphireLitBrush")
            : (Brush)FindResource("LwText2Brush");
        AutomationProperties.SetName(DownloadButton, _downloadWaiting
            ? "Loading download options"
            : downloaded ? "Downloaded" : "Download");
        DownloadButton.ToolTip = _downloadWaiting
            ? "Loading download options"
            : downloaded ? "Downloaded — click to remove" : "Download";
    }

    /// <summary>Downloaded already → offer removal; otherwise hand off to MainWindow's
    /// resolution-picker flow via <see cref="DownloadRequested"/>.</summary>
    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (Downloads.DownloadManager.Instance is { } manager && manager.IsDownloaded(_item.Id))
        {
            Diagnostics.AppLog.Detail("detail",
                $"event=interaction action=remove-download item={_item.Id:N} type={_item.Type}");
            DownloadRemoveRequested?.Invoke(_item);
            return;
        }
        if (_downloadActionPending || !IsVisible)
            return;

        var loadTask = _downloadOptionsTask ?? CreateDownloadOptionsPreload();
        Diagnostics.AppLog.Detail("detail",
            $"event=interaction action=download item={_item.Id:N} type={_item.Type} preload_ready={loadTask.IsCompleted}");
        var generation = _downloadOptionsGeneration;
        _downloadActionPending = true;
        _downloadWaiting = !loadTask.IsCompleted;
        RefreshDownloadAction();
        try
        {
            var result = await loadTask;
            if (generation != _downloadOptionsGeneration || !IsVisible || result.Cancelled)
                return;

            // Loading has finished. Restore the glyph before MainWindow opens the picker,
            // while keeping the button disabled until that one action fully settles.
            _downloadWaiting = false;
            RefreshDownloadAction();
            if (DownloadRequested is { } requested)
                await requested(this, _item, result.Options);
            if (result.Options is null)
                DiscardDownloadOptionsFailure(generation, loadTask);
        }
        finally
        {
            if (generation == _downloadOptionsGeneration)
            {
                _downloadActionPending = false;
                _downloadWaiting = false;
                RefreshDownloadAction();
            }
        }
    }

    /// <summary>Refresh the download glyph after MainWindow settles a download action.</summary>
    public void RefreshDownloadState() => RefreshDownloadAction();

    /// <summary>Renders the inline season episode list and its "view all" link.</summary>
    private void RenderSeason(List<MediaItem> episodes)
    {
        if (episodes.Count == 0)
        {
            SeasonSection.Visibility = Visibility.Collapsed;
            UpdateContentLayout();
            return;
        }

        SeasonHeader.Text = _item.SeasonName
            ?? (_item.ParentIndexNumber is { } s ? $"Season {s}" : "Episodes");
        EpisodesList.ItemsSource = episodes;
        // Mark the episode being viewed as the current one (garnet-lit, non-clickable).
        _currentEpisodeRow = episodes.FirstOrDefault(ep => ep.Id == _item.Id);
        EpisodesList.SelectedItem = _currentEpisodeRow;

        SeasonViewAll.Inlines.Clear();
        if ((_item.SeasonId ?? _item.ParentId) is { } && _item.SeriesId is { })
        {
            var link = MakeLink($"View all {episodes.Count} episodes",
                (Brush)FindResource("LwLightBaseBrush"), OpenSeason);
            link.FontSize = 13;
            SeasonViewAll.Inlines.Add(link);
        }

        SeasonSection.Visibility = Visibility.Visible;
        UpdateContentLayout();
    }

    /// <summary>Two-column layout: episodes take the main column with cast in a fixed
    /// 250-px rail; with no episode list the cast grid spans full width.</summary>
    private void UpdateContentLayout()
    {
        if (SeasonSection.Visibility == Visibility.Visible)
        {
            EpisodesColumn.Width = new GridLength(1, GridUnitType.Star);
            CastColumn.Width = new GridLength(250);
        }
        else
        {
            EpisodesColumn.Width = new GridLength(0);
            CastColumn.Width = new GridLength(1, GridUnitType.Star);
        }
    }

    /// <summary>Navigate to the show's season list. A minimal local MediaItem is enough:
    /// MainWindow routes browsables into a LibraryView, which only needs Id (+ Name).</summary>
    private void OpenSeries()
    {
        if (_item.SeriesId is not { } seriesId)
            return;
        Diagnostics.AppLog.Detail("detail", $"event=interaction action=open-series item={seriesId:N} type=Series");
        ItemSelected?.Invoke(new MediaItem
        {
            Id = seriesId,
            Name = _item.SeriesName ?? "Series",
            Type = BaseItemType.Series,
        });
    }

    /// <summary>Navigate to the season's episode list (LibraryView's Season branch
    /// needs SeriesId alongside the season id).</summary>
    private void OpenSeason()
    {
        if ((_item.SeasonId ?? _item.ParentId) is not { } seasonId || _item.SeriesId is not { } seriesId)
            return;
        Diagnostics.AppLog.Detail("detail", $"event=interaction action=open-season item={seasonId:N} type=Season");
        ItemSelected?.Invoke(new MediaItem
        {
            Id = seasonId,
            Name = _item.SeasonName
                ?? (_item.ParentIndexNumber is { } s ? $"Season {s}" : "Season"),
            Type = BaseItemType.Season,
            SeriesId = seriesId,
            SeriesName = _item.SeriesName,
            ParentIndexNumber = _item.ParentIndexNumber,
        });
    }

    /// <summary>Detail poster URL: an episode uses its season's poster (a proper 2:3 image)
    /// rather than its own landscape still; everything else uses its own poster.</summary>
    private string? PosterUrlFor(MediaItem item)
        => item.Type == BaseItemType.Episode && (item.SeasonId ?? item.ParentId) is { } seasonId
            ? _app.Jellyfin.GetPrimaryImageUrl(seasonId, null)
            : item.PosterUrl;

    /// <summary>Loads via the disk cache off the UI thread; drops the result if the
    /// view has moved on to a different image URL meanwhile.</summary>
    private async void LoadCachedImage(Image target, string? url, Func<MediaItem, string?> urlOf)
    {
        if (url is null)
        {
            target.Source = null;
            return;
        }
        var bitmap = await Imaging.ImageCache.GetImageAsync(url);
        if (bitmap is not null && urlOf(_item) == url)
            target.Source = bitmap;
    }

    /// <summary>Button content that pairs the Material play glyph (icon font) with a label
    /// (the button's own font), so Resume/Play read as "▶ Play" without a per-font hack.</summary>
    private TextBlock PlayGlyphLabel(string text) => GlyphLabel("IconPlay", text);

    /// <summary>Button content pairing a Material glyph (icon font) with a label in the
    /// button's own font (e.g. the play or trailer buttons).</summary>
    private TextBlock GlyphLabel(string iconKey, string text)
    {
        var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        tb.Inlines.Add(new Run((string)FindResource(iconKey))
        {
            FontFamily = (FontFamily)FindResource("LwFontIcon"),
            BaselineAlignment = BaselineAlignment.Center,
        });
        tb.Inlines.Add(new Run("  " + text));
        return tb;
    }

    private async void OnToggleWatched(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("detail",
            $"event=interaction action=toggle-watched item={_item.Id:N} type={_item.Type} target={!_item.Played}");
        WatchedButton.IsEnabled = false;
        try
        {
            if (await _app.Jellyfin.SetPlayedAsync(_item.Id, !_item.Played)
                && await _app.Jellyfin.GetItemAsync(_item.Id) is { } fresh)
            {
                _item = fresh;
                Render();
                _app.NotifyItemUserDataChanged(fresh);
            }
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Detail("detail",
                $"event=interaction action=toggle-watched outcome=failure item={_item.Id:N} type={_item.Type}", ex);
            // toggle stays at the server state on failure
        }
        finally
        {
            WatchedButton.IsEnabled = true;
        }
    }

    private async void OnToggleFavorite(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("detail",
            $"event=interaction action=toggle-favorite item={_item.Id:N} type={_item.Type} target={!_item.IsFavorite}");
        FavoriteButton.IsEnabled = false;
        try
        {
            if (await _app.Jellyfin.SetFavoriteAsync(_item.Id, !_item.IsFavorite)
                && await _app.Jellyfin.GetItemAsync(_item.Id) is { } fresh)
            {
                _item = fresh;
                Render();
                _app.NotifyItemUserDataChanged(fresh);
            }
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Detail("detail",
                $"event=interaction action=toggle-favorite outcome=failure item={_item.Id:N} type={_item.Type}", ex);
            // toggle stays at the server state on failure
        }
        finally
        {
            FavoriteButton.IsEnabled = true;
        }
    }

    /// <summary>Overflow menu: episode navigation. It used to also carry "Mark as watched" and
    /// "Add to favorites", which called the very same handlers as the check and heart icon
    /// buttons two positions to the left — a menu restating controls already on screen. Gated
    /// by <see cref="HasOverflowActions"/>, so this never opens empty.</summary>
    private void OnMore(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("detail", $"event=interaction action=open-more item={_item.Id:N} type={_item.Type}");
        var menu = new ContextMenu { Style = (Style)FindResource("LwContextMenu") };
        if (_item.SeriesId is { })
            menu.Items.Add(MenuItemFor("Go to series", OpenSeries));
        if ((_item.SeasonId ?? _item.ParentId) is { } && _item.SeriesId is { })
            menu.Items.Add(MenuItemFor("Go to season", OpenSeason));

        menu.PlacementTarget = MoreButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private MenuItem MenuItemFor(string header, Action action)
    {
        var item = new MenuItem { Header = header, Style = (Style)FindResource("LwMenuItem") };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>The current episode is non-hit-test, so a click on it falls through to the
    /// ListBox, which focuses its selected item and asks to be scrolled into view — that
    /// jumps the page. Suppress bring-into-view for the episode list entirely.</summary>
    private void OnSuppressBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        => e.Handled = true;

    /// <summary>Hover play over an episode-row thumbnail: play THAT episode directly,
    /// resuming from its saved position when it has one (same contract as the header
    /// Resume button — ResumePositionTicks is 0 for unstarted episodes). The button
    /// swallows the click, so the row never also navigates.</summary>
    private void OnEpisodeHoverPlay(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is MediaItem ep)
        {
            Diagnostics.AppLog.Detail("detail", $"event=interaction action=play-episode item={ep.Id:N} type={ep.Type}");
            PlayRequested?.Invoke(ep, ep.ResumePositionTicks);
        }
    }

    private void OnEpisodeSelection(object sender, SelectionChangedEventArgs e)
    {
        if (EpisodesList.SelectedItem is not MediaItem item || item.Id == _item.Id)
            return;   // the current episode stays selected as the marker; ignore it
        // Restore the current-episode marker (fires this handler once more, guarded out),
        // then navigate to the picked episode.
        EpisodesList.SelectedItem = _currentEpisodeRow;
        Diagnostics.AppLog.Detail("detail", $"event=interaction action=open-episode item={item.Id:N} type={item.Type}");
        ItemSelected?.Invoke(item);
    }

    /// <summary>Inner lists (cast, episodes, more-like-this) would otherwise swallow the
    /// wheel; forward it to the page scroller so the whole detail scrolls over them.</summary>
    private void OnContentWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        // Shift+wheel scrolls a horizontal row sideways (same contract as Home rails).
        if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)
            && sender is DependencyObject d
            && WheelScroll.FindScrollViewer(d) is { ScrollableWidth: > 0 } rowScroll)
        {
            rowScroll.ScrollToHorizontalOffset(rowScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
            return;
        }
        PageScroll.ScrollToVerticalOffset(PageScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void OnSimilarSelection(object sender, SelectionChangedEventArgs e)
    {
        if (SimilarRow.SelectedItem is MediaItem item)
        {
            Diagnostics.AppLog.Detail("detail", $"event=interaction action=open-similar item={item.Id:N} type={item.Type}");
            SimilarRow.SelectedItem = null;
            ItemSelected?.Invoke(item);
        }
    }

    /// <summary>A cast/crew tile was clicked — clear the selection (so re-picking the same
    /// face fires again) and open the person's pre-filtered Advanced Search (M5). People
    /// without a resolvable id are inert.</summary>
    private void OnPersonSelection(object sender, SelectionChangedEventArgs e)
    {
        if (PeopleRow.SelectedItem is PersonEntry person)
        {
            PeopleRow.SelectedItem = null;
            if (person.Id != Guid.Empty)
            {
                Diagnostics.AppLog.Detail("detail", $"event=interaction action=open-person item={person.Id:N}");
                PersonSelected?.Invoke(person);
            }
        }
    }

    /// <summary>Opens the item's remote trailer (YouTube) in the default browser — mpv stays
    /// reserved for library media, so the load-bearing HWND rule is untouched.</summary>
    private void OnTrailer(object sender, RoutedEventArgs e)
    {
        if (_item.TrailerUrl is not { Length: > 0 } url)
            return;
        Diagnostics.AppLog.Detail("detail", $"event=interaction action=open-trailer item={_item.Id:N} type={_item.Type}");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // nothing actionable if the shell can't open the URL
        }
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("detail", $"event=interaction action=play item={_item.Id:N} type={_item.Type} resume=false");
        PlayRequested?.Invoke(_item, 0);
    }

    private void OnResume(object sender, RoutedEventArgs e)
    {
        Diagnostics.AppLog.Detail("detail", $"event=interaction action=play item={_item.Id:N} type={_item.Type} resume=true");
        PlayRequested?.Invoke(_item, _item.ResumePositionTicks);
    }

    /// <summary>Receives the shell-wide user-data fan-out so a delayed item/episode refresh
    /// cannot paint an earlier watched/resume state over an action that just completed.</summary>
    public void ApplyUserDataUpdate(MediaItem fresh)
    {
        _userDataOverrides[fresh.Id] = new UserDataOverride(++_userDataRevision, fresh);
        if (_item.Id == fresh.Id)
        {
            _item = fresh;
            Render();
        }
        if (EpisodesList.ItemsSource is List<MediaItem> episodes)
            RenderSeason(ReconcileCachedUserData(episodes));
    }

    private bool IsCurrentSeasonLoad(Guid itemId, string? profileKey, int generation)
        => _item.Id == itemId && profileKey == _app.ActiveSessionKey
            && generation == _seasonLoadGeneration;

    private List<MediaItem> ReconcileCachedUserData(IEnumerable<MediaItem> items)
        => items.Select(item => _userDataOverrides.TryGetValue(item.Id, out var update)
            ? update.Item : item).ToList();

    private List<MediaItem> ReconcileNetworkUserData(IEnumerable<MediaItem> items, int requestRevision)
        => items.Select(item => _userDataOverrides.TryGetValue(item.Id, out var update)
                && update.Revision > requestRevision ? update.Item : item)
            .ToList();

    private int BeginUserDataRequest()
    {
        var requestId = ++_nextUserDataRequestId;
        _activeUserDataRequests[requestId] = _userDataRevision;
        return requestId;
    }

    private void EndUserDataRequest(int requestId)
    {
        _activeUserDataRequests.Remove(requestId);
        foreach (var id in _userDataOverrides.Where(pair =>
                     _activeUserDataRequests.Values.All(snapshot => snapshot >= pair.Value.Revision))
                     .Select(pair => pair.Key).ToList())
            _userDataOverrides.Remove(id);
    }

    private sealed record UserDataOverride(int Revision, MediaItem Item);
}
