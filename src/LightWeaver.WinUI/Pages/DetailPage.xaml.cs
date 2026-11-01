using System.Globalization;
using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Details;
using LightWeaver.Home;
using LightWeaver.Jellyfin;
using LightWeaver.Library;
using LightWeaver.Localization;
using LightWeaver.People;
using LightWeaver.Settings;
using LightWeaver.ViewModels;
using LightWeaver.WinUI.Controls;
using LightWeaver.WinUI.Services;
using LightWeaver.WinUI.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace LightWeaver.WinUI.Pages;

public sealed partial class DetailPage : UserControl, INavPage
{
    private readonly IAppShell _shell;
    private readonly DetailLayout _layout;
    private readonly double _previewAspect;
    private readonly string _profileKey;
    private readonly ItemRemovals _removals;
    private readonly UserDataOverrides _overrides = new();
    private readonly CardRow _similar;
    private readonly CardRow _cast;
    private readonly string _preferredAudio;
    private readonly string _preferredSubtitle;
    private readonly (double Column, double Row) _childSpacing;
    private MediaItem _item;
    private MediaItem? _series;
    private MediaItem? _target;
    private MediaSourceStreams? _streams;
    private int _streamsRevision;
    private Guid? _playbackLoadedFor;
    private Guid? _selectionsFor;
    private IReadOnlyList<MediaVersion> _versions = [];
    private Guid? _versionsFor;
    private string? _versionId;
    private int? _audioIndex;
    private int? _subtitleIndex;
    private int? _quality;
    private bool _forceTranscode;
    private TimeSpan _start;
    private bool _played;
    private bool _favorite;
    private bool _loaded;
    private bool _settingStart;
    private int _generation;
    private IReadOnlyList<MediaItem> _similarItems = [];
    private IReadOnlyList<MediaItem> _castItems = [];

    public DetailPage(IAppShell shell, MediaItem item)
    {
        _shell = shell;
        _item = item;
        _layout = DetailModel.LayoutOf(item);
        _previewAspect = DetailModel.PreviewAspect(_layout, item);
        _profileKey = shell.Model.ActiveSessionKey ?? "";
        _removals = ItemRemovals.ForProfile(shell.Model.ActiveSessionKey);
        var settings = App.Settings;
        _preferredAudio = settings.PreferredAudioLanguage;
        _preferredSubtitle = settings.PreferredSubtitleLanguage;
        InitializeComponent();

        _similar = new CardRow(shell, "DetailRow", "Similar", () => L.Text("Detail.MoreLikeThis"),
            () => shell.OpenAdvancedSearch(DetailModel.LikeThis(_series ?? _item)), CardKind.Poster, Ui.Resource<double>("LwDetailPosterWidth"), _overrides)
        {
            CardSubtitle = i => i.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? "",
            EmptyText = () => L.Text("Detail.NoSimilar"),
        };
        _cast = new CardRow(shell, "DetailRow", "Cast", () => L.Text("Detail.CastAndCrew"), () => shell.OpenCast(_item), CardKind.Person, Ui.Resource<double>("LwDetailPersonWidth"), _overrides)
        {
            CardSubtitle = p => p.Overview ?? "",
            Open = p => shell.OpenPerson(p.Id, p.Name),
            EmptyText = () => L.Text("Detail.NoCast"),
        };
        SimilarPanel.Child = _similar;
        CastPanel.Child = _cast;
        LostFocus += (_, e) => { if (ReferenceEquals(e.OriginalSource, this)) IsTabStop = false; };
        _similar.HoldPlace();
        _cast.HoldPlace();

        _childSpacing = (ChildGrid.ColumnSpacing, ChildGrid.RowSpacing);
        GridHeading.Visibility = _layout == DetailLayout.Movie ? Visibility.Collapsed : Visibility.Visible;
        ShuffleButton.Visibility = _layout is DetailLayout.Series or DetailLayout.Season ? Visibility.Visible : Visibility.Collapsed;
        HoldFormatChips();
        UpNextLeadFlyout.Attach(UpNextButton, Session.ServerUrl, Session.UserId, SeriesId);
        Scroller.ViewChanged += (_, _) => PreviewMoved?.Invoke(this, EventArgs.Empty);
        Preview.SizeChanged += (_, _) => PreviewMoved?.Invoke(this, EventArgs.Empty);
        StartArea.SizeChanged += (_, _) => ShowStartBar();
        Render();
        LiveText.Observe(this, (_, _) => DispatcherQueue.TryEnqueue(ApplyTexts));
        Loaded += (_, _) =>
        {
            if (!_loaded) _ = LoadAsync();
            WatchDownloads(true);
            ApplyDownloadState();
        };
        Unloaded += (_, _) => { if (!IsLoaded) WatchDownloads(false); };
    }

    /// <summary>Coming back refreshes in the background: the header, the episode grid and the rows
    /// change only where the server's answer did, and the chosen start, version and tracks stay
    /// unless the title to play changed.</summary>
    public void OnShown() { if (_loaded) _ = LoadAsync(); }

    /// <summary>The preview area, where the player's surface sits before and after full screen.</summary>
    public FrameworkElement PreviewHost => Preview;

    /// <summary>The preview's backdrop, which stands in for the video until its first frame.</summary>
    public ImageSource? BackdropSource => Backdrop.Source;

    /// <summary>The preview moved or changed size (scrolling, a resize).</summary>
    public event EventHandler? PreviewMoved;

