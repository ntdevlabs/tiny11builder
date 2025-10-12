<#
.SYNOPSIS
    Applies tiny11 modifications to an existing Windows installation.

.DESCRIPTION
    This script applies the tiny11 cleanup operations to a running Windows installation,
    removing bloatware, disabling telemetry, and applying various optimizations without
    requiring a WIM image or reinstallation.

.EXAMPLE
    .\Invoke-Tiny11Cleanup.ps1
    
    Runs the cleanup on the current Windows installation.

.NOTES
    Author: Based on tiny11maker.ps1 by ntdevlabs
    Date: 2025
    
    This script must be run as Administrator.
    A system restart is recommended after running this script.
#>

#---------[ Functions ]---------#
function Set-RegistryValue {
    param (
        [string]$path,
        [string]$name,
        [string]$type,
        [string]$value
    )
    try {
        # Ensure the registry path exists
        if (-not (Test-Path $path)) {
            New-Item -Path $path -Force | Out-Null
        }
        & 'reg' 'add' $path '/v' $name '/t' $type '/d' $value '/f' | Out-Null
        Write-Output "Set registry value: $path\$name"
    } catch {
        Write-Output "Error setting registry value: $_"
    }
}

function Remove-RegistryValue {
    param (
        [string]$path
    )
    try {
        if (Test-Path $path) {
            & 'reg' 'delete' $path '/f' | Out-Null
            Write-Output "Removed registry value: $path"
        }
    } catch {
        Write-Output "Error removing registry value: $_"
    }
}

#---------[ Execution ]---------#
# Check if PowerShell execution is restricted
if ((Get-ExecutionPolicy) -eq 'Restricted') {
    Write-Output "Your current PowerShell Execution Policy is set to Restricted, which prevents scripts from running. Do you want to change it to RemoteSigned? (yes/no)"
    $response = Read-Host
    if ($response -eq 'yes') {
        Set-ExecutionPolicy RemoteSigned -Scope CurrentUser -Confirm:$false
    } else {
        Write-Output "The script cannot be run without changing the execution policy. Exiting..."
        exit
    }
}

# Check and run the script as admin if required
$adminSID = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-32-544")
$adminGroup = $adminSID.Translate([System.Security.Principal.NTAccount])
$myWindowsID=[System.Security.Principal.WindowsIdentity]::GetCurrent()
$myWindowsPrincipal=new-object System.Security.Principal.WindowsPrincipal($myWindowsID)
$adminRole=[System.Security.Principal.WindowsBuiltInRole]::Administrator
if (! $myWindowsPrincipal.IsInRole($adminRole))
{
    Write-Output "Restarting script as admin in a new window, you can close this one."
    $newProcess = new-object System.Diagnostics.ProcessStartInfo "PowerShell";
    $newProcess.Arguments = $myInvocation.MyCommand.Definition;
    $newProcess.Verb = "runas";
    [System.Diagnostics.Process]::Start($newProcess);
    exit
}

# Start the transcript
Start-Transcript -Path "$PSScriptRoot\tiny11cleanup_$(get-date -f yyyyMMdd_HHmms).log"

$Host.UI.RawUI.WindowTitle = "Tiny11 Live Cleanup"
Clear-Host
Write-Output "=========================================="
Write-Output "   Tiny11 Live Cleanup - Release: 2025   "
Write-Output "=========================================="
Write-Output ""
Write-Warning "This script will remove bloatware and apply optimizations to your current Windows installation."
Write-Warning "A system restart is recommended after completion."
Write-Output ""
$confirm = Read-Host "Do you want to continue? (yes/no)"
if ($confirm -ne 'yes') {
    Write-Output "Operation cancelled."
    Stop-Transcript
    exit
}

Write-Output ""
Write-Output "Starting cleanup process..."
Write-Output ""

