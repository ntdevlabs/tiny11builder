namespace Tiny11Builder.Core;

/// <summary>
/// The Nano11 reduction plan, taken from the nano11.ps1 builder. Nano11 is Core plus a much smaller
/// component store: Core keeps every runtime/servicing family, Nano11 keeps only the families the booted
/// system needs and drops the rest. Every identity here is optional: a target that is absent from an image
/// is logged as not present and skipped, never guessed, because package identities differ by build.
/// </summary>
public static class Nano11Plan
{
    /// <summary>Provisioned-app <c>PackageName</c> fragments removed before servicing.</summary>
    public static IReadOnlyList<string> AppFragments { get; } =
    [
        "Zune", "Bing", "Clipchamp", "Gaming", "People", "PowerAutomate", "Teams", "Todos", "YourPhone",
        "SoundRecorder", "Solitaire", "FeedbackHub", "Maps", "OfficeHub", "Help", "Family", "Alarms",
        "CommunicationsApps", "Copilot", "CompatibilityEnhancements", "AV1VideoExtension",
        "AVCEncoderVideoExtension", "HEIFImageExtension", "HEVCVideoExtension", "MicrosoftStickyNotes",
        "OutlookForWindows", "RawImageExtension", "SecHealthUI", "VP9VideoExtensions", "WebpImageExtension",
        "DevHome", "Photos", "Camera", "QuickAssist", "CoreAI", "PeopleExperienceHost",
        "PinningConfirmationDialog", "SecureAssessmentBrowser", "Paint", "Notepad"
    ];

    /// <summary>Optional-feature and capability payloads removed for the image's own architecture.</summary>
    /// <param name="architecture">The image architecture: <c>x64</c> or <c>arm64</c>.</param>
    public static IReadOnlyList<string> FeatureIdentities(string architecture)
    {
        var native = architecture.Equals("arm64", StringComparison.OrdinalIgnoreCase) ? "arm64" : "amd64";
        return
        [
            "Microsoft-Windows-InternetExplorer-Optional-Package", "Microsoft-Windows-MediaPlayer-Package",
            "Microsoft-Windows-WordPad-FoD-Package", "Microsoft-Windows-StepsRecorder-Package",
            "Microsoft-Windows-MSPaint-FoD-Package", "Microsoft-Windows-SnippingTool-FoD-Package",
            "Microsoft-Windows-TabletPCMath-Package", "Microsoft-Windows-Xps-Xps-Viewer-Opt-Package",
            "Microsoft-Windows-PowerShell-ISE-FOD-Package", "OpenSSH-Client-Package",
            $"Microsoft-Windows-LanguageFeatures-Handwriting-{native}-Package",
            $"Microsoft-Windows-LanguageFeatures-OCR-{native}-Package",
            $"Microsoft-Windows-LanguageFeatures-Speech-{native}-Package",
            $"Microsoft-Windows-LanguageFeatures-TextToSpeech-{native}-Package",
            "Windows-Defender-Client-Package", "Microsoft-Windows-Search-Engine-Client-Package",
            "Microsoft-Windows-Kernel-LA57-FoD-Package", "Microsoft-Windows-Hello-Face-Package",
            "Microsoft-Windows-Hello-BioEnrollment-Package", "Microsoft-Windows-BitLocker-DriveEncryption-FVE-Package",
            "Microsoft-Windows-TPM-WMI-Provider-Package", "Microsoft-Windows-Narrator-App-Package",
            "Microsoft-Windows-Magnifier-App-Package", "Microsoft-Windows-Printing-PMCPPC-FoD-Package",
            "Microsoft-Windows-WebcamExperience-Package", "Microsoft-Media-MPEG2-Decoder-Package",
            "Microsoft-Windows-Wallpaper-Content-Extended-FoD-Package",
            "Language.Handwriting", "Language.OCR", "Language.Speech", "Language.TextToSpeech"
        ];
    }