    /// <summary>The player sits over the preview: its compact bar replaces this page's bar.</summary>
    public void SetPlayerAttached(bool attached)
    {
        Bar.Visibility = PreviewPlay.Visibility = attached || _target is null ? Visibility.Collapsed : Visibility.Visible;
        PreviewInfo.Visibility = attached || _layout == DetailLayout.Movie || PreviewItem is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>What the preview's lines describe: the title Play starts, or a virtual page's own item.</summary>
    private MediaItem? PreviewItem => _target ?? (_item.IsVirtual ? _item : null);

    private CardScale _scale = CardScale.None;

    /// <summary>Where the preview, the rows and the side panel sit now, for the frame log's settle count.</summary>
    internal DetailGeometry Geometry
        => new(Preview.ActualOffset.X, Preview.ActualOffset.Y, Preview.ActualWidth, Preview.ActualHeight, Rows.ActualOffset.Y, Side.ActualWidth,
            Overview.ActualOffset.Y, ChildGrid.ActualOffset.Y);

    public void Reload() => _ = LoadAsync();

    public void RemoveItems(IReadOnlySet<Guid> removed)
    {
        _removals.Remove(removed);
        ShowChildren(_removals.Filter(ChildGrid.Children.OfType<MediaCard>().Select(c => c.Item).OfType<MediaItem>()));
        _similar.SetItems(_similarItems = _removals.Filter(_similarItems));
        _cast.SetItems(_castItems = _removals.Filter(_castItems));
        if (_removals.Contains(_item.Id))
        {
            _generation++;
            _streamsRevision++;
            _target = null;
            Scroller.Visibility = Visibility.Collapsed;
            RemovedMessage.Visibility = Visibility.Visible;
            return;
        }
        if (_target is { } target && _removals.Contains(target.Id))
        {
            _target = null;
            _streamsRevision++;
            RenderPreview();
            _ = LoadAsync();
        }
    }

    private JellyfinService Session => _shell.Model.Jellyfin;

    private Guid? SeriesId => _layout switch
    {
        DetailLayout.Series => _item.Id,
        DetailLayout.Season => _item.SeriesId ?? _item.ParentId,
        DetailLayout.Episode => _item.SeriesId,
        _ => null,
    };

    private async Task LoadAsync()
    {
        if (_removals.Contains(_item.Id))
        {
            RemoveItems(new HashSet<Guid>());
            return;
        }
        var generation = ++_generation;
        var refresh = _loaded;
        var (item, series, target) = (_item, _series, _target);
        try
        {
            var headerReadAt = _overrides.Mark();
            if (await Session.GetItemAsync(_item.Id) is { } fresh && generation == _generation) _item = fresh;
            if (generation != _generation) return;
            Scroller.Visibility = Visibility.Visible;
            RemovedMessage.Visibility = Visibility.Collapsed;
            _overrides.Reconcile(headerReadAt, [_item]);
            (_played, _favorite) = _overrides.Of(_item);
            _series = _layout is DetailLayout.Season or DetailLayout.Episode && SeriesId is { } seriesId ? await Session.GetItemAsync(seriesId) : null;
            if (generation != _generation) return;
            if (!refresh || !SameHeader(item, _item) || !SameHeader(series, _series)) Render();
            else ApplyTexts();

            var readAt = _overrides.Mark();
            var children = await LoadChildrenAsync(generation);
            if (generation != _generation) return;
            _overrides.Reconcile(readAt, children.Where(child => child.Id != _item.Id));
            ShowChildren(children);
            var sameTarget = refresh && target is not null && _target is not null && ItemLists.SameShown(target, _target);
            if (!sameTarget) RenderPreview();
            _loaded = true;

            var keepPlayback = sameTarget && _playbackLoadedFor == _target?.Id;
            await Task.WhenAll(keepPlayback ? Task.CompletedTask : LoadPlaybackAsync(generation), LoadSimilarAsync(generation), LoadCastAsync(generation));
        }
        catch (Exception ex)
        {
            if (generation == _generation) _shell.ReportFailure(ex);
        }
    }

    /// <summary>Whether two copies of the page's item (or its show) render the same header.</summary>
    private static bool SameHeader(MediaItem? a, MediaItem? b)
        => a is null || b is null ? a is null && b is null
            : ItemLists.SameShown(a, b) && a.CommunityRating == b.CommunityRating && a.OfficialRating == b.OfficialRating
                && a.PremiereDate == b.PremiereDate && a.TrailerUrl == b.TrailerUrl && a.ImdbId == b.ImdbId
                && a.Genres.SequenceEqual(b.Genres);

    /// <summary>Loads surviving children and updates the play target for the current request.</summary>
    private async Task<IReadOnlyList<MediaItem>> LoadChildrenAsync(int generation)
    {
        switch (_layout)
        {
            case DetailLayout.Series:
                var seasons = Session.GetSeasonsAsync(_item.Id);
                var all = Session.GetSeriesEpisodesAsync(_item.Id);
                await Task.WhenAll(seasons, all);
                if (generation != _generation) return [];
                _target = DetailModel.NextEpisode(_removals.Filter(all.Result));
                return _removals.Filter(seasons.Result);
            case DetailLayout.Season when SeriesId is { } series:
                var episodes = DetailModel.SeasonEpisodes(await Session.GetEpisodesAsync(series, _item.Id, includeVirtual: true), DateTimeOffset.Now);
                if (generation != _generation) return [];
                var surviving = _removals.Filter(episodes);
                _target = DetailModel.NextEpisode(surviving);
                return surviving;
            case DetailLayout.Episode when SeriesId is { } series && _item.SeasonId is { } season:
                var siblings = DetailModel.SeasonEpisodes(await Session.GetEpisodesAsync(series, season, includeVirtual: true), DateTimeOffset.Now);
                if (generation != _generation) return [];
                _target = _removals.Contains(_item.Id) || !_item.IsPlayable ? null : _item;
                return _removals.Filter(siblings);
            case DetailLayout.Movie:
                _target = _item;
                return [];
            default:
                _target = _item.IsPlayable ? _item : null;
                return [];
        }
    }

    private async Task LoadPlaybackAsync(int generation)
    {
        if (_target is not { } target) return;
        var revision = ++_streamsRevision;
        if (_selectionsFor != target.Id)
        {
            _streams = null;
            _versions = [];
            _versionsFor = null;
            _versionId = null;
            _playbackLoadedFor = null;
            _selectionsFor = target.Id;
            ApplyDefaultStreams();
        }
        var streams = Session.GetMediaStreamsAsync(target.Id);
        var versions = Session.GetMediaVersionsAsync(target.Id);
        await Task.WhenAll(streams, versions);
        if (generation != _generation || revision != _streamsRevision || _removals.Contains(target.Id)) return;
        var loaded = streams.Result;
        _versions = versions.Result;
        _versionsFor = target.Id;
        var remembered = MediaVersionStore.Load(_profileKey, target.Id);
        _versionId = _versions.FirstOrDefault(v => v.Id == remembered)?.Id ?? _versions.FirstOrDefault(v => v.IsDefault)?.Id;
        if (_versionId is { } versionId && loaded?.SourceId is { } sourceId && sourceId != versionId)
        {
            loaded = await Session.GetMediaStreamsAsync(target.Id, versionId);
            if (generation != _generation || revision != _streamsRevision || _versionId != versionId || _removals.Contains(target.Id)) return;
        }
        _streams = loaded;
        _playbackLoadedFor = target.Id;
        ApplyDefaultStreams();
    }

    /// <summary>The default audio and subtitle picks for the streams shown.</summary>
    private void ApplyDefaultStreams()
    {
        _audioIndex = _streams is null ? null : DetailModel.DefaultAudio(_streams.Audio, _preferredAudio);
        _subtitleIndex = _streams is null ? null : DetailModel.DefaultSubtitle(_streams.Subtitles, _preferredSubtitle);
        RenderStreams();
    }

    /// <summary>A version picked from the bar: its own tracks replace the previous version's, so
    /// Play never sends one version's stream indices for another.</summary>
    private async Task LoadVersionStreamsAsync(MediaItem target, string versionId, int generation)
    {
        var revision = ++_streamsRevision;
        try
        {
            var streams = await Session.GetMediaStreamsAsync(target.Id, versionId);
            if (generation != _generation || revision != _streamsRevision || _versionId != versionId || _removals.Contains(target.Id)) return;
            _streams = streams;
            _playbackLoadedFor = target.Id;
            ApplyDefaultStreams();
        }
        catch (Exception ex)
        {
            if (generation == _generation) _shell.ReportFailure(ex);
        }
    }

    private async Task LoadSimilarAsync(int generation)
    {
        var similar = await Session.GetSimilarAsync(SeriesId ?? _item.Id, 16);
        if (generation != _generation) return;
        _similar.SetItems(_similarItems = _removals.Filter(similar));
    }

    private async Task LoadCastAsync(int generation)
    {
        var people = _item.People.Count > 0 ? _item.People : _series?.People ?? [];
        if (people.Count == 0 && _layout == DetailLayout.Season && SeriesId is { } seriesId && await Session.GetItemAsync(seriesId) is { } series)
            people = series.People;
        if (generation != _generation) return;
        _cast.SetItems(_castItems = _removals.Filter(people.Select(PersonCredits.AsItem)));
    }

    private void Render()
    {
        TitleText.Text = _item.Name;
        ParentLink.Visibility = _layout is DetailLayout.Season or DetailLayout.Episode && SeriesId is not null ? Visibility.Visible : Visibility.Collapsed;
        ParentLink.Content = _item.SeriesName ?? _series?.Name;

        var subtitle = _layout == DetailLayout.Episode ? DetailModel.EpisodeTag(_item) : "";
        SubtitleLine.Text = subtitle;
        SubtitleLine.Visibility = subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        Chips.Children.Clear();
        foreach (var chip in DetailModel.Chips(_item)) Chips.Children.Add(Chip(chip));
        HoldRatings();
        _ = LoadRatingsAsync(_item, ++_chipsVersion);
        var trailer = WebLinks.TryParse(_item.TrailerUrl, out _);
        TrailerButton.Opacity = trailer ? 1 : 0;
        TrailerButton.IsHitTestVisible = TrailerButton.IsTabStop = trailer;
        AutomationProperties.SetAccessibilityView(TrailerButton, trailer ? AccessibilityView.Content : AccessibilityView.Raw);
        ToolTipService.SetToolTip(TrailerButton, L.Text("ItemDetailView.Trailer.c9af45dc"));
        AutomationProperties.SetName(TrailerButton, L.Text("ItemDetailView.Trailer.c9af45dc"));
        GenresText.Text = string.Join(" · ", (_item.Genres.Count > 0 ? _item.Genres : _series?.Genres ?? []).Take(4));
        Overview.Text = _item.Overview ?? "";
        Overview.Visibility = Overview.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Overview.MaxLines = _layout == DetailLayout.Movie ? 0 : 4;
        TechBlock.Visibility = _layout is DetailLayout.Movie or DetailLayout.Episode && !_item.IsVirtual ? Visibility.Visible : Visibility.Collapsed;
        DownloadButton.Visibility = _item.IsVirtual ? Visibility.Collapsed : Visibility.Visible;
        InfoPlay.Visibility = _layout == DetailLayout.Movie ? Visibility.Visible : Visibility.Collapsed;
        _ = LoadBackdropAsync();
        ApplyTexts();
    }

    private void RenderPreview()
    {
        var hasTarget = _target is not null;
        PreviewPlay.Visibility = Bar.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        InfoPlay.IsEnabled = hasTarget;
        var shown = PreviewItem;
        PreviewInfo.Visibility = _layout != DetailLayout.Movie && shown is not null ? Visibility.Visible : Visibility.Collapsed;
        if (shown is not null)
        {
            PreviewTitle.Text = _layout == DetailLayout.Episode ? shown.SeriesName ?? "" : shown.Name;
            var parts = new[] { DetailModel.EpisodeTag(shown), _layout == DetailLayout.Episode ? shown.Name : "", DetailModel.Runtime(shown.RuntimeTicks) };
            PreviewSubtitle.Text = string.Join(" · ", parts.Where(p => p.Length > 0));
        }
        if (_target is { } target)
        {
            _start = DetailModel.DefaultStart(target);
            var runtime = target.RuntimeTicks is > 0 ? TimeSpan.FromTicks(target.RuntimeTicks.Value) : TimeSpan.Zero;
            _settingStart = true;
            StartSlider.Maximum = Math.Max(1, runtime.TotalSeconds);
            StartSlider.Value = _start.TotalSeconds;
            StartSlider.IsEnabled = runtime > TimeSpan.Zero;
            _settingStart = false;
            DurationText.Text = runtime > TimeSpan.Zero ? ItemMenu.Clock(runtime) : "";
        }
        _ = LoadBackdropAsync();
        ApplyTexts();
    }

    private void RenderStreams()
    {
        TechChips.Children.Clear();
        foreach (var chip in DetailModel.TechnicalChips(_streams)) TechChips.Children.Add(Chip(chip));
        ApplyTexts();
    }

    /// <summary>Names the tracks Play uses on the Video, Audio and Subtitles drop-downs, which are
    /// blank and disabled until the streams are known; Video only shows, as Play takes no video track.</summary>
    private void MarkTracks()
    {
        var tracks = _streams is null ? null : DetailModel.Tracks(_streams, _audioIndex, _subtitleIndex);
        Name(VideoTracks, VideoTracksText, tracks?.Video, t => L.Format("Detail.VideoIs", t));
        Name(AudioTracks, AudioTracksText, tracks?.Audio, t => L.Format("Detail.AudioIs", t));
        Name(SubtitleTracks, SubtitleTracksText, tracks?.Subtitles, t => L.Format("Detail.SubtitlesAre", t));
        AudioTracks.IsEnabled = _streams is { Audio.Count: > 1 };
        SubtitleTracks.IsEnabled = _streams is { Subtitles.Count: > 0 };

        static void Name(Button button, TextBlock text, IReadOnlyList<TrackLine>? lines, Func<string, string> name)
        {
            text.Text = lines is null ? "" : lines.FirstOrDefault(l => l.IsChosen)?.Text ?? L.Text("Detail.NoTracks");
            AutomationProperties.SetName(button, name(text.Text));
            ToolTipService.SetToolTip(button, text.Text.Length > 0 ? text.Text : null);
        }
    }

    /// <summary>An invisible badge holds the format badges' line from the start, so the streams that
    /// fill it later never move what is below.</summary>
    private void HoldFormatChips()
    {
        var probe = Chip("4K");
        AutomationProperties.SetAccessibilityView(probe.Child, AccessibilityView.Raw);
        TechProbe.Children.Add(probe);
    }

    /// <summary>The seasons or episodes. The same list again updates the cards in place.</summary>
    private void ShowChildren(IReadOnlyList<MediaItem> children)
    {
        var current = _layout switch
        {
            DetailLayout.Series => DetailModel.SeasonOf(children, _target),
            DetailLayout.Episode => _item,
            _ => _target,
        };
        var shown = ChildGrid.Children.OfType<MediaCard>().ToList();
        if (shown.Count == ChildGrid.Children.Count && shown.Select(c => c.Item?.Id).SequenceEqual(children.Select(c => (Guid?)c.Id)))
        {
            for (var i = 0; i < children.Count; i++)
            {
                shown[i].IsCurrent = current?.Id == children[i].Id;
                shown[i].Item = children[i];
            }
            return;
        }
        ChildGrid.Children.Clear();
        foreach (var child in children)
        {
            var card = new MediaCard
            {
                Kind = _layout == DetailLayout.Series ? CardKind.Poster : CardKind.Wide,
                Shell = _shell,
                Session = Session,
                Overrides = _overrides,
                Title = _layout == DetailLayout.Series ? null : e => e.IndexNumber is { } n ? L.Format("Detail.EpisodeNumbered", n, e.Name) : e.Name,
                Subtitle = _layout == DetailLayout.Series ? null : EpisodeSubtitle,
                Count = _layout == DetailLayout.Series ? s => s.UnplayedItemCount : null,
                IsCurrent = current?.Id == child.Id,
                FollowsCardScale = true,
            };
            card.ApplyScale(_scale);
            card.Item = child;
            ChildGrid.Children.Add(card);
        }
        PlaceChildren();
    }

    /// <summary>An episode card's second line: when it airs or that it is missing, else the time left or its runtime.</summary>
    private static string EpisodeSubtitle(MediaItem episode)
        => DetailModel.Availability(episode, DateTimeOffset.Now) is { Length: > 0 } availability ? availability
            : HomeSections.TimeLeft(episode) is { Length: > 0 } left ? left : DetailModel.Runtime(episode.RuntimeTicks);

    private async Task LoadBackdropAsync()
    {
        var scale = XamlRoot?.RasterizationScale ?? 1;
        var width = (int)Math.Min(1920, Math.Max(800, Preview.ActualWidth * scale));
        var step = Imaging.ArtworkSizes.Step(width);
        if (BackdropUrl(step) is not { } url) return;
        var image = await ImageLoader.LoadAsync(Session, BackdropUrl, width);
        if (image is not null && !_removals.Contains(_item.Id) && BackdropUrl(step) == url) Backdrop.Source = image;
    }

    private string? BackdropUrl(int width)
    {
        if (_layout == DetailLayout.Episode && _item.PrimaryImageTag is { Length: > 0 } still) return Session.GetPrimaryImageUrl(_item.Id, still, width);
        var source = _layout is DetailLayout.Season or DetailLayout.Episode ? _series ?? _item : _item;
        if (source.BackdropImageTag is { Length: > 0 } tag) return Session.GetBackdropImageUrl(source.Id, tag, width);
        return SeriesId is { } seriesId ? Session.GetBackdropImageUrl(seriesId, null, width) : null;
    }

    private void ApplyTexts()
    {
        RemovedMessage.Text = L.Text("MainWindow.ThisItemIsNoLongerOnTheServer.3c9e51a7");
        GridHeading.Text = _layout == DetailLayout.Series ? L.Text("Detail.Seasons") : L.Text("Detail.Episodes");
        NextLabel.Text = _target is null && _item.IsVirtual ? DetailModel.Availability(_item, DateTimeOffset.Now)
            : _layout == DetailLayout.Episode ? L.Text("Detail.ThisEpisode") : L.Text("Detail.NextEpisode");
        var play = _start > TimeSpan.Zero ? L.Text("Detail.Resume") : L.Text("Detail.Play");
        SetLabel(InfoPlayText, InfoPlaySpare, play, _start > TimeSpan.Zero ? L.Text("Detail.Play") : L.Text("Detail.Resume"));
        SetButtonName(BarPlay, play);
        AutomationProperties.SetName(InfoPlay, play);
        AutomationProperties.SetName(PreviewPlay, play);
        ToolTipService.SetToolTip(PreviewPlay, play);
        StartText.Text = ItemMenu.Clock(_start);
        ShowStartBar();
        SetButtonName(JumpBackButton, L.Format("Player.JumpBackBy", App.Settings.SkipBackwardSeconds));
        SetButtonName(JumpForwardButton, L.Format("Player.JumpForwardBy", App.Settings.LargeSkipSeconds));
        SetButtonName(InfoButton, L.Text("Player.Info"));
        SetButtonName(FullscreenButton, L.Text("Detail.PlayFullscreen"));
        AutomationProperties.SetName(StartSlider, L.Text("Detail.StartAt"));

        SetLabel(WatchedText, WatchedSpare, _played ? L.Text("Detail.Watched") : L.Text("Detail.MarkWatched"), _played ? L.Text("Detail.MarkWatched") : L.Text("Detail.Watched"));
        WatchedIcon.Foreground = _played ? Ui.Resource<Brush>("LwAccentFgBrush") : Ui.Resource<Brush>("LwTextBrush");
        AutomationProperties.SetName(WatchedButton, WatchedText.Text);
        SetLabel(FavoriteText, FavoriteSpare, _favorite ? L.Text("Detail.Favorited") : L.Text("Detail.Favorite"), _favorite ? L.Text("Detail.Favorite") : L.Text("Detail.Favorited"));
        AutomationProperties.SetName(FavoriteButton, FavoriteText.Text);
        FavoriteIcon.Glyph = _favorite ? Theme.Icons.HeartFilled : Theme.Icons.Heart;
        FavoriteIcon.Foreground = _favorite ? Ui.Resource<Brush>("LwAccentFgBrush") : Ui.Resource<Brush>("LwTextBrush");

        var audio = _streams?.Audio.FirstOrDefault(a => a.Index == _audioIndex)?.Display ?? L.Text("Detail.Default");
        var subtitle = _streams?.Subtitles.FirstOrDefault(s => s.Index == _subtitleIndex)?.Display ?? L.Text("Detail.Off");
        MarkTracks();
        AudioButton.IsEnabled = _streams is { Audio.Count: > 1 };
        SubtitleButton.IsEnabled = _streams is { Subtitles.Count: > 0 };
        SetButtonName(AudioButton, L.Format("Detail.AudioIs", audio));
        SetButtonName(SubtitleButton, L.Format("Detail.SubtitlesAre", subtitle));
        SetButtonName(QualityButton, L.Format("Detail.QualityIs", QualityLabel(_quality)));
        var versionsKnown = _target is { } shown && _versionsFor == shown.Id;
        var versionCount = (_target ?? (_layout is DetailLayout.Movie or DetailLayout.Episode ? _item : null))?.MediaSourceCount;
        var showVersions = DetailModel.ShowsVersions(versionCount, versionsKnown ? _versions.Count : null);
        VersionButton.Opacity = showVersions ? 1 : 0;
        VersionButton.IsHitTestVisible = VersionButton.IsTabStop = showVersions;
        AutomationProperties.SetAccessibilityView(VersionButton, showVersions ? AccessibilityView.Content : AccessibilityView.Raw);
        VersionButton.IsEnabled = versionsKnown && _versions.Count > 1;
        VersionText.Text = versionsKnown && _versions.FirstOrDefault(v => v.Id == _versionId) is { } version ? DetailModel.VersionName(version, _versions) : L.Text("Player.Version");
        SetButtonName(VersionButton, L.Format("Detail.VersionIs", VersionText.Text));
        ApplyAutoFullscreen();

        ApplyDownloadState();
        SetButtonName(ShuffleButton, L.Text("Playlist.Shuffle"));

        _similar.ApplyTexts();
        _cast.ApplyTexts();
    }

    /// <summary>The Download button says Downloaded, Downloading or Download as the title's
    /// download stands now.</summary>
    private void ApplyDownloadState()
    {
        var tracked = _shell.Downloads.IsTracked(_item.Id);
        var downloaded = _shell.Downloads.IsDownloaded(_item.Id);
        var download = downloaded ? L.Text("Detail.Downloaded") : tracked ? L.Text("Detail.Downloading")
            : _layout is DetailLayout.Series or DetailLayout.Season ? L.Text("ItemMenu.DownloadAll") : L.Text("ItemMenu.Download");
        DownloadIcon.Foreground = Ui.Resource<Brush>(tracked ? "LwAccentFgBrush" : "LwTextBrush");
        SetButtonName(DownloadButton, download);
    }

    private void WatchDownloads(bool watch)
    {
        _shell.Downloads.AvailabilityChanged -= OnAvailabilityChanged;
        if (watch) _shell.Downloads.AvailabilityChanged += OnAvailabilityChanged;
    }

    private void OnAvailabilityChanged() => DispatcherQueue.TryEnqueue(ApplyDownloadState);

    /// <summary>Download (Download all for a season or a show), or Downloads when the title is
    /// already downloaded or on its way.</summary>
    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (_shell.Downloads.IsTracked(_item.Id))
        {
            _shell.OpenDownloads();
            return;
        }
        await _shell.DownloadAsync(_item);
        ApplyTexts();
    }

