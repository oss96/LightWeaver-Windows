using System.Windows.Markup;
using System.Windows.Data;

namespace LightWeaver.Localization;

[MarkupExtensionReturnType(typeof(BindingExpression))]
public sealed class TextExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;
    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding("[" + Key + "]") { Source = L.Source, Mode = BindingMode.OneWay }
            .ProvideValue(serviceProvider);
}
