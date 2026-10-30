using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Tiny11Builder.App;

public sealed partial class MainWindow
{
    private async Task EnterCustomAsync(bool fromWelcome)
    {
        if (busy || modalOpen || Root.XamlRoot is null) return;
        var previousFocus = CurrentFocus();
        ContentDialogResult choice;
        modalOpen = true;
        try
        {
            CustomEntryDialog.XamlRoot = Root.XamlRoot;
            CustomEntryDialog.RequestedTheme = DialogTheme();
            choice = await CustomEntryDialog.ShowAsync();
        }
        catch (Exception error)
        {
            ShowNotice(UiText.Get("CustomEntry.Failed"), ErrorText.Reason(error), InfoBarSeverity.Error);
            return;
        }
        finally
        {
            modalOpen = false;
            RestoreFocus(previousFocus);
        }
        if (choice == ContentDialogResult.Secondary) await LoadRecipeAsync(useCustom: true);
        else if (choice == ContentDialogResult.Primary)
        {
            ModeChoice.SelectedIndex = (int)Tiny11Builder.Core.BuildMode.Custom;
            if (fromWelcome) GoStep(1);
        }
    }

    private async void OnLoadCustomRecipe(object sender, RoutedEventArgs args)
    {
        if (!busy && !modalOpen) await LoadRecipeAsync(useCustom: true);
    }
}
