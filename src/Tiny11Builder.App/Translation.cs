using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Tiny11Builder.App;

// Only these text properties are writable. Catalog text is never parsed as XAML or used as a destination.
public static class Translation
{
    public static readonly DependencyProperty PropertiesProperty = DependencyProperty.RegisterAttached(
        "Properties", typeof(string), typeof(Translation), new PropertyMetadata("", OnPropertiesChanged));
    private static readonly DependencyProperty RegisteredProperty = DependencyProperty.RegisterAttached(
        "Registered", typeof(bool), typeof(Translation), new PropertyMetadata(false));

    public static string GetProperties(DependencyObject element) => (string)element.GetValue(PropertiesProperty);
    public static void SetProperties(DependencyObject element, string value) => element.SetValue(PropertiesProperty, value);

    private static void OnPropertiesChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        Apply(element);
        if (element is not FrameworkElement control || (bool)control.GetValue(RegisteredProperty)) return;
        Action refresh = () => Apply(control);
        control.SetValue(RegisteredProperty, true);
        control.Loaded += (_, _) => { UiText.Changed -= refresh; UiText.Changed += refresh; Apply(control); };
        control.Unloaded += (_, _) => UiText.Changed -= refresh;
    }

    private static void Apply(DependencyObject element)
    {
        foreach (var entry in GetProperties(element).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = entry.IndexOf('=');
            if (split <= 0) continue;
            var property = entry[..split];
            var text = UiText.Get(entry[(split + 1)..]);
            switch (property)
            {
                case "Text" when element is TextBlock block: block.Text = text; break;
                case "Content" when element is ContentControl content: content.Content = text; break;
                case "Header" when element is TextBox box: box.Header = text; break;
                case "Header" when element is ComboBox combo: combo.Header = text; break;
                case "Header" when element is NumberBox number: number.Header = text; break;
                case "Header" when element is ToggleSwitch toggle: toggle.Header = text; break;
                case "Header" when element is Expander expander: expander.Header = text; break;
                case "PlaceholderText" when element is TextBox box: box.PlaceholderText = text; break;
                case "OffContent" when element is ToggleSwitch toggle: toggle.OffContent = text; break;
                case "OnContent" when element is ToggleSwitch toggle: toggle.OnContent = text; break;
                case "Title" when element is InfoBar bar: bar.Title = text; break;
                case "Message" when element is InfoBar bar: bar.Message = text; break;
                case "Title" when element is ContentDialog dialog: dialog.Title = text; break;
                case "PrimaryButtonText" when element is ContentDialog dialog: dialog.PrimaryButtonText = text; break;
                case "SecondaryButtonText" when element is ContentDialog dialog: dialog.SecondaryButtonText = text; break;
                case "CloseButtonText" when element is ContentDialog dialog: dialog.CloseButtonText = text; break;
                case "AutomationProperties.Name": AutomationProperties.SetName(element, text); break;
                case "AutomationProperties.HelpText": AutomationProperties.SetHelpText(element, text); break;
            }
        }
    }
}
