namespace LightWeaver.Navigation;

/// <summary>The page presentation while its server is unreachable.</summary>
public enum OfflineShow
{
    Page,
    CachedPage,
    Unreachable,
}

/// <summary>Chooses cached content when the page has a saved timestamp.</summary>
public static class OfflinePlan
{
    public static OfflineShow For(bool offline, string key, DateTime? cachedAtUtc)
        => !offline || key is "downloads" or "settings" or "login" ? OfflineShow.Page
            : cachedAtUtc is null ? OfflineShow.Unreachable
            : OfflineShow.CachedPage;
}