# Remove Provisioned AppX Packages
Write-Output "===== Removing Provisioned AppX Packages ====="
try {
    $packages = Get-AppxProvisionedPackage -Online | Select-Object -ExpandProperty DisplayName
    
    $packagePrefixes = 'AppUp.IntelManagementandSecurityStatus',
    'Clipchamp.Clipchamp', 
    'DolbyLaboratories.DolbyAccess',
    'DolbyLaboratories.DolbyDigitalPlusDecoderOEM',
    'Microsoft.BingNews',
    'Microsoft.BingSearch',
    'Microsoft.BingWeather',
    'Microsoft.Copilot',
    'Microsoft.Windows.CrossDevice',
    'Microsoft.GamingApp',
    'Microsoft.GetHelp',
    'Microsoft.Getstarted',
    'Microsoft.Microsoft3DViewer',
    'Microsoft.MicrosoftOfficeHub',
    'Microsoft.MicrosoftSolitaireCollection',
    'Microsoft.MicrosoftStickyNotes',
    'Microsoft.MixedReality.Portal',
    'Microsoft.MSPaint',
    'Microsoft.Office.OneNote',
    'Microsoft.OfficePushNotificationUtility',
    'Microsoft.OutlookForWindows',
    'Microsoft.Paint',
    'Microsoft.People',
    'Microsoft.PowerAutomateDesktop',
    'Microsoft.SkypeApp',
    'Microsoft.StartExperiencesApp',
    'Microsoft.Todos',
    'Microsoft.Wallet',
    'Microsoft.Windows.DevHome',
    'Microsoft.Windows.Copilot',
    'Microsoft.Windows.Teams',
    'Microsoft.WindowsAlarms',
    'Microsoft.WindowsCamera',
    'microsoft.windowscommunicationsapps',
    'Microsoft.WindowsFeedbackHub',
    'Microsoft.WindowsMaps',
    'Microsoft.WindowsSoundRecorder',
    'Microsoft.WindowsTerminal',
    'Microsoft.Xbox.TCUI',
    'Microsoft.XboxApp',
    'Microsoft.XboxGameOverlay',
    'Microsoft.XboxGamingOverlay',
    'Microsoft.XboxIdentityProvider',
    'Microsoft.XboxSpeechToTextOverlay',
    'Microsoft.YourPhone',
    'Microsoft.ZuneMusic',
    'Microsoft.ZuneVideo',
    'MicrosoftCorporationII.MicrosoftFamily',
    'MicrosoftCorporationII.QuickAssist',
    'MSTeams',
    'MicrosoftTeams', 
    'Microsoft.549981C3F5F10'
    
    foreach ($prefix in $packagePrefixes) {
        $matchingPackages = $packages | Where-Object { $_ -like "*$prefix*" }
        foreach ($pkg in $matchingPackages) {
            try {
                Write-Output "Removing provisioned package: $pkg"
                Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -eq $pkg } | Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | Out-Null
            } catch {
                Write-Output "  Failed to remove $pkg : $_"
            }
        }
    }
    
    # Also remove installed AppX packages for current user
    Write-Output "Removing installed AppX packages for all users..."
    foreach ($prefix in $packagePrefixes) {
        $matchingAppx = Get-AppxPackage -AllUsers | Where-Object { $_.Name -like "*$prefix*" }
        foreach ($app in $matchingAppx) {
            try {
                Write-Output "Removing installed package: $($app.Name)"
                Remove-AppxPackage -Package $app.PackageFullName -AllUsers -ErrorAction SilentlyContinue | Out-Null
            } catch {
                Write-Output "  Failed to remove $($app.Name) : $_"
            }
        }
    }
} catch {
    Write-Output "Error during AppX package removal: $_"
}

Write-Output ""
Write-Output "===== Applying Registry Tweaks ====="

# Bypass system requirements
Write-Output "Bypassing system requirements..."
Set-RegistryValue 'HKCU\Control Panel\UnsupportedHardwareNotificationCache' 'SV1' 'REG_DWORD' '0'
Set-RegistryValue 'HKCU\Control Panel\UnsupportedHardwareNotificationCache' 'SV2' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\SYSTEM\Setup\LabConfig' 'BypassCPUCheck' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\SYSTEM\Setup\LabConfig' 'BypassRAMCheck' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\SYSTEM\Setup\LabConfig' 'BypassSecureBootCheck' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\SYSTEM\Setup\LabConfig' 'BypassStorageCheck' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\SYSTEM\Setup\LabConfig' 'BypassTPMCheck' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\SYSTEM\Setup\MoSetup' 'AllowUpgradesWithUnsupportedTPMOrCPU' 'REG_DWORD' '1'

