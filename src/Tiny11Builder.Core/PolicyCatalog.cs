namespace Tiny11Builder.Core;

public static class PolicyCatalog
{
    public static IReadOnlyList<PolicyValue> ForOption(string id)
    {
        var values = new List<PolicyValue>();
        void Machine(string key, string name, object value) => values.Add(new(PolicyHive.Software, key, name, value));
        void User(string key, string name, object value) => values.Add(new(PolicyHive.DefaultUser, key, name, value));
        const string cloud = @"Policies\Microsoft\Windows\CloudContent";
        const string userCloud = @"Software\Policies\Microsoft\Windows\CloudContent";
        const string content = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
        switch (id)
        {
            case "ads":
                foreach (var name in new[] { "DisableWindowsConsumerFeatures", "DisableConsumerAccountStateContent", "DisableCloudOptimizedContent" })
                    Machine(cloud, name, 1);
                foreach (var name in new[] { "DisableWindowsSpotlightFeatures", "DisableThirdPartySuggestions", "DisableTailoredExperiencesWithDiagnosticData" })
                    User(userCloud, name, 1);
                foreach (var name in new[]
                {
                    "ContentDeliveryAllowed", "OemPreInstalledAppsEnabled", "PreInstalledAppsEnabled",
                    "SilentInstalledAppsEnabled", "SoftLandingEnabled", "SubscribedContentEnabled", "SystemPaneSuggestionsEnabled",
                    "SubscribedContent-310093Enabled", "SubscribedContent-338388Enabled", "SubscribedContent-338389Enabled",
                    "SubscribedContent-338393Enabled", "SubscribedContent-353694Enabled", "SubscribedContent-353696Enabled"
                }) User(content, name, 0);
                User(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSyncProviderNotifications", 0);
                Machine(@"Policies\Microsoft\PushToInstall", "DisablePushToInstall", 1);
                Machine(@"Policies\Microsoft\Teams", "DisableInstallation", 1);
                Machine(@"Policies\Microsoft\Windows\Windows Mail", "PreventRun", 1);
                break;
            case "privacy":
                Machine(@"Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1);
                Machine(@"Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", 1);
                User(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0);
                User(@"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0);
                User(@"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 0);
                User(@"Software\Microsoft\Input\TIPC", "Enabled", 0);
                User(@"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1);
                User(@"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1);
                User(@"Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0);
                break;
            case "copilot":
                Machine(@"Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1);
                User(@"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1);
                Machine(@"Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1);
                User(@"Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1);
                User(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowCopilotButton", 0);
                break;
            case "widgets":
                Machine(@"Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0);
                break;
            case "search":
                User(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1);
                Machine(@"Policies\Microsoft\Windows\Windows Search", "AllowCloudSearch", 0);
                Machine(@"Policies\Microsoft\Windows\Windows Search", "EnableDynamicContentInWSB", 0);
                break;
            case "onedrive":
                Machine(@"Policies\Microsoft\Windows\OneDrive", "DisableFileSyncNGSC", 1);
                break;
            case "edge":
                foreach (var name in new[] { "HideFirstRunExperience" }) Machine(@"Policies\Microsoft\Edge", name, 1);
                foreach (var name in new[] { "HubsSidebarEnabled", "ShowRecommendationsEnabled", "UserFeedbackAllowed", "SpotlightExperiencesAndRecommendationsEnabled" })
                    Machine(@"Policies\Microsoft\Edge", name, 0);
                break;
            case "oobe":
                Machine(@"Policies\Microsoft\Windows\OOBE", "DisablePrivacyExperience", 1);
                User(@"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0);
                User(content, "SubscribedContent-310093Enabled", 0);
                User(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarMn", 0);
                Machine(@"Policies\Microsoft\Windows\Windows Chat", "ChatIcon", 3);
                break;
            case "hardware-bypass":
                foreach (var name in new[] { "BypassCPUCheck", "BypassRAMCheck", "BypassSecureBootCheck", "BypassStorageCheck", "BypassTPMCheck" })
                    values.Add(new(PolicyHive.System, @"Setup\LabConfig", name, 1));
                values.Add(new(PolicyHive.System, @"Setup\MoSetup", "AllowUpgradesWithUnsupportedTPMOrCPU", 1));
                User(@"Control Panel\UnsupportedHardwareNotificationCache", "SV1", 0);
                User(@"Control Panel\UnsupportedHardwareNotificationCache", "SV2", 0);
                break;
            case "reserved-storage":
                Machine(@"Microsoft\Windows\CurrentVersion\ReserveManager", "ShippedWithReserves", 0);
                break;
            case "device-encryption":
                values.Add(new(PolicyHive.System, @"{ControlSet}\Control\BitLocker", "PreventDeviceEncryption", 1));
                break;
        }
        return values;
    }
}
