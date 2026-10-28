using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Tiny11Builder.Core;

namespace Tiny11Builder.App;

public sealed partial class MainWindow : Window
{
    private readonly BuildEngine engine = new(new ProcessRunner(), new MediaSourceFactory(),
        new OfflineRegistry(), new ImageOperations(), new BuildHost());
    private readonly List<OptionChoice> options = CleanupCatalog.Options.Select(o => new OptionChoice(o)).ToList();
    private List<InventoryChoice> inventory = [];
    private ExpertAction[] pendingActions = [];
    private int? pendingImageIndex;
    private string pendingSourcePath = "";
    private WindowsImage? image;
    private int step;
    private int reached;
    private bool initialized;
    private bool navigating;
    private bool applyingMode;
    private bool busy;
    private bool completed;
    private bool coreAcknowledged;
    private bool modalOpen;
    private int _appliedLanguageIndex;
    private enum NoticeAction { None, OpenLogs, CopyDetails, OpenTools, OpenOfficialDownload }
    private string officialDownloadPage = OfficialImageDownloader.DownloadPage;
    // The run RunOperation is executing and the cancel prompt open for it; both are null when nothing is happening.
    private OperationRun? active;
    private CancellationFlow? cancelFlow;
    private OperationKind running => active?.Kind ?? OperationKind.Other;
    private bool cancelRequested => active?.CancelRequested == true;
    // The build or resume that last stopped without producing an ISO, so the page can offer the right retry.
    private OperationKind? stoppedRun;
    private bool stoppedHasCheckpoint;
    private string? resumeJobFile;
    private string servicingTool = ToolLocator.FindDism();
    private string isoTool = ToolLocator.FindOscdimg();
    private readonly BuildStageBoard board = new();
    private BuildResult? result;
    private string? workDirectory;
    private readonly LiveLog liveLog = new();
    private ScrollViewer? logScroller;
    private readonly ConcurrentQueue<BuildProgress> progressUpdates = new();
    private readonly LatestProgress<DownloadProgress> downloadUpdates = new();
    private Func<DownloadProgress, string>? downloadText;
    private readonly DispatcherTimer progressTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly Windows.UI.ViewManagement.AccessibilitySettings accessibility = new();
    private bool? appliedContrast;
    private NoticeAction noticeAction;
    private string noticeDetails = "";
    private int activePhase = -1;
    private readonly string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tiny11Builder");

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleArea);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 900));
        AppWindow.Closing += (_, args) =>
        {
            if (!busy) return;
            args.Cancel = true;
            ShowNotice(L10n.T("OpStillRunning", "An operation is still running."), cancelRequested ? L10n.T("OpCancellingWait", "It is being cancelled. Wait for it to stop before closing.") :
                string.Format(L10n.T("OpFinishWait", "Choose {0} and wait for the image operation to finish before closing."), CancelPrompts.ButtonLabel), InfoBarSeverity.Warning);
        };
        WorkspaceBox.Text = @"C:\tiny11";
        SupportPurpose.Text = L10n.T("SupportPurpose", ProjectSupport.Purpose);
        OutputBox.Text = Path.Combine(WorkspaceBox.Text, $"tiny11-{DateTime.Now:yyyyMMdd-HHmmss}.iso");
        BuildStages.ItemsSource = board.Stages;
        progressTimer.Tick += (_, _) => { FlushBuildProgress(); DrainDownload(); };
        // Desktop apps cannot subscribe to AccessibilitySettings.HighContrastChanged (it throws), so
        // high contrast is re-read whenever the theme changes or the window is activated.
        Root.ActualThemeChanged += (_, _) => ApplyContrast();
        Activated += (_, _) => ApplyContrast();
        Closed += (_, _) => progressTimer.Stop();
        LatestDownloadDescription.Text = string.Format(L10n.T("OfficialIsoFmt", "Official Microsoft ISO · {0} · {1}"), HostArchitecture.ToUpperInvariant(), CultureInfo.CurrentUICulture.NativeName);
        foreach (var choice in options) choice.PropertyChanged += (_, _) => UpdateCounts();
        initialized = true;
        ApplyContrast();
        ApplyMode();
        FilterOptions();
        GoStep(0);
        var args = Environment.GetCommandLineArgs();
        var restoreIndex = Array.IndexOf(args, "--restore");
        if (restoreIndex >= 0 && restoreIndex + 1 < args.Length)
        {
            try
            {
                var recipe = JsonSerializer.Deserialize<BuildRecipe>(File.ReadAllText(args[restoreIndex + 1]), JsonDefaults.Options)
                    ?? throw new InvalidDataException("The saved session is empty.");
                RestoreRecipe(recipe);
                ShowNotice(L10n.T("SessionRestored", "Session restored."), L10n.T("SessionRestoredMsg", "Inspect the image again before continuing. No build starts automatically."));
            }
            catch (Exception error) { ShowNotice(L10n.T("SessionRestoreFail", "Session could not be restored."), ErrorText.Reason(error), InfoBarSeverity.Error); }
        }
        UpdateCounts();
        ApplyLanguageSelection();
    }

    private void ApplyLanguageSelection()
    {
        _appliedLanguageIndex = Localization.Current switch
        {
            "zh-CN" => 2,
            "en-US" => 1,
            _ => 0
        };
        if (LanguageChoice.SelectedIndex != _appliedLanguageIndex)
            LanguageChoice.SelectedIndex = _appliedLanguageIndex;
    }

    private FrameworkElement[] Panels => [WelcomePanel, SourcePanel, CleanupPanel, SettingsPanel, ReviewPanel, BuildPanel];
    private BuildMode SelectedMode => (BuildMode)ModeChoice.SelectedIndex;
    private bool UsesCore => SelectedMode == BuildMode.Tiny11Core ||
        (SelectedMode == BuildMode.Custom && CustomCoreCheck.IsOn);
    private static string HostArchitecture => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    private void OnContentSizeChanged(object sender, SizeChangedEventArgs args) => UpdateWelcomeLayout();

    private void OnContentViewChanged(object sender, ScrollViewerViewChangedEventArgs args)
    {
        StepNumber.Opacity = 0.10 * Math.Clamp(1 - ContentScroller.VerticalOffset / 32, 0, 1);
    }

    private void UpdateWelcomeLayout()
    {
        if (WelcomePanel is null || ModeCards is null || RemovalModeCards is null || ReviewCards is null || SourceCards is null) return;
        var padding = ContentScroller.Padding;
        WelcomePanel.MinHeight = Math.Max(0, ContentScroller.ActualHeight - padding.Top - padding.Bottom);
        var stacked = ContentScroller.ActualWidth - padding.Left - padding.Right < 560;
        ArrangeModeCards(ModeCards, [RegularModeCard, CoreModeCard, CustomModeCard], stacked);
        ArrangeModeCards(RemovalModeCards, [Tiny11Selection, CoreSelection, CustomSelection], stacked);
        ArrangeSourceCards(ContentScroller.ActualWidth - padding.Left - padding.Right);
        ReviewCards.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        FrameworkElement[] review = [ReviewImageCard, ReviewModeCard, ReviewPathsCard, ReviewOptionsCard];
        for (var index = 0; index < review.Length; index++)
        {
            ReviewCards.RowDefinitions[index].Height = stacked || index < 2 ? GridLength.Auto : new GridLength(0);
            Grid.SetColumn(review[index], stacked ? 0 : index % 2);
            Grid.SetRow(review[index], stacked ? index : index / 2);
        }
    }

    // The one-click download comes first so it is reachable without scrolling at the default window size;
    // with enough width the two options sit side by side instead of stacked.
    private void ArrangeSourceCards(double width)
    {
        var wide = width >= 960;
        SourceCards.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SourceCards.RowDefinitions[1].Height = wide ? new GridLength(0) : GridLength.Auto;
        Grid.SetColumn(LocalMediaCard, wide ? 1 : 0);
        Grid.SetRow(LocalMediaCard, wide ? 0 : 1);
    }

    private static void ArrangeModeCards(Grid container, FrameworkElement[] cards, bool stacked)
    {
        container.ColumnSpacing = stacked ? 0 : 16;
        container.RowSpacing = stacked ? 12 : 0;
        for (var index = 0; index < cards.Length; index++)
        {
            container.ColumnDefinitions[index].Width = index == 0 || !stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            container.RowDefinitions[index].Height = stacked ? GridLength.Auto :
                index == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(cards[index], stacked ? 0 : index);
            Grid.SetRow(cards[index], stacked ? index : 0);
        }
    }

    private void GoStep(int value)
    {
        var focused = CurrentFocus();
        step = value;
        reached = Math.Max(reached, value);
        for (var i = 0; i < Panels.Length; i++) Panels[i].Visibility = i == value ? Visibility.Visible : Visibility.Collapsed;
        navigating = true;
        StepRail.SelectedIndex = value;
        navigating = false;
        StepNumber.Text = (value + 1).ToString(CultureInfo.InvariantCulture);
        StepNumber.Opacity = 0.10;
        UpdateWelcomeLayout();
        ContentScroller.ChangeView(null, 0, null, disableAnimation: true);
        if (value == 4) UpdateReview();
        if (value == 5) UpdateBuildDescription(CreateRecipe());
        UpdateNavigation();
        // A step change can hide the control that had focus; keyboard users should not lose their place.
        DispatcherQueue.TryEnqueue(() => RestoreFocus(focused));
    }

    private void UpdateNavigation()
    {
        if (!initialized) return;
        BackButton.IsEnabled = !busy && step > 0 && !completed;
        BackButton.Visibility = step > 0 && !completed ? Visibility.Visible : Visibility.Collapsed;
        var customCount = inventory.Count > 0 ? inventory.Count(i => i.Selected) : pendingActions.Length;
        var canAdvance = step switch
        {
            1 => image?.IsSupported == true,
            2 => customCount == 0 || ExpertConsent.IsChecked == true,
            _ => true
        };
        NextButton.IsEnabled = !busy && canAdvance;
        // After a build stops, the primary action says what it will do: start over, or continue the resume.
        var retry = step == 5 && !completed && stoppedRun is not null;
        ActionLabel.Text = completed ? L10n.T("ActionBuildAnother", "Build another") : retry ? (stoppedRun == OperationKind.Resume ? L10n.T("ActionResumeAgain", "Resume again") : L10n.T("ActionBuildAgain", "Build again")) : step switch
        {
            0 => L10n.T("ActionStart", "Start"), 1 => L10n.T("ActionNext", "Next"), 2 => L10n.T("ActionNext", "Next"), 3 => L10n.T("ActionReview", "Review"), 4 => L10n.T("ActionContinue", "Continue"), _ => L10n.T("ActionBuild", "Build")
        };
        ActionLabel.FontSize = 32;
        Grid.SetColumn(NextButton, 2);
        NextButton.HorizontalAlignment = HorizontalAlignment.Right;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NextButton, ActionLabel.Text);
        StepRail.IsEnabled = !busy && !completed;
        RecoverButton.IsEnabled = !busy;
        SupportButton.IsEnabled = !busy;
        ResumeButton.IsEnabled = !busy;
        ThemeChoice.IsEnabled = !busy && !HighContrast;
        // Advisory only: the checkpoint file's presence decides whether to offer Resume; the engine verifies it.
        ResumeRunButton.Visibility = !busy && step == 5 && !completed && stoppedRun == OperationKind.Build && stoppedHasCheckpoint ?
            Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = busy && !cancelRequested;
        if (busy)
        {
            // Every busy operation is cancelled from this one button, captioned as the user named it. What it would
            // cancel is in its hint.
            var copy = CancelPrompts.For(running);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(CancelButton, copy.Hint);
            ToolTipService.SetToolTip(CancelButton, copy.Hint);
        }
        CancelHint.Visibility = busy && step == 5 && (running is OperationKind.Build or OperationKind.Resume) ? Visibility.Visible : Visibility.Collapsed;
        ContentScroller.IsEnabled = !busy || (step == 5 && running is OperationKind.Build or OperationKind.Resume);
    }

    private void OnRailChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!initialized || navigating || StepRail.SelectedIndex < 0) return;
        var next = StepRail.SelectedIndex;
        if (busy || next > reached || next > step)
        {
            navigating = true;
            StepRail.SelectedIndex = step;
            navigating = false;
            return;
        }
        GoStep(next);
    }

    private void OnBack(object sender, RoutedEventArgs args) { if (!busy && !modalOpen && step > 0) GoStep(step - 1); }

    private void OnModeCardClick(object sender, RoutedEventArgs args)
    {
        if (busy || modalOpen) return;
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<BuildMode>(tag, out var mode) || !Enum.IsDefined(mode))
        {
            ShowNotice(L10n.T("ModeSelectFail", "Build mode could not be selected."), L10n.T("ModeSelectFailMsg", "The selected card does not identify a valid build mode."), InfoBarSeverity.Error);
            return;
        }
        ModeChoice.SelectedIndex = (int)mode;
        GoStep(1);
    }

    private void OnRemovalModeClick(object sender, RoutedEventArgs args)
    {
        if (!busy && sender is Button { Tag: string tag } && Enum.TryParse<BuildMode>(tag, out var mode))
            ModeChoice.SelectedIndex = (int)mode;
    }

    private async void OnNext(object sender, RoutedEventArgs args)
    {
        if (busy || modalOpen) return;
        if (completed)
        {
            completed = false;
            coreAcknowledged = false;
            UpdateModePresentation();
            OutputBox.Text = Path.Combine(Path.GetDirectoryName(OutputBox.Text)!, $"tiny11-{DateTime.Now:yyyyMMdd-HHmmss}.iso");
            ResetBuildPage();
            GoStep(1);
            return;
        }
        if (step == 5)
        {
            await (stoppedRun == OperationKind.Resume && resumeJobFile is { } job ? ResumeJobAsync(job) : StartBuildAsync());
            return;
        }
        if (step == 2 && UsesCore && !coreAcknowledged)
        {
            coreAcknowledged = await Confirm(L10n.T("CoreConfirmTitle", "Core is not serviceable"),
                L10n.T("CoreConfirmBody", "No Windows Update\nNo Microsoft Defender\nNo recovery environment\nNo added languages or features\n\n" +
                "Use only in an isolated test VM.\nThe image may not boot."),
                L10n.T("CoreConfirmButton", "I agree"));
            CoreConsentStatus.Text = coreAcknowledged ? L10n.T("CoreConsentAccepted", "Core limitations accepted for this build.") : L10n.T("CoreConsentNotAccepted", "Core limitations have not been accepted.");
            if (!coreAcknowledged) return;
        }
        await RunOperation(OperationKind.Check, async token =>
        {
            Working(L10n.T("CheckingChoices", "Checking your choices…"));
            if (step >= 1 && image is null) throw new InvalidOperationException("Inspect the selected Windows image first.");
            image?.Validate();
            if (step >= 2) CreateRecipe().ValidateOptions();
            if (step >= 3)
            {
                await EnsureToolsAsync(token);
                var recipe = CreateRecipe();
                PathSafety.Validate(recipe);
                var selectedImage = image!;
                await Task.Run(() =>
                {
                    new BuildHost().ValidateTools(recipe);
                    new BuildHost().ValidateImage(recipe, selectedImage);
                }, token);
            }
            token.ThrowIfCancellationRequested();
            GoStep(step + 1);
            await Task.CompletedTask;
        });
    }

    private BuildRecipe CreateRecipe() => new()
    {
        SourcePath = SourcePathBox.Text.Trim(), ImageIndex = image?.Index ?? 1,
        WorkspacePath = WorkspaceBox.Text.Trim(), OutputPath = OutputBox.Text.Trim(),
        DismPath = servicingTool, OscdimgPath = isoTool,
        Mode = SelectedMode, Flavor = UsesCore ? BuildFlavor.Core : BuildFlavor.Regular,
        CoreAcknowledged = coreAcknowledged, ExpertAcknowledged = ExpertConsent.IsChecked == true,
        SelectedOptions = options.Where(o => o.Selected).Select(o => o.Option.Id).ToArray(),
        ExpertActions = inventory.Count > 0 ? inventory.Where(i => i.Selected).Select(i => i.Action).ToArray() : pendingActions,
        Compression = (Compression)CompressionChoice.SelectedIndex,
        CompactInstallation = CompactCheck.IsOn, EnableNetFx3 = NetFxCheck.IsOn,
        PreferLocalAccount = LocalAccountCheck.IsOn, KeepWorkingFiles = KeepFilesCheck.IsOn,
        TurboMode = TurboCheck.IsOn, RemoveRedundantData = RedundantDataCheck.IsOn,
        OmitNetFx3SetupPayload = OmitSetupCheck.IsOn
    };

    private async Task RunOperation(OperationKind kind, Func<CancellationToken, Task> action)
    {
        if (busy) return;
        var operation = new OperationRun(kind);
        busy = true;
        active = operation;
        Control? previousFocus = null;
        try
        {
            try
            {
                // Starting is part of the run: if the page cannot even show that it is busy, the run ends as failed
                // and says so, instead of leaving the page half-started.
                previousFocus = CurrentFocus();
                progressTimer.Start();
                Notice.IsOpen = false;
                UpdateNavigation();
                await action(operation.Token);
                // How the run really ended is recorded before anything else can go wrong. If a cancel arrived too late
                // to matter, this is still a success, and the prompt open for it says so.
                operation.Record(OperationEnd.Succeeded(kind));
            }
            catch (Exception error)
            {
                var outcome = Describe(kind, error);
                operation.Record(OperationEnd.From(outcome));
                try
                {
                    ShowStopped(kind, outcome);
                    if (OperationOutcomes.RequiresServicingTools(error))
                        ShowNotice(outcome.Title, outcome.FullMessage,
                            InfoBarSeverity.Error, NoticeAction.OpenTools, outcome.Details);
                    else if (OperationOutcomes.DownloadServiceProblem(error) is { } download)
                    {
                        officialDownloadPage = download.OfficialPage;
                        ShowNotice(outcome.Title, download.Message, InfoBarSeverity.Error, NoticeAction.OpenOfficialDownload, outcome.Details);
                    }
                }
                catch (Exception display)
                {
                    try { ShowNotice(L10n.T("OpNotComplete", "The operation did not complete."), ErrorText.Reason(error) + " " + display.Message, InfoBarSeverity.Error); }
                    catch (Exception) { ReportUiFailure("The outcome could not be shown.", display); }
                }
            }
        }
        finally
        {
            // Nothing here may stop the run from settling, and nothing may escape unreported: each step is caught and
            // shown on the page, and the last one completes the signal that the cancel prompt is waiting on.
            try
            {
                FlushBuildProgress(flushAll: true);
                DrainDownload();
            }
            catch (Exception error) { ReportUiFailure("The last progress could not be shown.", error); }
            finally
            {
                progressTimer.Stop();
                busy = false;
                active = null;
                try
                {
                    StopBuildActivity();
                    ClearWorking();
                    UpdateNavigation();
                    // A cancel prompt that is still open keeps focus until it closes; it restores focus itself.
                    if (!modalOpen) RestoreFocus(previousFocus);
                }
                catch (Exception error) { ReportUiFailure("The page could not be reset after the operation.", error); }
                finally { operation.Settle(); }
            }
        }
    }

    // A step of drawing that failed is reported where the user can see it, never only to the debugger: in the build
    // details, and in a notice. A notice that is already showing (usually the outcome) gets the problem added to it
    // instead of being replaced.
    private void ReportUiFailure(string what, Exception error)
    {
        Debug.WriteLine(error);
        try
        {
            var text = what + " " + ErrorText.Reason(error);
            liveLog.Add("[App] " + text);
            if (Notice.IsOpen) Notice.Message = ErrorText.Summary(Notice.Message + " " + text, 400);
            else ShowNotice(L10n.T("PageUpdateFail", "Part of the page could not be updated."), text, InfoBarSeverity.Warning, NoticeAction.CopyDetails, error.ToString());
        }
        catch (Exception) { /* even the notice cannot be drawn; the debugger output above is all that is left */ }
    }

    // How an operation stopped, as data only: it records what a retry may offer and which job folder holds the logs.
    // Every way an operation can end without success funnels through here, so the page, the notice and the cancel
    // prompt all describe what actually stopped.
    private OperationOutcome Describe(OperationKind kind, Exception error)
    {
        if (error is BuildFailureException failure) workDirectory = failure.WorkDirectory;
        var buildRun = kind is OperationKind.Build or OperationKind.Resume;
        var canResume = false;
        try { canResume = buildRun && workDirectory is not null && JobCheckpoint.Exists(workDirectory); }
        catch (Exception) { /* advisory only: without a readable checkpoint no resume is offered */ }
        var outcome = OperationOutcomes.Classify(kind, error, canResume);
        var logs = outcome.WorkDirectory ?? (kind == OperationKind.Resume ? workDirectory : null);
        if (logs is not null) workDirectory = logs;
        if (buildRun)
        {
            stoppedRun = kind;
            stoppedHasCheckpoint = canResume;
        }
        return outcome;
    }

    // Only a build or resume touches the build page.
    private void ShowStopped(OperationKind kind, OperationOutcome outcome)
    {
        FlushBuildProgress(flushAll: true);
        if (outcome.IsBuildRun)
        {
            // First, so that whatever below fails to draw, the page is not left spinning.
            StopBuildActivity();
            board.Stop(outcome.Cancelled);
            var noun = kind == OperationKind.Resume ? L10n.T("ResumeNoun", "Resume") : L10n.T("BuildNoun", "Build");
            BuildHeading.Text = outcome.Cancelled
                ? string.Format(L10n.T("HeadingCancelled", "{0} cancelled"), noun)
                : string.Format(L10n.T("HeadingStopped", "{0} stopped"), noun);
            CurrentMessage.Text = outcome.FullMessage;
            HistoryCount.Text = StageSummary.Describe(board);
        }
        var logs = outcome.WorkDirectory ?? (kind == OperationKind.Resume ? workDirectory : null);
        ShowNotice(outcome.Title, outcome.FullMessage, outcome.Level == NoticeLevel.Warning ? InfoBarSeverity.Warning : InfoBarSeverity.Error,
            logs is not null ? NoticeAction.OpenLogs : NoticeAction.CopyDetails, outcome.Details);
    }

    private void StopBuildActivity()
    {
        BuildActivity.IsActive = false;
        BuildBar.IsIndeterminate = false;
    }

    private void ShowNotice(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational,
        NoticeAction action = NoticeAction.None, string? details = null)
    {
        Notice.Title = title;
        Notice.Message = ErrorText.Summary(message, 400);
        Notice.Severity = severity;
        noticeAction = action;
        noticeDetails = details ?? "";
        NoticeButton.Visibility = action == NoticeAction.None ? Visibility.Collapsed : Visibility.Visible;
        var label = action switch
        {
            NoticeAction.OpenLogs => L10n.T("NoticeOpenLogs", "Open logs"), NoticeAction.OpenTools => L10n.T("NoticeOpenTools", "Get image tools"),
            NoticeAction.OpenOfficialDownload => L10n.T("NoticeOpenDownload", "Open Microsoft download page"), _ => L10n.T("NoticeCopyDetails", "Copy details")
        };
        NoticeButton.Content = label;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NoticeButton, label);
        Notice.IsOpen = true;
    }

    private void OnNoticeAction(object sender, RoutedEventArgs args)
    {
        if (noticeAction == NoticeAction.OpenLogs) { OnOpenLogs(sender, args); return; }
        if (noticeAction is NoticeAction.OpenTools or NoticeAction.OpenOfficialDownload)
        {
            var page = noticeAction == NoticeAction.OpenTools ? ServicingToolsRequiredException.DownloadPage : officialDownloadPage;
            try
            {
                if (page is not (ServicingToolsRequiredException.DownloadPage or OfficialImageDownloader.DownloadPage or OfficialImageDownloader.ArmDownloadPage))
                    throw new InvalidDataException("The suggested download page is not an approved Microsoft page.");
                using var browser = Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
                if (browser is null) throw new IOException("The browser could not open Microsoft's image-tools page.");
            }
            catch (Exception error) when (error is IOException or InvalidDataException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ShowNotice(L10n.T("NoticeDownloadPageFailTitle", "Could not open the download page."), page + "\n" +
                    ErrorText.Reason(error), InfoBarSeverity.Error, NoticeAction.CopyDetails, error.ToString());
            }
            return;
        }
        if (noticeAction != NoticeAction.CopyDetails || noticeDetails.Length == 0) return;
        NoticeButton.Content = CopyToClipboard(noticeDetails) ? L10n.T("CopiedLabel", "Copied") : L10n.T("CopyFailLabel", "Could not copy");
    }

    // A clipboard that is locked or unavailable is reported to the caller, never thrown into the page or a dialog.
    private static bool CopyToClipboard(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            return true;
        }
        catch (Exception) { return false; }
    }

    private void Working(string text)
    {
        BusyText.Text = text;
        BusyText.Visibility = Visibility.Visible;
        BusyBar.Visibility = Visibility.Visible;
    }

    private void ClearWorking()
    {
        BusyBar.Visibility = Visibility.Collapsed;
        BusyText.Visibility = Visibility.Collapsed;
    }

    private Control? CurrentFocus() =>
        Root.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as Control : null;

    private static bool IsShown(DependencyObject element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement { Visibility: Visibility.Collapsed }) return false;
        return true;
    }

    // Disabling or hiding the control that had focus drops focus entirely. If the user had focus somewhere,
    // put it back there, or on the primary action, unless it is already somewhere usable.
    private void RestoreFocus(Control? previous)
    {
        if (previous is null || Root.XamlRoot is null) return;
        if (FocusManager.GetFocusedElement(Root.XamlRoot) is UIElement current && IsShown(current) &&
            current is not Control { IsEnabled: false }) return;
        foreach (var candidate in new Control?[] { previous, NextButton, BackButton })
            if (candidate is { IsEnabled: true } && IsShown(candidate) && candidate.Focus(FocusState.Programmatic)) return;
    }

    private ElementTheme DialogTheme() => HighContrast ? ElementTheme.Default :
        Root.ActualTheme == ElementTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;

    // One modal prompt or picker at a time. A second request is ignored rather than queued, so a stray
    // keystroke can never confirm something the user has not seen.
    private async Task<bool> Confirm(string title, string content, string action)
    {
        if (modalOpen) return false;
        modalOpen = true;
        var previousFocus = CurrentFocus();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, RequestedTheme = DialogTheme(),
                Title = title, Content = content, PrimaryButtonText = action, CloseButtonText = L10n.T("DialogCancel", "Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            modalOpen = false;
            RestoreFocus(previousFocus);
        }
    }

    private async void OnSupport(object sender, RoutedEventArgs args)
    {
        if (busy || modalOpen || Root.XamlRoot is null) return;
        modalOpen = true;
        var previousFocus = CurrentFocus();
        try
        {
            SupportDialog.XamlRoot = Root.XamlRoot;
            SupportDialog.RequestedTheme = DialogTheme();
            SupportError.IsOpen = false;
            await SupportDialog.ShowAsync();
        }
        catch (Exception error)
        {
            ShowNotice(L10n.T("SupportOptionsFailTitle", "Support options could not be opened."), ErrorText.Reason(error),
                InfoBarSeverity.Error, NoticeAction.CopyDetails, error.ToString());
        }
        finally
        {
            modalOpen = false;
            RestoreFocus(previousFocus);
        }
    }

    private async void OnSupportDestination(object sender, RoutedEventArgs args)
    {
        try
        {
            if (sender is not FrameworkElement { Tag: string tag } ||
                !Enum.TryParse<SupportDestination>(tag, out var destination))
                throw new InvalidDataException(L10n.T("SupportLinkInvalid", "This support link has no valid destination."));
            var uri = ProjectSupport.Link(destination);
            if (!await Windows.System.Launcher.LaunchUriAsync(uri))
                throw new IOException(string.Format(L10n.T("BrowserOpenFailMsg", "Your browser could not open {0}. No payment was made."), uri));
            SupportError.IsOpen = false;
        }
        catch (Exception error)
        {
            SupportError.Title = L10n.T("SupportOpenFailTitle", "Could not open the support page");
            SupportError.Message = ErrorText.Reason(error);
            SupportError.IsOpen = true;
        }
    }

    private async Task<string?> Pick(Func<Task<string?>> pick)
    {
        if (modalOpen) return null;
        modalOpen = true;
        var previousFocus = CurrentFocus();
        try { return await pick(); }
        finally
        {
            modalOpen = false;
            RestoreFocus(previousFocus);
        }
    }

    private Task<string?> PickFile(params string[] extensions)
    {
        var picker = new FileOpenPicker(AppWindow.Id);
        foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
        return Pick(async () => (await picker.PickSingleFileAsync())?.Path);
    }
    private Task<string?> PickFolder() => Pick(async () => (await new FolderPicker(AppWindow.Id).PickSingleFolderAsync())?.Path);
    private Task<string?> SaveFile(string filename, string extension)
    {
        var picker = new FileSavePicker(AppWindow.Id) { SuggestedFileName = filename };
        picker.FileTypeChoices.Add(extension == ".iso" ? L10n.T("FileTypeIso", "ISO image") : L10n.T("FileTypeRecipe", "Image recipe"), new List<string> { extension });
        return Pick(async () => (await picker.PickSaveFileAsync())?.Path);
    }

    private async void OnChooseIso(object sender, RoutedEventArgs args) =>
        await RunOperation(OperationKind.Inspect, async token => { var path = await PickFile(".iso"); if (path is not null) { SourcePathBox.Text = path; await InspectSourceAsync(token); } });
    private async void OnChooseSourceFolder(object sender, RoutedEventArgs args) =>
        await RunOperation(OperationKind.Inspect, async token => { var path = await PickFolder(); if (path is not null) { SourcePathBox.Text = path; await InspectSourceAsync(token); } });
    private async void OnChooseWorkspace(object sender, RoutedEventArgs args) =>
        await RunOperation(OperationKind.Choose, async _ => { var path = await PickFolder(); if (path is not null) WorkspaceBox.Text = path; });
    private async void OnChooseOutput(object sender, RoutedEventArgs args) =>
        await RunOperation(OperationKind.Choose, async _ => { var path = await SaveFile("tiny11", ".iso"); if (path is not null) OutputBox.Text = path; });

    private void OnSourceChanged(object sender, TextChangedEventArgs args)
    {
        if (!initialized) return;
        image = null;
        pendingImageIndex = null;
        pendingSourcePath = "";
        EditionChoice.ItemsSource = null;
        ImageBadge.Text = L10n.T("ImageBadgeNone", "No image selected");
        ClearInventory();
        UpdateNavigation();
    }
    private void OnEditionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!initialized) return;
        var selected = EditionChoice.SelectedItem as WindowsImage;
        if (selected is not null && image?.Index != selected.Index &&
            !(pendingImageIndex == selected.Index &&
              SourcePathBox.Text.Trim().Equals(pendingSourcePath, StringComparison.OrdinalIgnoreCase)))
            ClearInventory();
        image = selected;
        ImageBadge.Text = image is null ? L10n.T("ImageBadgeNone", "No image selected") : $"{image.Name}\n{image.Architecture} · build {image.Version.Build}";
        if (image is not null && !image.IsSupported)
            ShowNotice(L10n.T("EditionNotSupported", "This edition is not supported."), L10n.T("EditionNotSupportedMsg", "Choose a Windows 11 client image, 22H2 or newer, in x64 or ARM64 format."), InfoBarSeverity.Warning);
        UpdateNavigation();
    }

    private async void OnInspect(object sender, RoutedEventArgs args)
    {
        if (!ToolLocator.IsAdministrator)
        {
            ShowNotice(L10n.T("AdminRequired", "Administrator privileges required."), L10n.T("AdminRequiredMsg", "Close and reopen Tiny11Builder.exe, then approve the Windows administrator prompt."), InfoBarSeverity.Warning);
            return;
        }
        await RunOperation(OperationKind.Inspect, InspectSourceAsync);
    }

    private async Task InspectSourceAsync(CancellationToken token)
    {
        if (!ToolLocator.IsAdministrator)
        {
            ShowNotice(L10n.T("ImageSelectedNotice", "Image selected."), L10n.T("ImageSelectedMsg", "Close and reopen Tiny11Builder.exe with administrator approval to inspect its editions."));
            return;
        }
        Working(L10n.T("InspectingImage", "Inspecting the image…"));
        var source = SourcePathBox.Text.Trim();
        servicingTool = ToolLocator.FindDism();
        await Task.Run(() => ToolLocator.Validate(servicingTool, "dism.exe"), token);
        var images = await Task.Run(() => engine.InspectAsync(source, servicingTool, token), token);
        var restored = source.Equals(pendingSourcePath, StringComparison.OrdinalIgnoreCase) ? pendingImageIndex : null;
        EditionChoice.ItemsSource = images;
        EditionChoice.SelectedIndex = ImageDefaults.SelectPosition(images, restored);
        ShowNotice(L10n.T("ImageInspected", "Image inspected."), string.Format(L10n.T("ImageInspectedFmt", "{0} editions found. {1} is selected."), images.Count, image?.Name));
    }

    private void BeginDownloadText(Func<DownloadProgress, string> format)
    {
        downloadUpdates.TryTake(out _);
        downloadText = format;
    }

    private void EndDownloadText()
    {
        downloadText = null;
        DownloadBar.IsIndeterminate = false;
    }

    // Download progress arrives from a worker thread far faster than the page should repaint. Only the
    // newest value is kept and the 150 ms timer paints it; the final value is painted explicitly when
    // the download returns, so it is never dropped.
    private void DrainDownload()
    {
        if (downloadText is not { } format || !downloadUpdates.TryTake(out var p)) return;
        DownloadBar.IsIndeterminate = p.Percent is null;
        if (p.Percent is double value) DownloadBar.Value = value;
        DownloadStatus.Text = format(p);
    }

    private static string FormatDownload(DownloadProgress p, string prefix) =>
        $"{prefix}{p.Bytes / (1024d * 1024):N0} MiB" +
        (p.Total is long total ? $" / {total / (1024d * 1024):N0} MiB" : "") +
        $" · {p.BytesPerSecond / (1024 * 1024):N1} MiB/s";

    private async void OnDownload(object sender, RoutedEventArgs args) => await RunOperation(OperationKind.Download, async token =>
    {
        var uri = OfficialImageDownloader.ValidateUrl(DownloadUrlBox.Text.Trim());
        var hash = OfficialImageDownloader.ValidateHash(PublishedHashBox.Text);
        var path = await SaveFile(Path.GetFileName(uri.AbsolutePath), ".iso");
        if (path is null) return;
        Working(L10n.T("DownloadingMicrosoft", "Downloading from Microsoft…"));
        using var client = OfficialImageDownloader.CreateClient();
        DownloadBar.IsIndeterminate = true;
        BeginDownloadText(p => FormatDownload(p, ""));
        try
        {
            var downloaded = await new OfficialImageDownloader(client).DownloadAsync(uri.AbsoluteUri, path, hash, downloadUpdates, token,
                status: new UiProgress<string>(text => DispatcherQueue.TryEnqueue(() => DownloadStatus.Text = text)));
            DrainDownload();
            SourcePathBox.Text = downloaded.Path;
            DownloadStatus.Text = $"SHA-256: {downloaded.Sha256}";
            ShowNotice(downloaded.VerifiedAgainstPublishedHash ? L10n.T("DownloadVerified", "Official ISO downloaded and hash-verified.") : L10n.T("DownloadMicrosoft", "Microsoft-hosted ISO downloaded."),
                downloaded.VerifiedAgainstPublishedHash ? L10n.T("DownloadVerifiedMsg", "The file matches the supplied published SHA-256. Inspect it to select an edition.") :
                L10n.T("DownloadUnverifiedMsg", "No published hash was supplied. The computed hash is shown, but authenticity has not been independently verified."), InfoBarSeverity.Informational);
        }
        finally { EndDownloadText(); }
    });

    private async void OnDownloadLatest(object sender, RoutedEventArgs args) => await RunOperation(OperationKind.Download, async token =>
    {
        Working(L10n.T("FindingLatest", "Finding the latest Windows 11…"));
        DownloadBar.IsIndeterminate = true;
        try
        {
            var catalogProgress = new UiProgress<string>(text => DispatcherQueue.TryEnqueue(() => DownloadStatus.Text = text));
            using var catalogClient = LatestWindowsMedia.CreateClient();
            var media = await new LatestWindowsMedia(catalogClient).ResolveAsync(HostArchitecture,
                CultureInfo.CurrentUICulture, catalogProgress, token);
            LatestDownloadDescription.Text = media.Description;
            var folder = Path.Combine(PathSafety.Full(WorkspaceBox.Text.Trim()), "downloads");
            PathSafety.RejectReparsePoints(folder);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, media.FileName);
            var hashFile = path + ".sha256";
            if (File.Exists(path) && File.Exists(hashFile))
            {
                Working(L10n.T("CheckingDownloaded", "Checking the downloaded ISO…"));
                await using var input = File.OpenRead(path);
                await IsoStructure.ValidateAsync(input, token);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
                if (actual != (await File.ReadAllTextAsync(hashFile, token)).Trim() ||
                    (media.Sha256 is not null && actual != media.Sha256))
                    throw new InvalidDataException(L10n.T("CachedIsoChanged", "The cached ISO has changed. It has not been accepted."));
                DownloadStatus.Text = L10n.T("PreviouslyVerified", "Previously downloaded ISO verified.");
            }
            else
            {
                if (File.Exists(path)) path = Path.Combine(folder, Path.GetFileNameWithoutExtension(media.FileName) + "-" + Guid.NewGuid().ToString("N")[..8] + ".iso");
                Working(L10n.T("DownloadingMicrosoft", "Downloading from Microsoft…"));
                using var downloadClient = OfficialImageDownloader.CreateClient();
                BeginDownloadText(p => FormatDownload(p, $"{media.Language} · "));
                var download = await new OfficialImageDownloader(downloadClient).DownloadAsync(media.Url.AbsoluteUri,
                    path, media.Sha256, downloadUpdates, token, status: catalogProgress);
                DrainDownload();
                await File.WriteAllTextAsync(path + ".sha256", download.Sha256, token);
                DownloadStatus.Text = string.Format(L10n.T("DownloadedFmt", "Downloaded {0}. SHA-256: {1}"), media.Description, download.Sha256) +
                    (download.VerifiedAgainstPublishedHash ? L10n.T("HashPublished", " (published hash verified)") : L10n.T("HashComputed", " (computed hash; Microsoft supplied no published hash)"));
            }
            SourcePathBox.Text = path;
            await InspectSourceAsync(token);
        }
        finally { EndDownloadText(); }
    });

    private async Task EnsureToolsAsync(CancellationToken token)
    {
        servicingTool = ToolLocator.FindDism();
        using var client = OfficialImageDownloader.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        isoTool = await new OscdimgAcquirer(client).EnsureAsync(Path.Combine(dataDirectory, "tools"),
            new UiProgress<string>(text => DispatcherQueue.TryEnqueue(() => ToolStatus.Text = text)), token);
        ToolStatus.Text = L10n.T("ToolsReady", "Image and ISO tools ready.");
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!initialized || applyingMode) return;
        ApplyMode();
    }
    private void ApplyMode()
    {
        applyingMode = true;
        var selected = CleanupCatalog.ForMode(SelectedMode);
        foreach (var choice in options) choice.Selected = selected.Contains(choice.Option.Id);
        foreach (var item in inventory) item.Selected = false;
        pendingActions = [];
        CustomCoreCheck.IsOn = false;
        coreAcknowledged = false;
        ExpertConsent.IsChecked = false;
        applyingMode = false;
        UpdateModePresentation();
        UpdateCounts();
    }
    private void UpdateModePresentation()
    {
        CustomChoices.Visibility = SelectedMode == BuildMode.Custom ? Visibility.Visible : Visibility.Collapsed;
        CoreWarning.IsOpen = UsesCore;
        CoreConsentStatus.Visibility = UsesCore ? Visibility.Visible : Visibility.Collapsed;
        CoreConsentStatus.Text = coreAcknowledged ? L10n.T("CoreConsentAccepted", "Core limitations accepted for this build.") : L10n.T("CoreConsentPending", "You will be asked to accept Core's limitations before continuing.");
        ModeDescription.Text = SelectedMode switch
        {
            BuildMode.Tiny11 => L10n.T("ModeDescTiny", "Original Tiny11 removals, including Edge/WebView2 and OneDrive binaries. Windows Update, Defender and recovery are kept. Choose Custom to change individual removals."),
            BuildMode.Tiny11Core => L10n.T("ModeDescCore", "Tiny11 Core removals, including irreversible component pruning. Choose Custom to change individual removals."),
            _ => L10n.T("ModeDescCustom", "Start with nothing selected, then choose what to remove or disable. You can also list individual components, including protected items.")
        };
        SourceMode.Text = string.Format(L10n.T("ModeLine", "Build mode: {0}"), (ModeChoice.SelectedItem as ComboBoxItem)?.Content);
        MarkChoice(Tiny11Selection, SelectedMode == BuildMode.Tiny11);
        MarkChoice(CoreSelection, SelectedMode == BuildMode.Tiny11Core);
        MarkChoice(CustomSelection, SelectedMode == BuildMode.Custom);
    }

    // The chosen card gets an accent outline and is announced as selected; unchosen cards keep full-strength text.
    private static void MarkChoice(Button card, bool selected)
    {
        card.Style = (Style)Application.Current.Resources[selected ? "ModeCardSelected" : "ModeCard"];
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(card, selected ? "Selected" : "Not selected");
    }
    private void OnChoicesChanged(object sender, RoutedEventArgs args)
    {
        if (!initialized || applyingMode) return;
        UpdateCounts();
    }
    private void OnCustomCoreChanged(object sender, RoutedEventArgs args)
    {
        if (!initialized || applyingMode) return;
        coreAcknowledged = false;
        UpdateModePresentation();
        UpdateCounts();
    }
    private void OnOptionFilterChanged(object sender, SelectionChangedEventArgs args) { if (initialized) FilterOptions(); }
    private void FilterOptions()
    {
        var category = (OptionFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
        OptionsList.ItemsSource = options.Where(o => category == "All" || o.Category == category).ToArray();
    }
    private void UpdateCounts()
    {
        if (!initialized) return;
        var custom = inventory.Count > 0 ? inventory.Count(i => i.Selected) : pendingActions.Length;
        SelectionCount.Text = string.Format(L10n.T("SelCountFmt", "{0} removal groups selected · {1} advanced actions"), options.Count(o => o.Selected), custom);
        ExpertCount.Text = string.Format(L10n.T("ExpCountFmt", "{0} components listed · {1} advanced actions selected."),
            inventory.Count.ToString("N0", CultureInfo.InvariantCulture), custom.ToString("N0", CultureInfo.InvariantCulture));
        UpdateNavigation();
    }

    private async void OnOpenExpert(object sender, RoutedEventArgs args)
    {
        if (busy || modalOpen || SelectedMode != BuildMode.Custom) return;
        if (image is null) { ShowNotice(L10n.T("InspectFirstTitle", "Inspect an image first."), L10n.T("InspectFirstMsg", "Select an edition before listing its components."), InfoBarSeverity.Warning); return; }
        if (!await Confirm(L10n.T("ProtectedConfirmTitle", "Protected components are included"),
            L10n.T("ProtectedConfirmBody", "This inventory includes boot drivers, security, licensing, system apps and component payloads. Removing them can make Windows unbootable, insecure or impossible to update. Protected packages may refuse removal. Direct payload removal is unsupported.\n\nContinue only for isolated testing, with backups and a known-good installer."),
            L10n.T("ProtectedConfirmAction", "Inspect components"))) return;
        await RunOperation(OperationKind.Inventory, async token =>
        {
            Working(L10n.T("ListingComponents", "Listing image components…"));
            var recipe = CreateRecipe() with { CoreAcknowledged = true, ExpertAcknowledged = true };
            var items = await Task.Run(() => engine.InventoryAsync(recipe, BuildProgressReporter(), token), token);
            var previous = inventory.Count > 0 ? inventory.Where(i => i.Selected).Select(i => i.Action).ToArray() : pendingActions;
            inventory = items.Select(item =>
            {
                var choice = new InventoryChoice(item);
                var prior = previous.FirstOrDefault(a => a.Kind == item.Kind && a.Identity == item.Identity);
                if (prior is not null) { choice.Selected = true; choice.ModeIndex = prior.Mode == ExpertActionMode.Disable ? 0 : 1; }
                choice.PropertyChanged += (_, _) => UpdateCounts();
                return choice;
            }).ToList();
            pendingActions = [];
            ExpertPanel.Visibility = Visibility.Visible;
            ExpertConsent.IsChecked = true;
            FilterInventory();
            UpdateCounts();
            if (!ToolLocator.SupportsServicing(recipe.DismPath, image!))
                ShowNotice(L10n.T("ComponentListReadOnly", "Component list loaded read-only."),
                    L10n.T("ComponentListReadOnlyMsg", "The installed tools could list this image, but building it requires newer Windows ADK Deployment Tools. Install them and reopen the app before building."),
                    InfoBarSeverity.Warning, NoticeAction.OpenTools);
            else
                ShowNotice(L10n.T("ComponentListLoaded", "Component list loaded."), L10n.T("ComponentListLoadedMsg", "Existing selections were preserved. Check the individual items you want to remove or disable."));
        });
    }

    private void ClearInventory()
    {
        inventory = [];
        pendingActions = [];
        InventoryList.ItemsSource = null;
        ExpertPanel.Visibility = Visibility.Collapsed;
        ExpertConsent.IsChecked = false;
        UpdateCounts();
    }
    private void OnInventoryFilterChanged(object sender, TextChangedEventArgs args) { if (initialized) FilterInventory(); }
    private void OnInventoryFilterSelection(object sender, SelectionChangedEventArgs args) { if (initialized) FilterInventory(); }
    private void FilterInventory()
    {
        var filter = (InventoryFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
        var query = InventorySearch.Text.Trim();
        InventoryList.ItemsSource = inventory.Where(i => (filter == "All" || i.Item.Kind.ToString() == filter) &&
            (query.Length == 0 || i.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             i.Detail.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
    }
    private void OnClearExpert(object sender, RoutedEventArgs args)
    {
        foreach (var item in inventory) item.Selected = false;
        pendingActions = [];
        UpdateCounts();
    }

    private string CreateReview()
    {
        var recipe = CreateRecipe();
        var text = new StringBuilder();
        text.AppendLine($"IMAGE       {image?.Summary ?? L10n.T("ReviewNotInspected", "Not inspected")}");
        text.AppendLine(string.Format(L10n.T("ReviewModeLabelFmt", "MODE        {0}"), (ModeChoice.SelectedItem as ComboBoxItem)?.Content));
        if (SelectedMode == BuildMode.Custom) text.AppendLine(string.Format(L10n.T("ReviewCoreRemovalsFmt", "CORE REMOVALS {0}"), UsesCore ? L10n.T("TurboOn", "On") : L10n.T("TurboOff", "Off")));
        text.AppendLine($"SOURCE      {recipe.SourcePath}");
        text.AppendLine($"OUTPUT      {recipe.OutputPath}");
        text.AppendLine($"WORKSPACE   {recipe.WorkspacePath}");
        text.AppendLine($"IMAGE TOOL  {recipe.DismPath}");
        text.AppendLine($"ISO TOOL    {recipe.OscdimgPath}");
        text.AppendLine($"COMPRESSION {recipe.Compression}");
        text.AppendLine($"TURBO       {recipe.TurboMode}");
        text.AppendLine($"CACHE CLEAN {recipe.RemoveRedundantData}");
        text.AppendLine($"OMIT SXS    {recipe.OmitNetFx3SetupPayload}");
        text.AppendLine($"COMPACT     {recipe.CompactInstallation}");
        text.AppendLine($".NET 3.5    {recipe.EnableNetFx3}");
        text.AppendLine($"LOCAL OOBE  {recipe.PreferLocalAccount} (best effort)");
        if (SetupLabels.LegacyReason(recipe) is { } legacyReason) text.AppendLine($"LEGACY SETUP automatic, needed for {legacyReason}");
        text.AppendLine("\n" + L10n.T("ReviewRemoveHeader", "REMOVE / DISABLE"));
        foreach (var option in options.Where(o => o.Selected)) text.AppendLine($"  {option.Title}");
        foreach (var action in recipe.ExpertActions) text.AppendLine(string.Format(L10n.T("ReviewExpertItemFmt", "  EXPERT {0} {1}: {2}"), action.Mode, action.Kind, action.Identity));
        text.AppendLine("\n" + L10n.T("ReviewUnselectedKept", "UNSELECTED ITEMS ARE KEPT."));
        if (recipe.Flavor == BuildFlavor.Core)
            text.AppendLine("\n" + L10n.T("ReviewCoreNote", "CORE: irreversible component pruning, WinRE removed, Update and Defender disabled. Install optional features now, not after creation."));
        if (recipe.ExpertActions.Length > 0)
            text.AppendLine("\n" + L10n.T("ReviewExpertNote", "EXPERT: dependencies are not resolved. Protected and boot-critical removals may produce an unbootable image."));
        text.AppendLine("\n" + L10n.T("ReviewPoliciesNote", "Policies may be ignored by a particular Windows build/edition or changed by updates. VM boot validation is required."));
        return text.ToString();
    }

    private void UpdateReview()
    {
        var recipe = CreateRecipe();
        ReviewText.Text = CreateReview();
        ReviewImage.Text = image?.Summary ?? L10n.T("ImageBadgeNone", "No image selected");
        ReviewMode.Text = (ModeChoice.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        ReviewRemovals.Text = string.Format(L10n.T("ReviewRemovalsFmt", "{0} removal groups · {1} individual actions"),
            recipe.SelectedOptions.Length, recipe.ExpertActions.Length) + "\n" +
            (UsesCore ? L10n.T("ReviewNoService", "No servicing, recovery or Defender.") : SelectedMode == BuildMode.Custom ?
                L10n.T("ReviewCustomWarn", "Custom removals may affect servicing, recovery or security.") : L10n.T("ReviewRetained", "Windows Update, recovery and Defender retained."));
        ReviewPaths.Text = L10n.T("PathIso", "ISO") + "\n" + recipe.OutputPath + "\n\n" + L10n.T("PathWorkspace", "Workspace") + "\n" + recipe.WorkspacePath;
        var compressionWord = recipe.Compression switch
        {
            Compression.Recovery => L10n.T("CompRecovery", "Recovery ESD"),
            Compression.Maximum => L10n.T("CompMaximum", "Maximum WIM"),
            _ => L10n.T("CompFastWim", "Fast WIM")
        };
        ReviewOptions.Text = string.Format(L10n.T("ReviewOptionsCompFmt", "{0} compression"), compressionWord) + "\n" +
            string.Format(L10n.T("ReviewTurboLine", "Turbo mode: {0}"), recipe.TurboMode ? L10n.T("TurboOn", "On") : L10n.T("TurboOff", "Off")) + "\n" +
            string.Format(L10n.T("ReviewRedundantLine", "Redundant data: {0}"), recipe.RemoveRedundantData ? L10n.T("RedundantRemove", "Remove") : L10n.T("RedundantKeep", "Keep")) + "\n" +
            string.Format(L10n.T("ReviewNetSetupLine", ".NET setup payload: {0}"), recipe.OmitNetFx3SetupPayload ? L10n.T("NetSetupOmit", "Omit") : L10n.T("NetSetupKeep", "Keep")) + "\n" +
            string.Format(L10n.T("ReviewCompactLine", "Compact installation: {0}"), recipe.CompactInstallation ? L10n.T("TurboOn", "On") : L10n.T("TurboOff", "Off")) + "\n" +
            string.Format(L10n.T("ReviewNetFxLine", ".NET 3.5: {0}"), recipe.EnableNetFx3 ? L10n.T("NetFxIncluded", "Included") : L10n.T("NetFxNotIncluded", "Not included")) + "\n" +
            string.Format(L10n.T("ReviewLocalLine", "Local-account setup: {0}"), recipe.PreferLocalAccount ? L10n.T("LocalPreferred", "Preferred") : L10n.T("LocalDefault", "Default")) + "\n" +
            string.Format(L10n.T("ReviewFilesLine", "Working files: {0}"), recipe.KeepWorkingFiles ? L10n.T("FilesKeep", "Keep") : L10n.T("FilesClean", "Clean after build")) +
            (SetupLabels.Legacy(recipe) is { } legacy ? "\n" + legacy : "");
    }

    private static string BuildDescriptionText => L10n.T("BuildDescText", "The selected edition will be copied, modified, compressed and saved as a bootable ISO.");

    // The legacy installer is derived from the removals, never chosen, so this only reports it.
    private void UpdateBuildDescription(BuildRecipe recipe) => BuildDescription.Text = BuildDescriptionText +
        (SetupLabels.LegacyReason(recipe) is { } reason ? string.Format(L10n.T("BuildDescLegacyFmt", " The legacy installer is turned on automatically for {0}."), reason) : "");

    private async Task StartBuildAsync()
    {
        BuildRecipe recipe;
        try { recipe = CreateRecipe(); recipe.ValidateOptions(); PathSafety.Validate(recipe); }
        catch (Exception error) { ShowNotice(L10n.T("RecipeNotReadyTitle", "The recipe is not ready."), ErrorText.Reason(error), InfoBarSeverity.Error); return; }
        if (!await Confirm(L10n.T("ConfirmBuildTitle", "Create this offline Windows image?"),
            string.Format(L10n.T("ConfirmBuildBodyFmt", "Output: {0}\n\nMode: {1}\nRemoval groups: {2}\nIndividual actions: {3}"),
                recipe.OutputPath, (ModeChoice.SelectedItem as ComboBoxItem)?.Content, recipe.SelectedOptions.Length, recipe.ExpertActions.Length) +
            (SetupLabels.Legacy(recipe) is { } legacy ? "\n" + legacy : ""),
            L10n.T("ActionBuild", "Build"))) return;
        await RunOperation(OperationKind.Build, async token =>
        {
            ResetBuildPage();
            BuildActivity.IsActive = true;
            BuildBar.IsIndeterminate = true;
            CurrentMessage.Text = L10n.T("PreparingTools", "Preparing image tools…");
            await EnsureToolsAsync(token);
            recipe = CreateRecipe();
            BeginBuildRun(L10n.T("BuildingHeading", "Building"));
            var reporter = BuildProgressReporter();
            var built = await Task.Run(() => engine.BuildAsync(recipe, reporter, token), token);
            ShowBuildResult(built, resumed: false, turbo: recipe.TurboMode);
        });
    }

    // The build page starts from nothing, so nothing from an earlier run (its job folder, result,
    // warnings or log) can be offered for this one.
    private void ResetBuildPage()
    {
        board.Reset();
        activePhase = -1;
        while (progressUpdates.TryDequeue(out _)) { }
        result = null;
        workDirectory = null;
        stoppedRun = null;
        stoppedHasCheckpoint = false;
        ResultPanel.Visibility = Visibility.Collapsed;
        ResultWarnings.IsOpen = false;
        liveLog.Clear();
        RefreshLog(force: true);
        BuildHeading.Text = L10n.T("BuildNoun", "Build");
        CurrentStage.Text = L10n.T("WaitingToStart", "Waiting to start");
        CurrentMessage.Text = L10n.T("NoChanges", "No changes have been made.");
        BuildGlyph.Glyph = "\uE7C5";
        BuildActivity.IsActive = false;
        BuildBar.IsIndeterminate = false;
        BuildBar.Value = 0;
        OverallBar.Value = 0;
        BuildPercent.Text = "0%";
        OverallStep.Text = "";
        CurrentPercent.Text = "";
        HistoryCount.Text = StageSummary.Describe(board);
    }

    private void BeginBuildRun(string heading)
    {
        BuildHeading.Text = heading;
        BuildActivity.IsActive = true;
        BuildBar.IsIndeterminate = true;
    }

    private void ShowBuildResult(BuildResult built, bool resumed, bool turbo)
    {
        FlushBuildProgress(flushAll: true);
        result = built;
        workDirectory = built.WorkDirectory;
        ResultPath.Text = built.IsoPath;
        ResultHash.Text = built.Sha256;
        var size = File.Exists(built.IsoPath) ? $" · {new FileInfo(built.IsoPath).Length / (1024d * 1024 * 1024):N2} GiB ISO" : "";
        var typeWord = resumed ? L10n.T("MetricResumedBuild", "Resumed build")
            : turbo ? L10n.T("MetricTurboBuild", "Turbo build") : L10n.T("MetricStandardBuild", "Standard build");
        var minutes = string.Format(L10n.T("MetricMinutes", "{0} minutes"), built.Duration.TotalMinutes.ToString("N1", CultureInfo.InvariantCulture));
        var total = resumed ? L10n.T("MetricInTotal", " in total") : "";
        var metrics = new List<string>
        {
            $"{typeWord} · {minutes}{total}{size}"
        };
        if (!resumed || built.RemovedLogicalBytes > 0 || built.OmittedSetupBytes > 0)
            metrics.Add(string.Format(L10n.T("MetricRedundantFmt", "Redundant data removed: {0} logical · Setup payload omitted: {1}"),
                (built.RemovedLogicalBytes / (1024d * 1024)).ToString("N1", CultureInfo.InvariantCulture),
                (built.OmittedSetupBytes / (1024d * 1024)).ToString("N1", CultureInfo.InvariantCulture)));
        metrics.Add(L10n.T("MetricTimingsSaved", "Detailed stage timings are saved with the build report."));
        ResultMetrics.Text = string.Join("\n", metrics);
        var warnings = built.Warnings;
        // A cancel prompt open for this run reports the warning too, instead of calling the end clean.
        if (warnings.Count > 0)
            active?.NoteWarning(L10n.T("WarnNoteDetail", "The ISO was saved with warnings. See the warnings on the page."));
        ResultWarnings.IsOpen = warnings.Count > 0;
        if (warnings.Count > 0)
            ResultWarnings.Message = string.Join("\n", warnings.Take(4).Select(w => "• " + ErrorText.Summary(w, 200))) +
                (warnings.Count > 4 ? string.Format(L10n.T("WarnMoreFmt", "\n• {0} more in the build log"), warnings.Count - 4) : "");
        ResultPanel.Visibility = Visibility.Visible;
        BuildHeading.Text = L10n.T("IsoCreated", "ISO created");
        CurrentStage.Text = L10n.T("Complete", "Complete");
        BuildActivity.IsActive = false;
        BuildGlyph.Glyph = "\uE73E";
        if (resumed) board.MarkEarlierPhasesDone();
        board.Complete();
        OverallBar.Value = 100;
        BuildPercent.Text = "100%";
        OverallStep.Text = L10n.T("AllStagesDone", "All stages done");
        CurrentPercent.Text = "100%";
        HistoryCount.Text = StageSummary.Describe(board);
        BuildBar.IsIndeterminate = false;
        BuildBar.Value = 100;
        CurrentMessage.Text = warnings.Count > 0 ?
            L10n.T("ResultWarnMsg", "The ISO was saved with warnings. See the skipped work and other details below.") :
            L10n.T("ResultOkMsg", "The ISO and build report have been saved. Test the ISO in a VM before use.");
        stoppedRun = null;
        stoppedHasCheckpoint = false;
        completed = true;
    }

    private IProgress<BuildProgress> BuildProgressReporter() => new UiProgress<BuildProgress>(progressUpdates.Enqueue);

    private void FlushBuildProgress(bool flushAll = false)
    {
        var clock = Stopwatch.StartNew();
        var count = 0;
        var applied = false;
        while ((flushAll || count++ < 256 && clock.ElapsedMilliseconds < 20) && progressUpdates.TryDequeue(out var p))
        {
            if (completed) continue;
            cancelFlow?.Observe(p);
            if (p.Stage == CancelCleanup.Stage)
            {
                // Winding a cancelled run down is shown by the cancel dialog and the build details. It is not a stage of
                // the build, so it must not move the page's stage, bars or history.
                if (!string.IsNullOrWhiteSpace(p.Message) && !BuildProgressText.IsConsoleProgress(p.Message))
                {
                    if (running is OperationKind.Build or OperationKind.Resume) CurrentMessage.Text = string.Format(L10n.T("CancelRequestedFmt", "Cancellation requested. {0}…"), p.Message);
                    liveLog.Add($"[{p.Stage}] {BuildProgressText.ForDisplay(p.Message)}");
                }
                continue;
            }
            var phase = board.Apply(p);
            applied |= phase >= 0;
            if (p.State is BuildOperationState.Pending or BuildOperationState.Skipped)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(p.Message) && !BuildProgressText.IsConsoleProgress(p.Message))
            {
                CurrentStage.Text = p.Stage;
                // Once cancellation has been requested, that status stays until the run actually stops.
                if (!cancelRequested)
                    CurrentMessage.Text = p.State == BuildOperationState.Failed ?
                        string.Format(L10n.T("FailedDetailFmt", "{0} failed. Expand this stage for error details."), p.Message) : p.Message;
            }
            BuildBar.IsIndeterminate = p.Percent is null && p.State is not (BuildOperationState.Completed or BuildOperationState.Failed or BuildOperationState.Cancelled);
            if (p.Percent is double percent) BuildBar.Value = percent;
            else if (p.State == BuildOperationState.Completed) BuildBar.Value = 100;
            CurrentPercent.Text = BuildBar.IsIndeterminate ? "" : $"{BuildBar.Value:0}%";
            if (step == 5 && phase >= 0)
            {
                var stage = board.Stages[phase];
                if (activePhase != phase && p.State is not BuildOperationState.Completed)
                {
                    activePhase = phase;
                    stage.Expanded = true;
                }
                var (start, end) = BuildProgressText.Range(p.Stage);
                var overall = start + (end - start) * stage.Fraction(p);
                OverallBar.Value = Math.Max(OverallBar.Value, overall);
                BuildPercent.Text = $"{OverallBar.Value:0}%";
                OverallStep.Text = string.Format(L10n.T("StageOfFmt", "Stage {0} of {1}"), phase + 1, board.Stages.Length);
                BuildGlyph.Glyph = stage.Glyph;
            }
            var line = p.Detail ?? (p.State is null ? null : p.Message);
            if (string.IsNullOrWhiteSpace(line) || BuildProgressText.IsConsoleProgress(line)) continue;
            liveLog.Add($"[{p.Stage}] {BuildProgressText.ForDisplay(line)}");
        }
        if (running == OperationKind.Resume) board.MarkEarlierPhasesDone();
        if (applied || running == OperationKind.Resume) HistoryCount.Text = StageSummary.Describe(board);
        RefreshLog();
    }

    // The log box is rewritten only when that cannot disturb the reader: not while it is collapsed and
    // not while text in it is selected. Scroll position is kept, or follows the newest line when the
    // reader was already at the end.
    private void RefreshLog(bool force = false)
    {
        if (!liveLog.TryRender(BuildDetails.IsExpanded, BuildLog.SelectionLength > 0, force, out var text)) return;
        var viewer = LogScroller();
        var follow = viewer is null || viewer.VerticalOffset >= viewer.ScrollableHeight - 8;
        var offset = viewer?.VerticalOffset ?? 0;
        BuildLog.Text = text;
        if (viewer is null) return;
        BuildLog.UpdateLayout();
        viewer.ChangeView(null, follow ? viewer.ScrollableHeight : Math.Min(offset, viewer.ScrollableHeight), null, disableAnimation: true);
    }

    private ScrollViewer? LogScroller() => logScroller ??= FindScroller(BuildLog);

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScroller(child) is { } nested) return nested;
        }
        return null;
    }

    private void OnDetailsExpanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        RefreshLog(force: true);
        // The box has no layout until it is shown; then start at the newest line, as a live log does.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (LogScroller() is { } viewer) viewer.ChangeView(null, viewer.ScrollableHeight, null, disableAnimation: true);
        });
    }

    private void OnLogSelectionChanged(object sender, RoutedEventArgs args)
    {
        if (BuildLog.SelectionLength == 0) RefreshLog();
    }

    // Cancel asks first. Nothing is sent until the confirm button is pressed, and the same dialog then carries the stop:
    // a wait that ends only when RunOperation has settled, cleanup included, followed by what really happened.
    private async void OnCancel(object sender, RoutedEventArgs args)
    {
        if (active is not { } run || modalOpen || Root.XamlRoot is null) return;
        if (CancellationFlow.TryBegin(run) is not { } flow) return;
        modalOpen = true;
        cancelFlow = flow;
        var previousFocus = CurrentFocus();
        try { await ShowCancelPrompt(flow); }
        catch (Exception error)
        {
            // The prompt could not be shown or kept. If nothing was sent, the operation simply carries on.
            ReportUiFailure(flow.Stage == CancelStage.Confirming ? "Cancel could not be confirmed." :
                "The cancel dialog stopped, but the cancel was sent and the operation is still stopping.", error);
        }
        finally
        {
            cancelFlow = null;
            flow.Close();
            modalOpen = false;
            RestoreFocus(previousFocus);
        }
    }

    private async Task ShowCancelPrompt(CancellationFlow flow)
    {
        var prompt = new CancelPrompt(flow, Root.XamlRoot, DialogTheme(), AfterCancelRequested, TryOpenFolder, CopyToClipboard, ReportUiFailure);
        _ = WatchSettled(flow);
        try
        {
            // The dialog is meant to stay until the stop has finished. Should it close anyway, it is shown again
            // rather than leaving a stop in progress with nothing on screen.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await prompt.ShowAsync();
                if (flow.Stage != CancelStage.Cancelling) return;
            }
            await flow.Run.Settled;
        }
        finally { prompt.Detach(); }
    }

    // The run settling is what moves the prompt on, whether it is still asking or already stopping.
    private async Task WatchSettled(CancellationFlow flow)
    {
        try
        {
            await flow.Run.Settled;
            flow.RunSettled();
        }
        catch (Exception error) { ReportUiFailure("The cancel dialog could not show how the operation ended.", error); }
    }

    // The cancel has been sent: say so on the page as well, so it stays true after the prompt closes.
    private void AfterCancelRequested()
    {
        if (running is OperationKind.Build or OperationKind.Resume)
            CurrentMessage.Text = L10n.T("CancelWaiting", "Cancellation requested. Waiting for the current operation to finish…");
        Working(L10n.T("Cancelling", "Cancelling…"));
        UpdateNavigation();
    }

    private static bool TryOpenFolder(string path)
    {
        if (!Directory.Exists(path)) return false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = $"\"{path}\"" });
            return true;
        }
        catch (Exception) { return false; }
    }

    // Draws a CancellationFlow in one ContentDialog. It stays open from the question, through the stop, to the result,
    // so the dialog that was confirmed is the one that reports how the closure went. Every step of drawing is guarded on
    // its own: the bar is set first and stops for good at the end, and a step that fails to draw or copy is reported on
    // the page (see ReportUiFailure) instead of being dropped.
    private sealed class CancelPrompt
    {
        private readonly CancellationFlow flow;
        private readonly Action cancelSent;
        private readonly Func<string, bool> openLogs;
        private readonly Func<string, bool> copyDetails;
        private readonly Action<string, Exception> report;
        private readonly ContentDialog dialog;
        private readonly TextBlock body = new() { TextWrapping = TextWrapping.Wrap };
        private readonly InfoBar notice = new() { IsClosable = false, IsOpen = false };
        private readonly StackPanel progress = new() { Spacing = 10 };
        private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        private readonly ProgressBar bar = new() { Minimum = 0, Maximum = 100 };
        private readonly TextBlock detail = new() { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock footnote = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

        public CancelPrompt(CancellationFlow flow, XamlRoot root, ElementTheme theme, Action cancelSent,
            Func<string, bool> openLogs, Func<string, bool> copyDetails, Action<string, Exception> report)
        {
            this.flow = flow;
            this.cancelSent = cancelSent;
            this.openLogs = openLogs;
            this.copyDetails = copyDetails;
            this.report = report;
            var hint = (Style)Application.Current.Resources["Hint"];
            detail.Style = hint;
            footnote.Style = hint;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(bar, "Cancellation progress");
            progress.Children.Add(status);
            progress.Children.Add(bar);
            progress.Children.Add(detail);
            var content = new StackPanel { Spacing = 16, MinWidth = 380 };
            content.Children.Add(body);
            content.Children.Add(notice);
            content.Children.Add(progress);
            content.Children.Add(footnote);
            dialog = new ContentDialog
            {
                XamlRoot = root, RequestedTheme = theme, Content = content,
                DefaultButton = ContentDialogButton.Close,   // the safe answer is always the default
                PrimaryButtonStyle = (Style)Application.Current.Resources["CriticalButtonStyle"]
            };
            dialog.PrimaryButtonClick += OnConfirm;
            dialog.SecondaryButtonClick += OnLink;
            dialog.Closing += OnClosing;
            flow.Changed += Render;
            Render();
        }

        public async Task ShowAsync() => await dialog.ShowAsync();

        public void Detach() => flow.Changed -= Render;

        private void Render()
        {
            CancelView view;
            try { view = flow.View; }
            catch (Exception error)
            {
                report("The cancel dialog could not read its state.", error);
                return;
            }
            Safely("The cancel dialog's progress could not be drawn.", () =>
            {
                bar.IsIndeterminate = view.Indeterminate;
                bar.Value = view.Percent;
                progress.Visibility = view.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
            });
            Safely("The cancel dialog could not show its message.", () =>
            {
                dialog.Title = view.Title;
                var errored = view.Tone == CancelTone.Error;
                body.Text = view.Body;
                body.Visibility = view.Body.Length > 0 && !errored ? Visibility.Visible : Visibility.Collapsed;
                // An outcome that needs attention is the notice itself; cleanup problems beside a clean end are its warning.
                var message = errored ? string.Join(" ", new[] { view.Body, view.Warning }.Where(text => text.Length > 0)) : view.Warning;
                notice.Severity = errored ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
                notice.Message = message;
                notice.IsOpen = message.Length > 0;
                Show(status, view.Status);
                Show(detail, view.Detail);
                Show(footnote, view.Footnote);
            });
            Safely("The cancel dialog could not update its buttons.", () =>
            {
                dialog.PrimaryButtonText = view.ConfirmText ?? "";
                dialog.SecondaryButtonText = view.LinkText;
                dialog.CloseButtonText = view.CloseText ?? "";
            });
        }

        private static void Show(TextBlock block, string text)
        {
            block.Text = text;
            block.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnConfirm(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            // The dialog stays open: from here it shows the stop, and then the result.
            args.Cancel = true;
            Safely("The page could not be updated after the cancel was sent.", () => { if (flow.Confirm()) cancelSent(); });
        }

        private void OnLink(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            // Open logs and Copy details are conveniences: they never close the dialog, and a failure only changes the label.
            args.Cancel = true;
            var end = flow.Run.End;
            var copy = flow.View.Link == CancelLink.CopyDetails;
            var done = copy ? end is { Details.Length: > 0 } && copyDetails(end.Details) : end?.WorkDirectory is { } directory && openLogs(directory);
            Safely("The cancel dialog could not update its link.",
                () => dialog.SecondaryButtonText = copy ? done ? "Copied" : "Could not copy" : done ? "Open logs" : "Could not open logs");
        }

        private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            // Escape and the close button are refused only while the stop is being carried out.
            if (!flow.CanClose) args.Cancel = true;
        }

        private void Safely(string what, Action step)
        {
            try { step(); }
            catch (Exception error) { report(what, error); }
        }
    }

    private async void OnSaveRecipe(object sender, RoutedEventArgs args) => await RunOperation(OperationKind.Recipe, async _ =>
    {
        var recipe = CreateRecipe();
        recipe.ValidateOptions();
        var path = await SaveFile("tiny11-recipe", ".json");
        if (path is not null) { recipe.Write(path); ShowNotice(L10n.T("RecipeSaved", "Recipe saved."), L10n.T("RecipeSavedMsg", "It contains selections and local paths, not downloaded media or credentials.")); }
    });
    private async void OnLoadRecipe(object sender, RoutedEventArgs args) => await RunOperation(OperationKind.Recipe, async _ =>
    {
        var path = await PickFile(".json");
        if (path is null) return;
        RestoreRecipe(BuildRecipe.Read(path));
        ShowNotice(L10n.T("RecipeLoaded", "Recipe loaded."), L10n.T("RecipeLoadedMsg", "Inspect the source again. Core/expert risk acknowledgements must be renewed."));
    });

    private void RestoreRecipe(BuildRecipe recipe)
    {
        SourcePathBox.Text = recipe.SourcePath;
        WorkspaceBox.Text = recipe.WorkspacePath;
        OutputBox.Text = recipe.OutputPath;
        servicingTool = ToolLocator.FindDism();
        applyingMode = true;
        ModeChoice.SelectedIndex = (int)CleanupCatalog.ModeForRecipe(recipe);
        CustomCoreCheck.IsOn = recipe.Flavor == BuildFlavor.Core && SelectedMode == BuildMode.Custom;
        foreach (var choice in options) choice.Selected = recipe.SelectedOptions.Contains(choice.Option.Id);
        applyingMode = false;
        CompressionChoice.SelectedIndex = (int)recipe.Compression;
        CompactCheck.IsOn = recipe.CompactInstallation;
        NetFxCheck.IsOn = recipe.EnableNetFx3;
        LocalAccountCheck.IsOn = recipe.PreferLocalAccount;
        KeepFilesCheck.IsOn = recipe.KeepWorkingFiles;
        TurboCheck.IsOn = recipe.TurboMode;
        RedundantDataCheck.IsOn = recipe.RemoveRedundantData;
        OmitSetupCheck.IsOn = recipe.OmitNetFx3SetupPayload;
        pendingActions = recipe.ExpertActions;
        pendingImageIndex = recipe.ImageIndex;
        pendingSourcePath = recipe.SourcePath;
        if (pendingActions.Length > 0) ExpertPanel.Visibility = Visibility.Visible;
        coreAcknowledged = false;
        ExpertConsent.IsChecked = false;
        UpdateModePresentation();
        reached = 1;
        GoStep(1);
        UpdateCounts();
    }

    private async void OnRecover(object sender, RoutedEventArgs args)
    {
        if (busy || modalOpen) return;
        if (!ToolLocator.IsAdministrator)
        {
            ShowNotice(L10n.T("AdminRecoverTitle", "Administrator privileges required."), L10n.T("AdminRecoverMsg", "Close and reopen Tiny11Builder.exe with administrator approval to recover a workspace."), InfoBarSeverity.Warning);
            return;
        }
        var path = await PickFile(".json");
        if (path is null) return;
        // Advisory only: the engine decides. This just avoids offering a discard it would refuse.
        if (JobCheckpoint.Exists(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ""))
        {
            if (await Confirm(L10n.T("JobHasProgress", "This job has saved progress"),
                L10n.T("JobHasProgressMsg", "Recover discards a job's working copy, and it does not do that for a job with saved progress. " +
                "Resume continues the build from that progress instead.\n\nNothing has been changed."), L10n.T("ConfirmResumeInstead", "Resume instead")))
                await ResumeJobAsync(path);
            return;
        }
        if (!await Confirm(L10n.T("DiscardWorkspace", "Discard this interrupted workspace?"),
            L10n.T("DiscardWorkspaceMsg", "Select only job.json from the interrupted tiny11 job. This discards that job's mounted working copy and removes its staged media. " +
            "Its logs, recipe, report and your source ISO are kept. A job whose image was already committed has saved progress and is never discarded here; " +
            "use Resume build for it. Active builds cannot be recovered."),
            L10n.T("ConfirmDiscard", "Discard working copy"))) return;
        await RunOperation(OperationKind.Recover, async token =>
        {
            Working(L10n.T("RecoveringWorkspace", "Recovering workspace…"));
            var dism = ToolLocator.FindDism();
            ToolLocator.Validate(dism, "dism.exe");
            await Task.Run(() => engine.RecoverAsync(path, dism, token), token);
            ShowNotice(L10n.T("WorkspaceRecovered", "Workspace recovered."), L10n.T("WorkspaceRecoveredMsg", "The interrupted working copy was discarded. Your source and logs were preserved."));
        });
    }

    private async void OnResume(object sender, RoutedEventArgs args)
    {
        if (busy || modalOpen) return;
        var path = await PickFile(".json");
        if (path is not null) await ResumeJobAsync(path);
    }

    private async void OnResumeStopped(object sender, RoutedEventArgs args)
    {
        if (busy || modalOpen || workDirectory is null) return;
        await ResumeJobAsync(Path.Combine(workDirectory, ResumeSupport.JobFileName));
    }

    // Resume continues one specific job from progress the engine verifies itself. It is separate from
    // Recover, which discards. Only the original recipe is passed, with freshly checked tool paths and,
    // when that name is taken, a new output file.
    private async Task ResumeJobAsync(string jobFile)
    {
        var candidate = ResumeSupport.Inspect(jobFile, JobCheckpoint.Exists, JobCheckpoint.RecipeFileName);
        if (candidate.Recipe is not { } original)
        {
            ShowNotice(L10n.T("ResumeNoProgress", "This build can't be resumed."), candidate.Problem ?? ResumeSupport.NoCheckpoint, InfoBarSeverity.Warning);
            return;
        }
        if (!ToolLocator.IsAdministrator)
        {
            ShowNotice(L10n.T("AdminRequired", "Administrator privileges required."), L10n.T("ResumeAdminMsg", "Close and reopen Tiny11Builder.exe with administrator approval, then choose Resume build. Nothing was changed."), InfoBarSeverity.Warning);
            return;
        }
        if (!await Confirm(L10n.T("ResumeThisBuild", "Resume this build?"), ResumeSupport.Describe(original), L10n.T("ConfirmResume", "Resume"))) return;
        var recipe = original;
        if (File.Exists(recipe.OutputPath))
        {
            var picked = await SaveFile(Path.GetFileNameWithoutExtension(recipe.OutputPath), ".iso");
            if (picked is null)
            {
                ShowNotice(L10n.T("ResumeNotStarted", "Resume not started."), L10n.T("OutputExistsMsg", "The output file already exists. Choose a new file name to continue."), InfoBarSeverity.Warning);
                return;
            }
            recipe = recipe with { OutputPath = picked };
        }
        resumeJobFile = candidate.JobFile;
        await RunOperation(OperationKind.Resume, async token =>
        {
            completed = false;
            GoStep(5);
            ResetBuildPage();
            UpdateBuildDescription(recipe);
            workDirectory = candidate.JobDirectory;
            BuildActivity.IsActive = true;
            BuildBar.IsIndeterminate = true;
            CurrentMessage.Text = L10n.T("PreparingTools", "Preparing image tools…");
            await EnsureToolsAsync(token);
            recipe = recipe with { DismPath = servicingTool, OscdimgPath = isoTool };
            new BuildHost().ValidateTools(recipe);
            BeginBuildRun(L10n.T("ResumingHeading", "Resuming"));
            var reporter = BuildProgressReporter();
            var built = await Task.Run(() => engine.ResumeAsync(candidate.JobFile, recipe, reporter, token), token);
            ShowBuildResult(built, resumed: true, turbo: recipe.TurboMode);
        });
    }

    private void OnOpenOutputFolder(object sender, RoutedEventArgs args)
    {
        if (result is not null) OpenFolder(Path.GetDirectoryName(result.IsoPath)!);
    }
    private void OnOpenLogs(object sender, RoutedEventArgs args)
    {
        if (workDirectory is null) return;
        if (!Directory.Exists(workDirectory))
        {
            ShowNotice(L10n.T("JobFolderGoneTitle", "The job folder is gone."), string.Format(L10n.T("JobFolderGoneMsg", "{0} no longer exists."), workDirectory), InfoBarSeverity.Warning);
            return;
        }
        OpenFolder(workDirectory);
    }
    private void OpenFolder(string path)
    {
        try { using var process = Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = $"\"{path}\"" }); }
        catch (Exception error) { ShowNotice(L10n.T("FolderOpenFailTitle", "Folder could not be opened."), ErrorText.Reason(error), InfoBarSeverity.Error); }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs args) => ApplyTheme();

    // Language resources are resolved when each window is created, so the cleanest way to apply a new
    // language everywhere is to persist the choice and restart the process.
    private void OnLanguageChanged(object sender, SelectionChangedEventArgs args)
    {
        if (LanguageChoice.SelectedIndex == _appliedLanguageIndex) return;
        _appliedLanguageIndex = LanguageChoice.SelectedIndex;
        Localization.Current = _appliedLanguageIndex switch
        {
            2 => "zh-CN",
            1 => "en-US",
            _ => string.Empty
        };
        RestartApplication();
    }

    private void RestartApplication()
    {
        string notice = L10n.T("LangAppliedNotice", "Language applied. Restart the app to see all text in the new language.");
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                Application.Current.Exit();
                return;
            }
        }
        catch (Exception error)
        {
            ShowNotice(notice, ErrorText.Reason(error), InfoBarSeverity.Warning);
            return;
        }
        ShowNotice(notice, L10n.T("LangRestartCloseMsg", "Close and reopen Tiny11 Builder."), InfoBarSeverity.Warning);
    }

    private bool HighContrast
    {
        get
        {
            try { return accessibility.HighContrast; }
            catch (COMException) { return false; }
        }
    }

    private void ApplyTheme()
    {
        if (Root is null) return;
        // High contrast supplies its own colors; forcing Light or Dark over it would hide them.
        Root.RequestedTheme = HighContrast ? ElementTheme.Default : ThemeChoice.SelectedIndex switch
        {
            1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default
        };
    }

    // Ordinary themes keep the faint headings and watermark. In high contrast the opacity resources
    // switch to full strength (App.xaml), the decorative step number is hidden, and the system colors win.
    private void ApplyContrast()
    {
        var contrast = HighContrast;
        if (contrast == appliedContrast) return;
        appliedContrast = contrast;
        StepNumber.Visibility = contrast ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(ThemeChoice, contrast ? "Windows high contrast is on, so colors follow your system settings." : null);
        ApplyTheme();
        UpdateNavigation();
    }
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

// Shows an element only for the listed operation states, e.g. ConverterParameter='Pending|Skipped'.
public sealed class StateVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is BuildOperationState state && parameter is string names && names.Split('|').Contains(state.ToString()) ?
            Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

internal sealed class UiProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