    /// <summary>Shuffle on a show or a season: its unwatched episodes in random order.</summary>
    private void OnShuffle(object sender, RoutedEventArgs e) => _ = ItemMenu.ShuffleAsync(_shell, Session, _item);

    /// <summary>Shows <paramref name="text"/> in a label as wide as the wider of it and
    /// <paramref name="other"/>, its invisible twin, so a toggle or a late answer never changes the
    /// button's width.</summary>
    private static void SetLabel(TextBlock shown, TextBlock spare, string text, string other)
    {
        shown.Text = text;
        spare.Text = other;
    }

    private static void SetButtonName(Button button, string name)
    {
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
    }

    private static string QualityLabel(int? mbps) => QualityNames.Tier(mbps, App.Settings.MaxStreamingBitrateMbps);

    /// <summary>Lays the page out for the size it is measured at, in that same pass: the preview in
    /// the shape fixed when the page opened, the rows at the height their held places already have,
    /// and the season or episode grid for the side panel's width. Nothing that arrives later is an
    /// input, so each size is laid out once.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (availableSize.Width is > 0 and < double.PositiveInfinity)
            ApplyLayout(availableSize.Width, double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
        return base.MeasureOverride(availableSize);
    }

    private void ApplyLayout(double width, double height)
    {
        ApplyScale(width);
        var spacing = new DetailSpacing(Body.Padding.Left + Body.Padding.Right, Body.Padding.Top + Body.Padding.Bottom, Body.ColumnSpacing, Body.RowSpacing);
        Rows.Measure(new Size(Math.Max(0, width - spacing.Horizontal), double.PositiveInfinity));
        var frame = DetailModel.Frame(width, height, Rows.DesiredSize.Height, _previewAspect, spacing);
        if (TopRow.Height.Value != frame.PreviewHeight) TopRow.Height = new GridLength(frame.PreviewHeight);
        if (SideColumn.Width.Value != frame.SideWidth) SideColumn.Width = RowsSideColumn.Width = new GridLength(frame.SideWidth);
        ApplyChildGrid(frame.SideWidth - Side.BorderThickness.Left - Side.BorderThickness.Right - SidePanel.Padding.Left - SidePanel.Padding.Right);
    }

