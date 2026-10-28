using LightWeaver.Downloads;
using LightWeaver.Jellyfin;
using LightWeaver.Library;
using LightWeaver.Localization;
using LightWeaver.WinUI.Controls;
using LightWeaver.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LightWeaver.WinUI.Shell;

public sealed partial class ServerUnreachableView : UserControl
{
    private readonly Connectivity _connectivity;
    private readonly string _serverName;
    private readonly string _serverHost;
    private bool _detailsOpen;

    public ServerUnreachableView(IAppShell shell, Connectivity connectivity, string? serverName, string? serverUrl)
    {
        _connectivity = connectivity;
        _serverHost = Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) ? uri.Authority : serverUrl ?? "";
        _serverName = string.IsNullOrWhiteSpace(serverName) ? _serverHost : serverName;
        InitializeComponent();
        connectivity.Countdown += OnCountdown;
        Unloaded += (_, _) => connectivity.Countdown -= OnCountdown;
        Refresh();
        ShowDownloaded(shell);
    }

    private void ShowDownloaded(IAppShell shell)
    {
        var downloaded = shell.Downloads.Snapshot().Where(d => d.Status == DownloadStatus.Completed).ToList();
        if (downloaded.Count == 0) return;
        var sizes = downloaded.ToDictionary(d => d.ItemId, d => d.TotalBytes > 0 ? DownloadManager.FormatBytes(d.TotalBytes) : d.Subtitle ?? "");
        var row = new CardRow(shell, "Offline", "Row", () => L.Text("Card.AvailableOffline"),
            () => OpenDownloadsRequested?.Invoke(this, EventArgs.Empty), CardKind.Poster, 140, new UserDataOverrides())
        {
            CardSubtitle = i => sizes.GetValueOrDefault(i.Id, ""),
            Open = i => shell.Play(i),
            ShowCardActions = false,
        };
        row.SetItems([.. downloaded.Select(d => new MediaItem { Id = d.ItemId, Name = d.Title })]);
        OfflineRow.Children.Add(row);
    }

    /// <summary>Opens the profile menu's server switcher.</summary>
    public event EventHandler? SwitchServerRequested;

    /// <summary>Opens Downloads, where downloaded titles stay playable.</summary>
    public event EventHandler? OpenDownloadsRequested;

    private void Refresh()
    {
        TitleText.Text = L.Format("Errors.CantReach", _serverName);
        MessageText.Text = L.Format("Errors.CantReachHelp", _serverHost);
        DetailsText.Text = _detailsOpen ? L.Format("Errors.DetailsReason", _connectivity.Reason) : L.Text("Errors.Details");
        DetailsChevron.Glyph = _detailsOpen ? Theme.Icons.ExpandMore : Theme.Icons.ChevronRightSmall;
        OnCountdown(null, EventArgs.Empty);
    }

    private void OnCountdown(object? sender, EventArgs e)
    {
        var seconds = Math.Max(0, (int)Math.Ceiling((_connectivity.NextAttempt - DateTimeOffset.Now).TotalSeconds));
        Banner.Title = L.Text("Errors.Offline");
        Banner.Message = L.Format("Errors.RetryCountdown", (int)Connectivity.RetryInterval.TotalSeconds, seconds);
    }

    private async void OnRetry(object sender, RoutedEventArgs e)
    {
        if (!await _connectivity.RetryAsync()) Refresh();
    }

    private void OnSwitchServer(object sender, RoutedEventArgs e) => SwitchServerRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenDownloads(object sender, RoutedEventArgs e) => OpenDownloadsRequested?.Invoke(this, EventArgs.Empty);

    private void OnDetails(object sender, RoutedEventArgs e)
    {
        _detailsOpen = !_detailsOpen;
        Refresh();
    }
}