# Disable Sponsored Apps
Write-Output "Disabling Sponsored Apps..."
Set-RegistryValue 'HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'OemPreInstalledAppsEnabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'PreInstalledAppsEnabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SilentInstalledAppsEnabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent' 'DisableWindowsConsumerFeatures' 'REG_DWORD' '1'
Set-RegistryValue 'HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'ContentDeliveryAllowed' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\SOFTWARE\Microsoft\PolicyManager\current\device\Start' 'ConfigureStartPins' 'REG_SZ' '{"pinnedList": [{}]}'
Set-RegistryValue 'HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'FeatureManagementEnabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'PreInstalledAppsEverEnabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SoftLandingEnabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SubscribedContentEnabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SubscribedContent-310093Enabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SubscribedContent-338388Enabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SubscribedContent-338389Enabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SubscribedContent-338393Enabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SubscribedContent-353694Enabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SubscribedContent-353696Enabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SystemPaneSuggestionsEnabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\SOFTWARE\Policies\Microsoft\PushToInstall' 'DisablePushToInstall' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\SOFTWARE\Policies\Microsoft\MRT' 'DontOfferThroughWUAU' 'REG_DWORD' '1'
Remove-RegistryValue 'HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\Subscriptions'
Remove-RegistryValue 'HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\SuggestedApps'
Set-RegistryValue 'HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent' 'DisableConsumerAccountStateContent' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent' 'DisableCloudOptimizedContent' 'REG_DWORD' '1'

# Enable Local Accounts on OOBE
Write-Output "Enabling Local Accounts on OOBE..."
Set-RegistryValue 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE' 'BypassNRO' 'REG_DWORD' '1'

# Disable Reserved Storage
Write-Output "Disabling Reserved Storage..."
Set-RegistryValue 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\ReserveManager' 'ShippedWithReserves' 'REG_DWORD' '0'

# Disable BitLocker Device Encryption
Write-Output "Disabling BitLocker Device Encryption..."
Set-RegistryValue 'HKLM\SYSTEM\CurrentControlSet\Control\BitLocker' 'PreventDeviceEncryption' 'REG_DWORD' '1'

# Disable Chat icon
Write-Output "Disabling Chat icon..."
Set-RegistryValue 'HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Chat' 'ChatIcon' 'REG_DWORD' '3'
Set-RegistryValue 'HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced' 'TaskbarMn' 'REG_DWORD' '0'

# Remove Edge related registries
Write-Output "Removing Edge related registries..."
Remove-RegistryValue "HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft Edge"
Remove-RegistryValue "HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft Edge Update"

# Disable OneDrive folder backup
Write-Output "Disabling OneDrive folder backup..."
Set-RegistryValue "HKLM\SOFTWARE\Policies\Microsoft\Windows\OneDrive" "DisableFileSyncNGSC" "REG_DWORD" "1"

# Disable Telemetry
Write-Output "Disabling Telemetry..."
Set-RegistryValue 'HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo' 'Enabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKCU\Software\Microsoft\Windows\CurrentVersion\Privacy' 'TailoredExperiencesWithDiagnosticDataEnabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKCU\Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy' 'HasAccepted' 'REG_DWORD' '0'
Set-RegistryValue 'HKCU\Software\Microsoft\Input\TIPC' 'Enabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKCU\Software\Microsoft\InputPersonalization' 'RestrictImplicitInkCollection' 'REG_DWORD' '1'
Set-RegistryValue 'HKCU\Software\Microsoft\InputPersonalization' 'RestrictImplicitTextCollection' 'REG_DWORD' '1'
Set-RegistryValue 'HKCU\Software\Microsoft\InputPersonalization\TrainedDataStore' 'HarvestContacts' 'REG_DWORD' '0'
Set-RegistryValue 'HKCU\Software\Microsoft\Personalization\Settings' 'AcceptedPrivacyPolicy' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection' 'AllowTelemetry' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\SYSTEM\CurrentControlSet\Services\dmwappushservice' 'Start' 'REG_DWORD' '4'