    /// <summary>Grows More like this, Cast and crew and the season or episode grid with the window,
    /// as on Home (<see cref="CardScale"/>).</summary>
    private void ApplyScale(double width)
    {
        var scale = CardRow.ScaleFor(width);
        if (!_scale.MovesTo(scale, CardRow.MaxScale)) return;
        _scale = scale;
        _similar.ApplyScale(scale);
        _cast.ApplyScale(scale);
        ChildGrid.ColumnSpacing = scale.Space(_childSpacing.Column);
        ChildGrid.RowSpacing = scale.Space(_childSpacing.Row);
        foreach (var shown in ChildGrid.Children.OfType<MediaCard>()) shown.ApplyScale(scale);
    }

    /// <summary>Gives the seasons or episodes as many columns as fit cards of the detail rows' size in
    /// a grid <paramref name="width"/> DIP wide (<see cref="DetailModel.ChildGrid"/>).</summary>
    private void ApplyChildGrid(double width)
    {
        var card = _scale.Width(Ui.Resource<double>(_layout == DetailLayout.Series ? "LwDetailPosterWidth" : "LwHomeWideCardWidth"));
        var (columns, _) = DetailModel.ChildGrid(width, card, ChildGrid.ColumnSpacing);
        if (columns == ChildGrid.ColumnDefinitions.Count) return;
        ChildGrid.ColumnDefinitions.Clear();
        for (var i = 0; i < columns; i++) ChildGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        PlaceChildren();
    }

