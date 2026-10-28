using System.Reflection;
using LightWeaver.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

namespace LightWeaver.WinUI.Localization;

/// <summary>The WPF app's <c>{loc:Text Key}</c> for WinUI 3, over the shared <see cref="L"/>.
/// Spike W1 proved the shape (criterion 7). Differences from WPF: WinUI markup extensions take no
/// positional argument, so it is <c>{loc:Text Key=...}</c>; and WinUI cannot return a Binding from
/// ProvideValue, so the extension keeps weak references to its targets and rewrites them when the
/// language changes.</summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class TextExtension : MarkupExtension
{
    private static readonly List<(WeakReference<DependencyObject> Target, DependencyProperty Property, string Key)> Live = [];

    static TextExtension() => L.Source.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName is nameof(LocalizationSource.Revision) or null or "") Refresh();
    };

    public string Key { get; set; } = "";

    protected override object ProvideValue(IXamlServiceProvider serviceProvider)
    {
        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget
            {
                TargetObject: DependencyObject target,
                TargetProperty: ProvideValueTargetProperty { DeclaringType: { } owner, Name: { } name },
            }
            && Resolve(owner, name) is { } property)
        {
            lock (Live)
            {
                Live.RemoveAll(e => !e.Target.TryGetTarget(out _));
                Live.Add((new WeakReference<DependencyObject>(target), property, Key));
            }
        }
        return L.Resolve(Key);
    }

    protected override object ProvideValue() => L.Resolve(Key);

    private static readonly Dictionary<(Type, string), DependencyProperty?> Properties = [];

    /// <summary>The dependency property behind <paramref name="name"/>: WinUI's own controls expose
    /// it as a static property, the app's controls as a static field.</summary>
    private static DependencyProperty? Resolve(Type owner, string name)
    {
        lock (Properties)
        {
            if (Properties.TryGetValue((owner, name), out var cached)) return cached;
            DependencyProperty? found = null;
            for (var t = owner; t is not null && found is null; t = t.BaseType)
                found = t.GetProperty(name + "Property", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as DependencyProperty
                    ?? t.GetField(name + "Property", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as DependencyProperty;
            return Properties[(owner, name)] = found;
        }
    }

    private static void Refresh()
    {
        List<(DependencyObject Target, DependencyProperty Property, string Key)> live = [];
        lock (Live)
        {
            Live.RemoveAll(e => !e.Target.TryGetTarget(out _));
            foreach (var (weak, property, key) in Live)
                if (weak.TryGetTarget(out var target)) live.Add((target, property, key));
        }
        foreach (var (target, property, key) in live)
            target.SetValue(property, L.Resolve(key));
    }
}