# Prevent installation of DevHome and Outlook
Write-Output "Preventing installation of DevHome and Outlook..."
Set-RegistryValue 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Orchestrator\UScheduler_Oobe\OutlookUpdate' 'workCompleted' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Orchestrator\UScheduler\OutlookUpdate' 'workCompleted' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Orchestrator\UScheduler\DevHomeUpdate' 'workCompleted' 'REG_DWORD' '1'
Remove-RegistryValue 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Orchestrator\UScheduler_Oobe\OutlookUpdate'
Remove-RegistryValue 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Orchestrator\UScheduler_Oobe\DevHomeUpdate'

# Disable Copilot
Write-Output "Disabling Copilot..."
Set-RegistryValue 'HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot' 'TurnOffWindowsCopilot' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\SOFTWARE\Policies\Microsoft\Edge' 'HubsSidebarEnabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\SOFTWARE\Policies\Microsoft\Windows\Explorer' 'DisableSearchBoxSuggestions' 'REG_DWORD' '1'

# Prevent installation of Teams
Write-Output "Preventing installation of Teams..."
Set-RegistryValue 'HKLM\SOFTWARE\Policies\Microsoft\Teams' 'DisableInstallation' 'REG_DWORD' '1'

# Prevent installation of New Outlook
Write-Output "Preventing installation of New Outlook..."
Set-RegistryValue 'HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Mail' 'PreventRun' 'REG_DWORD' '1'

Write-Output ""
Write-Output "===== Disabling Telemetry Scheduled Tasks ====="
try {
    $tasksPath = "$env:SystemRoot\System32\Tasks"
    
    # Application Compatibility Appraiser
    $taskFile = "$tasksPath\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser"
    if (Test-Path $taskFile) {
        Write-Output "Disabling: Microsoft Compatibility Appraiser"
        & schtasks /Change /TN "\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser" /Disable | Out-Null
    }
    
    # Customer Experience Improvement Program tasks
    Write-Output "Disabling: Customer Experience Improvement Program tasks"
    & schtasks /Change /TN "\Microsoft\Windows\Customer Experience Improvement Program\Consolidator" /Disable 2>$null | Out-Null
    & schtasks /Change /TN "\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip" /Disable 2>$null | Out-Null
    
    # Program Data Updater
    & schtasks /Change /TN "\Microsoft\Windows\Application Experience\ProgramDataUpdater" /Disable 2>$null | Out-Null
    
    # Windows Error Reporting
    Write-Output "Disabling: Windows Error Reporting"
    & schtasks /Change /TN "\Microsoft\Windows\Windows Error Reporting\QueueReporting" /Disable 2>$null | Out-Null
    
} catch {
    Write-Output "Error disabling scheduled tasks: $_"
}

Write-Output ""
Write-Output "=========================================="
Write-Output "   Cleanup Complete!                     "
Write-Output "=========================================="
Write-Output ""
Write-Output "The tiny11 cleanup has been applied to your Windows installation."
Write-Output ""
Write-Warning "IMPORTANT: A system restart is strongly recommended."
Write-Output ""
Write-Output "Log file saved to: $PSScriptRoot\tiny11cleanup_$(get-date -f yyyyMMdd_HHmms).log"
Write-Output ""
$restart = Read-Host "Do you want to restart now? (yes/no)"
if ($restart -eq 'yes') {
    Write-Output "Restarting system in 10 seconds..."
    Stop-Transcript
    shutdown /r /t 10 /c "Restarting after Tiny11 cleanup"
} else {
    Write-Output "Please restart your system manually to complete the cleanup."
    Stop-Transcript
}
