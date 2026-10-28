using LightWeaver.Navigation;
using Microsoft.UI.Xaml;

namespace LightWeaver.WinUI.Shell;

/// <summary>A page that takes part in Back/Forward and the Esc chain.</summary>
public interface INavPage
{
    /// <summary>Closes the page's innermost open layer (panel, results, morph). Returns false when
    /// there was nothing to close, so Esc is passed on.</summary>
    bool HandleEscape() => false;

    /// <summary>The page became current again (navigated to, or returned to through history).</summary>
    void OnShown() { }

    /// <summary>The server came back after an outage; reload what the page shows.</summary>
    void Reload() { }

    /// <summary>When the server last gave the data the page shows, which it keeps showing while the
    /// server is unreachable; null when it shows nothing of its own.</summary>
    DateTime? CachedAtUtc => null;

    /// <summary>The server is unreachable and the page has nothing to show yet: put what the cache
    /// holds for it on the page, then tell the window (<see cref="IAppShell.CachedDataShown"/>).</summary>
    void LoadCached() { }
}

/// <summary>A place was shown: its key, how it was reached (go, replace, back, forward), whether
/// its page had to be built, and when the switch started (<see cref="System.Diagnostics.Stopwatch"/>
/// timestamp), so the window can time it.</summary>
public sealed class NavShownEventArgs(string key, string mode, bool created, long started) : EventArgs
{
    public string Key { get; } = key;
    public string Mode { get; } = mode;
    public bool Created { get; } = created;
    public long Started { get; } = started;
}

/// <summary>Browser-style history for the window's content (<see cref="NavHistory{TPage}"/>):
/// Back/Forward keep each page's state (scroll position, filters) for the most recent entries and
/// rebuild older ones on demand.</summary>
public sealed class Navigator
{
    private readonly NavHistory<FrameworkElement> _history = new();

    public NavEntry<FrameworkElement>? Current => _history.Current;

    public bool CanGoBack => _history.CanGoBack;
    public bool CanGoForward => _history.CanGoForward;

    /// <summary>Raised after the current entry changed; the window shows <see cref="CurrentPage"/>.</summary>
    public event EventHandler? Navigated;

    /// <summary>Raised after the page was shown and told so (<see cref="INavPage.OnShown"/>).</summary>
    public event EventHandler<NavShownEventArgs>? Shown;

    public FrameworkElement? CurrentPage => Current?.Page;

    /// <summary>Whether <paramref name="page"/> is still kept by the current place or the history.</summary>
    public bool Holds(FrameworkElement page) => _history.Holds(page);

    /// <summary>Navigates to a new place. Navigating to the place already shown is a no-op unless
    /// <paramref name="refresh"/> rebuilds it. With <paramref name="reuse"/> (Home, a library) the
    /// place's page still alive in the history is shown as it was left instead of building it
    /// again; its old entry rebuilds on demand.</summary>
    public void Go(string key, Func<FrameworkElement> create, bool refresh = false, bool reuse = false)
    {
        _history.Go(key, create, refresh, reuse);
        Show("go");
    }

    /// <summary>Replaces the current place without adding history (login, server switch).</summary>
    public void Replace(string key, Func<FrameworkElement> create)
    {
        _history.Replace(key, create);
        Show("replace");
    }

    /// <summary>Forgets all history (signed out, profile switched).</summary>
    public void Reset(string key, Func<FrameworkElement> create)
    {
        _history.Reset(key, create);
        Show("replace");
    }

    public void Back()
    {
        if (_history.Back()) Show("back");
    }

    /// <summary>Goes back when the previous place is <paramref name="key"/>, so it returns as it was
    /// left (a breadcrumb). False when it is not.</summary>
    public bool BackTo(string key)
    {
        if (!_history.IsBack(key)) return false;
        Back();
        return true;
    }

    public void Forward()
    {
        if (_history.Forward()) Show("forward");
    }

    private void Show(string mode)
    {
        if (Current is null) return;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var created = _history.Realize();
        Navigated?.Invoke(this, EventArgs.Empty);
        (Current.Page as INavPage)?.OnShown();
        Shown?.Invoke(this, new NavShownEventArgs(Current.Key, mode, created, started));
    }
}
