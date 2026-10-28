namespace Tiny11Builder.Core;

public static class CleanupCatalog
{
    public static IReadOnlyList<CleanupOption> Options { get; } =
    [
        App("copilot-app", "Copilot apps", "Remove preinstalled Copilot apps. Keep shared runtimes and shell components.", true, true,
            "Microsoft.Copilot", "Microsoft.Windows.Copilot", "Microsoft.549981C3F5F10"),
        App("outlook", "New Outlook and Mail", "Remove Outlook and the retired Mail / Calendar apps.", true, true,
            "Microsoft.OutlookForWindows", "microsoft.windowscommunicationsapps"),
        App("teams", "Teams", "Remove personal Teams clients.", true, true, "MSTeams", "MicrosoftTeams", "Microsoft.Windows.Teams"),
        App("xbox", "Xbox and gaming overlays", "Remove Xbox apps and Game Bar. Gaming sign-in and recording may be unavailable.", true, true,
            "Microsoft.GamingApp", "Microsoft.XboxApp", "Microsoft.Xbox.TCUI", "Microsoft.XboxGameOverlay",
            "Microsoft.XboxGamingOverlay", "Microsoft.XboxIdentityProvider", "Microsoft.XboxSpeechToTextOverlay"),
        App("news-weather", "News, weather and Bing apps", "Remove packaged feed apps; local Windows Search is preserved.", true, true,
            "Microsoft.BingNews", "Microsoft.BingWeather", "Microsoft.BingSearch"),
        App("office-promos", "Microsoft 365 promotions", "Remove Office Hub and Office push-notification promotional apps.", true, true,
            "Microsoft.MicrosoftOfficeHub", "Microsoft.OfficePushNotificationUtility"),
        App("clipchamp", "Clipchamp", "Remove the bundled video editor.", true, true, "Clipchamp.Clipchamp"),
        App("solitaire", "Solitaire", "Remove bundled games.", true, true, "Microsoft.MicrosoftSolitaireCollection"),
        App("phone", "Phone Link and cross-device apps", "Remove phone integration apps. Cross-device features may stop working.", true, true,
            "Microsoft.YourPhone", "Microsoft.Windows.CrossDevice"),
        App("feedback", "Feedback, tips and help", "Remove Feedback Hub, Get Help, and introductory apps.", true, true,
            "Microsoft.WindowsFeedbackHub", "Microsoft.GetHelp", "Microsoft.Getstarted"),
        App("devhome", "Dev Home and Power Automate", "Remove Dev Home and Power Automate Desktop.", true, true,
            "Microsoft.Windows.DevHome", "Microsoft.PowerAutomateDesktop"),
        App("social", "People, To Do, Family and Skype", "Remove optional organizer and communication apps.", true, true,
            "Microsoft.People", "Microsoft.Todos", "Microsoft.SkypeApp", "MicrosoftCorporationII.MicrosoftFamily"),
        App("maps", "Maps and Mixed Reality", "Remove retired Maps and Mixed Reality clients.", true, true,
            "Microsoft.WindowsMaps", "Microsoft.MixedReality.Portal"),
        App("media", "Media Player and Movies", "Remove bundled media playback apps; codecs and shared frameworks remain.", false, true,
            "Microsoft.ZuneMusic", "Microsoft.ZuneVideo"),
        App("utilities", "Clock, Recorder and Sticky Notes", "Remove optional personal utilities.", false, true,
            "Microsoft.WindowsAlarms", "Microsoft.WindowsSoundRecorder", "Microsoft.MicrosoftStickyNotes"),
        App("quickassist", "Quick Assist", "Remove Microsoft's remote-assistance client.", false, true, "MicrosoftCorporationII.QuickAssist"),
        App("paint", "Paint, 3D Viewer and OneNote", "Remove Paint, 3D Viewer and OneNote. Leave unchecked to keep them.", false, false,
            "Microsoft.Paint", "Microsoft.MSPaint", "Microsoft.Microsoft3DViewer", "Microsoft.Office.OneNote"),
        App("camera", "Camera", "Remove the Camera app, not camera drivers.", false, false, "Microsoft.WindowsCamera"),
        App("terminal", "Windows Terminal", "Remove Terminal. PowerShell and Command Prompt remain.", false, false, "Microsoft.WindowsTerminal"),
        App("oem", "OEM promotional apps", "Remove Intel management status and Dolby promotional applications, not drivers.", false, false,
            "AppUp.IntelManagementandSecurityStatus", "DolbyLaboratories.DolbyAccess", "DolbyLaboratories.DolbyDigitalPlusDecoderOEM"),
        new("edge-files", L10n.T("edge-files.Title", "Remove Edge and WebView2"), L10n.T("edge-files.Description", "Delete Edge and WebView2 files. Apps that require WebView2 may stop working. Updates may reinstall Edge."),
            "Advanced / original builder", OptionKind.File, false, false, []),
        new("onedrive-files", L10n.T("onedrive-files.Title", "Remove OneDrive setup binaries"), L10n.T("onedrive-files.Description", "Remove both architecture-specific OneDrive installers from the offline image."),
            "Advanced / original builder", OptionKind.File, false, false, []),
        new("language-extras", L10n.T("language-extras.Title", "Speech, OCR and handwriting payloads"), L10n.T("language-extras.Description", "Remove optional language capabilities in every image language; retain basic language support."),
            "Optional features", OptionKind.Capability, false, false,
            ["Language.Handwriting", "Language.OCR", "Language.Speech", "Language.TextToSpeech"]),
        new("legacy-capabilities", L10n.T("legacy-capabilities.Title", "Retired accessories and wallpaper payloads"), L10n.T("legacy-capabilities.Description", "Remove WordPad, Steps Recorder, Math Recognizer, legacy Media Player and extended wallpapers where installed."),
            "Optional features", OptionKind.Capability, false, true,
            ["Microsoft.Windows.WordPad", "App.StepsRecorder", "MathRecognizer", "Media.WindowsMediaPlayer", "Microsoft.Windows.Wallpaper.Extended"]),
        Feature("xps", "XPS printing", "Disable optional XPS printing and remove its payload.", false, true, "Printing-XPSServices-Features"),
        Feature("workfolders", "Work Folders", "Disable enterprise Work Folders sync and remove its payload.", false, true, "WorkFolders-Client"),
        Feature("legacy", "Legacy Internet Explorer", "Disable the retired Internet Explorer optional feature where present.", true, true,
            "Internet-Explorer-Optional-amd64", "Internet-Explorer-Optional-arm64"),
        Feature("recall-feature", "Recall optional feature", "Disable and remove Recall's payload only if this image exposes it to DISM.", true, true, "Recall"),
        Policy("ads", "Ads, recommendations and sponsored apps", "Suppress consumer content, Settings suggestions, Explorer promotions and welcome tips.", true, true),
        Policy("privacy", "Optional diagnostics and personalization", "Keep required diagnostic data. Disable advertising ID, personalized suggestions and input collection.", true, true),
        Policy("copilot", "Disable Copilot and Recall", "Apply policies to disable Copilot and Recall. Support varies by build and edition.", true, true),
        Policy("widgets", "Widgets and taskbar feeds", "Disable Widgets by policy and hide its taskbar entry.", true, true),
        Policy("search", "Web search and search highlights", "Turn off web suggestions, cloud search and dynamic search highlights; keep local search.", true, true),
        Policy("onedrive", "OneDrive sync and backup prompts", "Disable OneDrive sync by policy. OneDrive binaries are retained; use this only if you do not need sync.", true, true),
        Policy("edge", "Edge first-run and promotional UI", "Suppress first-run, sidebar and recommendations. Preserve Edge and WebView2 for app compatibility.", true, true),
        Policy("oobe", "Setup privacy and finish-setting-up nags", "Suppress privacy setup and post-sign-in setup suggestions without creating accounts or accepting the EULA.", true, true),
        Policy("cleanup", "Component cleanup", "Regular mode preserves servicing. Core additionally resets the update base before its irreversible component pruning.", true, true),
        Policy("hardware-bypass", "Bypass setup hardware requirements", "Set CPU, RAM, TPM, storage and Secure Boot bypass values in setup and Windows. Unsupported hardware may not work.", false, false),
        Policy("reserved-storage", "Disable reserved storage", "Stop reserving disk space for updates. This can leave less space available during servicing.", false, false),
        Policy("device-encryption", "Disable automatic device encryption", "Prevent automatic BitLocker device encryption; do not use if you require encryption by default.", false, false)
    ];

