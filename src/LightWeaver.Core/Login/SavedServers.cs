using LightWeaver.Jellyfin;

namespace LightWeaver.Login;

/// <summary>A server with at least one saved profile. <see cref="Profile"/> is the remembered user
/// a one-click connect signs in as: the active profile when it is on this server, else the first.</summary>
public sealed record SavedServer(string Url, string Name, SavedCredentials Profile, IReadOnlyList<SavedCredentials> Profiles);

public static class SavedServers
{
    /// <summary>The saved profiles grouped by server, in the order they were first saved.</summary>
    public static IReadOnlyList<SavedServer> From(CredentialProfiles store)
        => [.. store.Profiles
            .GroupBy(p => p.ServerUrl.TrimEnd('/'), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var remembered = store.Active is { } active && g.Contains(active) ? active : g.First();
                var name = g.Select(p => p.ServerName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? ServerAddress.Display(remembered.ServerUrl);
                return new SavedServer(remembered.ServerUrl, name, remembered, [.. g]);
            })];
}