    /// <summary>Direct payload and file removals (paths are relative to the offline mount).</summary>
    public static IReadOnlyList<string> Payloads { get; } =
    [
        @"Program Files (x86)\Microsoft\Edge", @"Program Files (x86)\Microsoft\EdgeCore",
        @"Program Files (x86)\Microsoft\EdgeUpdate", @"Program Files (x86)\Microsoft\EdgeWebView",
        @"Windows\System32\Microsoft-Edge-Webview", @"Windows\System32\Recovery\winre.wim",
        @"Windows\System32\OneDriveSetup.exe", @"Windows\System32\InputMethod\CHS",
        @"Windows\System32\InputMethod\CHT", @"Windows\System32\InputMethod\JPN",
        @"Windows\System32\InputMethod\KOR", @"Windows\Web", @"Windows\Help", @"Windows\Cursors",
        @"Windows\Speech\Engines\TTS", @"ProgramData\Microsoft\Windows Defender\Definition Updates",
        @"Windows\Temp"
    ];

    /// <summary>Font families kept. Everything else in Windows\Fonts is removed.</summary>
    public static IReadOnlyList<string> KeptFontPrefixes { get; } =
    [
        "segoe", "segui", "tahoma", "marlett", "8541oem", "consol", "lucon", "calibri", "arial", "times", "cou",
        "8"
    ];

    /// <summary>Non-Latin font families removed even when the Windows language matches them.</summary>
    public static IReadOnlyList<string> RemovedFontPrefixes { get; } =
        ["mingli", "msjh", "msyh", "malgun", "meiryo", "yugoth", "segoeuihistoric"];

    /// <summary>Driver-store folders removed: printers, scanners, multi-function devices, smartcards, tape and Bluetooth PAN.</summary>
    public static IReadOnlyList<string> DriverPrefixes { get; } = ["prn", "scan", "mfd"];

    public static IReadOnlyList<string> DriverNames { get; } =
        ["wscsmd.inf", "tapdrv", "rdpbus.inf", "tdibth.inf"];

    /// <summary>
    /// Component-store families Nano11 keeps. Nano11 replaces the whole WinSxS folder with only these,
    /// so a missing family is a hard failure rather than a silent skip.
    /// </summary>
    public static IReadOnlyList<string> RetainedStoreFamilies(string architecture) =>
        architecture.Equals("arm64", StringComparison.OrdinalIgnoreCase) ?
        [
            "arm64_microsoft-windows-servicingstack-onecore", "arm64_microsoft-windows-servicingcommon",
            "arm64_microsoft-windows-servicing-onecore-uapi", "arm64_microsoft-windows-servicing-adm",
            "arm64_microsoft-windows-servicingstack", "arm64_microsoft-windows-servicingstack-inetsrv",
            "arm64_microsoft-windows-servicingstack-msg", "arm64_microsoft.vc80.crt",
            "arm64_microsoft.vc90.crt", "arm64_microsoft.windows.common-controls",
            "arm64_microsoft.windows.gdiplus", "arm64_microsoft.windows.c..-controls.resources",
            "arm64_microsoft.windows.i..utomation.proxystub", "arm64_microsoft.windows.isolationautomation",
            "x86_microsoft.vc80.crt", "x86_microsoft.vc90.crt", "x86_microsoft.windows.common-controls",
            "x86_microsoft.windows.gdiplus", "x86_microsoft.windows.c..-controls.resources",
            "x86_microsoft.windows.i..utomation.proxystub", "x86_microsoft.windows.isolationautomation"
        ] :
        [
            "amd64_microsoft-windows-servicingstack", "amd64_microsoft-windows-servicingstack-onecore",
            "amd64_microsoft-windows-servicingstack-inetsrv", "amd64_microsoft-windows-servicingstack-msg",
            "amd64_microsoft-windows-s..stack-inetsrv-extra", "amd64_microsoft-windows-s..stack-msg.resources",
            "amd64_microsoft-windows-servicingstack-msg", "amd64_microsoft.vc80.crt", "amd64_microsoft.vc90.crt",
            "amd64_microsoft.windows.common-controls", "amd64_microsoft.windows.gdiplus",
            "amd64_microsoft.windows.c..-controls.resources", "amd64_microsoft.windows.i..utomation.proxystub",
            "amd64_microsoft.windows.isolationautomation",
            "x86_microsoft.vc80.crt", "x86_microsoft.vc90.crt", "x86_microsoft.windows.common-controls",
            "x86_microsoft.windows.gdiplus", "x86_microsoft.windows.c..-controls.resources",
            "x86_microsoft.windows.i..utomation.proxystub", "x86_microsoft.windows.isolationautomation",
            "x86_microsoft-windows-s..ngstack-onecorebase", "x86_microsoft-windows-s..stack-termsrv-extra",
            "x86_microsoft-windows-servicingstack", "x86_microsoft-windows-servicingstack-inetsrv",
            "x86_microsoft-windows-servicingstack-onecore"
        ];

