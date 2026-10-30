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
    private bool stoppedWasCancelled;
    private bool resultResumed;
    private bool resultTurbo;
    private int resultUpdateCount;
    private bool coreAcknowledged;
    private bool modalOpen;
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
    private string servicingTool = "";
    private string isoTool = ToolLocator.FindOscdimg();
    private readonly BuildStageBoard board = new();
    private BuildResult? result;
    private string? workDirectory;
    private readonly LiveLog liveLog = new();
    private readonly List<string> servicingDiagnostics = [];
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
    private readonly UpdatePackageSelection updatePackages = new();
    private readonly LanguageCatalogs languageCatalogs;
    private string languagePreference = "system";
    private bool changingLanguage;
    private readonly string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tiny11Builder");

    public MainWindow()
    {
        languageCatalogs = LanguageCatalogs.Load(Path.Combine(AppContext.BaseDirectory, "Languages"));
        string? languageProblem = null;
        try
        {
            languagePreference = LanguagePreference.Read(Path.Combine(dataDirectory, "ui-language.json")).Language;
            if (!languageCatalogs.IsAvailable(languagePreference)) languagePreference = "system";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { languageProblem = error.Message; }
        UiText.Select(languageCatalogs.Resolve(languagePreference, CultureInfo.CurrentUICulture));
        InitializeComponent();
        Root.Language = UiText.Current.Info.Id;
        SetLanguageChoices();
        UpdatePackagesList.ItemsSource = updatePackages.Items;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleArea);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 900));
        AppWindow.Closing += (_, args) =>
        {
            if (!busy) return;
            args.Cancel = true;
            ShowNotice("An operation is still running.", cancelRequested ? "It is being cancelled. Wait for it to stop before closing." :
                UiText.Format("App.WaitBeforeClosing", CancelPrompts.ButtonLabel), InfoBarSeverity.Warning);
        };
        WorkspaceBox.Text = @"C:\tiny11";
        SupportPurpose.Text = ProjectSupport.Purpose;
        OutputBox.Text = Path.Combine(WorkspaceBox.Text, $"tiny11-{DateTime.Now:yyyyMMdd-HHmmss}.iso");
        board.RefreshLanguage();
        BuildStages.ItemsSource = board.Stages;
        progressTimer.Tick += (_, _) => { FlushBuildProgress(); DrainDownload(); };
        // Desktop apps cannot subscribe to AccessibilitySettings.HighContrastChanged (it throws), so
        // high contrast is re-read whenever the theme changes or the window is activated.
        Root.ActualThemeChanged += (_, _) => ApplyContrast();
        Activated += (_, _) => ApplyContrast();
        Closed += (_, _) => progressTimer.Stop();
        LatestDownloadDescription.Text = UiText.Format("Download.DefaultDescription", HostArchitecture.ToUpperInvariant(), CultureInfo.CurrentUICulture.NativeName);
        foreach (var choice in options) choice.PropertyChanged += (_, args) =>
        {
            if (initialized && !applyingMode && choice.Option.CustomOnly && args.PropertyName == nameof(OptionChoice.Selected))
                NativePayloadConsent.IsChecked = false;
            UpdateCounts();
        };
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
                ShowNotice("Session restored.", "Inspect the image again before continuing. No build starts automatically.");
            }
            catch (Exception error) { ShowNotice("Session could not be restored.", ErrorText.Reason(error), InfoBarSeverity.Error); }
        }
        UpdateCounts();
        UpdatePackageButtons();
        if (languageProblem is not null || languageCatalogs.Problems.Count > 0)
            ShowNotice(UiText.Get("Language.SomeFilesUnavailable"), UiText.Get("Language.EnglishFallback"),
                InfoBarSeverity.Warning, NoticeAction.CopyDetails,
                languageProblem ?? string.Join("\n", languageCatalogs.Problems.Select(problem => problem.FileName + ": " + problem.Message)));
        try
        {
            servicingTool = FindServicingTool();
            if (SkippedImageTools)
                ShowNotice(UiText.Get("Tools.SomeSkipped"), UiText.Get("Tools.TrustedFallback"), InfoBarSeverity.Warning,
                    NoticeAction.CopyDetails, string.Join("\n", servicingDiagnostics));
        }
        catch (Exception error)
        {
            ShowNotice(UiText.Get("Tools.NotReady"), ErrorText.Reason(error), InfoBarSeverity.Warning,
                OperationOutcomes.RequiresServicingTools(error) ? NoticeAction.OpenTools : NoticeAction.CopyDetails, error.ToString());
        }
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
        var expertReady = !RequiresExpertAcknowledgement || ExpertAccepted;
        var canAdvance = step switch
        {
            1 => image?.IsSupported == true,
            2 => expertReady,
            _ => true
        };
        NextButton.IsEnabled = !busy && canAdvance;
        // After a build stops, the primary action says what it will do: start over, or continue the resume.
        var retry = step == 5 && !completed && stoppedRun is not null;
        var actionLabel = completed ? "Build another" : retry ? (stoppedRun == OperationKind.Resume ? "Resume again" : "Build again") : step switch
        {
            0 => "Start", 1 => "Next", 2 => "Next", 3 => "Review", 4 => "Continue", _ => "Build"
        };
        ActionLabel.Text = UiText.FromEnglish(actionLabel);
        ActionLabel.FontSize = 32;
        Grid.SetColumn(NextButton, 2);
        NextButton.HorizontalAlignment = HorizontalAlignment.Right;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NextButton, ActionLabel.Text);
        NavigationHint.Text = StepNavigation.Hint(step, image?.IsSupported == true,
            expertReady);
        NavigationHint.Visibility = NavigationHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        StepRail.IsEnabled = !busy && !completed;
        for (var index = 0; index < StepRail.Items.Count; index++)
        {
            var item = (ListViewItem)StepRail.Items[index];
            item.IsEnabled = StepNavigation.CanSelect(index, step, reached, image?.IsSupported == true,
                expertReady, busy, completed);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(item,
                UiText.Get(index == step ? "Navigation.CurrentStep" : item.IsEnabled ? "Navigation.AvailableStep" : "Navigation.LockedStep"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(item, item.IsEnabled ? "" :
                image?.IsSupported != true && index >= 2 ? UiText.Get("Navigation.InspectFirst") : UiText.Get("Navigation.CompletePrevious"));
        }
        LanguageChoice.IsEnabled = !busy && !modalOpen;
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

    private async void OnRailChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!initialized || navigating || StepRail.SelectedIndex < 0 || StepRail.SelectedIndex == step) return;
        var target = StepRail.SelectedIndex;
        navigating = true;
        StepRail.SelectedIndex = step;
        navigating = false;
        var expertReady = !RequiresExpertAcknowledgement || ExpertAccepted;
        if (!StepNavigation.CanSelect(target, step, reached, image?.IsSupported == true, expertReady, busy, completed, modalOpen))
        {
            if (!busy && !modalOpen)
                ShowNotice(UiText.Get("Navigation.StepNotReady"), image?.IsSupported != true && target >= 2 ?
                    UiText.Get("Navigation.InspectFirst") : UiText.Get("Navigation.CompletePrevious"), InfoBarSeverity.Warning);
            return;
        }
        if (target < step) GoStep(target);
        else await AdvanceToAsync(target);
    }

    private void OnBack(object sender, RoutedEventArgs args) { if (!busy && !modalOpen && step > 0) GoStep(step - 1); }

    private async void OnModeCardClick(object sender, RoutedEventArgs args)
    {
        if (busy || modalOpen) return;
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<BuildMode>(tag, out var mode) || !Enum.IsDefined(mode))
        {
            ShowNotice("Build mode could not be selected.", "The selected card does not identify a valid build mode.", InfoBarSeverity.Error);
            return;
        }
        if (mode == BuildMode.Custom) await EnterCustomAsync(fromWelcome: true);
        else
        {
            ModeChoice.SelectedIndex = (int)mode;
            GoStep(1);
        }
    }

    private async void OnRemovalModeClick(object sender, RoutedEventArgs args)
    {
        if (busy || modalOpen) return;
        if (sender is Button { Tag: string tag } && Enum.TryParse<BuildMode>(tag, out var mode) && Enum.IsDefined(mode))
        {
            if (mode == BuildMode.Custom) await EnterCustomAsync(fromWelcome: false);
            else ModeChoice.SelectedIndex = (int)mode;
        }
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
        await AdvanceToAsync(step + 1);
    }

    // The rail and primary action share all checks. Revisited steps never bypass a changed source or recipe.
    private async Task AdvanceToAsync(int target)
    {
        if (busy || modalOpen || target <= step || target >= StepNavigation.StepCount) return;
        if (target >= 3 && UsesCore && !coreAcknowledged)
        {
            coreAcknowledged = await Confirm(UiText.Get("Core.ConsentTitle"), UiText.Get("Core.ConsentBody"), UiText.Get("Core.Agree"));
            CoreConsentStatus.Text = UiText.Get(coreAcknowledged ? "Core.Accepted" : "Core.NotAccepted");
            if (!coreAcknowledged) return;
        }
        await RunOperation(OperationKind.Check, async token =>
        {
            Working(UiText.Get("Navigation.CheckingChoices"));
            if (target >= 2 && image is null) throw new InvalidOperationException(UiText.Get("Navigation.InspectFirst"));
            if (target >= 2) image!.Validate();
            if (target >= 3) CreateRecipe().ValidateOptions();
            if (target >= 4)
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
            GoStep(target);
        });
    }

    private BuildRecipe CreateRecipe() => new()
    {
        SourcePath = SourcePathBox.Text.Trim(), ImageIndex = image?.Index ?? 1,
        WorkspacePath = WorkspaceBox.Text.Trim(), OutputPath = OutputBox.Text.Trim(),
        DismPath = servicingTool, OscdimgPath = isoTool,
        Mode = SelectedMode, Flavor = UsesCore ? BuildFlavor.Core : BuildFlavor.Regular,
        CoreAcknowledged = coreAcknowledged, ExpertAcknowledged = ExpertAccepted,
        SelectedOptions = options.Where(o => o.Selected).Select(o => o.Option.Id).ToArray(),
        ExpertActions = inventory.Count > 0 ? inventory.Where(i => i.Selected).Select(i => i.Action).ToArray() : pendingActions,
        Compression = (Compression)CompressionChoice.SelectedIndex,
        CompactInstallation = CompactCheck.IsOn, EnableNetFx3 = NetFxCheck.IsOn,
        PreferLocalAccount = LocalAccountCheck.IsOn, KeepWorkingFiles = KeepFilesCheck.IsOn,
        TurboMode = TurboCheck.IsOn, RemoveRedundantData = RedundantDataCheck.IsOn,
        OmitNetFx3SetupPayload = OmitSetupCheck.IsOn,
        UpdatePackages = updatePackages.Paths,
        IsoSizeLimitBytes = SelectedMode == BuildMode.Custom ? recipeIsoSizeLimit : null,
        ShrinkBootImage = SelectedMode == BuildMode.Custom && ShrinkBootCheck.IsChecked == true
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
            var text = UiText.FromEnglish(what) + " " + ErrorText.Reason(error);
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
            stoppedWasCancelled = outcome.Cancelled;
            var noun = kind == OperationKind.Resume ? "Resume" : "Build";
            BuildHeading.Text = UiText.Format(outcome.Cancelled ? "Build.CancelledHeading" : "Build.StoppedHeading", UiText.FromEnglish(noun));
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
        Notice.Title = UiText.FromEnglish(title);
        Notice.Message = ErrorText.Summary(UiText.FromEnglish(message), 400);
        Notice.Severity = severity;
        noticeAction = action;
        noticeDetails = details ?? "";
        NoticeButton.Visibility = action == NoticeAction.None ? Visibility.Collapsed : Visibility.Visible;
        var label = action switch
        {
            NoticeAction.OpenLogs => "Open logs", NoticeAction.OpenTools => "Get image tools",
            NoticeAction.OpenOfficialDownload => "Open Microsoft download page", _ => "Copy details"
        };
        label = UiText.FromEnglish(label);
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
                ShowNotice("Could not open the download page.", page + "\n" +
                    ErrorText.Reason(error), InfoBarSeverity.Error, NoticeAction.CopyDetails, error.ToString());
            }
            return;
        }
        if (noticeAction != NoticeAction.CopyDetails || noticeDetails.Length == 0) return;
        NoticeButton.Content = UiText.Get(CopyToClipboard(noticeDetails) ? "Common.Copied" : "Common.CopyFailed");
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
        BusyText.Text = UiText.FromEnglish(text);
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
                Title = UiText.FromEnglish(title), Content = UiText.FromEnglish(content),
                PrimaryButtonText = UiText.FromEnglish(action), CloseButtonText = UiText.Get("Common.Cancel"),
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
            ShowNotice("Support options could not be opened.", ErrorText.Reason(error),
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
                throw new InvalidDataException("This support link has no valid destination.");
            var uri = ProjectSupport.Link(destination);
            if (!await Windows.System.Launcher.LaunchUriAsync(uri))
                throw new IOException($"Your browser could not open {uri}. No payment was made.");
            SupportError.IsOpen = false;
        }
        catch (Exception error)
        {
            SupportError.Title = UiText.FromEnglish("Could not open the support page");
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
        picker.FileTypeChoices.Add(UiText.Get(extension switch
        {
            ".iso" => "Picker.IsoImage", ".txt" => "Picker.TextDocument", _ => "Picker.ImageRecipe"
        }), new List<string> { extension });
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
        reached = Math.Min(reached, 1);
        pendingImageIndex = null;
        pendingSourcePath = "";
        EditionChoice.ItemsSource = null;
        ImageBadge.Text = UiText.FromEnglish("No image selected");
        ClearInventory(preserveSelections: true);
        UpdateNavigation();
    }
    private void OnEditionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!initialized) return;
        var selected = EditionChoice.SelectedItem as WindowsImage;
        if (selected is not null && image?.Index != selected.Index &&
            !(pendingImageIndex == selected.Index &&
              SourcePathBox.Text.Trim().Equals(pendingSourcePath, StringComparison.OrdinalIgnoreCase)))
            ClearInventory(preserveSelections: true);
        image = selected;
        ImageBadge.Text = image is null ? UiText.Get("Image.NoneSelected") : UiText.Format("Image.Badge", image.Name, image.Architecture, image.Version.Build);
        if (image is not null && !image.IsSupported)
            ShowNotice(image.Installation.StartsWith("Server", StringComparison.OrdinalIgnoreCase) ||
                image.Name.Contains("Windows Server", StringComparison.OrdinalIgnoreCase) ?
                UiText.Get("Image.ServerUnsupported") : UiText.FromEnglish("This edition is not supported."),
                UiText.FromEnglish("Choose a Windows 11 client image, 22H2 or newer, in x64 or ARM64 format."), InfoBarSeverity.Warning);
        UpdateNavigation();
    }

    private async void OnInspect(object sender, RoutedEventArgs args)
    {
        if (!ToolLocator.IsAdministrator)
        {
            ShowNotice("Administrator privileges required.", "Close and reopen Tiny11Builder.exe, then approve the Windows administrator prompt.", InfoBarSeverity.Warning);
            return;
        }
        await RunOperation(OperationKind.Inspect, InspectSourceAsync);
    }

    private async Task InspectSourceAsync(CancellationToken token)
    {
        if (!ToolLocator.IsAdministrator)
        {
            ShowNotice("Image selected.", "Close and reopen Tiny11Builder.exe with administrator approval to inspect its editions.");
            return;
        }
        Working("Inspecting the image…");
        var source = SourcePathBox.Text.Trim();
        servicingTool = FindServicingTool();
        await Task.Run(() => ToolLocator.Validate(servicingTool, "dism.exe"), token);
        var images = await Task.Run(() => engine.InspectAsync(source, servicingTool, token), token);
        var restored = source.Equals(pendingSourcePath, StringComparison.OrdinalIgnoreCase) ? pendingImageIndex : null;
        EditionChoice.ItemsSource = images;
        EditionChoice.SelectedIndex = ImageDefaults.SelectPosition(images, restored);
        ShowNotice(UiText.Get("Image.Inspected"), UiText.Format("Image.EditionsFound", images.Count, image?.Name) +
            (SkippedImageTools ? " " + UiText.Get("Tools.TrustedFallback") : ""),
            SkippedImageTools ? InfoBarSeverity.Warning : InfoBarSeverity.Informational,
            SkippedImageTools ? NoticeAction.CopyDetails : NoticeAction.None,
            SkippedImageTools ? string.Join("\n", servicingDiagnostics) : null);
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
        Working("Downloading from Microsoft…");
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
            ShowNotice(downloaded.VerifiedAgainstPublishedHash ? "Official ISO downloaded and hash-verified." : "Microsoft-hosted ISO downloaded.",
                downloaded.VerifiedAgainstPublishedHash ? "The file matches the supplied published SHA-256. Inspect it to select an edition." :
                "No published hash was supplied. The computed hash is shown, but authenticity has not been independently verified.", InfoBarSeverity.Informational);
        }
        finally { EndDownloadText(); }
    });

    private async void OnDownloadLatest(object sender, RoutedEventArgs args) => await RunOperation(OperationKind.Download, async token =>
    {
        Working("Finding the latest Windows 11…");
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
                Working("Checking the downloaded ISO…");
                await using var input = File.OpenRead(path);
                await IsoStructure.ValidateAsync(input, token);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
                if (actual != (await File.ReadAllTextAsync(hashFile, token)).Trim() ||
                    (media.Sha256 is not null && actual != media.Sha256))
                    throw new InvalidDataException("The cached ISO has changed. It has not been accepted.");
                DownloadStatus.Text = UiText.FromEnglish("Previously downloaded ISO verified.");
            }
            else
            {
                if (File.Exists(path)) path = Path.Combine(folder, Path.GetFileNameWithoutExtension(media.FileName) + "-" + Guid.NewGuid().ToString("N")[..8] + ".iso");
                Working("Downloading from Microsoft…");
                using var downloadClient = OfficialImageDownloader.CreateClient();
                BeginDownloadText(p => FormatDownload(p, $"{media.Language} · "));
                var download = await new OfficialImageDownloader(downloadClient).DownloadAsync(media.Url.AbsoluteUri,
                    path, media.Sha256, downloadUpdates, token, status: catalogProgress);
                DrainDownload();
                await File.WriteAllTextAsync(path + ".sha256", download.Sha256, token);
                DownloadStatus.Text = $"Downloaded {media.Description}. SHA-256: {download.Sha256}" +
                    (download.VerifiedAgainstPublishedHash ? " (published hash verified)" : " (computed hash; Microsoft supplied no published hash)");
            }
            SourcePathBox.Text = path;
            await InspectSourceAsync(token);
        }
        finally { EndDownloadText(); }
    });

    private async void OnAddUpdatePackage(object sender, RoutedEventArgs args)
    {
        if (busy || modalOpen) return;
        await RunOperation(OperationKind.Choose, async _ =>
        {
            var path = await PickFile(".msu", ".cab");
            if (path is null) return;
            updatePackages.Add(path);
            UpdatePackagesList.SelectedIndex = updatePackages.Items.Count - 1;
            UpdatePackageButtons();
        });
    }

    private void OnUpdatePackageSelectionChanged(object sender, SelectionChangedEventArgs args) => UpdatePackageButtons();

    private void OnRemoveUpdatePackage(object sender, RoutedEventArgs args)
    {
        if (busy || modalOpen) return;
        var index = UpdatePackagesList.SelectedIndex;
        if (updatePackages.Remove(index)) UpdatePackagesList.SelectedIndex = Math.Min(index, updatePackages.Items.Count - 1);
        UpdatePackageButtons();
    }

    private void OnMoveUpdatePackage(object sender, RoutedEventArgs args)
    {
        if (busy || modalOpen || sender is not Button { Tag: string direction }) return;
        var index = UpdatePackagesList.SelectedIndex;
        var offset = direction == "Up" ? -1 : 1;
        if (updatePackages.Move(index, offset)) UpdatePackagesList.SelectedIndex = index + offset;
        UpdatePackageButtons();
    }

    private void UpdatePackageButtons()
    {
        if (UpdatePackagesList is null || RemoveUpdatePackageButton is null) return;
        var index = UpdatePackagesList.SelectedIndex;
        RemoveUpdatePackageButton.IsEnabled = !busy && index >= 0;
        MoveUpdateUpButton.IsEnabled = !busy && index > 0;
        MoveUpdateDownButton.IsEnabled = !busy && index >= 0 && index < updatePackages.Items.Count - 1;
        UpdatePackageCount.Text = UiText.Format("Updates.SelectionCount", updatePackages.Items.Count);
    }

    private async void OnExportInventory(object sender, RoutedEventArgs args)
    {
        if (busy || modalOpen || inventory.Count == 0) return;
        var allItems = inventory.Select(choice => choice.Item).ToArray();
        await RunOperation(OperationKind.Choose, async token =>
        {
            var path = await SaveFile("tiny11-components", ".txt");
            if (path is null) return;
            await File.WriteAllTextAsync(path, InventoryTextExport.Create(allItems), new UTF8Encoding(true), token);
            ShowNotice(UiText.Get("Inventory.Exported"), UiText.Format("Inventory.ExportComplete", allItems.Length, path));
        });
    }

    private void SetLanguageChoices()
    {
        changingLanguage = true;
        var choices = new[] { new LanguageInfo("system", UiText.Get("Language.System")) }.Concat(languageCatalogs.Available).ToArray();
        LanguageChoice.ItemsSource = choices;
        LanguageChoice.SelectedItem = choices.First(choice => choice.Id.Equals(languagePreference, StringComparison.OrdinalIgnoreCase));
        changingLanguage = false;
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!initialized || changingLanguage || LanguageChoice.SelectedItem is not LanguageInfo selected) return;
        if (busy || modalOpen) { SetLanguageChoices(); return; }
        var previousLanguage = UiText.Current;
        TextBlock[] statefulLabels = [ImageBadge, LatestDownloadDescription, DownloadStatus, BuildHeading, CurrentStage, CurrentMessage, ToolStatus];
        var previousLabels = statefulLabels.Select(label => (Label: label, Text: label.Text)).ToArray();
        var previousNoticeTitle = Notice.Title;
        var previousNoticeMessage = Notice.Message;
        languagePreference = selected.Id;
        UiText.Select(languageCatalogs.Resolve(languagePreference, CultureInfo.CurrentUICulture));
        Root.Language = UiText.Current.Info.Id;
        foreach (var (label, text) in previousLabels) label.Text = UiText.Relocalize(previousLanguage, text);
        Notice.Title = UiText.Relocalize(previousLanguage, previousNoticeTitle);
        Notice.Message = UiText.Relocalize(previousLanguage, previousNoticeMessage);
        SupportPurpose.Text = ProjectSupport.Purpose;
        ImageBadge.Text = image is null ? UiText.Get("Image.NoneSelected") : UiText.Format("Image.Badge", image.Name, image.Architecture, image.Version.Build);
        if (LatestDownloadDescription.Text == previousLanguage.Format("Download.DefaultDescription", HostArchitecture.ToUpperInvariant(), CultureInfo.CurrentUICulture.NativeName))
            LatestDownloadDescription.Text = UiText.Format("Download.DefaultDescription", HostArchitecture.ToUpperInvariant(), CultureInfo.CurrentUICulture.NativeName);
        SetLanguageChoices();
        foreach (var choice in options) choice.RefreshLanguage();
        foreach (var choice in inventory) choice.RefreshLanguage();
        board.RefreshLanguage();
        UpdateModePresentation();
        FilterOptions();
        FilterInventory();
        UpdateCounts();
        UpdatePackageButtons();
        HistoryCount.Text = StageSummary.Describe(board);
        if (step == 4) UpdateReview();
        if (step == 5) UpdateBuildDescription(CreateRecipe());
        if (completed && result is { } built)
        {
            BuildHeading.Text = UiText.FromEnglish("ISO created");
            CurrentStage.Text = UiText.Get("Progress.Complete");
            OverallStep.Text = UiText.FromEnglish("All stages done");
            RenderResultMetrics(built, resultResumed, resultTurbo, resultUpdateCount);
        }
        else if (stoppedRun is { } stopped)
            BuildHeading.Text = UiText.Format(stoppedWasCancelled ? "Build.CancelledHeading" : "Build.StoppedHeading",
                UiText.FromEnglish(stopped == OperationKind.Resume ? "Resume" : "Build"));
        if (!completed && activePhase >= 0) OverallStep.Text = UiText.Format("Progress.StageCount", activePhase + 1, board.Stages.Length);
        try { new LanguagePreference(languagePreference).Write(Path.Combine(dataDirectory, "ui-language.json")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ShowNotice(UiText.Get("Language.NotSaved"), ErrorText.Reason(error), InfoBarSeverity.Warning); }
        if (UiText.Current.MissingKeys.Count > 0)
            ShowNotice(UiText.Get("Language.IncompleteTitle"), UiText.Format("Language.IncompleteBody", UiText.Current.MissingKeys.Count));
    }

    private string FindServicingTool()
    {
        servicingDiagnostics.Clear();
        return ToolLocator.FindDism(message =>
        {
            servicingDiagnostics.Add(message);
            liveLog.Add("[Tools] " + message);
        });
    }

    private bool SkippedImageTools => servicingDiagnostics.Any(message => message.StartsWith("Skipped installed DISM", StringComparison.Ordinal));

    private async Task EnsureToolsAsync(CancellationToken token)
    {
        servicingTool = FindServicingTool();
        using var client = OfficialImageDownloader.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        isoTool = await new OscdimgAcquirer(client).EnsureAsync(Path.Combine(dataDirectory, "tools"),
            new UiProgress<string>(text => DispatcherQueue.TryEnqueue(() => ToolStatus.Text = text)), token);
        ToolStatus.Text = UiText.FromEnglish("Image and ISO tools ready.") + (SkippedImageTools ? " " + UiText.Get("Tools.TrustedFallback") : "");
        ToolTipService.SetToolTip(ToolStatus, string.Join("\n", servicingDiagnostics));
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
        NativePayloadConsent.IsChecked = false;
        recipeIsoSizeLimit = null;
        ShrinkBootCheck.IsChecked = false;
        applyingMode = false;
        UpdateModePresentation();
        UpdateCounts();
    }
    private void UpdateModePresentation()
    {
        CustomChoices.Visibility = SelectedMode == BuildMode.Custom ? Visibility.Visible : Visibility.Collapsed;
        CustomMediaSettings.Visibility = SelectedMode == BuildMode.Custom ? Visibility.Visible : Visibility.Collapsed;
        PresetPreview.Visibility = SelectedMode == BuildMode.Custom ? Visibility.Collapsed : Visibility.Visible;
        var presetOptions = PresetRemovalPreview.ForMode(SelectedMode);
        var presetRemovesEdgeFiles = presetOptions.Any(option => option.Id == "edge-files");
        PresetOptionsList.ItemsSource = presetOptions.Select(option => new OptionChoice(option, presetRemovesEdgeFiles)).ToArray();
        PresetCoreDetails.Visibility = UsesCore ? Visibility.Visible : Visibility.Collapsed;
        CoreWarning.IsOpen = UsesCore;
        CoreConsentStatus.Visibility = UsesCore ? Visibility.Visible : Visibility.Collapsed;
        CoreConsentStatus.Text = UiText.Get(coreAcknowledged ? "Core.Accepted" : "Core.WillAsk");
        ModeDescription.Text = UiText.FromEnglish(SelectedMode switch
        {
            BuildMode.Tiny11 => "Original Tiny11 removals, including Edge/WebView2 and OneDrive binaries. Windows Update, Defender and recovery are kept. Choose Custom to change individual removals.",
            BuildMode.Tiny11Core => "Tiny11 Core removals, including irreversible component pruning. Choose Custom to change individual removals.",
            _ => "Start with nothing selected, then choose what to remove or disable. You can also list individual components, including protected items."
        });
        SourceMode.Text = UiText.Format("Mode.Selected", (ModeChoice.SelectedItem as ComboBoxItem)?.Content);
        MarkChoice(Tiny11Selection, SelectedMode == BuildMode.Tiny11);
        MarkChoice(CoreSelection, SelectedMode == BuildMode.Tiny11Core);
        MarkChoice(CustomSelection, SelectedMode == BuildMode.Custom);
    }

    // The chosen card gets an accent outline and is announced as selected; unchosen cards keep full-strength text.
    private static void MarkChoice(Button card, bool selected)
    {
        card.Style = (Style)Application.Current.Resources[selected ? "ModeCardSelected" : "ModeCard"];
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(card, UiText.Get(selected ? "Common.Selected" : "Common.NotSelected"));
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
        var category = (OptionFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        OptionsList.ItemsSource = options.Where(o => category.Length == 0 || o.Option.Category == category).ToArray();
    }
    private void UpdateCounts()
    {
        if (!initialized) return;
        var custom = inventory.Count > 0 ? inventory.Count(i => i.Selected) : pendingActions.Length;
        SelectionCount.Text = UiText.Format("Removals.SelectionCount", options.Count(o => o.Selected), custom);
        ExpertCount.Text = UiText.Format("Inventory.Count", inventory.Count, custom);
        var directGroups = SelectedMode == BuildMode.Custom && options.Any(option => option.Selected && option.Option.CustomOnly);
        NativePayloadWarning.IsOpen = directGroups;
        NativePayloadConsent.Visibility = directGroups ? Visibility.Visible : Visibility.Collapsed;
        RecipeLimitPanel.Visibility = SelectedMode == BuildMode.Custom && recipeIsoSizeLimit is not null ? Visibility.Visible : Visibility.Collapsed;
        RecipeLimitText.Text = recipeIsoSizeLimit is { } limit ? UiText.Format("CustomMedia.ReviewLimit", limit) : "";
        ExportInventoryButton.IsEnabled = !busy && inventory.Count > 0;
        UpdateNavigation();
    }

    private async void OnOpenExpert(object sender, RoutedEventArgs args)
    {
        if (busy || modalOpen || SelectedMode != BuildMode.Custom) return;
        if (image is null) { ShowNotice("Inspect an image first.", "Select an edition before listing its components.", InfoBarSeverity.Warning); return; }
        if (!await Confirm("Protected components are included",
            "This inventory includes boot drivers, security, licensing, system apps and component payloads. Removing them can make Windows unbootable, insecure or impossible to update. Protected packages may refuse removal. Direct payload removal is unsupported.\n\nContinue only for isolated testing, with backups and a known-good installer.",
            "Inspect components")) return;
        await RunOperation(OperationKind.Inventory, async token =>
        {
            Working("Listing image components…");
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
                ShowNotice("Component list loaded read-only.", "The installed tools could list this image, but building it requires newer " +
                    "Windows ADK Deployment Tools. Install them and reopen the app before building.",
                    InfoBarSeverity.Warning, NoticeAction.OpenTools);
            else
                ShowNotice("Component list loaded.", "Existing selections were preserved. Check the individual items you want to remove or disable.");
        });
    }

    private void ClearInventory(bool preserveSelections = false)
    {
        var retained = preserveSelections ? (inventory.Count > 0 ?
            inventory.Where(item => item.Selected).Select(item => item.Action).ToArray() : pendingActions) : [];
        inventory = [];
        pendingActions = retained;
        InventoryList.ItemsSource = null;
        ExpertPanel.Visibility = retained.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExpertConsent.IsChecked = false;
        NativePayloadConsent.IsChecked = false;
        coreAcknowledged = false;
        UpdateCounts();
    }
    private void OnInventoryFilterChanged(object sender, TextChangedEventArgs args) { if (initialized) FilterInventory(); }
    private void OnInventoryFilterSelection(object sender, SelectionChangedEventArgs args) { if (initialized) FilterInventory(); }
    private void FilterInventory()
    {
        var filter = (InventoryFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        var query = InventorySearch.Text.Trim();
        InventoryList.ItemsSource = inventory.Where(i => (filter.Length == 0 || i.Item.Kind.ToString() == filter) &&
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
        void Row(string key, object? value) => text.AppendLine(UiText.Format(key, value));
        Row("Review.Image", image?.Summary ?? UiText.Get("Image.NotInspected"));
        Row("Review.Mode", (ModeChoice.SelectedItem as ComboBoxItem)?.Content);
        if (SelectedMode == BuildMode.Custom) Row("Review.CoreRemovals", YesNo(UsesCore));
        if (recipe.IsoSizeLimitBytes is { } limit) Row("CustomMedia.ReviewLimit", limit);
        Row("Review.Source", recipe.SourcePath);
        Row("Review.Output", recipe.OutputPath);
        Row("Review.Workspace", recipe.WorkspacePath);
        Row("Review.ImageTool", recipe.DismPath);
        Row("Review.IsoTool", recipe.OscdimgPath);
        Row("Review.Compression", CompressionLabel(recipe.Compression));
        Row("Review.Turbo", OnOff(recipe.TurboMode));
        Row("Review.RedundantData", UiText.Get(recipe.RemoveRedundantData ? "Common.Remove" : "Common.Keep"));
        Row("Review.SetupPayload", UiText.Get(recipe.OmitNetFx3SetupPayload ? "Common.Omit" : "Common.Keep"));
        Row("Review.Compact", OnOff(recipe.CompactInstallation));
        Row("Review.NetFx", YesNo(recipe.EnableNetFx3));
        Row("Review.LocalAccount", UiText.Get(recipe.PreferLocalAccount ? "Common.Preferred" : "Common.Default"));
        if (SetupLabels.Legacy(recipe) is { } legacy) text.AppendLine(legacy);
        text.AppendLine();
        text.AppendLine(UpdatePackagesSummary(recipe));
        text.AppendLine();
        text.AppendLine(UiText.Get("Review.RemoveDisable"));
        foreach (var option in options.Where(option => option.Selected)) text.AppendLine("  " + option.Title);
        foreach (var action in recipe.ExpertActions) text.AppendLine($"  {action.Mode} {action.Kind}: {action.Identity}");
        text.AppendLine();
        text.AppendLine(UiText.Get("Review.UnselectedKept"));
        if (recipe.Flavor == BuildFlavor.Core) text.AppendLine(UiText.Get("Review.CoreWarning"));
        if (recipe.ExpertActions.Length > 0) text.AppendLine(UiText.Get("Review.ExpertWarning"));
        text.AppendLine(UiText.Get("Review.PolicyWarning"));
        return text.ToString();
    }

    private static string OnOff(bool value) => UiText.Get(value ? "Common.On" : "Common.Off");
    private static string YesNo(bool value) => UiText.Get(value ? "Common.Yes" : "Common.No");
    private static string CompressionLabel(Compression value) => UiText.Get(value switch
    {
        Compression.Recovery => "Compression.Recovery", Compression.Maximum => "Compression.Maximum", _ => "Compression.Fast"
    });

    private static string UpdatePackagesSummary(BuildRecipe recipe) => UiText.Format("Updates.ReviewCount", recipe.UpdatePackages.Length) +
        (recipe.UpdatePackages.Length == 0 ? "" : "\n" + string.Join("\n", recipe.UpdatePackages.Select((path, index) => $"{index + 1}. {Path.GetFileName(path)}")));

    private void UpdateReview()
    {
        var recipe = CreateRecipe();
        ReviewText.Text = CreateReview();
        ReviewImage.Text = image?.Summary ?? UiText.Get("Image.NoneSelected");
        ReviewMode.Text = (ModeChoice.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        ReviewRemovals.Text = UiText.Format("Review.RemovalCounts", recipe.SelectedOptions.Length, recipe.ExpertActions.Length) + "\n" +
            UiText.Get(UsesCore ? "Review.CoreEffects" : SelectedMode == BuildMode.Custom ? "Review.CustomEffects" : "Review.RegularEffects");
        ReviewPaths.Text = UiText.Format("Review.Paths", recipe.OutputPath, recipe.WorkspacePath);
        ReviewOptions.Text = UiText.Format("Review.Options", CompressionLabel(recipe.Compression), OnOff(recipe.TurboMode),
            UiText.Get(recipe.RemoveRedundantData ? "Common.Remove" : "Common.Keep"),
            UiText.Get(recipe.OmitNetFx3SetupPayload ? "Common.Omit" : "Common.Keep"), OnOff(recipe.CompactInstallation),
            UiText.Get(recipe.EnableNetFx3 ? "Common.Included" : "Common.NotIncluded"),
            UiText.Get(recipe.PreferLocalAccount ? "Common.Preferred" : "Common.Default"),
            UiText.Get(recipe.KeepWorkingFiles ? "Common.Keep" : "Common.CleanAfterBuild")) +
            (SetupLabels.Legacy(recipe) is { } legacy ? "\n" + legacy : "") + "\n\n" + UpdatePackagesSummary(recipe) +
            "\n" + CustomMediaSummary(recipe);
    }

    private const string BuildDescriptionText = "The selected edition will be copied, modified, compressed and saved as a bootable ISO.";

    // The legacy installer is derived from the removals, never chosen, so this only reports it.
    private void UpdateBuildDescription(BuildRecipe recipe) => BuildDescription.Text =
        UiText.FromEnglish(BuildDescriptionText) +
        (SetupLabels.LegacyReason(recipe) is { } reason ? UiText.Format("Build.LegacyDescription", reason) : "") +
        "\n" + CustomMediaSummary(recipe);

    private async Task StartBuildAsync()
    {
        BuildRecipe recipe;
        try { recipe = CreateRecipe(); recipe.ValidateOptions(); PathSafety.Validate(recipe); }
        catch (Exception error) { ShowNotice("The recipe is not ready.", ErrorText.Reason(error), InfoBarSeverity.Error); return; }
        if (!await Confirm("Create this offline Windows image?",
            UiText.Format("Build.Confirmation", recipe.OutputPath, (ModeChoice.SelectedItem as ComboBoxItem)?.Content,
                recipe.SelectedOptions.Length, recipe.ExpertActions.Length) + "\n" + UpdatePackagesSummary(recipe) +
            (SetupLabels.Legacy(recipe) is { } legacy ? "\n" + legacy : "") + "\n" + CustomMediaSummary(recipe),
            "Build")) return;
        await RunOperation(OperationKind.Build, async token =>
        {
            ResetBuildPage();
            BuildActivity.IsActive = true;
            BuildBar.IsIndeterminate = true;
            CurrentMessage.Text = UiText.FromEnglish("Preparing image tools…");
            await EnsureToolsAsync(token);
            recipe = CreateRecipe();
            BeginBuildRun("Building");
            var reporter = BuildProgressReporter();
            var built = await Task.Run(() => engine.BuildAsync(recipe, reporter, token), token);
            ShowBuildResult(built, resumed: false, turbo: recipe.TurboMode, updateCount: recipe.UpdatePackages.Length);
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
        foreach (var diagnostic in servicingDiagnostics) liveLog.Add("[Tools] " + diagnostic);
        RefreshLog(force: true);
        BuildHeading.Text = UiText.FromEnglish("Build");
        CurrentStage.Text = UiText.FromEnglish("Waiting to start");
        CurrentMessage.Text = UiText.FromEnglish("No changes have been made.");
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
        BuildHeading.Text = UiText.FromEnglish(heading);
        BuildActivity.IsActive = true;
        BuildBar.IsIndeterminate = true;
    }

    private void ShowBuildResult(BuildResult built, bool resumed, bool turbo, int updateCount)
    {
        FlushBuildProgress(flushAll: true);
        result = built;
        workDirectory = built.WorkDirectory;
        ResultPath.Text = built.IsoPath;
        ResultHash.Text = built.Sha256;
        resultResumed = resumed;
        resultTurbo = turbo;
        resultUpdateCount = updateCount;
        RenderResultMetrics(built, resumed, turbo, updateCount);
        var warnings = built.Warnings;
        // A cancel prompt open for this run reports the warning too, instead of calling the end clean.
        if (warnings.Count > 0)
            active?.NoteWarning(UiText.FromEnglish("The ISO was saved with warnings. See the warnings on the page."));
        ResultWarnings.IsOpen = warnings.Count > 0;
        if (warnings.Count > 0)
            ResultWarnings.Message = string.Join("\n", warnings.Take(4).Select(w => "• " + ErrorText.Summary(w, 200))) +
                (warnings.Count > 4 ? "\n• " + UiText.Format("Result.MoreWarnings", warnings.Count - 4) : "");
        ResultPanel.Visibility = Visibility.Visible;
        BuildHeading.Text = UiText.FromEnglish("ISO created");
        CurrentStage.Text = UiText.FromEnglish("Complete");
        BuildActivity.IsActive = false;
        BuildGlyph.Glyph = "\uE73E";
        if (resumed) board.MarkEarlierPhasesDone();
        board.Complete();
        OverallBar.Value = 100;
        BuildPercent.Text = "100%";
        OverallStep.Text = UiText.FromEnglish("All stages done");
        CurrentPercent.Text = "100%";
        HistoryCount.Text = StageSummary.Describe(board);
        BuildBar.IsIndeterminate = false;
        BuildBar.Value = 100;
        CurrentMessage.Text = UiText.FromEnglish(warnings.Count > 0 ?
            "The ISO was saved with warnings. See the skipped work and other details below." :
            "The ISO and build report have been saved. Test the ISO in a VM before use.");
        stoppedRun = null;
        stoppedHasCheckpoint = false;
        completed = true;
    }

    private void RenderResultMetrics(BuildResult built, bool resumed, bool turbo, int updateCount)
    {
        var size = File.Exists(built.IsoPath) ? UiText.Format("Result.IsoSize", new FileInfo(built.IsoPath).Length / (1024d * 1024 * 1024)) : "";
        var metrics = new List<string>
        {
            UiText.Format("Result.Duration", UiText.Get(resumed ? "Result.ResumedBuild" : turbo ? "Result.TurboBuild" : "Result.StandardBuild"),
                built.Duration.TotalMinutes, resumed ? UiText.Get("Result.InTotal") : "", size)
        };
        if (!resumed || built.RemovedLogicalBytes > 0 || built.OmittedSetupBytes > 0)
            metrics.Add(UiText.Format("Result.RemovedData", built.RemovedLogicalBytes / (1024d * 1024), built.OmittedSetupBytes / (1024d * 1024)));
        if (updateCount > 0) metrics.Add(UiText.Format("Result.UpdateResults", updateCount));
        if (built.IsoSizeLimitBytes is { } budget && File.Exists(built.IsoPath))
            metrics.Add(UiText.Format("CustomMedia.ResultSize", new FileInfo(built.IsoPath).Length, budget));
        metrics.Add(UiText.Get("Result.TimingsSaved"));
        ResultMetrics.Text = string.Join("\n", metrics);
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
                    if (running is OperationKind.Build or OperationKind.Resume) CurrentMessage.Text = UiText.Format("Cancel.RequestedDetail", UiText.FromEnglish(p.Message));
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
                CurrentStage.Text = UiText.FromEnglish(p.Stage);
                // Once cancellation has been requested, that status stays until the run actually stops.
                if (!cancelRequested)
                    CurrentMessage.Text = p.State == BuildOperationState.Failed ?
                        UiText.Format("Progress.OperationFailed", UiText.FromEnglish(p.Message)) : UiText.FromEnglish(p.Message);
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
                OverallStep.Text = UiText.Format("Progress.StageCount", phase + 1, board.Stages.Length);
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
            CurrentMessage.Text = UiText.FromEnglish("Cancellation requested. Waiting for the current operation to finish…");
        Working("Cancelling…");
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
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(bar, UiText.Get("Cancel.Progress"));
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
                dialog.Title = UiText.FromEnglish(view.Title);
                var errored = view.Tone == CancelTone.Error;
                body.Text = UiText.FromEnglish(view.Body);
                body.Visibility = view.Body.Length > 0 && !errored ? Visibility.Visible : Visibility.Collapsed;
                // An outcome that needs attention is the notice itself; cleanup problems beside a clean end are its warning.
                var message = errored ? string.Join(" ", new[] { view.Body, view.Warning }.Where(text => text.Length > 0)) : view.Warning;
                notice.Severity = errored ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
                notice.Message = UiText.FromEnglish(message);
                notice.IsOpen = message.Length > 0;
                Show(status, view.Status);
                Show(detail, view.Detail);
                Show(footnote, view.Footnote);
            });
            Safely("The cancel dialog could not update its buttons.", () =>
            {
                dialog.PrimaryButtonText = UiText.FromEnglish(view.ConfirmText ?? "");
                dialog.SecondaryButtonText = UiText.FromEnglish(view.LinkText);
                dialog.CloseButtonText = UiText.FromEnglish(view.CloseText ?? "");
            });
        }

        private static void Show(TextBlock block, string text)
        {
            block.Text = UiText.FromEnglish(text);
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
                () => dialog.SecondaryButtonText = UiText.Get(copy ? done ? "Common.Copied" : "Common.CopyFailed" : done ? "Common.OpenLogs" : "Common.OpenLogsFailed"));
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
        if (path is not null) { recipe.Write(path); ShowNotice("Recipe saved.", "It contains selections and local paths, not downloaded media or credentials."); }
    });
    private async void OnLoadRecipe(object sender, RoutedEventArgs args) => await LoadRecipeAsync(useCustom: false);

    private Task LoadRecipeAsync(bool useCustom) => RunOperation(OperationKind.Recipe, async _ =>
    {
        var path = await PickFile(".json");
        if (path is null) return;
        var recipe = RecipeSelection.Read(path, useCustom);
        RestoreRecipe(recipe);
        ShowNotice("Recipe loaded.", "Inspect the source again. Core/expert risk acknowledgements must be renewed.");
    });

    private void RestoreRecipe(BuildRecipe recipe)
    {
        var portable = string.IsNullOrWhiteSpace(recipe.SourcePath);
        var priorIndex = image?.Index;
        recipe = RecipeSelection.BindPaths(recipe, CreateRecipe());
        ClearInventory();
        SourcePathBox.Text = recipe.SourcePath;
        WorkspaceBox.Text = recipe.WorkspacePath;
        OutputBox.Text = recipe.OutputPath;
        servicingTool = FindServicingTool();
        applyingMode = true;
        ModeChoice.SelectedIndex = (int)CleanupCatalog.ModeForRecipe(recipe);
        CustomCoreCheck.IsOn = recipe.Flavor == BuildFlavor.Core && SelectedMode == BuildMode.Custom;
        RecipeSelection.ApplyGroups(options, recipe.SelectedOptions);
        applyingMode = false;
        CompressionChoice.SelectedIndex = (int)recipe.Compression;
        CompactCheck.IsOn = recipe.CompactInstallation;
        NetFxCheck.IsOn = recipe.EnableNetFx3;
        LocalAccountCheck.IsOn = recipe.PreferLocalAccount;
        KeepFilesCheck.IsOn = recipe.KeepWorkingFiles;
        TurboCheck.IsOn = recipe.TurboMode;
        RedundantDataCheck.IsOn = recipe.RemoveRedundantData;
        OmitSetupCheck.IsOn = recipe.OmitNetFx3SetupPayload;
        updatePackages.Restore(recipe.UpdatePackages);
        UpdatePackageButtons();
        pendingActions = recipe.ExpertActions;
        pendingImageIndex = portable ? priorIndex : recipe.ImageIndex;
        pendingSourcePath = recipe.SourcePath;
        recipeIsoSizeLimit = recipe.IsoSizeLimitBytes;
        ShrinkBootCheck.IsChecked = recipe.ShrinkBootImage;
        NativePayloadConsent.IsChecked = false;
        if (pendingActions.Length > 0) ExpertPanel.Visibility = Visibility.Visible;
        coreAcknowledged = false;
        ExpertConsent.IsChecked = false;
        image = null;
        EditionChoice.ItemsSource = null;
        ImageBadge.Text = UiText.Get("Image.NoneSelected");
        OptionFilter.SelectedIndex = 0;
        FilterOptions();
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
            ShowNotice("Administrator privileges required.", "Close and reopen Tiny11Builder.exe with administrator approval to recover a workspace.", InfoBarSeverity.Warning);
            return;
        }
        var path = await PickFile(".json");
        if (path is null) return;
        // Advisory only: the engine decides. This just avoids offering a discard it would refuse.
        if (JobCheckpoint.Exists(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ""))
        {
            if (await Confirm("This job has saved progress",
                "Recover discards a job's working copy, and it does not do that for a job with saved progress. " +
                "Resume continues the build from that progress instead.\n\nNothing has been changed.", "Resume instead"))
                await ResumeJobAsync(path);
            return;
        }
        if (!await Confirm("Discard this interrupted workspace?",
            "Select only job.json from the interrupted tiny11 job. This discards that job's mounted working copy and removes its staged media. " +
            "Its logs, recipe, report and your source ISO are kept. A job whose image was already committed has saved progress and is never discarded here; " +
            "use Resume build for it. Active builds cannot be recovered.",
            "Discard working copy")) return;
        await RunOperation(OperationKind.Recover, async token =>
        {
            Working("Recovering workspace…");
            var dism = FindServicingTool();
            ToolLocator.Validate(dism, "dism.exe");
            await Task.Run(() => engine.RecoverAsync(path, dism, token), token);
            ShowNotice("Workspace recovered.", "The interrupted working copy was discarded. Your source and logs were preserved.");
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
            ShowNotice("This build can't be resumed.", candidate.Problem ?? ResumeSupport.NoCheckpoint, InfoBarSeverity.Warning);
            return;
        }
        if (!ToolLocator.IsAdministrator)
        {
            ShowNotice("Administrator privileges required.", "Close and reopen Tiny11Builder.exe with administrator approval, then choose Resume build. Nothing was changed.", InfoBarSeverity.Warning);
            return;
        }
        if (!await Confirm("Resume this build?", ResumeSupport.Describe(original), "Resume")) return;
        var recipe = original;
        if (File.Exists(recipe.OutputPath))
        {
            var picked = await SaveFile(Path.GetFileNameWithoutExtension(recipe.OutputPath), ".iso");
            if (picked is null)
            {
                ShowNotice("Resume not started.", "The output file already exists. Choose a new file name to continue.", InfoBarSeverity.Warning);
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
            CurrentMessage.Text = UiText.FromEnglish("Preparing image tools…");
            await EnsureToolsAsync(token);
            recipe = recipe with { DismPath = servicingTool, OscdimgPath = isoTool };
            new BuildHost().ValidateTools(recipe);
            BeginBuildRun("Resuming");
            var reporter = BuildProgressReporter();
            var built = await Task.Run(() => engine.ResumeAsync(candidate.JobFile, recipe, reporter, token), token);
            ShowBuildResult(built, resumed: true, turbo: recipe.TurboMode, updateCount: recipe.UpdatePackages.Length);
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
            ShowNotice(UiText.Get("Logs.FolderGone"), UiText.Format("Logs.FolderMissing", workDirectory), InfoBarSeverity.Warning);
            return;
        }
        OpenFolder(workDirectory);
    }
    private void OpenFolder(string path)
    {
        try { using var process = Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = $"\"{path}\"" }); }
        catch (Exception error) { ShowNotice("Folder could not be opened.", ErrorText.Reason(error), InfoBarSeverity.Error); }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs args) => ApplyTheme();

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
        ToolTipService.SetToolTip(ThemeChoice, contrast ? UiText.FromEnglish("Windows high contrast is on, so colors follow your system settings.") : null);
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