    private static CleanupOption App(string id, string title, string description, bool balanced, bool minimal, params string[] targets)
        => new(id, L10n.T(id + ".Title", title), L10n.T(id + ".Description", description), "Bundled apps", OptionKind.App, balanced, minimal, targets);
    private static CleanupOption Feature(string id, string title, string description, bool balanced, bool minimal, params string[] targets)
        => new(id, L10n.T(id + ".Title", title), L10n.T(id + ".Description", description), "Optional features", OptionKind.Feature, balanced, minimal, targets);
    private static CleanupOption Policy(string id, string title, string description, bool balanced, bool minimal)
        => new(id, L10n.T(id + ".Title", title), L10n.T(id + ".Description", description), "Windows settings", OptionKind.Policy, balanced, minimal, []);

    public static string[] ForPreset(CleanupPreset preset) => Options
        .Where(o => preset switch
        {
            CleanupPreset.Balanced => o.Balanced,
            CleanupPreset.Minimal => o.Minimal,
            CleanupPreset.Original => o.Kind == OptionKind.App || o.Id is not ("language-extras" or "legacy-capabilities"),
            _ => false
        })
        .Select(o => o.Id).ToArray();

    public static string[] ForMode(BuildMode mode) => mode switch
    {
        BuildMode.Tiny11 => ForPreset(CleanupPreset.Original),
        BuildMode.Tiny11Core => Options.Where(o => o.Minimal || o.Id is
            "language-extras" or "edge-files" or "onedrive-files" or
            "hardware-bypass" or "reserved-storage" or "device-encryption")
            .Select(o => o.Id).ToArray(),
        BuildMode.Custom => [],
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static BuildMode ModeForRecipe(BuildRecipe recipe)
    {
        if (recipe.Mode is { } mode) return mode;
        var preset = recipe.Flavor == BuildFlavor.Core ? BuildMode.Tiny11Core : BuildMode.Tiny11;
        return recipe.ExpertActions.Length == 0 &&
            recipe.SelectedOptions.ToHashSet(StringComparer.Ordinal).SetEquals(ForMode(preset))
            ? preset : BuildMode.Custom;
    }

    public static bool MatchesApp(CleanupOption option, ProvisionedApp app) =>
        option.Kind == OptionKind.App && option.Targets.Any(target =>
            app.DisplayName.Equals(target, StringComparison.OrdinalIgnoreCase) &&
            app.PackageName.StartsWith(target + "_", StringComparison.OrdinalIgnoreCase));
}
