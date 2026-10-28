using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace LightWeaver.Login;

/// <summary>A Jellyfin server the login form found.</summary>
public sealed record ServerInfo(string Url, string Name);

/// <summary>A user the server lists on its login screen (GET /Users/Public).</summary>
public sealed record PublicUser(Guid Id, string Name, string? ImageTag, bool HasPassword)
{
    public string? ImageUrl(string serverUrl, int size)
        => ImageTag is null ? null : $"{serverUrl}/Users/{Id:N}/Images/Primary?tag={ImageTag}&maxWidth={size}&quality=90";
}

/// <summary>The unauthenticated calls the login form makes before anyone has signed in.</summary>
public sealed class ServerProbe(HttpClient http)
{
    /// <summary>Per attempt; three candidates must still answer well inside the design's wait.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(5);

    public static ServerProbe Shared { get; } = new(new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(4) }));

    /// <summary>Tries each candidate URL in order and returns the first that answers as a Jellyfin server.</summary>
    public async Task<ServerInfo?> FindAsync(string input, CancellationToken ct = default)
    {
        foreach (var url in ServerAddress.Candidates(input))
        {
            ct.ThrowIfCancellationRequested();
            if (await InfoAsync(url, ct).ConfigureAwait(false) is { } info) return info;
        }
        return null;
    }

    /// <summary>The server's public info, or null when nothing (or not Jellyfin) answers there.</summary>
    public async Task<ServerInfo?> InfoAsync(string url, CancellationToken ct = default)
    {
        var body = await GetAsync<PublicInfo>(url + "/System/Info/Public", ct).ConfigureAwait(false);
        return body is { Id.Length: > 0 } ? new ServerInfo(url, string.IsNullOrWhiteSpace(body.ServerName) ? ServerAddress.Display(url) : body.ServerName) : null;
    }

    public async Task<bool> QuickConnectEnabledAsync(string url, CancellationToken ct = default)
        => await GetAsync<bool?>(url + "/QuickConnect/Enabled", ct).ConfigureAwait(false) == true;

    public async Task<IReadOnlyList<PublicUser>> PublicUsersAsync(string url, CancellationToken ct = default)
    {
        var users = await GetAsync<List<UserBody>>(url + "/Users/Public", ct).ConfigureAwait(false) ?? [];
        return [.. users
            .Where(u => !string.IsNullOrWhiteSpace(u.Name) && Guid.TryParse(u.Id, out _))
            .Select(u => new PublicUser(Guid.Parse(u.Id!), u.Name!, u.PrimaryImageTag, u.HasPassword))];
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AttemptTimeout);
        try
        {
            return await http.GetFromJsonAsync<T>(url, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            return default;
        }
    }

    private sealed record PublicInfo(
        [property: JsonPropertyName("Id")] string? Id,
        [property: JsonPropertyName("ServerName")] string? ServerName);

    private sealed record UserBody(
        [property: JsonPropertyName("Id")] string? Id,
        [property: JsonPropertyName("Name")] string? Name,
        [property: JsonPropertyName("PrimaryImageTag")] string? PrimaryImageTag,
        [property: JsonPropertyName("HasPassword")] bool HasPassword);
}
