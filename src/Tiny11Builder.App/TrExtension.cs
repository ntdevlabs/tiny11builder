using Microsoft.UI.Xaml.Markup;

namespace Tiny11Builder.App;

[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class TrExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    protected override object ProvideValue() => Loc.Get(Key);
}
