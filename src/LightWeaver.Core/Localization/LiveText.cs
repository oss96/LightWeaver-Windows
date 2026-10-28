using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LightWeaver.Localization;

/// <summary>The framework-neutral part of the WPF app's <c>LiveText</c>: a weak subscription to
/// language changes, so view models (<see cref="ViewModels.ObservableObject"/>) re-raise their
/// labels without being kept alive by the language source. The WPF file uses WindowsBase's
/// PropertyChangedEventManager for the same thing; this assembly has no WPF.</summary>
internal static class LiveText
{
    private static readonly Lock Gate = new();
    // One handler per target, alive exactly as long as the target: a delegate to the target's
    // instance method stored as the value does not keep the key alive, and collected keys drop
    // out of the table on their own.
    private static readonly ConditionalWeakTable<object, EventHandler<PropertyChangedEventArgs>> Handlers = new();
    private static bool _hooked;

    public static void Observe(object target, EventHandler<PropertyChangedEventArgs> handler)
    {
        lock (Gate)
        {
            if (!_hooked)
            {
                L.Source.PropertyChanged += OnSourceChanged;
                _hooked = true;
            }
        }
        Handlers.AddOrUpdate(target, handler);
    }

    private static void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(LocalizationSource.Revision) or null or "")) return;
        // Snapshot first: a handler may observe new targets while the change is delivered.
        foreach (var (_, handler) in Handlers.ToArray()) handler(sender, e);
    }
}
