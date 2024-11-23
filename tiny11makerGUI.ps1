$t = '[DllImport("user32.dll")] public static extern bool ShowWindow(int handle, int state);'
add-type -name win -member $t -namespace native
[native.win]::ShowWindow(([System.Diagnostics.Process]::GetCurrentProcess() | Get-Process).MainWindowHandle, 0) # by Andy Lowry found at : https://stackoverflow.com/questions/1802127/how-to-run-a-powershell-script-without-displaying-a-window

# Enable debugging
#Set-PSDebug -Trace 1

param (
    [ValidatePattern('^[c-zC-Z]$')]
    [string]$ScratchDisk
)

if (-not $ScratchDisk) {
    $ScratchDisk = $PSScriptRoot -replace '[\\]+$', ''
} else {
    $ScratchDisk = $ScratchDisk + ":"
}

Write-Output "Scratch disk set to $ScratchDisk"

# Check if PowerShell execution is restricted
if ((Get-ExecutionPolicy) -eq 'Restricted') {
    Add-Log "Your current PowerShell Execution Policy is set to Restricted, which prevents scripts from running. Do you want to change it to RemoteSigned? (yes/no)"
    $response = Read-Host
    if ($response -eq 'yes') {
        Set-ExecutionPolicy RemoteSigned -Scope CurrentUser -Confirm:$false
    } else {
        Add-Log "The script cannot be run without changing the execution policy. Exiting..."
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
    Add-Log "Restarting Tiny11 image creator as admin in a new window, you can close this one."
    $newProcess = new-object System.Diagnostics.ProcessStartInfo "PowerShell";
    $newProcess.Arguments = $myInvocation.MyCommand.Definition;
    $newProcess.Verb = "runas";
    [System.Diagnostics.Process]::Start($newProcess);
    exit
}

Add-Type -assembly System.Windows.Forms
Add-Type -assembly System.Drawing

# Log function
function Add-Log {
    param (
        [string]$message
    )
    $LogsTextBox.Text += "$message`r`n"
    $LogsTextBox.SelectionStart = $LogsTextBox.Text.Length
    $LogsTextBox.ScrollToCaret()
}

function Mount-Image {
    param (
        [string]$imagePath
    )
    try {
        Mount-DiskImage -ImagePath $imagePath
        Add-Log "Mounted: $imagePath"
    } catch {
        Add-Log "Error during mounting: $_"
        return $false
    }
    return $true
}

function Set-RegistryValue {
    param (
        [string]$path,
        [string]$name,
        [string]$type,
        [string]$value
    )
    try {
        & 'reg' 'add' $path '/v' $name '/t' $type '/d' $value '/f' | Out-Null
        Add-Log "Set registry value: $path\$name"
    } catch {
        Add-Log "Error setting registry value: $_"
    }
}

function Remove-RegistryValue {
    param (
		[string]$path
	)
	try {
		& 'reg' 'delete' $path '/f' | Out-Null
		Add-Log "Removed registry value: $path"
	} catch {
		Add-Log "Error removing registry value: $_"
	}
}

# Main Form
$main_form = New-Object System.Windows.Forms.Form
$main_form.Text = 'Tiny11makerGUI'
$main_form.Width = 485
$main_form.Height = 450
$main_form.StartPosition = 'CenterScreen'

# Disable Minimize, Maximize, and Resize
$main_form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
$main_form.MaximizeBox = $false
$main_form.MinimizeBox = $false

# Title Label (Centered)
$TitleLabel = New-Object System.Windows.Forms.Label
$TitleLabel.Text = "Tiny11 image creator"
$TitleLabel.Font = New-Object System.Drawing.Font('Consolas', 20, [System.Drawing.FontStyle]::Bold)
$TitleLabel.AutoSize = $true
$TitleLabel.Location = New-Object System.Drawing.Point(
    ($main_form.Width / 2) - 100, # Dynamically center
    10
)

# ISO Selection TextBox
$IsoTextBox = New-Object System.Windows.Forms.TextBox
$IsoTextBox.Text = "Select an .iso to mount"
$IsoTextBox.Width = 250
$IsoTextBox.Location = New-Object System.Drawing.Point(20, 65)
$IsoTextBox.Enabled = $false

# Choose Button
$ChooseButton = New-Object System.Windows.Forms.Button
$ChooseButton.Text = "Choose"
$ChooseButton.Location = New-Object System.Drawing.Point(280, 60)
$ChooseButton.Size = New-Object System.Drawing.Size(80, 30)
$ChooseButton.Add_Click({
    # Placeholder logic to simulate file selection
    $FileDialog = New-Object System.Windows.Forms.OpenFileDialog
    $FileDialog.Filter = "ISO Files (*.iso)|*.iso|All Files (*.*)|*.*"
    if ($FileDialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $IsoTextBox.Text = $FileDialog.FileName
        $MountButton.Enabled = $true
        Add-Log "Selected: $($FileDialog.FileName)"
    }
})

# Mount Button
$MountButton = New-Object System.Windows.Forms.Button
$MountButton.Text = "Mount"
$MountButton.Location = New-Object System.Drawing.Point(370, 60)
$MountButton.Size = New-Object System.Drawing.Size(80, 30)
$MountButton.Enabled = $false
$MountButton.Add_Click({
    Add-Log "Mounting: $($IsoTextBox.Text)"
    $ChooseButton.Enabled = $false
    $MountButton.Enabled = $false
    
    try {
        Mount-Image $IsoTextBox.Text
        Add-Log "Mounted: $($IsoTextBox.Text)"
    } catch {
        Add-Log "Error during mounting: $_"
        return
    }
            
    # Retrieve drive letters
    $DriveLetters = Get-PSDrive -PSProvider FileSystem
    foreach ($Letter in $DriveLetters) {
        $DriveComboBox.Items.Add($Letter.Name)
        Add-Log "Drive found: $($Letter.Name)"
    }
        
    # Enable related controls
    $DriveLabel.Enabled = $true
    $DriveComboBox.Enabled = $true
    $StartButton.Enabled = $true
})

# Drive Letter Label
$DriveLabel = New-Object System.Windows.Forms.Label
$DriveLabel.Text = "Drive Letter:"
$DriveLabel.Font = New-Object System.Drawing.Font('Consolas', 10)
$DriveLabel.Location = New-Object System.Drawing.Point(20, 110)
$DriveLabel.AutoSize = $true
$DriveLabel.Enabled = $false

# Drive Letter ComboBox
$DriveComboBox = New-Object System.Windows.Forms.ComboBox
$DriveComboBox.Width = 120
$DriveComboBox.Location = New-Object System.Drawing.Point(20, 135)
$DriveComboBox.Enabled = $false

# SKU Index Label
$ImageIndexLabel = New-Object System.Windows.Forms.Label
$ImageIndexLabel.Text = "SKU index:"
$ImageIndexLabel.Font = New-Object System.Drawing.Font('Consolas', 10)
$ImageIndexLabel.Location = New-Object System.Drawing.Point(150, 110)
$ImageIndexLabel.AutoSize = $true
$ImageIndexLabel.Enabled = $false   

# SKU Index ComboBox
$ImageIndexComboBox = New-Object System.Windows.Forms.ComboBox
$ImageIndexComboBox.Width = 120
$ImageIndexComboBox.Location = New-Object System.Drawing.Point(150, 135)
$ImageIndexComboBox.Enabled = $false

# Start Button
$StartButton = New-Object System.Windows.Forms.Button
$StartButton.Text = "Start"
$StartButton.Location = New-Object System.Drawing.Point(280, 130)
$StartButton.Size = New-Object System.Drawing.Size(170, 30)
$StartButton.Enabled = $false
$StartButton.Add_Click({
    $StartButton.Enabled = $false
    $DriveComboBox.Enabled = $false
    $DriveLabel.Enabled = $false
	Add-Log "Starting..."
	Add-Log "Drive: $($DriveComboBox.SelectedItem)"
	Add-Log "Scratch disk: $ScratchDisk"
	
	$hostArchitecture = $Env:PROCESSOR_ARCHITECTURE
    New-Item -ItemType Directory -Force -Path "$ScratchDisk\tiny11\sources" | Out-Null
    do {
    if ($DriveComboBox.SelectedItem -match '^[c-zC-Z]$') {
        $DriveLetter = $DriveComboBox.SelectedItem + ":"
        Add-Log "Drive letter set to $DriveLetter"
    } else {
        Add-Log "Invalid drive letter. Please enter a letter between C and Z."
    }
} while ($DriveLetter -notmatch '^[c-zC-Z]:$')

if ((Test-Path "$DriveLetter\sources\boot.wim") -eq $false -or (Test-Path "$DriveLetter\sources\install.wim") -eq $false) {
    if ((Test-Path "$DriveLetter\sources\install.esd") -eq $true) {
        Add-Log "Found install.esd, converting to install.wim..."
        Get-WindowsImage -ImagePath $DriveLetter\sources\install.esd
        Add-Log "Please select the image index"
        Add-Log ' '
        Add-Log 'Converting install.esd to install.wim. This may take a while...'
        Export-WindowsImage -SourceImagePath $DriveLetter\sources\install.esd -SourceIndex $index -DestinationImagePath $ScratchDisk\tiny11\sources\install.wim -Compressiontype Maximum -CheckIntegrity
    } else {
        Add-Log "Can't find Windows OS Installation files in the specified Drive Letter.."
        Add-Log "Please enter the correct DVD Drive Letter.."
        exit
    }
}
Add-Log "Copying Windows image..."
Copy-Item -Path "$DriveLetter\*" -Destination "$ScratchDisk\tiny11" -Recurse -Force | Out-Null
Set-ItemProperty -Path "$ScratchDisk\tiny11\sources\install.esd" -Name IsReadOnly -Value $false > $null 2>&1
Remove-Item "$ScratchDisk\tiny11\sources\install.esd" > $null 2>&1
Add-Log "Copy complete!"
Start-Sleep -Seconds 2
Add-Log "Getting image information:"
# save information about the image
$SKUInfo = & dism /English /Get-WimInfo "/wimFile:$($ScratchDisk)\tiny11\sources\install.wim" | Out-String
# add the image index to the combobox based on Get-WindowsImage -ImagePath $ScratchDisk\tiny11\sources\install.wim
$ImageIndexComboBox.Items.AddRange((Get-WindowsImage -ImagePath $ScratchDisk\tiny11\sources\install.wim).ImageIndex)
$ImageIndexLabel.Enabled = $true
$ImageIndexComboBox.Enabled = $true
[System.Windows.Forms.MessageBox]::Show("Please select the image under ""SKU"" look in logs to find desired edition.", "Image selection", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Information)
Add-Log "Please select the image index in 'SKU'"
Add-Log ' '
Add-Log $SKUInfo
$ImageIndexComboBox.Add_SelectedIndexChanged({
    $index = $ImageIndexComboBox.SelectedItem
Add-Log "Mounting Windows image. This may take a while."
Add-Log "The GUI will freeze multiple times during process, please be patient and do not close the window."
Add-Log "...I am working on a progress bar..."
$wimFilePath = "$ScratchDisk\tiny11\sources\install.wim"
& takeown "/F" $wimFilePath 
& icacls $wimFilePath "/grant" "$($adminGroup.Value):(F)"
try {
    Set-ItemProperty -Path $wimFilePath -Name IsReadOnly -Value $false -ErrorAction Stop
} catch {
    # This block will catch the error and suppress it.
}
New-Item -ItemType Directory -Force -Path "$ScratchDisk\scratchdir" > $null
Mount-WindowsImage -ImagePath $ScratchDisk\tiny11\sources\install.wim -Index $index -Path $ScratchDisk\scratchdir

$imageIntl = & dism /English /Get-Intl "/Image:$($ScratchDisk)\scratchdir"
$languageLine = $imageIntl -split '\n' | Where-Object { $_ -match 'Default system UI language : ([a-zA-Z]{2}-[a-zA-Z]{2})' }

if ($languageLine) {
    $languageCode = $Matches[1]
    Add-Log "Default system UI language code: $languageCode"
} else {
    Add-Log "Default system UI language code not found."
}

$imageInfo = & 'dism' '/English' '/Get-WimInfo' "/wimFile:$($ScratchDisk)\tiny11\sources\install.wim" "/index:$index"
$lines = $imageInfo -split '\r?\n'

foreach ($line in $lines) {
    if ($line -like '*Architecture : *') {
        $architecture = $line -replace 'Architecture : ',''
        # If the architecture is x64, replace it with amd64
        if ($architecture -eq 'x64') {
            $architecture = 'amd64'
        }
        Add-Log "Architecture: $architecture"
        break
    }
}

if (-not $architecture) {
    Add-Log "Architecture information not found."
}

Add-Log "Mounting complete! Performing removal of applications..."

$packages = & 'dism' '/English' "/image:$($ScratchDisk)\scratchdir" '/Get-ProvisionedAppxPackages' |
    ForEach-Object {
        if ($_ -match 'PackageName : (.*)') {
            $matches[1]
        }
    }
$packagePrefixes = 'Clipchamp.Clipchamp_', 'Microsoft.BingNews_', 'Microsoft.BingWeather_', 'Microsoft.GamingApp_', 'Microsoft.GetHelp_', 'Microsoft.Getstarted_', 'Microsoft.MicrosoftOfficeHub_', 'Microsoft.MicrosoftSolitaireCollection_', 'Microsoft.People_', 'Microsoft.PowerAutomateDesktop_', 'Microsoft.Todos_', 'Microsoft.WindowsAlarms_', 'microsoft.windowscommunicationsapps_', 'Microsoft.WindowsFeedbackHub_', 'Microsoft.WindowsMaps_', 'Microsoft.WindowsSoundRecorder_', 'Microsoft.Xbox.TCUI_', 'Microsoft.XboxGamingOverlay_', 'Microsoft.XboxGameOverlay_', 'Microsoft.XboxSpeechToTextOverlay_', 'Microsoft.YourPhone_', 'Microsoft.ZuneMusic_', 'Microsoft.ZuneVideo_', 'MicrosoftCorporationII.MicrosoftFamily_', 'MicrosoftCorporationII.QuickAssist_', 'MicrosoftTeams_', 'Microsoft.549981C3F5F10_'

$packagesToRemove = $packages | Where-Object {
    $packageName = $_
    $packagePrefixes -contains ($packagePrefixes | Where-Object { $packageName -like "$_*" })
}
foreach ($package in $packagesToRemove) {
    & 'dism' '/English' "/image:$($ScratchDisk)\scratchdir" '/Remove-ProvisionedAppxPackage' "/PackageName:$package"
}


Add-Log "Removing Edge:"
Remove-Item -Path "$ScratchDisk\scratchdir\Program Files (x86)\Microsoft\Edge" -Recurse -Force | Out-Null
Remove-Item -Path "$ScratchDisk\scratchdir\Program Files (x86)\Microsoft\EdgeUpdate" -Recurse -Force | Out-Null
Remove-Item -Path "$ScratchDisk\scratchdir\Program Files (x86)\Microsoft\EdgeCore" -Recurse -Force | Out-Null
if ($architecture -eq 'amd64') {
    $folderPath = Get-ChildItem -Path "$ScratchDisk\scratchdir\Windows\WinSxS" -Filter "amd64_microsoft-edge-webview_31bf3856ad364e35*" -Directory | Select-Object -ExpandProperty FullName

    if ($folderPath) {
        & 'takeown' '/f' $folderPath '/r' | Out-Null
        & icacls $folderPath  "/grant" "$($adminGroup.Value):(F)" '/T' '/C' | Out-Null
        Remove-Item -Path $folderPath -Recurse -Force | Out-Null
    } else {
        Add-Log "Folder not found."
    }
} elseif ($architecture -eq 'arm64') {
    $folderPath = Get-ChildItem -Path "$ScratchDisk\scratchdir\Windows\WinSxS" -Filter "arm64_microsoft-edge-webview_31bf3856ad364e35*" -Directory | Select-Object -ExpandProperty FullName | Out-Null

    if ($folderPath) {
        & 'takeown' '/f' $folderPath '/r'| Out-Null
        & icacls $folderPath  "/grant" "$($adminGroup.Value):(F)" '/T' '/C' | Out-Null
        Remove-Item -Path $folderPath -Recurse -Force | Out-Null
    } else {
        Add-Log "Folder not found."
    }
} else {
    Add-Log "Unknown architecture: $architecture"
}
& 'takeown' '/f' "$ScratchDisk\scratchdir\Windows\System32\Microsoft-Edge-Webview" '/r' | Out-Null
& 'icacls' "$ScratchDisk\scratchdir\Windows\System32\Microsoft-Edge-Webview" '/grant' "$($adminGroup.Value):(F)" '/T' '/C' | Out-Null
Remove-Item -Path "$ScratchDisk\scratchdir\Windows\System32\Microsoft-Edge-Webview" -Recurse -Force | Out-Null
Add-Log "Removing OneDrive:"
& 'takeown' '/f' "$ScratchDisk\scratchdir\Windows\System32\OneDriveSetup.exe" | Out-Null
& 'icacls' "$ScratchDisk\scratchdir\Windows\System32\OneDriveSetup.exe" '/grant' "$($adminGroup.Value):(F)" '/T' '/C' | Out-Null
Remove-Item -Path "$ScratchDisk\scratchdir\Windows\System32\OneDriveSetup.exe" -Force | Out-Null
Add-Log "Removal complete!"
Start-Sleep -Seconds 2
Clear-Host
Add-Log "Loading registry..."
reg load HKLM\zCOMPONENTS $ScratchDisk\scratchdir\Windows\System32\config\COMPONENTS | Out-Null
reg load HKLM\zDEFAULT $ScratchDisk\scratchdir\Windows\System32\config\default | Out-Null
reg load HKLM\zNTUSER $ScratchDisk\scratchdir\Users\Default\ntuser.dat | Out-Null
reg load HKLM\zSOFTWARE $ScratchDisk\scratchdir\Windows\System32\config\SOFTWARE | Out-Null
reg load HKLM\zSYSTEM $ScratchDisk\scratchdir\Windows\System32\config\SYSTEM | Out-Null
Add-Log "Bypassing system requirements(on the system image):"
Set-RegistryValue 'HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache' 'SV1' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache' 'SV2' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache' 'SV1' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache' 'SV2' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\zSYSTEM\Setup\LabConfig' 'BypassCPUCheck' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\zSYSTEM\Setup\LabConfig' 'BypassRAMCheck' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\zSYSTEM\Setup\LabConfig' 'BypassSecureBootCheck' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\zSYSTEM\Setup\LabConfig' 'BypassStorageCheck' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\zSYSTEM\Setup\LabConfig' 'BypassTPMCheck' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\zSYSTEM\Setup\MoSetup' 'AllowUpgradesWithUnsupportedTPMOrCPU' 'REG_DWORD' '1'
Add-Log "Disabling Sponsored Apps:"
Set-RegistryValue 'HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'OemPreInstalledAppsEnabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'PreInstalledAppsEnabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SilentInstalledAppsEnabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\zSOFTWARE\Policies\Microsoft\Windows\CloudContent' 'DisableWindowsConsumerFeatures' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'ContentDeliveryAllowed' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\zSOFTWARE\Microsoft\PolicyManager\current\device\Start' 'ConfigureStartPins' 'REG_SZ' '{"pinnedList": [{}]}'
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'ContentDeliveryAllowed' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'ContentDeliveryAllowed' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'FeatureManagementEnabled' 'REG_DWORD' '0' 
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'OemPreInstalledAppsEnabled' 'REG_DWORD' '0' 
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'PreInstalledAppsEnabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'PreInstalledAppsEverEnabled' 'REG_DWORD' '0' 
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SilentInstalledAppsEnabled' 'REG_DWORD' '0' 
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SoftLandingEnabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SubscribedContentEnabled' 'REG_DWORD' '0' 
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SubscribedContent-310093Enabled' 'REG_DWORD' '0' 
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SubscribedContent-338388Enabled' 'REG_DWORD' '0' 
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SubscribedContent-338389Enabled' 'REG_DWORD' '0' 
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SubscribedContent-338393Enabled' 'REG_DWORD' '0' 
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SubscribedContent-353694Enabled' 'REG_DWORD' '0' 
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SubscribedContent-353696Enabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SubscribedContentEnabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SystemPaneSuggestionsEnabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\zSOFTWARE\Policies\Microsoft\PushToInstall' 'DisablePushToInstall' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\zSOFTWARE\Policies\Microsoft\MRT' 'DontOfferThroughWUAU' 'REG_DWORD' '1'
Remove-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\Subscriptions'
Remove-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\SuggestedApps'
Set-RegistryValue 'HKLM\zSOFTWARE\Policies\Microsoft\Windows\CloudContent' 'DisableConsumerAccountStateContent' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\zSOFTWARE\Policies\Microsoft\Windows\CloudContent' 'DisableCloudOptimizedContent' 'REG_DWORD' '1'
Add-Log "Enabling Local Accounts on OOBE:"
Set-RegistryValue 'HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\OOBE' 'BypassNRO' 'REG_DWORD' '1'
Copy-Item -Path "$PSScriptRoot\autounattend.xml" -Destination "$ScratchDisk\scratchdir\Windows\System32\Sysprep\autounattend.xml" -Force | Out-Null
Add-Log "Disabling Reserved Storage:"
Set-RegistryValue 'HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\ReserveManager' 'ShippedWithReserves' 'REG_DWORD' '0'
Add-Log "Disabling BitLocker Device Encryption"
Set-RegistryValue 'HKLM\zSYSTEM\ControlSet001\Control\BitLocker' 'PreventDeviceEncryption' 'REG_DWORD' '1'
Add-Log "Disabling Chat icon:"
Set-RegistryValue 'HKLM\zSOFTWARE\Policies\Microsoft\Windows\Windows Chat' 'ChatIcon' 'REG_DWORD' '3'
Set-RegistryValue 'HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced' 'TaskbarMn' 'REG_DWORD' '0'
Add-Log "Removing Edge related registries"
Remove-RegistryValue "HKEY_LOCAL_MACHINE\zSOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft Edge"
Remove-RegistryValue  "HKEY_LOCAL_MACHINE\zSOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft Edge Update"
Add-Log "Disabling OneDrive folder backup"
Set-RegistryValue "HKLM\zSOFTWARE\Policies\Microsoft\Windows\OneDrive" 'DisableFileSyncNGSC' 'REG_DWORD' '1'
Add-Log "Disabling Telemetry:"
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo' 'Enabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Windows\CurrentVersion\Privacy' 'TailoredExperiencesWithDiagnosticDataEnabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy' 'HasAccepted' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Input\TIPC' 'Enabled' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\InputPersonalization' 'RestrictImplicitInkCollection' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\InputPersonalization' 'RestrictImplicitTextCollection' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\InputPersonalization\TrainedDataStore' 'HarvestContacts' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\zNTUSER\Software\Microsoft\Personalization\Settings' 'AcceptedPrivacyPolicy' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\zSOFTWARE\Policies\Microsoft\Windows\DataCollection' 'AllowTelemetry' 'REG_DWORD' '0'
Set-RegistryValue 'HKLM\zSYSTEM\ControlSet001\Services\dmwappushservice' 'Start' 'REG_DWORD' '4'
## Prevents installation or DevHome and Outlook
Add-Log "Prevents installation or DevHome and Outlook:"
Set-RegistryValue 'HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Orchestrator\UScheduler\OutlookUpdate' 'workCompleted' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Orchestrator\UScheduler\DevHomeUpdate' 'workCompleted' 'REG_DWORD' '1' 
Remove-RegistryValue 'HKLM\zSOFTWARE\Microsoft\WindowsUpdate\Orchestrator\UScheduler_Oobe\OutlookUpdate'
Remove-RegistryValue 'HKLM\zSOFTWARE\Microsoft\WindowsUpdate\Orchestrator\UScheduler_Oobe\DevHomeUpdate'

## this function allows PowerShell to take ownership of the Scheduled Tasks registry key from TrustedInstaller. Based on Jose Espitia's script.
function Enable-Privilege {
 param(
  [ValidateSet(
   "SeAssignPrimaryTokenPrivilege", "SeAuditPrivilege", "SeBackupPrivilege",
   "SeChangeNotifyPrivilege", "SeCreateGlobalPrivilege", "SeCreatePagefilePrivilege",
   "SeCreatePermanentPrivilege", "SeCreateSymbolicLinkPrivilege", "SeCreateTokenPrivilege",
   "SeDebugPrivilege", "SeEnableDelegationPrivilege", "SeImpersonatePrivilege", "SeIncreaseBasePriorityPrivilege",
   "SeIncreaseQuotaPrivilege", "SeIncreaseWorkingSetPrivilege", "SeLoadDriverPrivilege",
   "SeLockMemoryPrivilege", "SeMachineAccountPrivilege", "SeManageVolumePrivilege",
   "SeProfileSingleProcessPrivilege", "SeRelabelPrivilege", "SeRemoteShutdownPrivilege",
   "SeRestorePrivilege", "SeSecurityPrivilege", "SeShutdownPrivilege", "SeSyncAgentPrivilege",
   "SeSystemEnvironmentPrivilege", "SeSystemProfilePrivilege", "SeSystemtimePrivilege",
   "SeTakeOwnershipPrivilege", "SeTcbPrivilege", "SeTimeZonePrivilege", "SeTrustedCredManAccessPrivilege",
   "SeUndockPrivilege", "SeUnsolicitedInputPrivilege")]
  $Privilege,
  ## The process on which to adjust the privilege. Defaults to the current process.
  $ProcessId = $pid,
  ## Switch to disable the privilege, rather than enable it.
  [Switch] $Disable
 )
 $definition = @'
 using System;
 using System.Runtime.InteropServices;
  
 public class AdjPriv
 {
  [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
  internal static extern bool AdjustTokenPrivileges(IntPtr htok, bool disall,
   ref TokPriv1Luid newst, int len, IntPtr prev, IntPtr relen);
  
  [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
  internal static extern bool OpenProcessToken(IntPtr h, int acc, ref IntPtr phtok);
  [DllImport("advapi32.dll", SetLastError = true)]
  internal static extern bool LookupPrivilegeValue(string host, string name, ref long pluid);
  [StructLayout(LayoutKind.Sequential, Pack = 1)]
  internal struct TokPriv1Luid
  {
   public int Count;
   public long Luid;
   public int Attr;
  }
  
  internal const int SE_PRIVILEGE_ENABLED = 0x00000002;
  internal const int SE_PRIVILEGE_DISABLED = 0x00000000;
  internal const int TOKEN_QUERY = 0x00000008;
  internal const int TOKEN_ADJUST_PRIVILEGES = 0x00000020;
  public static bool EnablePrivilege(long processHandle, string privilege, bool disable)
  {
   bool retVal;
   TokPriv1Luid tp;
   IntPtr hproc = new IntPtr(processHandle);
   IntPtr htok = IntPtr.Zero;
   retVal = OpenProcessToken(hproc, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, ref htok);
   tp.Count = 1;
   tp.Luid = 0;
   if(disable)
   {
    tp.Attr = SE_PRIVILEGE_DISABLED;
   }
   else
   {
    tp.Attr = SE_PRIVILEGE_ENABLED;
   }
   retVal = LookupPrivilegeValue(null, privilege, ref tp.Luid);
   retVal = AdjustTokenPrivileges(htok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
   return retVal;
  }
 }
'@

 $processHandle = (Get-Process -id $ProcessId).Handle
 $type = Add-Type $definition -PassThru
 $type[0]::EnablePrivilege($processHandle, $Privilege, $Disable)
}

Enable-Privilege SeTakeOwnershipPrivilege

$regKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey("zSOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks",[Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree,[System.Security.AccessControl.RegistryRights]::TakeOwnership)
$regACL = $regKey.GetAccessControl()
$regACL.SetOwner($adminGroup)
$regKey.SetAccessControl($regACL)
$regKey.Close()
Add-Log "Owner changed to Administrators."
$regKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey("zSOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks",[Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree,[System.Security.AccessControl.RegistryRights]::ChangePermissions)
$regACL = $regKey.GetAccessControl()
$regRule = New-Object System.Security.AccessControl.RegistryAccessRule ($adminGroup,"FullControl","ContainerInherit","None","Allow")
$regACL.SetAccessRule($regRule)
$regKey.SetAccessControl($regACL)
Add-Log "Permissions modified for Administrators group."
Add-Log "Registry key permissions successfully updated."
$regKey.Close()

Add-Log 'Deleting Application Compatibility Appraiser'
Remove-RegistryValue "HKEY_LOCAL_MACHINE\zSOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks\{0600DD45-FAF2-4131-A006-0B17509B9F78}"
Add-Log 'Deleting Customer Experience Improvement Program'
Remove-RegistryValue "HKEY_LOCAL_MACHINE\zSOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks\{4738DE7A-BCC1-4E2D-B1B0-CADB044BFA81}"
Remove-RegistryValue "HKEY_LOCAL_MACHINE\zSOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks\{6FAC31FA-4A85-4E64-BFD5-2154FF4594B3}"
Remove-RegistryValue "HKEY_LOCAL_MACHINE\zSOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks\{FC931F16-B50A-472E-B061-B6F79A71EF59}"
Add-Log 'Deleting Program Data Updater' 
Remove-RegistryValue "HKEY_LOCAL_MACHINE\zSOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks\{0671EB05-7D95-4153-A32B-1426B9FE61DB}"
Add-Log 'Deleting autochk proxy'
Remove-RegistryValue "HKEY_LOCAL_MACHINE\zSOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks\{87BF85F4-2CE1-4160-96EA-52F554AA28A2}" 
Remove-RegistryValue "HKEY_LOCAL_MACHINE\zSOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks\{8A9C643C-3D74-4099-B6BD-9C6D170898B1}" 
Add-Log 'Deleting QueueReporting'
Remove-RegistryValue "HKEY_LOCAL_MACHINE\zSOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks\{E3176A65-4E44-4ED3-AA73-3283660ACB9C}"
Add-Log "Tweaking complete!"
Add-Log "Unmounting Registry..."
$regKey.Close()
reg unload HKLM\zCOMPONENTS | Out-Null
reg unload HKLM\zDRIVERS | Out-Null
reg unload HKLM\zDEFAULT | Out-Null
reg unload HKLM\zNTUSER | Out-Null
reg unload HKLM\zSCHEMA | Out-Null
reg unload HKLM\zSOFTWARE
reg unload HKLM\zSYSTEM | Out-Null
Add-Log "Cleaning up image..."
Repair-WindowsImage -Path $ScratchDisk\scratchdir -StartComponentCleanup -ResetBase
Add-Log "Cleanup complete."
Add-Log ' '
Add-Log "Unmounting image..."
Add-Log "The GUI will freeze during the process, please be patient and do not close the window."
Dismount-WindowsImage -Path $ScratchDisk\scratchdir -Save
Add-Log "Exporting image..."
# Compressiontype Recovery is not supported with PShell https://learn.microsoft.com/en-us/powershell/module/dism/export-windowsimage?view=windowsserver2022-ps#-compressiontype
Export-WindowsImage -SourceImagePath $ScratchDisk\tiny11\sources\install.wim -SourceIndex $index -DestinationImagePath $ScratchDisk\tiny11\sources\install2.wim -CompressionType Fast
Remove-Item -Path "$ScratchDisk\tiny11\sources\install.wim" -Force | Out-Null
Rename-Item -Path "$ScratchDisk\tiny11\sources\install2.wim" -NewName "install.wim" | Out-Null
Add-Log "Windows image completed. Continuing with boot.wim."
Start-Sleep -Seconds 2
Clear-Host
Add-Log "Mounting boot image:"
$wimFilePath = "$ScratchDisk\tiny11\sources\boot.wim" 
& takeown "/F" $wimFilePath | Out-Null
& icacls $wimFilePath "/grant" "$($adminGroup.Value):(F)"
Set-ItemProperty -Path $wimFilePath -Name IsReadOnly -Value $false
Mount-WindowsImage -ImagePath $ScratchDisk\tiny11\sources\boot.wim -Index 2 -Path $ScratchDisk\scratchdir
Add-Log "Loading registry..."
reg load HKLM\zCOMPONENTS $ScratchDisk\scratchdir\Windows\System32\config\COMPONENTS
reg load HKLM\zDEFAULT $ScratchDisk\scratchdir\Windows\System32\config\default
reg load HKLM\zNTUSER $ScratchDisk\scratchdir\Users\Default\ntuser.dat
reg load HKLM\zSOFTWARE $ScratchDisk\scratchdir\Windows\System32\config\SOFTWARE
reg load HKLM\zSYSTEM $ScratchDisk\scratchdir\Windows\System32\config\SYSTEM
Add-Log "Bypassing system requirements(on the setup image):"
Set-RegistryValue 'HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache' 'SV1' 'REG_DWORD' '0' 
Set-RegistryValue 'HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache' 'SV2' 'REG_DWORD' '0' 
Set-RegistryValue 'HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache' 'SV1' 'REG_DWORD' '0' 
Set-RegistryValue 'HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache' 'SV2' 'REG_DWORD' '0' 
Set-RegistryValue 'HKLM\zSYSTEM\Setup\LabConfig' 'BypassCPUCheck' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\zSYSTEM\Setup\LabConfig' 'BypassRAMCheck' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\zSYSTEM\Setup\LabConfig' 'BypassSecureBootCheck' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\zSYSTEM\Setup\LabConfig' 'BypassStorageCheck' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\zSYSTEM\Setup\LabConfig' 'BypassTPMCheck' 'REG_DWORD' '1'
Set-RegistryValue 'HKLM\zSYSTEM\Setup\MoSetup' 'AllowUpgradesWithUnsupportedTPMOrCPU' 'REG_DWORD' '1'
Add-Log "Tweaking complete!"
Add-Log "Unmounting Registry..."
$regKey.Close()
reg unload HKLM\zCOMPONENTS | Out-Null
reg unload HKLM\zDRIVERS | Out-Null
reg unload HKLM\zDEFAULT | Out-Null
reg unload HKLM\zNTUSER | Out-Null
reg unload HKLM\zSCHEMA | Out-Null
$regKey.Close()
reg unload HKLM\zSOFTWARE
reg unload HKLM\zSYSTEM | Out-Null
Add-Log "Unmounting image..."
Dismount-WindowsImage -Path $ScratchDisk\scratchdir -Save
Clear-Host
Add-Log "The tiny11 image is now completed. Proceeding with the making of the ISO..."
Add-Log "Copying unattended file for bypassing MS account on OOBE..."
Copy-Item -Path "$PSScriptRoot\autounattend.xml" -Destination "$ScratchDisk\tiny11\autounattend.xml" -Force | Out-Null
Add-Log "Creating ISO image..."
$ADKDepTools = "C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\$hostarchitecture\Oscdimg"
$localOSCDIMGPath = "$PSScriptRoot\oscdimg.exe"

if ([System.IO.Directory]::Exists($ADKDepTools)) {
    Add-Log "Will be using oscdimg.exe from system ADK."
    $OSCDIMG = "$ADKDepTools\oscdimg.exe"
} else {
    Add-Log "ADK folder not found. Will be using bundled oscdimg.exe."
    
    $url = "https://msdl.microsoft.com/download/symbols/oscdimg.exe/3D44737265000/oscdimg.exe"

    if (-not (Test-Path -Path $localOSCDIMGPath)) {
        Add-Log "Downloading oscdimg.exe..."
        Invoke-WebRequest -Uri $url -OutFile $localOSCDIMGPath

        if (Test-Path $localOSCDIMGPath) {
            Add-Log "oscdimg.exe downloaded successfully."
        } else {
            Write-Error "Failed to download oscdimg.exe."
            exit 1
        }
    } else {
        Add-Log "oscdimg.exe already exists locally."
    }

    $OSCDIMG = $localOSCDIMGPath
}

& "$OSCDIMG" '-m' '-o' '-u2' '-udfver102' "-bootdata:2#p0,e,b$ScratchDisk\tiny11\boot\etfsboot.com#pEF,e,b$ScratchDisk\tiny11\efi\microsoft\boot\efisys.bin" "$ScratchDisk\tiny11" "$PSScriptRoot\tiny11.iso"

# Finishing up
[System.Windows.Forms.MessageBox]::Show("Process completed successfully.", "Completion", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Information)
Add-Log "Performing Cleanup..."
Remove-Item -Path "$ScratchDisk\tiny11" -Recurse -Force | Out-Null
Remove-Item -Path "$ScratchDisk\scratchdir" -Recurse -Force | Out-Null
Add-Log "Cleanup complete!"
Add-Log "Dismount used images..."
Get-Volume -DriveLetter $DriveComboBox.SelectedItem | Get-DiskImage | Dismount-DiskImage # by 790 at https://rcmtech.wordpress.com/2012/12/07/powershell-mounting-and-dismounting-iso-images-on-windows-server-2012-and-windows-8/
Add-Log "Dismount complete!"
Add-Log " "
Add-Log "You can close the app now."
})
})

# Logs Label
$LogsLabel = New-Object System.Windows.Forms.Label
$LogsLabel.Text = "Logs:"
$LogsLabel.Font = New-Object System.Drawing.Font('Consolas', 12)
$LogsLabel.Location = New-Object System.Drawing.Point(20, 180)
$LogsLabel.AutoSize = $true

# Logs TextBox
$LogsTextBox = New-Object System.Windows.Forms.TextBox
$LogsTextBox.Multiline = $true
$LogsTextBox.ScrollBars = 'Vertical'
$LogsTextBox.Location = New-Object System.Drawing.Point(20, 210)
$LogsTextBox.Width = 430
$LogsTextBox.Height = 180
$LogsTextBox.ReadOnly = $true
Add-Log "main_form.Controls loaded..."

# Adding Controls to Form
$main_form.Controls.Add($TitleLabel)
$main_form.Controls.Add($IsoTextBox)
$main_form.Controls.Add($ChooseButton)
$main_form.Controls.Add($MountButton)
$main_form.Controls.Add($DriveLabel)
$main_form.Controls.Add($DriveComboBox)
$main_form.Controls.Add($ImageIndexLabel)
$main_form.Controls.Add($ImageIndexComboBox)
$main_form.Controls.Add($LogsLabel)
$main_form.Controls.Add($LogsTextBox)
$main_form.Controls.Add($StartButton)

# Show Form
$main_form.ShowDialog()