    private void PlaceChildren()
    {
        var columns = ChildGrid.ColumnDefinitions.Count;
        var cards = ChildGrid.Children.OfType<MediaCard>().ToList();
        ChildGrid.RowDefinitions.Clear();
        for (var i = 0; i < cards.Count; i++)
        {
            if (i % columns == 0) ChildGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(cards[i], i / columns);
            Grid.SetColumn(cards[i], i % columns);
        }
    }

    private int _chipsVersion;

    /// <summary>With an OMDb key, the ratings line holds its place from the start: invisible chips as
    /// wide as the three scores, so scores that arrive later fill it without moving what is below.
    /// A season, which rarely has an IMDb id, gets the line only when scores arrive.</summary>
    private void HoldRatings()
    {
        RatingChips.Children.Clear();
        RatingProbe.Children.Clear();
        var hold = App.Settings.OmdbApiKey is { Length: > 0 } && _layout != DetailLayout.Season;
        Ratings.Visibility = hold ? Visibility.Visible : Visibility.Collapsed;
        if (!hold) return;
        foreach (var text in new[] { L.Format("Detail.RatingImdb", "0.0"), L.Format("Detail.RatingRottenTomatoes", "100%"), L.Format("Detail.RatingMetacritic", "100") })
        {
            var probe = Chip(text);
            AutomationProperties.SetAccessibilityView(probe.Child, AccessibilityView.Raw);
            RatingProbe.Children.Add(probe);
        }
    }

