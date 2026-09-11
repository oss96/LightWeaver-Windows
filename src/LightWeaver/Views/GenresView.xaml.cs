using System.Windows;
using System.Windows.Controls;
using LightWeaver.Jellyfin;
using LightWeaver.ViewModels;

namespace LightWeaver.Views;

/// <summary>
/// Genre tile grid for one library (Phase 5 M5). Picking a genre opens the
/// library's grid pre-filtered to it (M3's genre param).
/// </summary>
public partial class GenresView : UserControl
{
    private readonly AppViewModel _app;
    private readonly MediaItem _library;

    /// <summary>The user picked a genre of this library.</summary>
    public event Action<string>? GenreSelected;

    public GenresView(AppViewModel app, MediaItem library)
    {
        InitializeComponent();
        _app = app;
        _library = library;
        Loaded += async (_, _) => { if (GenreGrid.Items.Count == 0) await LoadAsync(); };
    }

    private async Task LoadAsync()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        Diagnostics.AppLog.Detail("genres", $"event=load outcome=start item={_library.Id:N} type={_library.Type}");
        // First paint of an empty surface, so the skeleton is IMMEDIATE - no 250 ms gate.
        // Holding a blank screen for a quarter second at navigation time reads as broken,
        // which is exactly why the reveal delay is reserved for already-painted pages.
        SkeletonHost.Content = SkeletonFactory.Tiles(this, 12, "GenresSkeleton");
        SkeletonHost.Visibility = Visibility.Visible;
        try
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("LIGHTWEAVER_SLOW_LOAD_MS"), out var slowMs) && slowMs > 0)
                await Task.Delay(slowMs);
            var genres = await _app.Jellyfin.GetGenresAsync(_library.Id);
            GenreGrid.ItemsSource = genres;
            if (genres.Count == 0)
                EmptyView.Show("No genres here", "This library has no genre tags to browse.");
            else
                EmptyView.Visibility = Visibility.Collapsed;
            Diagnostics.AppLog.Detail("genres",
                $"event=load outcome=success elapsed_ms={started.ElapsedMilliseconds} count={genres.Count} item={_library.Id:N} type={_library.Type}");
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Detail("genres",
                $"event=load outcome=failure elapsed_ms={started.ElapsedMilliseconds} item={_library.Id:N} type={_library.Type}", ex);
            StatusText.Text = $"Failed to load: {ex.Message}";
            StatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            // Unconditional: this view has no requery path (Loaded fires once, guarded by
            // GenreGrid.Items.Count), so there is no competing generation to defer to.
            SkeletonHost.Visibility = Visibility.Collapsed;
            SkeletonHost.Content = null;
        }
    }

    private void OnSelection(object sender, SelectionChangedEventArgs e)
    {
        if (GenreGrid.SelectedItem is string genre)
        {
            Diagnostics.AppLog.Detail("genres", "event=interaction action=select-genre");
            GenreGrid.SelectedItem = null;
            GenreSelected?.Invoke(genre);
        }
    }
}
