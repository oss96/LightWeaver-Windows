using System.Windows.Controls;

namespace LightWeaver.ViewModels;

/// <summary>One entry in the browse history: the live view instance (so scroll/filter
/// state restores byte-for-exact on back/forward), its header title, the nav-rail item
/// that should be lit while it shows, and whether it is a rail root (Home / a library
/// opened from the rail — used to de-dupe re-selecting the rail item you're already on).</summary>
public sealed record NavFrame(UserControl View, string Title, RadioButton? Rail, bool IsRailRoot = false);

/// <summary>
/// Back + forward browse history (Phase 7 M3). Replaces the old append-only,
/// self-truncating stack: every transition is recorded — including rail Home and rail
/// library clicks, which used to reset the stack — so Back always returns to the exact
/// prior screen and Forward re-enters. Standard browser semantics: a fresh
/// <see cref="Navigate"/> truncates the forward branch.
/// </summary>
public sealed class NavigationService
{
    /// <summary>How many past frames stay reachable. Each frame holds a LIVE UserControl —
    /// deliberately, so scroll/filter state restores exactly — plus its MediaItem lists and every
    /// bitmap its cards reference, and an Image element holding a bitmap keeps ImageCache's LRU
    /// from releasing it. Unbounded, that made browse memory grow monotonically until logout or
    /// exit (B24). The exact-restore promise only matters for the recent few screens; beyond this
    /// depth the oldest entries are dropped, which shortens Back rather than corrupting it.</summary>
    private const int MaxBackDepth = 20;

    private readonly List<NavFrame> _back = [];
    private readonly List<NavFrame> _forward = [];

    public NavFrame? Current { get; private set; }
    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>All live frames (back + current + forward) — for the user-data fan-out
    /// that keeps cached list cards' badges fresh.</summary>
    public IEnumerable<NavFrame> Frames =>
        _back.Concat(Current is null ? Enumerable.Empty<NavFrame>() : [Current]).Concat(_forward);

    /// <summary>Records a new destination: pushes the current frame onto the back stack and
    /// clears the forward branch. Re-selecting the rail root you are already on is a no-op.</summary>
    public void Navigate(NavFrame frame)
    {
        if (frame.IsRailRoot && Current is { IsRailRoot: true } cur
            && ReferenceEquals(cur.Rail, frame.Rail))
        {
            Log("navigate_skip", frame);
            return;
        }
        if (Current is not null)
            _back.Add(Current);
        // Trim the oldest frames so the live-view retention stays bounded (see MaxBackDepth).
        if (_back.Count > MaxBackDepth)
            _back.RemoveRange(0, _back.Count - MaxBackDepth);
        _forward.Clear();
        Current = frame;
        Log("navigate", frame);
    }

    /// <summary>Returns to the prior frame (pushing the current one onto the forward
    /// branch). No-op at the root; returns the frame now current.</summary>
    public NavFrame? GoBack()
    {
        if (_back.Count == 0)
        {
            Log("back_skip", Current);
            return Current;
        }
        if (Current is not null)
            _forward.Add(Current);
        Current = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        Log("back", Current);
        return Current;
    }

    /// <summary>Re-enters what <see cref="GoBack"/> left. No-op with no forward branch.</summary>
    public NavFrame? GoForward()
    {
        if (_forward.Count == 0)
        {
            Log("forward_skip", Current);
            return Current;
        }
        if (Current is not null)
            _back.Add(Current);
        Current = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        Log("forward", Current);
        return Current;
    }

    /// <summary>Clears all history and sets the root (or null on logout).</summary>
    public void Reset(NavFrame? root)
    {
        _back.Clear();
        _forward.Clear();
        Current = root;
        Log("reset", root);
    }

    /// <summary>Mutates the current frame in place (search refine — updates the view/title
    /// without adding a history entry).</summary>
    public void ReplaceCurrent(NavFrame frame)
    {
        Current = frame;
        Log("replace", frame);
    }

    private void Log(string action, NavFrame? frame)
        => Diagnostics.AppLog.Detail("navigation",
            $"event={action} view={frame?.View.GetType().Name ?? "none"} "
            + $"root={frame?.IsRailRoot == true} back={_back.Count} forward={_forward.Count}");
}