    /// <summary>IMDb, Rotten Tomatoes and Metacritic scores from OMDb, when Settings has a key and
    /// the title an IMDb id; they fill the ratings line under the chips.</summary>
    private async Task LoadRatingsAsync(MediaItem item, int version)
    {
        if (App.Settings.OmdbApiKey is not { Length: > 0 } key || item.ImdbId is not { Length: > 0 } imdb) return;
        var ratings = await Metadata.OmdbClient.GetByImdbIdAsync(imdb, key);
        if (ratings is not { HasAny: true } || version != _chipsVersion) return;
        Ratings.Visibility = Visibility.Visible;
        void Add(string? score, Func<string, string> text, string id)
        {
            if (score is null) return;
            var chip = Chip(text(score));
            AutomationProperties.SetAutomationId(chip.Child, id);
            RatingChips.Children.Add(chip);
        }
        Add(ratings.Imdb?.Split('/')[0], s => L.Format("Detail.RatingImdb", s), "DetailRatingImdb");
        Add(ratings.RottenTomatoes, s => L.Format("Detail.RatingRottenTomatoes", s), "DetailRatingRottenTomatoes");
        Add(ratings.Metacritic?.Split('/')[0], s => L.Format("Detail.RatingMetacritic", s), "DetailRatingMetacritic");
    }

