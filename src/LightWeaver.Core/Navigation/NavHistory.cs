namespace LightWeaver.Navigation;

/// <summary>One place in the history: what to show and how to make it again.</summary>
public sealed class NavEntry<TPage>(string key, Func<TPage> create) where TPage : class
{
    public string Key { get; } = key;
    public Func<TPage> Create { get; } = create;
    public TPage? Page { get; internal set; }
}

/// <summary>Browser-style history: Back/Forward keep each page (scroll position, filters) for the
/// most recent entries and rebuild older ones on demand. Framework-free; the window's navigator
/// shows <see cref="Current"/> after each change.</summary>
public sealed class NavHistory<TPage> where TPage : class
{
    private const int KeptAlive = 6;
    private const int MaxHistory = 50;
    private readonly List<NavEntry<TPage>> _back = [];
    private readonly List<NavEntry<TPage>> _forward = [];

    public NavEntry<TPage>? Current { get; private set; }

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>Makes <paramref name="key"/> current. The place already current stays as it is
    /// unless <paramref name="refresh"/> rebuilds it. With <paramref name="reuse"/> the place's page
    /// still alive in Back or Forward history moves to the new entry; its old entry rebuilds on
    /// demand.</summary>
    public void Go(string key, Func<TPage> create, bool refresh = false, bool reuse = false)
    {
        if (Current?.Key == key && !refresh) return;
        var kept = reuse ? TakeKeptPage(key) : null;
        if (Current is not null && Current.Key != key) Push(_back, Current);
        _forward.Clear();
        Current = new NavEntry<TPage>(key, create) { Page = kept };
    }

    /// <summary>Replaces the current place without adding history.</summary>
    public void Replace(string key, Func<TPage> create) => Current = new NavEntry<TPage>(key, create);

    /// <summary>Forgets all history and makes <paramref name="key"/> current.</summary>
    public void Reset(string key, Func<TPage> create)
    {
        _back.Clear();
        _forward.Clear();
        Replace(key, create);
    }

    /// <summary>False when there is nothing to go back to.</summary>
    public bool Back()
    {
        if (_back.Count == 0 || Current is null) return false;
        Push(_forward, Current);
        Current = Pop(_back);
        return true;
    }

    /// <summary>Whether the previous place is <paramref name="key"/>.</summary>
    public bool IsBack(string key) => _back.Count > 0 && _back[^1].Key == key;

    /// <summary>False when there is nothing to go forward to.</summary>
    public bool Forward()
    {
        if (_forward.Count == 0 || Current is null) return false;
        Push(_back, Current);
        Current = Pop(_forward);
        return true;
    }

    /// <summary>Whether <paramref name="page"/> is the current place's page or one Back or Forward still keeps.</summary>
    public bool Holds(TPage page)
        => ReferenceEquals(Current?.Page, page) || _back.Concat(_forward).Any(e => ReferenceEquals(e.Page, page));

    /// <summary>Builds the current page if it has none and lets go of pages too far back or
    /// forward to keep. Returns whether the page was built.</summary>
    public bool Realize()
    {
        if (Current is null) return false;
        var created = Current.Page is null;
        Current.Page ??= Current.Create();
        var recent = _back.TakeLast(KeptAlive).Concat(_forward.TakeLast(KeptAlive)).ToHashSet();
        foreach (var entry in _back.Concat(_forward))
            if (!recent.Contains(entry)) entry.Page = null;
        return created;
    }

    private TPage? TakeKeptPage(string key)
    {
        foreach (var stack in new[] { _back, _forward })
            for (var i = stack.Count - 1; i >= 0; i--)
            {
                if (stack[i].Key != key || stack[i].Page is not { } page) continue;
                stack[i].Page = null;
                return page;
            }
        return null;
    }

    private static void Push(List<NavEntry<TPage>> stack, NavEntry<TPage> entry)
    {
        stack.Add(entry);
        if (stack.Count > MaxHistory) stack.RemoveAt(0);
    }

    private static NavEntry<TPage> Pop(List<NavEntry<TPage>> stack)
    {
        var entry = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return entry;
    }
}
