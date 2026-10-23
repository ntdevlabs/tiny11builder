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
    private enum NoticeAction { None, OpenLogs, CopyDetails, OpenTools, OpenOfficialDownload, Restart }
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
        Title = Loc.Get("App__Title");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleArea);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 900));
        AppWindow.Closing += (_, args) =>
        {
            if (!busy) return;
            args.Cancel = true;
            ShowNotice(Loc.Get("Notice__CloseDuringBuild__Title"), cancelRequested ? Loc.Get("Notice__CloseDuringBuild__Cancelling") :
                Loc.Format("Notice__CloseDuringBuild__Message", Loc.Get("Cancel__ButtonLabel")), InfoBarSeverity.Warning);
        };
        WorkspaceBox.Text = @"C:\tiny11";
        SupportPurpose.Text = Loc.Get("Support__Purpose");
        LanguageChoice.ItemsSource = LanguageCatalog.All;
        LanguageChoice.SelectedItem = LanguageCatalog.For(AppSettings.Load().Language);
        OutputBox.Text = Path.Combine(WorkspaceBox.Text, $"tiny11-{DateTime.Now:yyyyMMdd-HHmmss}.iso");
        BuildStages.ItemsSource = board.Stages;
        progressTimer.Tick += (_, _) => { FlushBuildProgress(); DrainDownload(); };
        // Desktop apps cannot subscribe to AccessibilitySettings.HighContrastChanged (it throws), so
        // high contrast is re-read whenever the theme changes or the window is activated.
        Root.ActualThemeChanged += (_, _) => ApplyContrast();
        Activated += (_, _) => ApplyContrast();
        Closed += (_, _) => progressTimer.Stop();
        LatestDownloadDescription.Text = Loc.Format("Source__Download__AutoDescription", HostArchitecture.ToUpperInvariant(), CultureInfo.CurrentUICulture.NativeName);
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
                    ?? throw new InvalidDataException(Loc.Get("Error__SavedSessionEmpty"));
                RestoreRecipe(recipe);
                ShowNotice(Loc.Get("Notice__SessionRestored__Title"), Loc.Get("Notice__SessionRestored__Message"));
            }
            catch (Exception error) { ShowNotice(Loc.Get("Notice__SessionRestoreFailed__Title"), ErrorText.Reason(error), InfoBarSeverity.Error); }
        }
        UpdateCounts();
    }

    private FrameworkElement[] Panels => [WelcomePanel, SourcePanel, CleanupPanel, SettingsPanel, ReviewPanel, BuildPanel];
    private BuildMode SelectedMode => (BuildMode)ModeChoice.SelectedIndex;
    private bool UsesCore => SelectedMode == BuildMode.Tiny11Core ||
        (SelectedMode == BuildMode.Custom && CustomCoreCheck.IsOn);
    private static string HostArchitecture => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    // Engine stages are stable English identifiers used for phase mapping; only their display name is localized.
    private static string StageName(string stage) => stage switch
    {
        "Prepare" => Loc.Get("Build__StageNames__Prepare"),
        "Copy media" => Loc.Get("Build__StageNames__CopyMedia"),
        "Export edition" => Loc.Get("Build__StageNames__ExportEdition"),
        "Mount" => Loc.Get("Build__StageNames__Mount"),
        "Inventory" => Loc.Get("Build__StageNames__Inventory"),
        "Custom servicing" => Loc.Get("Build__StageNames__CustomServicing"),
        "Remove apps" => Loc.Get("Build__StageNames__RemoveApps"),
        "Optional features" => Loc.Get("Build__StageNames__OptionalFeatures"),
        ".NET 3.5" => Loc.Get("Build__StageNames__NetFx"),
        "Windows settings" => Loc.Get("Build__StageNames__WindowsSettings"),
        "Component cleanup" => Loc.Get("Build__StageNames__ComponentCleanup"),
        "Advanced payloads" => Loc.Get("Build__StageNames__AdvancedPayloads"),
        "Core pruning" => Loc.Get("Build__StageNames__CorePruning"),
        "Redundant data" => Loc.Get("Build__StageNames__RedundantData"),
        "Commit" => Loc.Get("Build__StageNames__Commit"),
        "Setup image" => Loc.Get("Build__StageNames__SetupImage"),
        "Compress" => Loc.Get("Build__StageNames__Compress"),
        "Create ISO" => Loc.Get("Build__StageNames__CreateIso"),
        "Verify output" => Loc.Get("Build__StageNames__VerifyOutput"),
        "Check saved progress" => Loc.Get("Build__StageNames__ResumeCheck"),
        _ => stage
    };

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
        ActionLabel.Text = completed ? Loc.Get("Action__BuildAnother") :
            retry ? Loc.Get(stoppedRun == OperationKind.Resume ? "Action__ResumeAgain" : "Action__BuildAgain") : step switch
            {
                0 => Loc.Get("Action__Start"), 1 => Loc.Get("Action__Next"), 2 => Loc.Get("Action__Next"),
                3 => Loc.Get("Action__Review"), 4 => Loc.Get("Action__Continue"), _ => Loc.Get("Action__Build")
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
        LanguageChoice.IsEnabled = !busy;
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
            ShowNotice(Loc.Get("Notice__BuildModeInvalid__Title"), Loc.Get("Notice__BuildModeInvalid__Message"), InfoBarSeverity.Error);
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
            coreAcknowledged = await Confirm(Loc.Get("Dialog__Core__Title"), Loc.Get("Dialog__Core__Body"), Loc.Get("Dialog__Core__Accept"));
            CoreConsentStatus.Text = Loc.Get(coreAcknowledged ? "Components__CoreConsent__Accepted" : "Components__CoreConsent__NotAccepted");
            if (!coreAcknowledged) return;
        }
        await RunOperation(OperationKind.Check, async token =>
        {
            Working(Loc.Get("Working__CheckingChoices"));
            if (step >= 1 && image is null) throw new InvalidOperationException(Loc.Get("Error__InspectFirst"));
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
                    try { ShowNotice("The operation did not complete.", ErrorText.Reason(error) + " " + display.Message, InfoBarSeverity.Error); }
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
            else ShowNotice("Part of the page could not be updated.", text, InfoBarSeverity.Warning, NoticeAction.CopyDetails, error.ToString());
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
            var noun = Loc.Get(kind == OperationKind.Resume ? "Build__Noun__Resume" : "Build__Noun__Build");
            BuildHeading.Text = outcome.Cancelled ? Loc.Format("Build__CancelledHeading", noun) : Loc.Format("Build__Stopped", noun);
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
            NoticeAction.OpenLogs => Loc.Get("Notice__OpenLogs"),
            NoticeAction.OpenTools => Loc.Get("Notice__OpenTools"),
            NoticeAction.OpenOfficialDownload => Loc.Get("Notice__OpenOfficialDownload"),
            NoticeAction.Restart => Loc.Get("Notice__Restart"),
            _ => Loc.Get("Notice__CopyDetails")
        };
        NoticeButton.Content = label;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NoticeButton, label);
        Notice.IsOpen = true;
    }

    private void OnNoticeAction(object sender, RoutedEventArgs args)
    {
        if (noticeAction == NoticeAction.Restart) { RestartForLanguage(); return; }
        if (noticeAction == NoticeAction.OpenLogs) { OnOpenLogs(sender, args); return; }
        if (noticeAction is NoticeAction.OpenTools or NoticeAction.OpenOfficialDownload)
        {
            var page = noticeAction == NoticeAction.OpenTools ? ServicingToolsRequiredException.DownloadPage : officialDownloadPage;
            try
            {
                if (page is not (ServicingToolsRequiredException.DownloadPage or OfficialImageDownloader.DownloadPage or OfficialImageDownloader.ArmDownloadPage))
                    throw new InvalidDataException(Loc.Get("Error__NotApprovedPage"));
                using var browser = Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
                if (browser is null) throw new IOException(Loc.Get("Error__BrowserMicrosoftPage"));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ShowNotice(Loc.Get("Notice__CouldNotOpenDownload"), page + "\n" +
                    ErrorText.Reason(error), InfoBarSeverity.Error, NoticeAction.CopyDetails, error.ToString());
            }
            return;
        }
        if (noticeAction != NoticeAction.CopyDetails || noticeDetails.Length == 0) return;
        NoticeButton.Content = Loc.Get(CopyToClipboard(noticeDetails) ? "Notice__Copied" : "Notice__CouldNotCopy");
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
                Title = title, Content = content, PrimaryButtonText = action, CloseButtonText = Loc.Get("Dialog__Close"),
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
            ShowNotice(Loc.Get("Notice__SupportOpenFailed__Title"), ErrorText.Reason(error),
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
                throw new InvalidDataException(Loc.Get("Error__SupportLink"));
            var uri = ProjectSupport.Link(destination);
            if (!await Windows.System.Launcher.LaunchUriAsync(uri))
                throw new IOException(Loc.Format("Notice__BrowserFailed", uri));
            SupportError.IsOpen = false;
        }
        catch (Exception error)
        {
            SupportError.Title = Loc.Get("Notice__SupportPageFailed__Title");
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
        picker.FileTypeChoices.Add(Loc.Get(extension == ".iso" ? "File__IsoImage" : "File__ImageRecipe"), new List<string> { extension });
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
        ImageBadge.Text = Loc.Get("Image__None");
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
        ImageBadge.Text = image is null ? Loc.Get("Image__None") : $"{image.Name}\n{image.Architecture} · build {image.Version.Build}";
        if (image is not null && !image.IsSupported)
            ShowNotice(Loc.Get("Notice__UnsupportedEdition__Title"), Loc.Get("Notice__UnsupportedEdition__Message"), InfoBarSeverity.Warning);
        UpdateNavigation();
    }

    private async void OnInspect(object sender, RoutedEventArgs args)
    {
        if (!ToolLocator.IsAdministrator)
        {
            ShowNotice(Loc.Get("Notice__AdminRequired__Title"), Loc.Get("Notice__AdminInspect__Message"), InfoBarSeverity.Warning);
            return;
        }
        await RunOperation(OperationKind.Inspect, InspectSourceAsync);
    }

    private async Task InspectSourceAsync(CancellationToken token)
    {
        if (!ToolLocator.IsAdministrator)
        {
            ShowNotice(Loc.Get("Notice__ImageSelected__Title"), Loc.Get("Notice__AdminInspectSelected__Message"));
            return;
        }
        Working(Loc.Get("Working__Inspecting"));
        var source = SourcePathBox.Text.Trim();
        servicingTool = ToolLocator.FindDism();
        await Task.Run(() => ToolLocator.Validate(servicingTool, "dism.exe"), token);
        var images = await Task.Run(() => engine.InspectAsync(source, servicingTool, token), token);
        var restored = source.Equals(pendingSourcePath, StringComparison.OrdinalIgnoreCase) ? pendingImageIndex : null;
        EditionChoice.ItemsSource = images;
        EditionChoice.SelectedIndex = ImageDefaults.SelectPosition(images, restored);
        ShowNotice(Loc.Get("Notice__ImageInspected__Title"), Loc.Format("Notice__ImageInspected__Message", images.Count, image?.Name));
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
        Working(Loc.Get("Working__Downloading"));
        using var client = OfficialImageDownloader.CreateClient();
        DownloadBar.IsIndeterminate = true;
        BeginDownloadText(p => FormatDownload(p, ""));
        try
        {
            var downloaded = await new OfficialImageDownloader(client).DownloadAsync(uri.AbsoluteUri, path, hash, downloadUpdates, token,
                status: new UiProgress<string>(text => DispatcherQueue.TryEnqueue(() => DownloadStatus.Text = text)));
            DrainDownload();
            SourcePathBox.Text = downloaded.Path;
            DownloadStatus.Text = Loc.Format("Notice__DownloadHash", downloaded.Sha256);
            ShowNotice(Loc.Get(downloaded.VerifiedAgainstPublishedHash ? "Notice__DownloadVerified__Title" : "Notice__DownloadUnverified__Title"),
                Loc.Get(downloaded.VerifiedAgainstPublishedHash ? "Notice__DownloadVerified__Message" : "Notice__DownloadUnverified__Message"), InfoBarSeverity.Informational);
        }
        finally { EndDownloadText(); }
    });

    private async void OnDownloadLatest(object sender, RoutedEventArgs args) => await RunOperation(OperationKind.Download, async token =>
    {
        Working(Loc.Get("Working__FindingLatest"));
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
                Working(Loc.Get("Working__CheckingDownload"));
                await using var input = File.OpenRead(path);
                await IsoStructure.ValidateAsync(input, token);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
                if (actual != (await File.ReadAllTextAsync(hashFile, token)).Trim() ||
                    (media.Sha256 is not null && actual != media.Sha256))
                    throw new InvalidDataException(Loc.Get("Notice__CachedIsoChanged"));
                DownloadStatus.Text = Loc.Get("Notice__PreviouslyDownloaded");
            }
            else
            {
                if (File.Exists(path)) path = Path.Combine(folder, Path.GetFileNameWithoutExtension(media.FileName) + "-" + Guid.NewGuid().ToString("N")[..8] + ".iso");
                Working(Loc.Get("Working__Downloading"));
                using var downloadClient = OfficialImageDownloader.CreateClient();
                BeginDownloadText(p => FormatDownload(p, $"{media.Language} · "));
                var download = await new OfficialImageDownloader(downloadClient).DownloadAsync(media.Url.AbsoluteUri,
                    path, media.Sha256, downloadUpdates, token, status: catalogProgress);
                DrainDownload();
                await File.WriteAllTextAsync(path + ".sha256", download.Sha256, token);
                DownloadStatus.Text = Loc.Format("Notice__DownloadedSummary", media.Description, download.Sha256) +
                    Loc.Get(download.VerifiedAgainstPublishedHash ? "Notice__HashVerifiedSuffix" : "Notice__ComputedHashSuffix");
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
        ToolStatus.Text = Loc.Get("Settings__Tools__Ready");
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
        CoreConsentStatus.Text = Loc.Get(coreAcknowledged ? "Components__CoreConsent__Accepted" : "Components__CoreConsent__Pending");
        ModeDescription.Text = Loc.Get(SelectedMode switch
        {
            BuildMode.Tiny11 => "Components__ModeDescription__Tiny11",
            BuildMode.Tiny11Core => "Components__ModeDescription__Core",
            _ => "Components__ModeDescription__Custom"
        });
        SourceMode.Text = Loc.Format("Source__BuildMode", (ModeChoice.SelectedItem as ComboBoxItem)?.Content);
        MarkChoice(Tiny11Selection, SelectedMode == BuildMode.Tiny11);
        MarkChoice(CoreSelection, SelectedMode == BuildMode.Tiny11Core);
        MarkChoice(CustomSelection, SelectedMode == BuildMode.Custom);
    }

    // The chosen card gets an accent outline and is announced as selected; unchosen cards keep full-strength text.
    private static void MarkChoice(Button card, bool selected)
    {
        card.Style = (Style)Application.Current.Resources[selected ? "ModeCardSelected" : "ModeCard"];
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(card, Loc.Get(selected ? "Components__Selected" : "Components__NotSelected"));
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
        var category = (OptionFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? Loc.Get("Common__All");
        OptionsList.ItemsSource = options.Where(o => category == Loc.Get("Common__All") || o.Category == category).ToArray();
    }
    private void UpdateCounts()
    {
        if (!initialized) return;
        var custom = inventory.Count > 0 ? inventory.Count(i => i.Selected) : pendingActions.Length;
        SelectionCount.Text = Loc.Format("Components__Counts__Groups", options.Count(o => o.Selected), custom);
        ExpertCount.Text = Loc.Format("Components__Counts__Inventory", inventory.Count.ToString("N0"), custom.ToString("N0"));
        UpdateNavigation();
    }

    private async void OnOpenExpert(object sender, RoutedEventArgs args)
    {
        if (busy || modalOpen || SelectedMode != BuildMode.Custom) return;
        if (image is null) { ShowNotice(Loc.Get("Notice__InspectImageFirst__Title"), Loc.Get("Notice__InspectImageFirst__Message"), InfoBarSeverity.Warning); return; }
        if (!await Confirm(Loc.Get("Dialog__Expert__Title"), Loc.Get("Dialog__Expert__Body"), Loc.Get("Dialog__Expert__Inspect"))) return;
        await RunOperation(OperationKind.Inventory, async token =>
        {
            Working(Loc.Get("Working__ListingComponents"));
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
                ShowNotice(Loc.Get("Notice__ComponentListReadOnly__Title"), Loc.Get("Notice__ComponentListReadOnly__Message"),
                    InfoBarSeverity.Warning, NoticeAction.OpenTools);
            else
                ShowNotice(Loc.Get("Notice__ComponentListLoaded__Title"), Loc.Get("Notice__ComponentListLoaded__Message"));
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
        var filter = (InventoryFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? Loc.Get("Common__All");
        var query = InventorySearch.Text.Trim();
        InventoryList.ItemsSource = inventory.Where(i => (filter == Loc.Get("Common__All") || i.Item.Kind.ToString() == filter) &&
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
        text.AppendLine($"IMAGE       {image?.Summary ?? "Not inspected"}");
        text.AppendLine($"MODE        {(ModeChoice.SelectedItem as ComboBoxItem)?.Content}");
        if (SelectedMode == BuildMode.Custom) text.AppendLine($"CORE REMOVALS {UsesCore}");
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
        text.AppendLine("\nREMOVE / DISABLE");
        foreach (var option in options.Where(o => o.Selected)) text.AppendLine($"  {option.Title}");
        foreach (var action in recipe.ExpertActions) text.AppendLine($"  EXPERT {action.Mode} {action.Kind}: {action.Identity}");
        text.AppendLine("\nUNSELECTED ITEMS ARE KEPT.");
        if (recipe.Flavor == BuildFlavor.Core)
            text.AppendLine("\nCORE: irreversible component pruning, WinRE removed, Update and Defender disabled. Install optional features now, not after creation.");
        if (recipe.ExpertActions.Length > 0)
            text.AppendLine("\nEXPERT: dependencies are not resolved. Protected and boot-critical removals may produce an unbootable image.");
        text.AppendLine("\nPolicies may be ignored by a particular Windows build/edition or changed by updates. VM boot validation is required.");
        return text.ToString();
    }

    private void UpdateReview()
    {
        var recipe = CreateRecipe();
        ReviewText.Text = CreateReview();
        ReviewImage.Text = image?.Summary ?? Loc.Get("Review__NoImage");
        ReviewMode.Text = (ModeChoice.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        ReviewRemovals.Text = Loc.Format("Review__Removals__Summary", recipe.SelectedOptions.Length, recipe.ExpertActions.Length) + "\n" +
            Loc.Get(UsesCore ? "Review__Removals__Core" : SelectedMode == BuildMode.Custom ?
                "Review__Removals__Custom" : "Review__Removals__Tiny11");
        ReviewPaths.Text = Loc.Format("Review__Paths__Value", recipe.OutputPath, recipe.WorkspacePath);
        var compression = Loc.Get(recipe.Compression switch { Compression.Recovery => "Settings__Compression__Recovery", Compression.Maximum => "Settings__Compression__Maximum", _ => "Settings__Compression__Fast" });
        ReviewOptions.Text = Loc.Format("Review__Options__Compression", compression) + "\n" +
            Loc.Format("Review__Options__Turbo", Loc.Get(recipe.TurboMode ? "Common__On" : "Common__Off")) + "\n" +
            Loc.Format("Review__Options__Redundant", Loc.Get(recipe.RemoveRedundantData ? "Common__Remove" : "Common__Keep")) + "\n" +
            Loc.Format("Review__Options__OmitSetup", Loc.Get(recipe.OmitNetFx3SetupPayload ? "Common__Omit" : "Common__Keep")) + "\n" +
            Loc.Format("Review__Options__Compact", Loc.Get(recipe.CompactInstallation ? "Common__On" : "Common__Off")) + "\n" +
            Loc.Format("Review__Options__NetFx", Loc.Get(recipe.EnableNetFx3 ? "Review__Options__Included" : "Review__Options__NotIncluded")) + "\n" +
            Loc.Format("Review__Options__LocalAccount", Loc.Get(recipe.PreferLocalAccount ? "Review__Options__Preferred" : "Review__Options__Default")) + "\n" +
            Loc.Format("Review__Options__KeepFiles", Loc.Get(recipe.KeepWorkingFiles ? "Common__Keep" : "Review__Options__CleanAfterBuild")) +
            (SetupLabels.Legacy(recipe) is { } legacy ? "\n" + legacy : "");
    }

    // The legacy installer is derived from the removals, never chosen, so this only reports it.
    private void UpdateBuildDescription(BuildRecipe recipe) => BuildDescription.Text = Loc.Get("Build__Description") +
        (SetupLabels.LegacyReason(recipe) is { } reason ? Loc.Format("Build__Description__Legacy", reason) : "");

    private async Task StartBuildAsync()
    {
        BuildRecipe recipe;
        try { recipe = CreateRecipe(); recipe.ValidateOptions(); PathSafety.Validate(recipe); }
        catch (Exception error) { ShowNotice(Loc.Get("Notice__RecipeNotReady__Title"), ErrorText.Reason(error), InfoBarSeverity.Error); return; }
        if (!await Confirm(Loc.Get("Dialog__Build__Title"),
            Loc.Format("Dialog__Build__Body", recipe.OutputPath, (ModeChoice.SelectedItem as ComboBoxItem)?.Content,
                recipe.SelectedOptions.Length, recipe.ExpertActions.Length,
                SetupLabels.Legacy(recipe) is { } legacy ? "\n" + legacy : ""),
            Loc.Get("Dialog__Build__Action"))) return;
        await RunOperation(OperationKind.Build, async token =>
        {
            ResetBuildPage();
            BuildActivity.IsActive = true;
            BuildBar.IsIndeterminate = true;
            CurrentMessage.Text = Loc.Get("Build__PreparingTools");
            await EnsureToolsAsync(token);
            recipe = CreateRecipe();
            BeginBuildRun(Loc.Get("Build__Building"));
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
        BuildHeading.Text = Loc.Get("Build__Heading");
        CurrentStage.Text = Loc.Get("Build__Waiting");
        CurrentMessage.Text = Loc.Get("Build__NoChanges");
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
        var size = File.Exists(built.IsoPath) ? " · " + Loc.Format("Build__Result__IsoSize", (new FileInfo(built.IsoPath).Length / (1024d * 1024 * 1024)).ToString("N2")) : "";
        var minutes = Loc.Format("Build__Result__Minutes", built.Duration.TotalMinutes.ToString("N1"));
        var metrics = new List<string>
        {
            Loc.Get(resumed ? "Build__Result__Resumed" : turbo ? "Build__Result__Turbo" : "Build__Result__Standard") + " · " +
                (resumed ? Loc.Format("Build__Result__InTotal", minutes) : minutes) + size
        };
        if (!resumed || built.RemovedLogicalBytes > 0 || built.OmittedSetupBytes > 0)
            metrics.Add(Loc.Format("Build__Result__RedundantRemoved", (built.RemovedLogicalBytes / (1024d * 1024)).ToString("N1"),
                (built.OmittedSetupBytes / (1024d * 1024)).ToString("N1")));
        metrics.Add(Loc.Get("Build__Result__StageTimings"));
        ResultMetrics.Text = string.Join("\n", metrics);
        var warnings = built.Warnings;
        // A cancel prompt open for this run reports the warning too, instead of calling the end clean.
        if (warnings.Count > 0)
            active?.NoteWarning(Loc.Get("Build__Result__WarningNotice"));
        ResultWarnings.IsOpen = warnings.Count > 0;
        if (warnings.Count > 0)
            ResultWarnings.Message = string.Join("\n", warnings.Take(4).Select(w => "• " + ErrorText.Summary(w, 200))) +
                (warnings.Count > 4 ? "\n• " + Loc.Format("Build__Result__MoreWarnings", warnings.Count - 4) : "");
        ResultPanel.Visibility = Visibility.Visible;
        BuildHeading.Text = Loc.Get("Build__IsoCreated");
        CurrentStage.Text = Loc.Get("Build__Stage__Complete");
        BuildActivity.IsActive = false;
        BuildGlyph.Glyph = "\uE73E";
        if (resumed) board.MarkEarlierPhasesDone();
        board.Complete();
        OverallBar.Value = 100;
        BuildPercent.Text = "100%";
        OverallStep.Text = Loc.Format("Build__Stages__AllDone", board.Stages.Length);
        CurrentPercent.Text = "100%";
        HistoryCount.Text = StageSummary.Describe(board);
        BuildBar.IsIndeterminate = false;
        BuildBar.Value = 100;
        CurrentMessage.Text = Loc.Get(warnings.Count > 0 ? "Build__Result__SavedWarnings" : "Build__Result__Saved");
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
                    if (running is OperationKind.Build or OperationKind.Resume) CurrentMessage.Text = Loc.Format("Build__CancellationRequested", p.Message);
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
                CurrentStage.Text = StageName(p.Stage);
                // Once cancellation has been requested, that status stays until the run actually stops.
                if (!cancelRequested)
                    CurrentMessage.Text = p.State == BuildOperationState.Failed ?
                        Loc.Format("Build__StageFailed__Message", p.Message) : p.Message;
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
                OverallStep.Text = Loc.Format("Build__Stage__Of", phase + 1, board.Stages.Length);
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
            CurrentMessage.Text = Loc.Get("Build__CancellationWaiting");
        Working(Loc.Get("Build__Cancelling"));
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
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(bar, Loc.Get("Notice__CancellationProgress"));
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
                () => dialog.SecondaryButtonText = copy ? Loc.Get(done ? "Notice__Copied" : "Notice__CouldNotCopy") : Loc.Get(done ? "Notice__OpenLogs" : "Notice__CouldNotOpenLogs"));
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
        if (path is not null) { recipe.Write(path); ShowNotice(Loc.Get("Notice__RecipeSaved__Title"), Loc.Get("Notice__RecipeSaved__Message")); }
    });
    private async void OnLoadRecipe(object sender, RoutedEventArgs args) => await RunOperation(OperationKind.Recipe, async _ =>
    {
        var path = await PickFile(".json");
        if (path is null) return;
        RestoreRecipe(BuildRecipe.Read(path));
        ShowNotice(Loc.Get("Notice__RecipeLoaded__Title"), Loc.Get("Notice__RecipeLoaded__Message"));
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
            ShowNotice(Loc.Get("Notice__AdminRequired__Title"), Loc.Get("Notice__AdminRecover__Message"), InfoBarSeverity.Warning);
            return;
        }
        var path = await PickFile(".json");
        if (path is null) return;
        // Advisory only: the engine decides. This just avoids offering a discard it would refuse.
        if (JobCheckpoint.Exists(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ""))
        {
            if (await Confirm(Loc.Get("Dialog__RecoverProgress__Title"), Loc.Get("Dialog__RecoverProgress__Body"), Loc.Get("Dialog__RecoverProgress__Resume")))
                await ResumeJobAsync(path);
            return;
        }
        if (!await Confirm(Loc.Get("Dialog__Recover__Title"), Loc.Get("Dialog__Recover__Body"), Loc.Get("Dialog__Recover__Discard"))) return;
        await RunOperation(OperationKind.Recover, async token =>
        {
            Working(Loc.Get("Working__Recovering"));
            var dism = ToolLocator.FindDism();
            ToolLocator.Validate(dism, "dism.exe");
            await Task.Run(() => engine.RecoverAsync(path, dism, token), token);
            ShowNotice(Loc.Get("Notice__WorkspaceRecovered__Title"), Loc.Get("Notice__WorkspaceRecovered__Message"));
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
            ShowNotice(Loc.Get("Notice__CannotResume__Title"), candidate.Problem ?? Loc.Get("Resume__NoCheckpoint"), InfoBarSeverity.Warning);
            return;
        }
        if (!ToolLocator.IsAdministrator)
        {
            ShowNotice(Loc.Get("Notice__AdminRequired__Title"), Loc.Get("Notice__AdminResume__Message"), InfoBarSeverity.Warning);
            return;
        }
        if (!await Confirm(Loc.Get("Dialog__Resume__Title"), ResumeSupport.Describe(original), Loc.Get("Dialog__Resume__Action"))) return;
        var recipe = original;
        if (File.Exists(recipe.OutputPath))
        {
            var picked = await SaveFile(Path.GetFileNameWithoutExtension(recipe.OutputPath), ".iso");
            if (picked is null)
            {
                ShowNotice(Loc.Get("Notice__ResumeNotStarted__Title"), Loc.Get("Notice__ResumeNotStarted__Message"), InfoBarSeverity.Warning);
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
            CurrentMessage.Text = Loc.Get("Build__PreparingTools");
            await EnsureToolsAsync(token);
            recipe = recipe with { DismPath = servicingTool, OscdimgPath = isoTool };
            new BuildHost().ValidateTools(recipe);
            BeginBuildRun(Loc.Get("Build__Resuming"));
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
            ShowNotice(Loc.Get("Notice__JobFolderGone__Title"), Loc.Format("Notice__JobFolderGone__Message", workDirectory), InfoBarSeverity.Warning);
            return;
        }
        OpenFolder(workDirectory);
    }
    private void OpenFolder(string path)
    {
        try { using var process = Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = $"\"{path}\"" }); }
        catch (Exception error) { ShowNotice(Loc.Get("Notice__FolderOpenFailed__Title"), ErrorText.Reason(error), InfoBarSeverity.Error); }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs args) => ApplyTheme();

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!initialized || LanguageChoice.SelectedItem is not AppLanguage language) return;
        var settings = AppSettings.Load();
        settings.Language = language.Tag;
        if (!settings.TrySave())
        {
            ShowNotice(Loc.Get("Language__SaveFailed__Title"), Loc.Get("Language__SaveFailed__Message"), InfoBarSeverity.Warning);
            return;
        }
        ShowNotice(Loc.Get("Language__Restart__Title"), Loc.Get("Language__Restart__Message"),
            InfoBarSeverity.Informational, NoticeAction.Restart);
    }

    // A language change is applied by a fresh process: XAML resources are resolved when a page is
    // loaded, and restarting is the only way to re-resolve every string without losing the build state.
    private void RestartForLanguage()
    {
        if (busy) return;
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable)) throw new InvalidOperationException(Loc.Get("Error__RestartPath"));
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false });
            Application.Current.Exit();
        }
        catch (Exception error)
        {
            ShowNotice(Loc.Get("Language__RestartFailed__Title"), ErrorText.Reason(error),
                InfoBarSeverity.Error, NoticeAction.CopyDetails, error.ToString());
        }
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
        ToolTipService.SetToolTip(ThemeChoice, contrast ? Loc.Get("Notice__HighContrastTooltip") : null);
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
