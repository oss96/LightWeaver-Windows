using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LightWeaver.Localization;

/// <summary>Live code-created labels. Only explicitly localized properties are bound.</summary>
internal static class LiveText
{
    private static readonly DependencyProperty ObserverProperty = DependencyProperty.RegisterAttached(
        "Observer", typeof(ViewObserver), typeof(LiveText));

    public static void Watch(FrameworkElement root)
    {
        if (root.GetValue(ObserverProperty) is null)
            root.SetValue(ObserverProperty, new ViewObserver(root));
    }

    public static T Bind<T>(T target, string property, Func<object?> value) where T : DependencyObject
    {
        var descriptor = DependencyPropertyDescriptor.FromName(property, target.GetType(), target.GetType())
            ?? throw new ArgumentException("Not a dependency property: " + property, nameof(property));
        return Bind(target, descriptor.DependencyProperty, value);
    }

    public static T Bind<T>(T target, DependencyProperty property, Func<object?> value) where T : DependencyObject
    {
        BindingOperations.SetBinding(target, property,
            new Binding(nameof(Expression.Value)) { Source = new Expression(value), Mode = BindingMode.OneWay });
        return target;
    }

    public static void Observe(object target, EventHandler<PropertyChangedEventArgs> handler)
        => PropertyChangedEventManager.AddHandler(L.Source, handler, nameof(LocalizationSource.Revision));

    public static void ReplaceChoices(Selector box, IEnumerable<object> labels)
    {
        var index = box.SelectedIndex;
        box.Items.Clear();
        foreach (var label in labels) box.Items.Add(label);
        box.SelectedIndex = index;
    }

    private sealed class ViewObserver
    {
        private readonly FrameworkElement _root;
        public ViewObserver(FrameworkElement root)
        {
            _root = root;
            Observe(this, OnLanguageChanged);
            root.Loaded += (_, _) => Refresh();
        }

        private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_root.Dispatcher.CheckAccess()) Refresh();
            else _ = _root.Dispatcher.BeginInvoke(Refresh);
        }

        private void Refresh()
        {
            var visited = new HashSet<DependencyObject>();
            Visit(_root);
            void Visit(DependencyObject node)
            {
                if (!visited.Add(node)) return;
                // Refresh display bindings only. Editable text and selections keep their values.
                if (node is not TextBoxBase && node is not ComboBox)
                {
                    var values = node.GetLocalValueEnumerator();
                    while (values.MoveNext())
                    {
                        var property = values.Current.Property;
                        if (property.Name is not ("Text" or "Content" or "Header" or "ToolTip" or "Title")) continue;
                        if (property.PropertyType != typeof(string) && node.GetValue(property) is not string) continue;
                        switch (BindingOperations.GetBindingExpressionBase(node, property))
                        {
                            case BindingExpression binding: binding.UpdateTarget(); break;
                            case MultiBindingExpression binding: binding.UpdateTarget(); break;
                        }
                    }
                }
                if (node is Visual or Visual3D)
                    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i));
                foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().ToArray()) Visit(child);
                if (node is Popup { Child: { } childRoot }) Visit(childRoot);
            }
        }
    }

    private sealed class Expression : INotifyPropertyChanged
    {
        private readonly Func<object?> _value;
        public event PropertyChangedEventHandler? PropertyChanged;
        public object? Value => _value();

        // WPF's weak event subscription does not retain detached controls or cached shells.
        public Expression(Func<object?> value)
        {
            _value = value;
            Observe(this, OnLanguageChanged);
        }

        private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
    }
}
