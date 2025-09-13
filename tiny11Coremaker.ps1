<#
.WICHTIGER HINWEIS ZUR SICHERHEITSWARNUNG
Wenn PowerShell eine Sicherheitswarnung anzeigt ("Führen Sie ausschließlich vertrauenswürdige Skripts aus..."),
liegt das daran, dass die Datei aus dem Internet heruntergeladen wurde. Das ist ein Schutzmechanismus von Windows.

UM DIESE WARNUNG DAUERHAFT ZU ENTFERNEN, führen Sie einmalig diesen Befehl in einem PowerShell-Fenster aus:
Unblock-File -Path "PFAD\ZU\DIESER\DATEI\tiny11Coremaker_fixed.ps1"

(Ersetzen Sie "PFAD\ZU\DIESER\DATEI" mit dem tatsächlichen Pfad auf Ihrem Computer.)
Danach wird das Skript ohne Warnung starten.
#>

# --- Automatic Execution Policy Fix ---
# This command temporarily allows the script to run in the current session without manual confirmation.
Set-ExecutionPolicy Bypass -Scope Process -Force

# Check and run the script as admin if required
$myWindowsID=[System.Security.Principal.WindowsIdentity]::GetCurrent()
$myWindowsPrincipal=new-object System.Security.Principal.WindowsPrincipal($myWindowsID)
$adminRole=[System.Security.Principal.WindowsBuiltInRole]::Administrator
if (! $myWindowsPrincipal.IsInRole($adminRole))
{
    Write-Host "Restarting Tiny11 image creator as admin in a new window, you can close this one."
    $newProcess = new-object System.Diagnostics.ProcessStartInfo "PowerShell";
    $newProcess.Arguments = "-File `"$($myInvocation.MyCommand.Definition)`""
    $newProcess.Verb = "runas";
    [System.Diagnostics.Process]::Start($newProcess);
    exit
}

# Get the Administrators group in a language-independent way
$adminGroupSid = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
$adminGroup = $adminGroupSid.Translate([System.Security.Principal.NTAccount])

# --- Functions ---
function Set-ItemOwnershipAndAccess {
    param(
        [string]$Path,
        [switch]$Recurse
    )
    if (-not (Test-Path $Path)) {
        Write-Warning "Path not found: $Path"
        return
    }
    Write-Host "Taking ownership and setting permissions for: $Path"
    try {
        $acl = Get-Acl $Path
        $acl.SetOwner($adminGroup)
        if ($Recurse) {
            $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($adminGroup, [System.Security.AccessControl.FileSystemRights]::FullControl, "ContainerInherit, ObjectInherit", "None", "Allow")
        } else {
            $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($adminGroup, [System.Security.AccessControl.FileSystemRights]::FullControl, "Allow")
        }
        $acl.AddAccessRule($rule)
        Set-Acl -Path $Path -AclObject $acl
        Write-Host "  - Success."
    } catch {
        Write-Error "Error processing '$Path': $_"
    }
}

Start-Transcript -Path "$PSScriptRoot\tiny11.log" 
# Ask the user for input
Write-Host "Welcome to tiny11 core builder! BETA 09-05-25"
Write-Host "This script generates a significantly reduced Windows 11 image. However, it's not suitable for regular use due to its lack of serviceability - you can't add languages, updates, or features post-creation. tiny11 Core is not a full Windows 11 substitute but a rapid testing or development tool, potentially useful for VM environments."
Write-Host "Do you want to continue? (y/n)"
$input = Read-Host

if ($input.ToLower() -eq 'y') {
    Write-Host "Off we go..."
    Start-Sleep -Seconds 3
    Clear-Host

    $mainOSDrive = $env:SystemDrive
    $hostArchitecture = $Env:PROCESSOR_ARCHITECTURE
    New-Item -ItemType Directory -Force -Path "$mainOSDrive\tiny11\sources" >null
    $DriveLetter = Read-Host "Please enter the drive letter for the Windows 11 image"
    $DriveLetter = $DriveLetter + ":"

    if ((Test-Path "$DriveLetter\sources\boot.wim") -eq $false -or (Test-Path "$DriveLetter\sources\install.wim") -eq $false) {
        if ((Test-Path "$DriveLetter\sources\install.esd") -eq $true) {
            Write-Host "Found install.esd, converting to install.wim..."
            &  'dism' '/English' "/Get-WimInfo" "/wimfile:$DriveLetter\sources\install.esd"
            $index = Read-Host "Please enter the image index"
            Write-Host ' '
            Write-Host 'Converting install.esd to install.wim. This may take a while...'
            & 'DISM' /Export-Image /SourceImageFile:"$DriveLetter\sources\install.esd" /SourceIndex:$index /DestinationImageFile:"$mainOSDrive\tiny11\sources\install.wim" /Compress:max /CheckIntegrity
        } else {
            Write-Host "Can't find Windows OS Installation files in the specified Drive Letter.."
            Write-Host "Please enter the correct DVD Drive Letter.."
            exit
        }
    }

    Write-Host "Copying Windows image..."
    Copy-Item -Path "$DriveLetter\*" -Destination "$mainOSDrive\tiny11" -Recurse -Force > null
    Set-ItemProperty -Path "$mainOSDrive\tiny11\sources\install.esd" -Name IsReadOnly -Value $false > $null 2>&1
    Remove-Item "$mainOSDrive\tiny11\sources\install.esd" > $null 2>&1
    Write-Host "Copy complete!"
    Start-Sleep -Seconds 2
    Clear-Host
    Write-Host "Getting image information:"
    &  'dism' '/English' "/Get-WimInfo" "/wimfile:$mainOSDrive\tiny11\sources\install.wim"
    $index = Read-Host "Please enter the image index"
    Write-Host "Mounting Windows image. This may take a while."
    $wimFilePath = "$($env:SystemDrive)\tiny11\sources\install.wim" 
    
    Set-ItemOwnershipAndAccess -Path $wimFilePath

    try {
        Set-ItemProperty -Path $wimFilePath -Name IsReadOnly -Value $false -ErrorAction Stop
    } catch {
        # This block will catch the error and suppress it.
    }
    New-Item -ItemType Directory -Force -Path "$mainOSDrive\scratchdir" > $null
    & dism /English "/mount-image" "/imagefile:$($env:SystemDrive)\tiny11\sources\install.wim" "/index:$index" "/mountdir:$($env:SystemDrive)\scratchdir"

    $imageIntl = & dism /English /Get-Intl "/Image:$($env:SystemDrive)\scratchdir"
    $languageLine = $imageIntl -split '\n' | Where-Object { $_ -match 'Default system UI language : ([a-zA-Z]{2}-[a-zA-Z]{2})' }

    if ($languageLine) {
        $languageCode = $Matches[1]
        Write-Host "Default system UI language code: $languageCode"
    } else {
        Write-Host "Default system UI language code not found."
    }

    $imageInfo = & 'dism' '/English' '/Get-WimInfo' "/wimFile:$($env:SystemDrive)\tiny11\sources\install.wim" "/index:$index"
    $lines = $imageInfo -split '\r?\n'

    foreach ($line in $lines) {
        if ($line -like '*Architecture : *') {
            $architecture = $line -replace 'Architecture : ',''
            if ($architecture -eq 'x64') {
                $architecture = 'amd64'
            }
            Write-Host "Architecture: $architecture"
            break
        }
    }

    if (-not $architecture) {
        Write-Host "Architecture information not found."
    }

    Write-Host "Mounting complete! Performing removal of applications..."

    $packages = & 'dism' '/English' "/image:$($env:SystemDrive)\scratchdir" '/Get-ProvisionedAppxPackages' |
        ForEach-Object {
            if ($_ -match 'PackageName : (.*)') {
                $matches[1]
            }
        }
    $packagePrefixes = 'Clipchamp.Clipchamp_', 'Microsoft.BingNews_', 'Microsoft.BingWeather_', 'Microsoft.GamingApp_', 'Microsoft.GetHelp_', 'Microsoft.Getstarted_', 'Microsoft.MicrosoftOfficeHub_', 'Microsoft.MicrosoftSolitaireCollection_', 'Microsoft.People_', 'Microsoft.PowerAutomateDesktop_', 'Microsoft.Todos_', 'Microsoft.WindowsAlarms_', 'microsoft.windowscommunicationsapps_', 'Microsoft.WindowsFeedbackHub_', 'Microsoft.WindowsMaps_', 'Microsoft.WindowsSoundRecorder_', 'Microsoft.Xbox.TCUI_', 'Microsoft.XboxGamingOverlay_', 'Microsoft.XboxGameOverlay_', 'Microsoft.XboxSpeechToTextOverlay_', 'Microsoft.YourPhone_', 'Microsoft.ZuneMusic_', 'Microsoft.ZuneVideo_', 'MicrosoftCorporationII.MicrosoftFamily_', 'MicrosoftCorporationII.QuickAssist_', 'MicrosoftTeams_', 'Microsoft.549981C3F5F10_', 'Microsoft.Windows.Copilot', 'MSTeams_', 'Microsoft.OutlookForWindows_', 'Microsoft.Windows.Teams_', 'Microsoft.Copilot_'

    $packagesToRemove = $packages | Where-Object {
        $packageName = $_
        $packagePrefixes -contains ($packagePrefixes | Where-Object { $packageName -like "$_*" })
    }
    foreach ($package in $packagesToRemove) {
        write-host "Removing $package :"
        & 'dism' '/English' "/image:$($env:SystemDrive)\scratchdir" '/Remove-ProvisionedAppxPackage' "/PackageName:$package"
    }

    Write-Host "Removing of system apps complete! Now proceeding to removal of system packages..."
    Start-Sleep -Seconds 1
    Clear-Host

    $scratchDir = "$($env:SystemDrive)\scratchdir"
    $packagePatterns = @(
        "Microsoft-Windows-InternetExplorer-Optional-Package~31bf3856ad364e35",
        "Microsoft-Windows-Kernel-LA57-FoD-Package~31bf3856ad364e35~amd64",
        "Microsoft-Windows-LanguageFeatures-Handwriting-$languageCode-Package~31bf3856ad364e35",
        "Microsoft-Windows-LanguageFeatures-OCR-$languageCode-Package~31bf3856ad364e35",
        "Microsoft-Windows-LanguageFeatures-Speech-$languageCode-Package~31bf3856ad364e35",
        "Microsoft-Windows-LanguageFeatures-TextToSpeech-$languageCode-Package~31bf3856ad364e35",
        "Microsoft-Windows-MediaPlayer-Package~31bf3856ad364e35",
        "Microsoft-Windows-Wallpaper-Content-Extended-FoD-Package~31bf3856ad364e35",
        "Windows-Defender-Client-Package~31bf3856ad364e35~",
        "Microsoft-Windows-WordPad-FoD-Package~",
        "Microsoft-Windows-TabletPCMath-Package~",
        "Microsoft-Windows-StepsRecorder-Package~"
    )

    $allPackages = & dism /image:$scratchDir /Get-Packages /Format:Table
    $allPackages = $allPackages -split "`n" | Select-Object -Skip 1

    foreach ($packagePattern in $packagePatterns) {
        $packagesToRemove = $allPackages | Where-Object { $_ -like "$packagePattern*" }
        foreach ($package in $packagesToRemove) {
            $packageIdentity = ($package -split "\s+")[0]
            Write-Host "Removing $packageIdentity..."
            & dism /image:$scratchDir /Remove-Package /PackageName:$packageIdentity 
        }
    }

    Write-Host "Do you want to enable .NET 3.5? This cannot be done after the image has been created! (y/n)"
    $inputNet = Read-Host

    if ($inputNet.ToLower() -eq 'y') {
        Write-Host "Enabling .NET 3.5..."
        & 'dism'  "/image:$scratchDir" '/enable-feature' '/featurename:NetFX3' '/All' "/source:$($env:SystemDrive)\tiny11\sources\sxs" 
        Write-Host ".NET 3.5 has been enabled."
    }
    else {
        Write-Host "You chose not to enable .NET 3.5. Continuing..."
    }

    Write-Host "Removing Edge:"
    Remove-Item -Path "$mainOSDrive\scratchdir\Program Files (x86)\Microsoft\Edge" -Recurse -Force >null
    Remove-Item -Path "$mainOSDrive\scratchdir\Program Files (x86)\Microsoft\EdgeUpdate" -Recurse -Force >null
    Remove-Item -Path "$mainOSDrive\scratchdir\Program Files (x86)\Microsoft\EdgeCore" -Recurse -Force >null

    $edgeWebViewPathSystem32 = "$mainOSDrive\scratchdir\Windows\System32\Microsoft-Edge-Webview"
    
    # FIX: Robustly delete Edge folders from WinSxS and System32
    $emptyDirForEdge = Join-Path -Path $scratchDir -ChildPath "empty_edge_delete"
    New-Item -Path $emptyDirForEdge -ItemType Directory -Force | Out-Null

    $edgeFilter = switch ($architecture) {
        'amd64' { "amd64_microsoft-edge-webview_31bf3856ad364e35*" }
        'arm64' { "arm64_microsoft-edge-webview_31bf3856ad364e35*" }
        default { Write-Host "Unknown architecture: $architecture"; return }
    }
    
    $edgeFoldersInWinSxS = Get-ChildItem -Path "$mainOSDrive\scratchdir\Windows\WinSxS" -Filter $edgeFilter -Directory
    if ($edgeFoldersInWinSxS) {
        foreach ($folder in $edgeFoldersInWinSxS) {
            Write-Host "Force-deleting Edge folder: $($folder.FullName)"
            Set-ItemOwnershipAndAccess -Path $folder.FullName -Recurse
            & robocopy $emptyDirForEdge $folder.FullName /MIR /R:0 /W:0 | Out-Null
            Remove-Item -Path $folder.FullName -Recurse -Force
        }
    } else {
        Write-Host "Edge WebView folder not found in WinSxS."
    }

    if (Test-Path $edgeWebViewPathSystem32) {
        Write-Host "Force-deleting Edge folder: $edgeWebViewPathSystem32"
        Set-ItemOwnershipAndAccess -Path $edgeWebViewPathSystem32 -Recurse
        & robocopy $emptyDirForEdge $edgeWebViewPathSystem32 /MIR /R:0 /W:0 | Out-Null
        Remove-Item -Path $edgeWebViewPathSystem32 -Recurse -Force
    }
    Remove-Item -Path $emptyDirForEdge -Recurse -Force

    Write-Host "Removing WinRE"
    $recoveryPath = "$mainOSDrive\scratchdir\Windows\System32\Recovery"
    Set-ItemOwnershipAndAccess -Path $recoveryPath -Recurse
    Remove-Item -Path "$recoveryPath\winre.wim" -Recurse -Force
    New-Item -Path "$recoveryPath\winre.wim" -ItemType File -Force > $null

    Write-Host "Removing OneDrive:"
    $oneDrivePath = "$mainOSDrive\scratchdir\Windows\System32\OneDriveSetup.exe"
    Set-ItemOwnershipAndAccess -Path $oneDrivePath
    Remove-Item -Path $oneDrivePath -Force >null
    Write-Host "Removal complete!"
    Start-Sleep -Seconds 2
    Clear-Host

    Write-Host "Taking ownership of the WinSxS folder. This might take a while..."
    Set-ItemOwnershipAndAccess -Path "$mainOSDrive\scratchdir\Windows\WinSxS" -Recurse
    Write-host "Complete!"
    Start-Sleep -Seconds 2
    Clear-Host

    Write-Host "Preparing..."
    $folderPath = Join-Path -Path $mainOSDrive -ChildPath "\scratchdir\Windows\WinSxS_edit"
    $sourceDirectory = "$mainOSDrive\scratchdir\Windows\WinSxS"
    $destinationDirectory = "$mainOSDrive\scratchdir\Windows\WinSxS_edit"
    New-Item -Path $folderPath -ItemType Directory
    if ($architecture -eq "amd64") {
        $dirsToCopy = @( "x86_microsoft.windows.common-controls_6595b64144ccf1df_*", "x86_microsoft.windows.gdiplus_6595b64144ccf1df_*", "x86_microsoft.windows.i..utomation.proxystub_6595b64144ccf1df_*", "x86_microsoft.windows.isolationautomation_6595b64144ccf1df_*", "x86_microsoft-windows-s..ngstack-onecorebase_31bf3856ad364e35_*", "x86_microsoft-windows-s..stack-termsrv-extra_31bf3856ad364e35_*", "x86_microsoft-windows-servicingstack_31bf3856ad364e35_*", "x86_microsoft-windows-servicingstack-inetsrv_*", "x86_microsoft-windows-servicingstack-onecore_*", "amd64_microsoft.vc80.crt_1fc8b3b9a1e18e3b_*", "amd64_microsoft.vc90.crt_1fc8b3b9a1e18e3b_*", "amd64_microsoft.windows.c..-controls.resources_6595b64144ccf1df_*", "amd64_microsoft.windows.common-controls_6595b64144ccf1df_*", "amd64_microsoft.windows.gdiplus_6595b64144ccf1df_*", "amd64_microsoft.windows.i..utomation.proxystub_6595b64144ccf1df_*", "amd64_microsoft.windows.isolationautomation_6595b64144ccf1df_*", "amd64_microsoft-windows-s..stack-inetsrv-extra_31bf3856ad364e35_*", "amd64_microsoft-windows-s..stack-msg.resources_31bf3856ad364e35_*", "amd64_microsoft-windows-s..stack-termsrv-extra_31bf3856ad364e35_*", "amd64_microsoft-windows-servicingstack_31bf3856ad364e35_*", "amd64_microsoft-windows-servicingstack-inetsrv_31bf3856ad364e35_*", "amd64_microsoft-windows-servicingstack-msg_31bf3856ad364e35_*", "amd64_microsoft-windows-servicingstack-onecore_31bf3856ad364e35_*", "Catalogs", "FileMaps", "Fusion", "InstallTemp", "Manifests", "x86_microsoft.vc80.crt_1fc8b3b9a1e18e3b_*", "x86_microsoft.vc90.crt_1fc8b3b9a1e18e3b_*", "x86_microsoft.windows.c..-controls.resources_6595b64144ccf1df_*", "x86_microsoft.windows.c..-controls.resources_6595b64144ccf1df_*" )
    } elseif ($architecture -eq "arm64") {
        $dirsToCopy = @( "arm64_microsoft-windows-servicingstack-onecore_31bf3856ad364e35_*", "Catalogs", "FileMaps", "Fusion", "InstallTemp", "Manifests", "SettingsManifests", "Temp", "x86_microsoft.vc80.crt_1fc8b3b9a1e18e3b_*", "x86_microsoft.vc90.crt_1fc8b3b9a1e18e3b_*", "x86_microsoft.windows.c..-controls.resources_6595b64144ccf1df_*", "x86_microsoft.windows.common-controls_6595b64144ccf1df_*", "x86_microsoft.windows.gdiplus_6595b64144ccf1df_*", "x86_microsoft.windows.i..utomation.proxystub_6595b64144ccf1df_*", "x86_microsoft.windows.isolationautomation_6595b64144ccf1df_*", "arm_microsoft.windows.c..-controls.resources_6595b64144ccf1df_*", "arm_microsoft.windows.common-controls_6595b64144ccf1df_*", "arm_microsoft.windows.gdiplus_6595b64144ccf1df_*", "arm_microsoft.windows.i..utomation.proxystub_6595b64144ccf1df_*", "arm_microsoft.windows.isolationautomation_6595b64144ccf1df_*", "arm64_microsoft.vc80.crt_1fc8b3b9a1e18e3b_*", "arm64_microsoft.vc90.crt_1fc8b3b9a1e18e3b_*", "arm64_microsoft.windows.c..-controls.resources_6595b64144ccf1df_*", "arm64_microsoft.windows.common-controls_6595b64144ccf1df_*", "arm64_microsoft.windows.gdiplus_6595b64144ccf1df_*", "arm64_microsoft.windows.i..utomation.proxystub_6595b64144ccf1df_*", "arm64_microsoft.windows.isolationautomation_6595b64144ccf1df_*", "arm64_microsoft-windows-servicing-adm_31bf3856ad364e35_*", "arm64_microsoft-windows-servicingcommon_31bf3856ad364e35_*", "arm64_microsoft-windows-servicing-onecore-uapi_31bf3856ad364e35_*", "arm64_microsoft-windows-servicingstack_31bf3856ad364e35_*", "arm64_microsoft-windows-servicingstack-inetsrv_31bf3856ad364e35_*", "arm64_microsoft-windows-servicingstack-msg_31bf3856ad364e35_*" )
    }
    foreach ($dir in $dirsToCopy) {
        $sourceDirs = Get-ChildItem -Path $sourceDirectory -Filter $dir -Directory
        foreach ($sourceDir in $sourceDirs) {
            $destDir = Join-Path -Path $destinationDirectory -ChildPath $sourceDir.Name
            Write-Host "Copying $sourceDir.FullName to $destDir"
            Copy-Item -Path $sourceDir.FullName -Destination $destDir -Recurse -Force
        }
    }  

    Write-Host "Deleting WinSxS. This may take a while..."
    # FIX: Use robocopy to reliably delete the protected WinSxS folder contents.
    $emptyDir = Join-Path -Path $scratchDir -ChildPath "empty_temp_for_delete"
    New-Item -Path $emptyDir -ItemType Directory -Force | Out-Null
    & robocopy $emptyDir "$mainOSDrive\scratchdir\Windows\WinSxS" /MIR /R:0 /W:0 | Out-Null
    Remove-Item -Path "$mainOSDrive\scratchdir\Windows\WinSxS" -Recurse -Force
    Remove-Item -Path $emptyDir -Recurse -Force

    Rename-Item -Path "$mainOSDrive\scratchdir\Windows\WinSxS_edit" -NewName "$mainOSDrive\scratchdir\Windows\WinSxS"
    Write-Host "Complete!"

    Write-Host "Loading registry..."
    reg load HKLM\zCOMPONENTS $ScratchDisk\scratchdir\Windows\System32\config\COMPONENTS | Out-Null
    reg load HKLM\zDEFAULT $ScratchDisk\scratchdir\Windows\System32\config\default | Out-Null
    reg load HKLM\zNTUSER $ScratchDisk\scratchdir\Users\Default\ntuser.dat | Out-Null
    reg load HKLM\zSOFTWARE $ScratchDisk\scratchdir\Windows\System32\config\SOFTWARE | Out-Null
    reg load HKLM\zSYSTEM $ScratchDisk\scratchdir\Windows\System32\config\SYSTEM | Out-Null
    Write-Host "Bypassing system requirements(on the system image):"
    & 'reg' 'add' 'HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache' '/v' 'SV1' '/t' 'REG_DWORD' '/d' '0' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache' '/v' 'SV2' '/t' 'REG_DWORD' '/d' '0' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache' '/v' 'SV1' '/t' 'REG_DWORD' '/d' '0' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache' '/v' 'SV2' '/t' 'REG_DWORD' '/d' '0' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zSYSTEM\Setup\LabConfig' '/v' 'BypassCPUCheck' '/t' 'REG_DWORD' '/d' '1' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zSYSTEM\Setup\LabConfig' '/v' 'BypassRAMCheck' '/t' 'REG_DWORD' '/d' '1' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zSYSTEM\Setup\LabConfig' '/v' 'BypassSecureBootCheck' '/t' 'REG_DWORD' '/d' '1' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zSYSTEM\Setup\LabConfig' '/v' 'BypassStorageCheck' '/t' 'REG_DWORD' '/d' '1' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zSYSTEM\Setup\LabConfig' '/v' 'BypassTPMCheck' '/t' 'REG_DWORD' '/d' '1' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zSYSTEM\Setup\MoSetup' '/v' 'AllowUpgradesWithUnsupportedTPMOrCPU' '/t' 'REG_DWORD' '/d' '1' '/f' | Out-Null
    Write-Host "Disabling Sponsored Apps:"
    & 'reg' 'add' 'HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' '/v' 'OemPreInstalledAppsEnabled' '/t' 'REG_DWORD' '/d' '0' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' '/v' 'PreInstalledAppsEnabled' '/t' 'REG_DWORD' '/d' '0' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' '/v' 'SilentInstalledAppsEnabled' '/t' 'REG_DWORD' '/d' '0' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zSOFTWARE\Policies\Microsoft\Windows\CloudContent' '/v' 'DisableWindowsConsumerFeatures' '/t' 'REG_DWORD' '/d' '1' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' '/v' 'ContentDeliveryAllowed' '/t' 'REG_DWORD' '/d' '0' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zSOFTWARE\Microsoft\PolicyManager\current\device\Start' '/v' 'ConfigureStartPins' '/t' 'REG_SZ' '/d' '{"pinnedList": [{}]}' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' '/v' 'FeatureManagementEnabled' '/t' 'REG_DWORD' '/d' '0' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' '/v' 'PreInstalledAppsEverEnabled' '/t' 'REG_DWORD' '/d' '0' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' '/v' 'SoftLandingEnabled' '/t' 'REG_DWORD' '/d' '0' '/f'| Out-Null
    & 'reg' 'add' 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' '/v' 'SubscribedContentEnabled' '/t' 'REG_DWORD' '/d' '0' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' '/v' 'SystemPaneSuggestionsEnabled' '/t' 'REG_DWORD' '/d' '0' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zSOFTWARE\Policies\Microsoft\PushToInstall' '/v' 'DisablePushToInstall' '/t' 'REG_DWORD' '/d' '1' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zSOFTWARE\Policies\Microsoft\MRT' '/v' 'DontOfferThroughWUAU' '/t' 'REG_DWORD' '/d' '1' '/f' | Out-Null
    & 'reg' 'delete' 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\Subscriptions' '/f' | Out-Null
    & 'reg' 'delete' 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\SuggestedApps' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zSOFTWARE\Policies\Microsoft\Windows\CloudContent' '/v' 'DisableConsumerAccountStateContent' '/t' 'REG_DWORD' '/d' '1' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zSOFTWARE\Policies\Microsoft\Windows\CloudContent' '/v' 'DisableCloudOptimizedContent' '/t' 'REG_DWORD' '/d' '1' '/f' | Out-Null
    Write-Host "Enabling Local Accounts on OOBE:"
    & 'reg' 'add' 'HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\OOBE' '/v' 'BypassNRO' '/t' 'REG_DWORD' '/d' '1' '/f' | Out-Null
    Copy-Item -Path "$PSScriptRoot\autounattend.xml" -Destination "$ScratchDisk\scratchdir\Windows\System32\Sysprep\autounattend.xml" -Force | Out-Null
    Write-Host "Disabling Reserved Storage:"
    & 'reg' 'add' 'HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\ReserveManager' '/v' 'ShippedWithReserves' '/t' 'REG_DWORD' '/d' '0' '/f' | Out-Null
    Write-Host "Disabling BitLocker Device Encryption"
    & 'reg' 'add' 'HKLM\zSYSTEM\ControlSet001\Control\BitLocker' '/v' 'PreventDeviceEncryption' '/t' 'REG_DWORD' '/d' '1' '/f' | Out-Null
    Write-Host "Disabling Chat icon:"
    & 'reg' 'add' 'HKLM\zSOFTWARE\Policies\Microsoft\Windows\Windows Chat' '/v' 'ChatIcon' '/t' 'REG_DWORD' '/d' '3' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced' '/v' 'TaskbarMn' '/t' 'REG_DWORD' '/d' '0' '/f' | Out-Null
    Write-Host "Removing Edge related registries"
    reg delete "HKEY_LOCAL_MACHINE\zSOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft Edge" /f | Out-Null
    reg delete "HKEY_LOCAL_MACHINE\zSOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft Edge Update" /f | Out-Null
    Write-Host "Disabling OneDrive folder backup"
    & 'reg' 'add' "HKLM\zSOFTWARE\Policies\Microsoft\Windows\OneDrive" '/v' 'DisableFileSyncNGSC' '/t' 'REG_DWORD' '/d' '1' '/f' | Out-Null
    Write-Host "Disabling Telemetry:"
    & 'reg' 'add' 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo' '/v' 'Enabled' '/t' 'REG_DWORD' '/d' '0' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\Privacy' '/v' 'TailoredExperiencesWithDiagnosticDataEnabled' '/t' 'REG_DWORD' '/d' '0' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zNTUSER\Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy' '/v' 'HasAccepted' '/t' 'REG_DWORD' '/d' '0' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zNTUSER\Software\Microsoft\Input\TIPC' '/v' 'Enabled' '/t' 'REG_DWORD' '/d' '0' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zNTUSER\Software\Microsoft\InputPersonalization' '/v' 'RestrictImplicitInkCollection' '/t' 'REG_DWORD' '/d' '1' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zNTUSER\Software\Microsoft\InputPersonalization' '/v' 'RestrictImplicitTextCollection' '/t' 'REG_DWORD' '/d' '1' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zNTUSER\Software\Microsoft\InputPersonalization\TrainedDataStore' '/v' 'HarvestContacts' '/t' 'REG_DWORD' '/d' '0' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zNTUSER\Software\Microsoft\Personalization\Settings' '/v' 'AcceptedPrivacyPolicy' '/t' 'REG_DWORD' '/d' '0' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zSOFTWARE\Policies\Microsoft\Windows\DataCollection' '/v' 'AllowTelemetry' '/t' 'REG_DWORD' '/d' '0' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zSYSTEM\ControlSet001\Services\dmwappushservice' '/v' 'Start' '/t' 'REG_DWORD' '/d' '4' '/f' | Out-Null
    Write-Host "Prevents installation or DevHome and Outlook:"
    & 'reg' 'add' 'HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Orchestrator\UScheduler\OutlookUpdate' '/v' 'workCompleted' '/t' 'REG_DWORD' '/d' '1' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Orchestrator\UScheduler\DevHomeUpdate' '/v' 'workCompleted' '/t' 'REG_DWORD' '/d' '1' '/f' | Out-Null
    & 'reg' 'delete' 'HKLM\zSOFTWARE\Microsoft\WindowsUpdate\Orchestrator\UScheduler_Oobe\OutlookUpdate' '/f' | Out-Null
    & 'reg' 'delete' 'HKLM\zSOFTWARE\Microsoft\WindowsUpdate\Orchestrator\UScheduler_Oobe\DevHomeUpdate' '/f' | Out-Null
    Write-Host "Disabling Copilot"
    & 'reg' 'add' 'HKLM\zSOFTWARE\Policies\Microsoft\Windows\WindowsCopilot' '/v' 'TurnOffWindowsCopilot' '/t' 'REG_DWORD' '/d' '1' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zSOFTWARE\Policies\Microsoft\Edge' '/v' 'HubsSidebarEnabled' '/t' 'REG_DWORD' '/d' '0' '/f' | Out-Null
    & 'reg' 'add' 'HKLM\zSOFTWARE\Policies\Microsoft\Windows\Explorer' '/v' 'DisableSearchBoxSuggestions' '/t' 'REG_DWORD' '/d' '1' '/f' | Out-Null
    Write-Host "Prevents installation of Teams:"
    & 'reg' 'add' 'HKLM\zSOFTWARE\Policies\Microsoft\Teams' '/v' 'DisableInstallation' '/t' 'REG_DWORD' '/d' '1' '/f' | Out-Null
    Write-Host "Prevent installation of New Outlook":
    & 'reg' 'add' 'HKLM\zSOFTWARE\Policies\Microsoft\Windows\Windows Mail' '/v' 'PreventRun' '/t' 'REG_DWORD' '/d' '1' '/f' | Out-Null
    $tasksPath = "$mainOSDrive\scratchdir\Windows\System32\Tasks"

    Write-Host "Deleting scheduled task definition files..."

    Remove-Item -Path "$tasksPath\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser" -Force -ErrorAction SilentlyContinue
    Remove-Item -Path "$tasksPath\Microsoft\Windows\Customer Experience Improvement Program" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path "$tasksPath\Microsoft\Windows\Application Experience\ProgramDataUpdater" -Force -ErrorAction SilentlyContinue
    Remove-Item -Path "$tasksPath\Microsoft\Windows\Chkdsk\Proxy" -Force -ErrorAction SilentlyContinue
    Remove-Item -Path "$tasksPath\Microsoft\Windows\Windows Error Reporting\QueueReporting" -Force -ErrorAction SilentlyContinue

    Write-Host "Task files have been deleted."
    Write-Host "Disabling Windows Update..."
    & 'reg' 'add' "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" '/v' 'StopWUPostOOBE1' '/t' 'REG_SZ' '/d' 'net stop wuauserv' '/f'
    & 'reg' 'add' "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" '/v' 'StopWUPostOOBE2' '/t' 'REG_SZ' '/d' 'sc stop wuauserv' '/f'
    & 'reg' 'add' "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" '/v' 'StopWUPostOOBE3' '/t' 'REG_SZ' '/d' 'sc config wuauserv start= disabled' '/f'
    & 'reg' 'add' "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" '/v' 'DisbaleWUPostOOBE1' '/t' 'REG_SZ' '/d' 'reg add HKLM\SYSTEM\CurrentControlSet\Services\wuauserv /v Start /t REG_DWORD /d 4 /f' '/f'
    & 'reg' 'add' "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" '/v' 'DisbaleWUPostOOBE2' '/t' 'REG_SZ' '/d' 'reg add HKLM\SYSTEM\ControlSet001\Services\wuauserv /v Start /t REG_DWORD /d 4 /f' '/f'
    & 'reg' 'add' 'HKLM\zSOFTWARE\Policies\Microsoft\Windows\WindowsUpdate' '/v' 'DoNotConnectToWindowsUpdateInternetLocations' '/t' 'REG_DWORD' '/d' '1' '/f'
    & 'reg' 'add' 'HKLM\zSOFTWARE\Policies\Microsoft\Windows\WindowsUpdate' '/v' 'DisableWindowsUpdateAccess' '/t' 'REG_DWORD' '/d' '1' '/f' 
    & 'reg' 'add' 'HKLM\zSOFTWARE\Policies\Microsoft\Windows\WindowsUpdate' '/v' 'WUServer' '/t' 'REG_SZ' '/d' 'localhost' '/f' 
    & 'reg' 'add' 'HKLM\zSOFTWARE\Policies\Microsoft\Windows\WindowsUpdate' '/v' 'WUStatusServer' '/t' 'REG_SZ' '/d' 'localhost' '/f' 
    & 'reg' 'add' 'HKLM\zSOFTWARE\Policies\Microsoft\Windows\WindowsUpdate' '/v' 'UpdateServiceUrlAlternate' '/t' 'REG_SZ' '/d' 'localhost' '/f' 
    & 'reg' 'add' 'HKLM\zSOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' '/v' 'UseWUServer' '/t' 'REG_DWORD' '/d' '1' '/f' 
    & 'reg' 'add' 'HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\OOBE' '/v' 'DisableOnline' '/t' 'REG_DWORD' '/d' '1' '/f' 
    & 'reg' 'add' 'HKLM\zSYSTEM\ControlSet001\Services\wuauserv' '/v' 'Start' '/t' 'REG_DWORD' '/d' '4' '/f' 
    & 'reg' 'delete' 'HKLM\zSYSTEM\ControlSet001\Services\WaaSMedicSVC' '/f'
    & 'reg' 'delete' 'HKLM\zSYSTEM\ControlSet001\Services\UsoSvc' '/f'
    & 'reg' 'add' 'HKEY_LOCAL_MACHINE\zSOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' '/v' 'NoAutoUpdate' '/t' 'REG_DWORD' '/d' '1' '/f'
    Write-Host "Disabling Windows Defender"
    $servicePaths = @( "WinDefend", "WdNisSvc", "WdNisDrv", "WdFilter", "Sense" )
    foreach ($path in $servicePaths) {
        Set-ItemProperty -Path "HKLM:\zSYSTEM\ControlSet001\Services\$path" -Name "Start" -Value 4
    }
    & 'reg' 'add' 'HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer' '/v' 'SettingsPageVisibility' '/t' 'REG_SZ' '/d' 'hide:virus;windowsupdate' '/f' 
    Write-Host "Tweaking complete!"
    Write-Host "Unmounting Registry..."
    reg unload HKLM\zCOMPONENTS >null
    reg unload HKLM\zDEFAULT >null
    reg unload HKLM\zNTUSER >null
    reg unload HKLM\zSOFTWARE
    reg unload HKLM\zSYSTEM >null
    Write-Host "Cleaning up image..."
    & 'dism' '/English' "/image:$mainOSDrive\scratchdir" '/Cleanup-Image' '/StartComponentCleanup' '/ResetBase' >null
    Write-Host "Cleanup complete."
    Write-Host ' '
    Write-Host "Unmounting image..."
    & 'dism' '/English' '/unmount-image' "/mountdir:$mainOSDrive\scratchdir" '/commit'
    Write-Host "Exporting image..."
    & 'dism' '/English' '/Export-Image' "/SourceImageFile:$mainOSDrive\tiny11\sources\install.wim" "/SourceIndex:$index" "/DestinationImageFile:$mainOSDrive\tiny11\sources\install2.wim" '/compress:max'
    Remove-Item -Path "$mainOSDrive\tiny11\sources\install.wim" -Force >null
    Rename-Item -Path "$mainOSDrive\tiny11\sources\install2.wim" -NewName "install.wim" >null
    Write-Host "Windows image completed. Continuing with boot.wim."
    Start-Sleep -Seconds 2
    Clear-Host
    Write-Host "Mounting boot image:"
    $bootWimPath = "$($env:SystemDrive)\tiny11\sources\boot.wim" 
    Set-ItemOwnershipAndAccess -Path $bootWimPath
    Set-ItemProperty -Path $bootWimPath -Name IsReadOnly -Value $false
    & 'dism' '/English' '/mount-image' "/imagefile:$mainOSDrive\tiny11\sources\boot.wim" '/index:2' "/mountdir:$mainOSDrive\scratchdir"
    Write-Host "Loading registry..."
    reg load HKLM\zCOMPONENTS $mainOSDrive\scratchdir\Windows\System32\config\COMPONENTS | Out-Null
    reg load HKLM\zDEFAULT $mainOSDrive\scratchdir\Windows\System32\config\default | Out-Null
    reg load HKLM\zNTUSER $mainOSDrive\scratchdir\Users\Default\ntuser.dat | Out-Null
    reg load HKLM\zSOFTWARE $mainOSDrive\scratchdir\Windows\System32\config\SOFTWARE | Out-Null
    reg load HKLM\zSYSTEM $mainOSDrive\scratchdir\Windows\System32\config\SYSTEM | Out-Null
    Write-Host "Bypassing system requirements(on the setup image):"
    & 'reg' 'add' 'HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache' '/v' 'SV1' '/t' 'REG_DWORD' '/d' '0' '/f' >null
    & 'reg' 'add' 'HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache' '/v' 'SV2' '/t' 'REG_DWORD' '/d' '0' '/f' >null
    & 'reg' 'add' 'HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache' '/v' 'SV1' '/t' 'REG_DWORD' '/d' '0' '/f' >null
    & 'reg' 'add' 'HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache' '/v' 'SV2' '/t' 'REG_DWORD' '/d' '0' '/f' >null
    & 'reg' 'add' 'HKLM\zSYSTEM\Setup\LabConfig' '/v' 'BypassCPUCheck' '/t' 'REG_DWORD' '/d' '1' '/f' >null
    & 'reg' 'add' 'HKLM\zSYSTEM\Setup\LabConfig' '/v' 'BypassRAMCheck' '/t' 'REG_DWORD' '/d' '1' '/f' >null
    & 'reg' 'add' 'HKLM\zSYSTEM\Setup\LabConfig' '/v' 'BypassSecureBootCheck' '/t' 'REG_DWORD' '/d' '1' '/f' >null
    & 'reg' 'add' 'HKLM\zSYSTEM\Setup\LabConfig' '/v' 'BypassStorageCheck' '/t' 'REG_DWORD' '/d' '1' '/f' >null
    & 'reg' 'add' 'HKLM\zSYSTEM\Setup\LabConfig' '/v' 'BypassTPMCheck' '/t' 'REG_DWORD' '/d' '1' '/f' >null
    & 'reg' 'add' 'HKLM\zSYSTEM\Setup\MoSetup' '/v' 'AllowUpgradesWithUnsupportedTPMOrCPU' '/t' 'REG_DWORD' '/d' '1' '/f' >null
    & 'reg' 'add' 'HKEY_LOCAL_MACHINE\zSYSTEM\Setup' '/v' 'CmdLine' '/t' 'REG_SZ' '/d' 'X:\sources\setup.exe' '/f' >null
    Write-Host "Tweaking complete!"
    Write-Host "Unmounting Registry..."
    reg unload HKLM\zCOMPONENTS >null
    reg unload HKLM\zDEFAULT >null
    reg unload HKLM\zNTUSER >null
    reg unload HKLM\zSOFTWARE >null
    reg unload HKLM\zSYSTEM >null
    Write-Host "Unmounting image..."
    & 'dism' '/English' '/unmount-image' "/mountdir:$mainOSDrive\scratchdir" '/commit'
    Clear-Host
    Write-Host "Exporting ESD. This may take a while..."
    & dism /Export-Image /SourceImageFile:"$mainOSDrive\tiny11\sources\install.wim" /SourceIndex:1 /DestinationImageFile:"$mainOSDrive\tiny11\sources\install.esd" /Compress:recovery
    Remove-Item "$mainOSDrive\tiny11\sources\install.wim" > $null 2>&1
    Write-Host "The tiny11 image is now completed. Proceeding with the making of the ISO..."
    Write-Host "Creating ISO image..."
    $ADKDepTools = "C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\$hostarchitecture\Oscdimg"
    $localOSCDIMGPath = "$PSScriptRoot\oscdimg.exe"

    if ([System.IO.Directory]::Exists($ADKDepTools)) {
        Write-Host "Will be using oscdimg.exe from system ADK."
        $OSCDIMG = "$ADKDepTools\oscdimg.exe"
    } else {
        Write-Host "ADK folder not found. Will be using bundled oscdimg.exe."
        $url = "https://msdl.microsoft.com/download/symbols/oscdimg.exe/3D44737265000/oscdimg.exe"

        if (-not (Test-Path -Path $localOSCDIMGPath)) {
            Write-Host "Downloading oscdimg.exe..."
            Invoke-WebRequest -Uri $url -OutFile $localOSCDIMGPath

            if (Test-Path $localOSCDIMGPath) {
                Write-Host "oscdimg.exe downloaded successfully."
            } else {
                Write-Error "Failed to download oscdimg.exe."
                exit 1
            }
        } else {
            Write-Host "oscdimg.exe already exists locally."
        }
        $OSCDIMG = $localOSCDIMGPath
    }

    & "$OSCDIMG" '-m' '-o' '-u2' '-udfver102' "-bootdata:2#p0,e,b$mainOSDrive\tiny11\boot\etfsboot.com#pEF,e,b$mainOSDrive\tiny11\efi\microsoft\boot\efisys.bin" "$mainOSDrive\tiny11" "$PSScriptRoot\tiny11.iso"

    # Finishing up
    Write-Host "Creation completed! Press any key to exit the script..."
    Read-Host "Press Enter to continue"
    Write-Host "Performing Cleanup..."
    Remove-Item -Path "$mainOSDrive\tiny11" -Recurse -Force >null
    Remove-Item -Path "$mainOSDrive\scratchdir" -Recurse -Force >null

    # Stop the transcript
    Stop-Transcript
    exit
}
else {
    Write-Host "You chose not to continue. The script will now exit."
    exit
}

