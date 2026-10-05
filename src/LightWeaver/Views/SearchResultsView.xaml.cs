using LightWeaver.Localization;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Jellyfin;

namespace LightWeaver.Views;

/// <summary>Grid of search results; the shell owns the query and pushes results in.
/// Results are grouped into type sections (Movies / Shows / Episodes / ...) so mixed
/// portrait/landscape cards read as clean per-type grids instead of one ragged row (M3.5).</summary>
public partial class SearchResultsView : UserControl
{
    private readonly ObservableCollection<MediaItem> _items = [];

    public event Action<MediaItem>? ItemSelected;

    public SearchResultsView()
    {
        InitializeComponent();
        LiveText.Watch(this);
        var view = new CollectionViewSource { Source = _items };
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MediaItem.Type))
        {
            Converter = new TypeGroupConverter(),
        });
        ItemsGrid.ItemsSource = view.View;
    }

    public void SetResults(IReadOnlyList<MediaItem> items)
    {
        Diagnostics.AppLog.Detail("search", $"event=render_results count={items.Count}");
        _items.Clear();
        // Order by group rank so the sections appear Movies -> Shows -> Episodes -> ...;
        // OrderBy is stable, so relevance order is preserved within each type.
        foreach (var item in items.OrderBy(i => GroupOf(i.Type).Rank))
            _items.Add(item);
        StatusText.Visibility = Visibility.Collapsed;   // M24: composed empty state
        if (items.Count == 0)
            EmptyView.Show(() => L.Text("SearchResultsView.NoResults.33cf7927"), () => L.Text("SearchResultsView.TryADifferentSearchTermOrThe.fc5f1cbd"));
        else
            EmptyView.Visibility = Visibility.Collapsed;
    }

    private void OnSelection(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsGrid.SelectedItem is MediaItem item)
        {
            Diagnostics.AppLog.Detail("search", $"event=interaction action=open-card item={item.Id:N} type={item.Type}");
            ItemsGrid.SelectedItem = null;
            ItemSelected?.Invoke(item);
        }
    }

    /// <summary>Invariant section identity and display rank for an item type.</summary>
    private static TypeGroup GroupOf(BaseItemDto_Type type) => new(type switch
    {
        BaseItemDto_Type.Movie => 0,
        BaseItemDto_Type.Series => 1,
        BaseItemDto_Type.Episode => 2,
        BaseItemDto_Type.BoxSet => 3,
        BaseItemDto_Type.Person => 4,
        _ => 5,
    });

    private sealed record TypeGroup(int Rank)
    {
        public string Label => Rank switch
        {
            0 => L.Text("SearchResultsView.Movies.049dbe79"),
            1 => L.Text("SearchResultsView.Shows.c9405784"),
            2 => L.Text("SearchResultsView.Episodes.abbe28a9"),
            3 => L.Text("SearchResultsView.Collections.1d60b322"),
            4 => L.Text("SearchResultsView.People.63fea934"),
            _ => L.Text("SearchResultsView.Other.9f077fc0"),
        };
    }

    private sealed class TypeGroupConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is BaseItemDto_Type t ? GroupOf(t) : new TypeGroup(5);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
