using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Jellyfin;
using LightWeaver.Library;
using LightWeaver.Localization;
using LightWeaver.People;
using LightWeaver.WinUI.Controls;
using LightWeaver.WinUI.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LightWeaver.WinUI.Pages;

/// <summary>"Cast and crew â†’" from a detail page: everyone credited on the item, as person cards
/// that open the person's page.</summary>
public sealed partial class CastPage : UserControl, INavPage
{
    private readonly IAppShell _shell;
    private readonly ItemRemovals _removals;
    private readonly MediaItem _item;
    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly WrapPanel _people;
    private readonly UserDataOverrides _overrides = new();

    public CastPage(IAppShell shell, MediaItem item)
    {
        _shell = shell;
        _removals = ItemRemovals.ForProfile(shell.CacheProfileKey);
        _item = item;
        AutomationProperties.SetAutomationId(this, "CastPage");
        _title = new TextBlock { FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.Bold, CharacterSpacing = -10 };
        AutomationProperties.SetAutomationId(_title, "CastTitle");
        AutomationProperties.SetHeadingLevel(_title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        _subtitle = new TextBlock { FontSize = 13, Foreground = Ui.Resource<Brush>("LwText2Brush"), Text = item.Name };
        _people = new WrapPanel { HorizontalSpacing = 14, VerticalSpacing = 16 };
        var body = new StackPanel { Padding = new Thickness(24, 14, 24, 24), Spacing = 4 };
        body.Children.Add(_title);
        body.Children.Add(_subtitle);
        _people.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(_people);
        var scroller = new ScrollViewer { Content = body, HorizontalScrollMode = ScrollMode.Disabled };
        AutomationProperties.SetAutomationId(scroller, "CastScroller");
        Content = scroller;
        ApplyTexts();
        LiveText.Observe(this, (_, _) => DispatcherQueue.TryEnqueue(ApplyTexts));
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var readAt = _removals.Mark();
        try
        {
            var fresh = await _shell.Model.Jellyfin.GetItemAsync(_item.Id) ?? _item;
            if (_removals.Contains(_item.Id, readAt)) return;
            var people = fresh.People;
            if (people.Count == 0 && fresh.Type == BaseItemDto_Type.Season && (fresh.SeriesId ?? fresh.ParentId) is { } seriesId
                && await _shell.Model.Jellyfin.GetItemAsync(seriesId) is { } series)
                people = series.People;
            if (_removals.Contains(_item.Id, readAt)) return;
            _people.Children.Clear();
            _subtitle.Text = _item.Name;
            foreach (var person in people.Where(person => !_removals.Contains(person.Id, readAt)))
            {
                var card = new MediaCard
                {
                    Width = 130,
                    Kind = CardKind.Person,
                    Shell = _shell,
                    Session = _shell.Model.Jellyfin,
                    Overrides = _overrides,
                    Subtitle = p => p.Overview ?? "",
                    ShowActions = false,
                };
                card.Invoked += (_, p) => _shell.OpenPerson(p.Id, p.Name);
                card.Item = PersonCredits.AsItem(person);
                _people.Children.Add(card);
            }
        }
        catch (Exception ex) when (JellyfinService.IsNotFound(ex))
        {
            if (_removals.Contains(_item.Id, readAt)) return;
            _removals.Remove([_item.Id]);
            RemoveItems(new HashSet<Guid> { _item.Id });
        }
        catch (Exception ex)
        {
            _shell.ReportFailure(ex);
        }
    }

    public void RemoveItems(IReadOnlySet<Guid> ids)
    {
        foreach (var card in _people.Children.OfType<MediaCard>().Where(card => card.Item is { } item && ids.Contains(item.Id)).ToList())
            _people.Children.Remove(card);
        if (!ids.Contains(_item.Id)) return;
        _people.Children.Clear();
        _subtitle.Text = L.Text("MainWindow.ThisItemIsNoLongerOnTheServer.3c9e51a7");
    }

    private void ApplyTexts()
    {
        _title.Text = L.Text("Detail.CastAndCrew");
        foreach (var card in _people.Children.OfType<MediaCard>()) card.Refresh();
    }
}
