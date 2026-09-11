using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LightWeaver.Jellyfin;

namespace LightWeaver.Views;

/// <summary>
/// Full-screen listing behind a Home rail's "see all" header (Phase 7): Continue
/// Watching / Next Up / Recently Added / Favorites, each just a different paged
/// query delegate over the same grid. Infinite scroll + generation-guarded loads
/// follow <see cref="AdvancedSearchView"/>; card badges stay fresh via
/// <see cref="ApplyUserDataUpdate"/> (fanned out by MainWindow like the other
/// cached list views).
/// </summary>
public partial class SectionView : UserControl
{
    private const int PageSize = 50;

    private readonly Func<int, int, Task<(List<MediaItem> Items, int TotalCount)>> _query;
    private readonly ObservableCollection<MediaItem> _items = [];

    private int _totalCount;
    private bool _loadingMore;
    private int _loadGeneration;

    public event Action<MediaItem>? ItemSelected;

    public SectionView(Func<int, int, Task<(List<MediaItem> Items, int TotalCount)>> query)
    {
        InitializeComponent();
        _query = query;
        SkeletonHost.Children.Add(SkeletonFactory.Grid(this, 12, "SectionSkeleton"));
        ItemsGrid.ItemsSource = _items;
        Loaded += async (_, _) =>
        {
            if (_items.Count == 0 && _loadGeneration == 0)
                await LoadFirstAsync();
        };
        ItemsGrid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScroll));
    }

    private async Task LoadFirstAsync()
    {
        var generation = ++_loadGeneration;
        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("section", $"event=load outcome=start mode=initial generation={generation}");
        SkeletonScroll.Visibility = Visibility.Visible;
        try
        {
            // Test hook (see LibraryView): makes the skeleton observable on fast LANs.
            if (int.TryParse(Environment.GetEnvironmentVariable("LIGHTWEAVER_SLOW_LOAD_MS"), out var slowMs) && slowMs > 0)
                await Task.Delay(slowMs);
            var (items, total) = await _query(0, PageSize);
            if (generation != _loadGeneration)
            {
                Diagnostics.AppLog.Detail("section", $"event=load outcome=stale mode=initial generation={generation}");
                return;
            }
            _totalCount = total;
            _items.Clear();
            foreach (var item in items)
                _items.Add(item);
            SkeletonScroll.Visibility = Visibility.Collapsed;
            UpdateCount();
            if (_items.Count == 0)
                EmptyView.Show("Nothing here", "This section has no items right now.");
            else
                EmptyView.Visibility = Visibility.Collapsed;
            Diagnostics.AppLog.Detail("section",
                $"event=load outcome=success mode=initial generation={generation} elapsed_ms={started.ElapsedMilliseconds} count={items.Count} total={total}");
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration)
            {
                Diagnostics.AppLog.Detail("section",
                    $"event=load outcome=stale mode=initial generation={generation} phase=exception");
                return;
            }
            Diagnostics.AppLog.Detail("section",
                $"event=load outcome=failure mode=initial generation={generation} elapsed_ms={started.ElapsedMilliseconds}", ex);
            SkeletonScroll.Visibility = Visibility.Collapsed;
            StatusText.Text = $"Failed to load: {ex.Message}";
            StatusText.Visibility = Visibility.Visible;
        }
    }

    private async void OnScroll(object sender, ScrollChangedEventArgs e)
    {
        if (_loadingMore || _items.Count >= _totalCount)
            return;
        if (e.VerticalOffset < e.ExtentHeight - e.ViewportHeight - 200)
            return;

        _loadingMore = true;
        // Append-owned: hidden unconditionally in the finally. See LibraryView.OnScroll.
        MoreLoading.Show();
        var generation = _loadGeneration;
        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("section", $"event=load outcome=start mode=append generation={generation} offset={_items.Count}");
        try
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("LIGHTWEAVER_SLOW_LOAD_MS"), out var slowMs) && slowMs > 0)
                await Task.Delay(slowMs);
            var (more, total) = await _query(_items.Count, PageSize);
            if (generation != _loadGeneration)
            {
                Diagnostics.AppLog.Detail("section", $"event=load outcome=stale mode=append generation={generation}");
                return;
            }
            _totalCount = total;
            foreach (var item in more)
                _items.Add(item);
            UpdateCount();
            Diagnostics.AppLog.Detail("section",
                $"event=load outcome=success mode=append generation={generation} elapsed_ms={started.ElapsedMilliseconds} count={more.Count} total={total}");
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Detail("section",
                $"event=load outcome=failure mode=append generation={generation} elapsed_ms={started.ElapsedMilliseconds}", ex);
            // next scroll retries
        }
        finally
        {
            _loadingMore = false;
            MoreLoading.Hide();
        }
    }

    private void UpdateCount() =>
        CountText.Text = _totalCount == 1 ? "1 item" : $"{_totalCount} items";

    /// <summary>Swap a refreshed item in place so watched/favorite badges follow changes
    /// made elsewhere (mirrors LibraryView/HomeView; MediaItem is immutable).</summary>
    public void ApplyUserDataUpdate(MediaItem fresh)
    {
        for (var i = 0; i < _items.Count; i++)
            if (_items[i].Id == fresh.Id)
            {
                _items[i] = fresh;
                break;
            }
    }

    private void OnSelection(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsGrid.SelectedItem is MediaItem item)
        {
            Diagnostics.AppLog.Detail("section", $"event=interaction action=open-card item={item.Id:N} type={item.Type}");
            ItemsGrid.SelectedItem = null;
            ItemSelected?.Invoke(item);
        }
    }
}