    /// <summary>
    /// Confirms the retained families exist in a mounted image before any Nano11 file is removed. Nano11 replaces the
    /// whole component store, so an image without these families would be reduced to an unbootable one.
    /// </summary>
    public static void RequireRetainedFamilies(string mount, string architecture)
    {
        var store = Path.Combine(mount, @"Windows\WinSxS");
        if (!Directory.Exists(store))
            throw new InvalidDataException("Nano11 cannot find this image's component store. Nothing was removed.");
        var names = Directory.EnumerateDirectories(store).Select(Path.GetFileName).OfType<string>().ToArray();
        var missing = RetainedStoreFamilies(architecture)
            .Where(family => !names.Any(name => name.StartsWith(family, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (missing.Length == 0) return;
        throw new InvalidDataException($"Nano11 cannot identify {missing.Length} required {architecture} component " +
            $"famil{(missing.Length == 1 ? "y" : "ies")} in this image ({string.Join(", ", missing.Take(6))}" +
            (missing.Length > 6 ? ", ..." : "") + "). The build stopped before any payload was removed.");
    }

    /// <summary>Services deleted from every control set. Audio services are excluded: they are a known boot hazard.</summary>
    public static IReadOnlyList<string> RemovedServices { get; } =
    [
        "Spooler", "PrintNotify", "Fax", "RemoteRegistry", "diagsvc", "WerSvc", "PcaSvc", "MapsBroker",
        "WalletService", "BthAvctpSvc", "BluetoothUserService", "wuauserv", "UsoSvc", "WaaSMedicSvc"
    ];

    /// <summary>Defender services switched off rather than deleted.</summary>
    public static IReadOnlyList<string> DisabledServices { get; } =
        ["WinDefend", "WdNisSvc", "WdNisDrv", "WdFilter", "Sense", "dmwappushservice"];

    /// <summary>Scheduled-task definition files removed from Windows\System32\Tasks.</summary>
    public static IReadOnlyList<string> TaskFiles { get; } =
    [
        @"Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
        @"Microsoft\Windows\Application Experience\ProgramDataUpdater",
        @"Microsoft\Windows\Customer Experience Improvement Program",
        @"Microsoft\Windows\Chkdsk\Proxy", @"Microsoft\Windows\Windows Error Reporting\QueueReporting"
    ];

    /// <summary>Extra offline registry values written only for Nano11.</summary>
    public static IReadOnlyList<PolicyValue> Policies { get; } =
    [
        // Prevent Dev Home and new Outlook from being installed after first sign-in.
        new(PolicyHive.Software, @"Microsoft\Windows\CurrentVersion\WindowsUpdate\Orchestrator\UScheduler\OutlookUpdate", "workCompleted", 1),
        new(PolicyHive.Software, @"Microsoft\Windows\CurrentVersion\WindowsUpdate\Orchestrator\UScheduler\DevHomeUpdate", "workCompleted", 1),
        // Windows Update is already disabled by the Core policies; this also hides its settings page.
        new(PolicyHive.Software, @"Microsoft\Windows\CurrentVersion\Policies\Explorer", "SettingsPageVisibility", "hide:virus;windowsupdate")
    ];
}
