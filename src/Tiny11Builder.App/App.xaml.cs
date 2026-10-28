using Microsoft.UI.Xaml;

namespace Tiny11Builder.App;

public partial class App : Application
{
    private Window? window;
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Localization.Initialize();
        window = new MainWindow();
        window.Activate();
    }
}