    private static Border Chip(string text) => new()
    {
        Padding = new Thickness(7, 2, 7, 3),
        CornerRadius = Ui.Resource<CornerRadius>("LwTokenRadius"),
        Background = Ui.Resource<Brush>("LwBoxBrush"),
        BorderBrush = Ui.Resource<Brush>("LwStrokeBrush"),
        BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = text, FontSize = 11.5, Foreground = Ui.Resource<Brush>("LwTextBrush") },
    };

    /// <summary>The server's remote trailer (usually YouTube) in the browser; only a web link
    /// (<see cref="WebLinks"/>) is handed to the shell.</summary>
    private void OnTrailer(object sender, RoutedEventArgs e)
    {
        if (!WebLinks.TryParse(_item.TrailerUrl, out var trailer)) return;
        _ = Windows.System.Launcher.LaunchUriAsync(trailer);
    }

    /// <summary>What Play sends; subtitles Off is Jellyfin's -1 once the tracks are known, so a
    /// transcode never burns in the server's default. Picks made for another title are not sent.</summary>
    private PlaybackChoice Choice() => _selectionsFor == _target?.Id
        ? new(_versionId, _audioIndex, _subtitleIndex ?? (_streams is null ? null : -1), _start, _quality, _forceTranscode)
        : new(null, null, null, _start, _quality, _forceTranscode);

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_target is { } target) _shell.Play(target, Choice());
    }

    /// <summary>The bar's full screen button: plays with the picks, straight into full screen.</summary>
    private void OnPlayFullscreen(object sender, RoutedEventArgs e)
    {
        if (_target is { } target) _shell.Play(target, Choice(), fullscreen: true);
    }

    /// <summary>The bar's back and forward buttons move where Play starts.</summary>
    private void OnJumpBack(object sender, RoutedEventArgs e) => MoveStart(-App.Settings.SkipBackwardSeconds);

    private void OnJumpForward(object sender, RoutedEventArgs e) => MoveStart(App.Settings.LargeSkipSeconds);

    private void MoveStart(int seconds) => StartSlider.Value = Math.Clamp(StartSlider.Value + seconds, 0, StartSlider.Maximum);

    private void OnInfo(object sender, RoutedEventArgs e)
    {
        if (_target is { } target) _shell.ShowInfo(target);
    }

    /// <summary>The seek bar's filled part shows where Play starts, as the player's shows the position.</summary>
    private void ShowStartBar()
        => StartPlayed.Width = StartSlider.Maximum > 0 ? Math.Max(0, StartArea.ActualWidth) * Math.Clamp(StartSlider.Value / StartSlider.Maximum, 0, 1) : 0;

    private void OnParent(object sender, RoutedEventArgs e)
    {
        if (SeriesId is { } seriesId) _shell.OpenItem(_series ?? new MediaItem { Id = seriesId, Name = _item.SeriesName ?? "", Type = BaseItemDto_Type.Series });
    }

    /// <summary>Seeking in preselection mode sets where Play starts.</summary>
    private void OnStartChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_settingStart || _target is null) return;
        _start = DetailModel.ClampStart(TimeSpan.FromSeconds(Math.Round(e.NewValue)), _target.RuntimeTicks);
        ApplyTexts();
    }

    private void OnAudio(object sender, RoutedEventArgs e) => ShowAudioChoices(AudioButton, "DetailAudioOption", inPanel: false);

    private void OnAudioTracks(object sender, RoutedEventArgs e) => ShowAudioChoices(AudioTracks, "DetailAudioTrackOption", inPanel: true);

    private void ShowAudioChoices(Button anchor, string automationId, bool inPanel)
    {
        if (_streams is not { } streams) return;
        ShowChoices(anchor, automationId, streams.Audio.Select(a => (a.Display, a.Index == _audioIndex, (Action)(() => _audioIndex = a.Index))), inPanel);
    }

    private void OnSubtitles(object sender, RoutedEventArgs e) => ShowSubtitleChoices(SubtitleButton, "DetailSubtitleOption", inPanel: false);

    private void OnSubtitleTracks(object sender, RoutedEventArgs e) => ShowSubtitleChoices(SubtitleTracks, "DetailSubtitleTrackOption", inPanel: true);

    private void ShowSubtitleChoices(Button anchor, string automationId, bool inPanel)
    {
        if (_streams is not { } streams) return;
        var options = new List<(string, bool, Action)> { (L.Text("Detail.Off"), _subtitleIndex is null, () => _subtitleIndex = null) };
        options.AddRange(streams.Subtitles.Select(s => (s.Display, s.Index == _subtitleIndex, (Action)(() => _subtitleIndex = s.Index))));
        ShowChoices(anchor, automationId, options, inPanel);
    }

    /// <summary>The player's quality tiers and its "Always transcode" switch; Play starts with what
    /// is set here.</summary>
    private void OnQuality(object sender, RoutedEventArgs e)
    {
        var menu = FastUi.Flyout(new MenuFlyout { Placement = FlyoutPlacementMode.Top, MenuFlyoutPresenterStyle = Ui.Resource<Style>("LwPlayerMenuPresenter") });
        foreach (var mbps in PlayerViewModel.QualityTierMbps)
        {
            var item = new RadioMenuFlyoutItem { Text = QualityLabel(mbps), IsChecked = mbps == _quality, GroupName = "DetailQualityOption" };
            AutomationProperties.SetAutomationId(item, "DetailQualityOption");
            item.Click += (_, _) => { _quality = mbps; ApplyTexts(); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var force = new ToggleMenuFlyoutItem { Text = L.Text("Player.ForceTranscode"), IsChecked = _forceTranscode };
        AutomationProperties.SetAutomationId(force, "DetailForceTranscode");
        force.RegisterPropertyChangedCallback(ToggleMenuFlyoutItem.IsCheckedProperty, (_, _) =>
        {
            _forceTranscode = force.IsChecked;
            ApplyTexts();
        });
        menu.Items.Add(force);
        menu.ShowAt(QualityButton);
    }

    /// <summary>The version drop-down, shown when the title has two or more versions; each entry
    /// names the version and its size or bitrate.</summary>
    private void OnVersion(object sender, RoutedEventArgs e)
    {
        if (_versions.Count < 2 || _target is not { } target) return;
        ShowChoices(VersionButton, "DetailVersionOption", _versions.Select(v => (
            v.Detail() is { Length: > 0 } detail ? $"{v.Label} · {detail}" : v.Label,
            v.Id == _versionId,
            (Action)(() => PickVersion(target, v.Id)))));
    }

    /// <summary>The bar's "full screen on play" switch, kept in Settings.</summary>
    private void OnAutoFullscreen(object sender, RoutedEventArgs e)
    {
        App.Settings.AutoFullscreen = !App.Settings.AutoFullscreen;
        App.SaveSettings();
        ApplyAutoFullscreen();
    }

    private void ApplyAutoFullscreen()
    {
        var on = App.Settings.AutoFullscreen;
        AutoFullscreenText.Text = L.Text("Detail.AutoFullscreenLabel");
        AutoFullscreenButton.Foreground = on ? Ui.Resource<Brush>("LwAccentFgBrush") : new SolidColorBrush(Microsoft.UI.Colors.White);
        AutoFullscreenCheck.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SetButtonName(AutoFullscreenButton, on ? L.Text("Detail.AutoFullscreenOn") : L.Text("Detail.AutoFullscreenOff"));
    }

    /// <summary>A version picked from the version drop-down: its own tracks load for it.</summary>
    private void PickVersion(MediaItem target, string versionId)
    {
        if (_versionId == versionId) return;
        _versionId = versionId;
        MediaVersionStore.Save(_profileKey, target.Id, versionId);
        _streams = null;
        _playbackLoadedFor = null;
        ApplyDefaultStreams();
        _ = LoadVersionStreamsAsync(target, versionId, _generation);
    }

    /// <summary>A radio menu above a bar button, or below a drop-down of the info panel
    /// (<paramref name="inPanel"/>), clear of the button so it never opens under the pointer; a choice
    /// applies at once and Play uses it.</summary>
    private void ShowChoices(Button anchor, string automationId, IEnumerable<(string Label, bool Selected, Action Choose)> options, bool inPanel = false)
    {
        var menu = FastUi.Flyout(new MenuFlyout());
        if (!inPanel)
        {
            menu.Placement = FlyoutPlacementMode.Top;
            menu.MenuFlyoutPresenterStyle = Ui.Resource<Style>("LwPlayerMenuPresenter");
        }
        foreach (var (label, selected, choose) in options)
        {
            var item = new RadioMenuFlyoutItem { Text = label, IsChecked = selected, GroupName = automationId };
            AutomationProperties.SetAutomationId(item, automationId);
            item.Click += (_, _) => { choose(); ApplyTexts(); };
            menu.Items.Add(item);
        }
        if (!inPanel)
        {
            menu.ShowAt(anchor);
            return;
        }
        if (anchor.FocusState != FocusState.Keyboard) menu.Closed += (_, _) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => ReleaseFocus(anchor));
        var clear = new Rect(0, -MenuShadowAbove, anchor.ActualWidth, anchor.ActualHeight + MenuShadowAbove + MenuShadowBelow);
        menu.ShowAt(anchor, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft, ExclusionRect = clear });
    }

    /// <summary>How far a menu's shadow reaches below a menu that opens above the button, and above one
    /// that opens below it: the button's exclusion grows by these so the menu's popup window, shadow
    /// included, stays off the pointer.</summary>
    private const double MenuShadowAbove = 20, MenuShadowBelow = 4;

    /// <summary>After a pointer pick the page takes focus, which draws no ring, so Space and the
    /// player's other keys reach the window instead of the drop-down; a keyboard user keeps it. Only
    /// while the menu's button still holds focus: focus the user moved meanwhile stays where it went.</summary>
    private void ReleaseFocus(Button anchor)
    {
        if (XamlRoot is not { } root || !ReferenceEquals(FocusManager.GetFocusedElement(root), anchor)) return;
        IsTabStop = true;
        if (!Focus(FocusState.Programmatic)) IsTabStop = false;
    }

    private bool _changingWatched;

    private async void OnToggleWatched(object sender, RoutedEventArgs e)
    {
        if (_changingWatched) return;
        _changingWatched = true;
        var target = !_played;
        try
        {
            if (await ChangeHeaderAsync(UserDataField.Played, target, () => Session.SetPlayedAsync(_item.Id, target)))
                await LoadAsync();
        }
        finally { _changingWatched = false; }
    }

    private async void OnToggleFavorite(object sender, RoutedEventArgs e)
    {
        var target = !_favorite;
        await ChangeHeaderAsync(UserDataField.Favorite, target, () => Session.SetFavoriteAsync(_item.Id, target));
    }

    private async Task<bool> ChangeHeaderAsync(UserDataField field, bool value, Func<Task<bool>> send)
    {
        var change = _overrides.ChangeAsync(_item, field, value, send);
        (_played, _favorite) = _overrides.Of(_item);
        ApplyTexts();
        var accepted = await change;
        (_played, _favorite) = _overrides.Of(_item);
        ApplyTexts();
        return accepted;
    }
}
