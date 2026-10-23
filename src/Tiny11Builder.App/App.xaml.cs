using Microsoft.UI.Xaml;
using Microsoft.Windows.Globalization;

namespace Tiny11Builder.App;

public partial class App : Application
{
    private Window? window;

    public App()
    {
        // The override must be in place before any XAML is loaded: markup-extension text is resolved
        // when a page is parsed, not when the window is shown.
        var language = LanguageCatalog.Normalize(AppSettings.Load().Language);
        if (language.Length > 0) ApplicationLanguages.PrimaryLanguageOverride = language;
        InitializeComponent();
        ResourceProvider.Install();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
    }
}
