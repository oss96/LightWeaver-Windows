using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Details;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;
using LightWeaver.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace LightWeaver.WinUI.Shell;

/// <summary>Info on a title with a file: a dialog with the technical media info Jellyfin reports
/// for each version (container, size, bitrate and every stream), with Copy.</summary>
public sealed partial class MainWindow
{
    private bool _mediaInfoOpen;

    /// <summary>Info (Ctrl+I): the media info of a title with a file, the detail page otherwise.</summary>
    public async void ShowInfo(MediaItem item)
    {
        if (!item.IsPlayable)
        {
            OpenItem(item);
            return;
        }
        if (_mediaInfoOpen) return;
        _mediaInfoOpen = true;
        try
        {
            var versions = MediaInfoModel.Build(await Model.Jellyfin.GetMediaSourcesAsync(item.Id));
            if (versions.Count == 0)
            {
                ToastHost.Show(ToastKind.Error, L.Text("MediaInfo.Empty"));
                return;
            }
            await ShowMediaInfoAsync(item, versions);
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
        finally
        {
            _mediaInfoOpen = false;
        }
    }

    private async Task ShowMediaInfoAsync(MediaItem item, IReadOnlyList<MediaInfoVersion> versions)
    {
        var title = item.Type == BaseItemDto_Type.Episode && item.SeriesName is { Length: > 0 } series ? $"{series} · {item.QueueDisplay}" : item.Name;
        var list = new ItemsControl();
        AutomationProperties.SetAutomationId(list, "MediaInfoList");
        AutomationProperties.SetName(list, L.Text("MediaInfo.Heading"));
        list.Items.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        foreach (var version in versions)
        {
            if (versions.Count > 1)
                list.Items.Add(new TextBlock { Text = version.Name, FontSize = 14, Margin = new Thickness(0, 18, 0, 0), Foreground = Ui.Resource<Brush>("LwAccentFgBrush"), TextWrapping = TextWrapping.Wrap });
            foreach (var section in version.Sections) list.Items.Add(SectionView(section));
        }
        var scroll = new ScrollViewer { Content = list, MaxHeight = 520, MinWidth = 440, Padding = new Thickness(0, 0, 14, 0), IsTabStop = true };
        AutomationProperties.SetName(scroll, L.Text("MediaInfo.Heading"));

        var dialog = new LwDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = L.Text("MediaInfo.Heading"),
            Content = scroll,
            PrimaryButtonText = L.Text("MediaInfo.Copy"),
            CloseButtonText = L.Text("MediaInfo.Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "MediaInfoDialog");
        dialog.Opened += (_, _) => scroll.Focus(FocusState.Programmatic);
        var before = FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
        var result = await dialog.ShowAsync();
        if (before is { IsLoaded: true }) before.Focus(FocusState.Programmatic);
        if (result != ContentDialogResult.Primary) return;
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(MediaInfoModel.AsText(title, versions));
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        ToastHost.Show(ToastKind.Success, L.Text("MediaInfo.Copied"));
    }

    /// <summary>A block's heading over its label and value rows.</summary>
    private static StackPanel SectionView(MediaInfoSection section)
    {
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(new TextBlock { Text = section.Title, Style = Ui.Resource<Style>("LwColumnHeader") });
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 3 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < section.Lines.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = section.Lines[i].Label, FontSize = 13, Foreground = Ui.Resource<Brush>("LwText2Brush"), TextWrapping = TextWrapping.Wrap };
            var value = new TextBlock { Text = section.Lines[i].Value, FontSize = 13, Foreground = Ui.Resource<Brush>("LwTextBrush"), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }
        panel.Children.Add(grid);
        return panel;
    }
}
