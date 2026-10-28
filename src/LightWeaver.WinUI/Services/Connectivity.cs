using System.Net.Sockets;
using Microsoft.UI.Dispatching;

namespace LightWeaver.WinUI.Services;

/// <summary>Whether the active server answers (design 14a). A failed server request marks it
/// unreachable; the app then probes the server's public info every 15 s and reports when it is
/// back, so the page restores itself.</summary>
public sealed class Connectivity
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(15);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly DispatcherQueueTimer _timer;
    private string? _serverUrl;
    private string? _activeServer;

    public Connectivity(DispatcherQueue ui)
    {
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += async (_, _) =>
        {
            if (DateTimeOffset.Now < NextAttempt) { Countdown?.Invoke(this, EventArgs.Empty); return; }
            await RetryAsync();
        };
    }

    public bool IsOffline { get; private set; }

    /// <summary>The technical reason of the last failure, shown under Details.</summary>
    public string Reason { get; private set; } = "";

    public DateTimeOffset NextAttempt { get; private set; }

    /// <summary>The server went away or came back.</summary>
    public event EventHandler? Changed;

    /// <summary>Once a second while offline, for the banner's countdown.</summary>
    public event EventHandler? Countdown;

    /// <summary>True for failures that mean "cannot reach the server" rather than a refused request.</summary>
    public static bool IsUnreachable(Exception ex) => ex is HttpRequestException or SocketException or TaskCanceledException or TimeoutException
        || ex.InnerException is { } inner && IsUnreachable(inner);

    /// <summary>Called by any server-backed page when a request failed. Unless
    /// <paramref name="confirmed"/>, the server is probed first, so a late failure of a request
    /// to another server cannot take a reachable one offline.</summary>
    public void Report(string? serverUrl, Exception ex, bool confirmed = false)
    {
        if (!IsUnreachable(ex) || IsOtherServer(serverUrl)) return;
        Reason = ex.InnerException?.Message ?? ex.Message;
        if (IsOffline) return;
        if (confirmed) GoOffline(serverUrl);
        else _ = ConfirmAsync(serverUrl);
    }

    private readonly HashSet<string> _confirming = [];

    private async Task ConfirmAsync(string? serverUrl)
    {
        var key = (serverUrl ?? "").TrimEnd('/').ToLowerInvariant();
        if (!_confirming.Add(key)) return;
        try
        {
            if (await ProbeAsync(serverUrl) || IsOffline || IsOtherServer(serverUrl)) return;
            GoOffline(serverUrl);
        }
        finally
        {
            _confirming.Remove(key);
        }
    }

    private void GoOffline(string? serverUrl)
    {
        _serverUrl = serverUrl;
        IsOffline = true;
        NextAttempt = DateTimeOffset.Now + RetryInterval;
        _timer.Start();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool IsOtherServer(string? serverUrl) => _activeServer is not null && serverUrl is not null && !SameServer(serverUrl, _activeServer);

    /// <summary>Probes now (the Retry button, and the 15 s timer).</summary>
    public async Task<bool> RetryAsync()
    {
        NextAttempt = DateTimeOffset.Now + RetryInterval;
        var probed = _serverUrl;
        var reachable = await ProbeAsync(probed);
        if (!string.Equals(probed, _serverUrl, StringComparison.Ordinal)) return reachable;
        if (reachable && IsOffline)
        {
            IsOffline = false;
            _timer.Stop();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return reachable;
    }

    /// <summary>A session for <paramref name="serverUrl"/> became active: an outage of another
    /// server no longer applies, and only this server's failures count from now on.</summary>
    public void Track(string? serverUrl)
    {
        _activeServer = serverUrl;
        if (!IsOffline || serverUrl is not null && _serverUrl is not null && SameServer(serverUrl, _serverUrl)) return;
        _serverUrl = serverUrl;
        IsOffline = false;
        _timer.Stop();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool SameServer(string a, string b) => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the server's unauthenticated info endpoint answers within 8 s.</summary>
    public async Task<bool> ProbeAsync(string? serverUrl)
    {
        if (string.IsNullOrEmpty(serverUrl)) return false;
        try
        {
            using var response = await Http.GetAsync(serverUrl.TrimEnd('/') + "/System/Info/Public");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            Reason = ex.InnerException?.Message ?? ex.Message;
            return false;
        }
    }
}
