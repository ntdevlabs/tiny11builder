
$packages_to_remove = @("Clipchamp.Clipchamp", "Microsoft.BingNews", "Microsoft.BingWeather", "Microsoft.GamingApp", "Microsoft.GetHelp", "Microsoft.Getstarted", "Microsoft.MicrosoftOfficeHub", "Microsoft.MicrosoftSolitaireCollection", "Microsoft.People", "Microsoft.PowerAutomateDesktop", "Microsoft.Todos", "Microsoft.WindowsAlarms", "microsoft.windowscommunicationsapps", "Microsoft.WindowsFeedbackHub", "Microsoft.WindowsMaps", "Microsoft.WindowsSoundRecorder", "Microsoft.Xbox.TCUI", "Microsoft.XboxGamingOverlay", "Microsoft.XboxGameOverlay", "Microsoft.XboxSpeechToTextOverlay", "Microsoft.YourPhone", "Microsoft.ZuneMusic", "Microsoft.ZuneVideo", "MicrosoftCorporationII.MicrosoftFamily", "MicrosoftCorporationII.QuickAssist", "MicrosoftTeams", "Microsoft.549981C3F5F10", "Microsoft-Windows-InternetExplorer-Optional-Package", "Microsoft-Windows-Kernel-LA57-FoD-Package", "Microsoft-Windows-LanguageFeatures-Handwriting", "Microsoft-Windows-LanguageFeatures-OCR", "Microsoft-Windows-LanguageFeatures-Speech", "Microsoft-Windows-LanguageFeatures-TextToSpeech", "Microsoft-Windows-MediaPlayer-Package", "Microsoft-Windows-TabletPCMath-Package", "Microsoft-Windows-Wallpaper-Content-Extended-FoD-Package")
Write-Host "Welcome to the tiny11 image creator!"

$drive_letter = Read-Host -Prompt 'Please enter the drive letter for the Windows 11 image'
$drive = "$($drive_letter):"

$boot_wim = "$drive\sources\boot.wim"
$install_wim = "$drive\sources\install.wim"

if(  (-Not (Test-Path -Path $install_wim)) -Or (-Not (Test-Path -Path $boot_wim))) {
	Write-Host "Can't find Windows OS Installation files in the specified Drive Letter..`nPlease enter the correct DVD Drive Letter.."
}
New-Item -ItemType Directory -Path C:\tiny11 2>&1>$null
xcopy.exe /E /I /H /R /Y /J $drive c:\tiny11 2>&1>$null
Write-Host Copying Windows image...
Write-Host Copy complete!
Write-Host Getting image information:
dism /Get-WimInfo /wimfile:c:\tiny11\sources\install.wim
[int]$image_index = 1
while($true) {
	try{
		[int]$image_index = Read-Host -Prompt 'Please enter the image index. Ej:2'
		break
	}catch {
		Write-Host "Invalid Image index"
	}
}
Write-Host "Mounting Windows image. This may take a while."
New-Item -ItemType Directory -Path C:\scratchdir 2>&1>$null
dism /mount-image /imagefile:c:\tiny11\sources\install.wim /index:$image_index /mountdir:c:\scratchdir
Write-Host "Mounting complete! Performing removal of applications..."

$regex = "^Package Identity : ([^\n]+)$"
$package_list = dism /image:c:\scratchdir /Get-Packages

$lines = $package_list.split("`n")
$installed_packages= New-Object System.Collections.ArrayList

foreach($line in $lines) {
	if($line -match $regex) {
		$package_name = $Matches[1]
		foreach($package_to_remove in $packages_to_remove) {
			if($package_name.StartsWith($package_to_remove)) {
				$installed_packages.Add($package_name) > $null
			}
		}
		
	}
}

foreach($package in $installed_packages) {
	Write-Host "Removing $package"
	dism /image:c:\scratchdir /Remove-Package /PackageName:$package > $null
}
Write-Host "Removing Edge:"
Remove-Item -Force -Recurse -Path  "C:\scratchdir\Program Files (x86)\Microsoft\Edge" 2>&1>$null
Remove-Item -Force -Recurse -Path  "C:\scratchdir\Program Files (x86)\Microsoft\EdgeUpdate" 2>&1>$null
Write-Host "Removing OneDrive:"
takeown /f C:\scratchdir\Windows\System32\OneDriveSetup.exe
icacls C:\scratchdir\Windows\System32\OneDriveSetup.exe /grant Administrators:F /T /C 2>&1>$null
Remove-Item -Force -Path "C:\scratchdir\Windows\System32\OneDriveSetup.exe" 2>&1>$null
Write-Host "Removal complete!"
Start-Sleep 2
Write-Host "Loading registry..."

reg load HKLM\zCOMPONENTS "c:\scratchdir\Windows\System32\config\COMPONENTS" >$null
reg load HKLM\zDEFAULT "c:\scratchdir\Windows\System32\config\default" >$null
reg load HKLM\zNTUSER "c:\scratchdir\Users\Default\ntuser.dat" >$null
reg load HKLM\zSOFTWARE "c:\scratchdir\Windows\System32\config\SOFTWARE" >$null
reg load HKLM\zSYSTEM "c:\scratchdir\Windows\System32\config\SYSTEM" >$null
Write-Host "Bypassing system requirements(on the system image):"
Reg add "HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache" /v "SV1" /t REG_DWORD /d "0" /f 2>&1>$null
Reg add "HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache" /v "SV2" /t REG_DWORD /d "0" /f 2>&1>$null
Reg add "HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache" /v "SV1" /t REG_DWORD /d "0" /f 2>&1>$null
Reg add "HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache" /v "SV2" /t REG_DWORD /d "0" /f 2>&1>$null
Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassCPUCheck" /t REG_DWORD /d "1" /f 2>&1>$null
Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassRAMCheck" /t REG_DWORD /d "1" /f 2>&1>$null
Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassSecureBootCheck" /t REG_DWORD /d "1" /f 2>&1>$null
Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassStorageCheck" /t REG_DWORD /d "1" /f 2>&1>$null
Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassTPMCheck" /t REG_DWORD /d "1" /f 2>&1>$null
Reg add "HKLM\zSYSTEM\Setup\MoSetup" /v "AllowUpgradesWithUnsupportedTPMOrCPU" /t REG_DWORD /d "1" /f 2>&1>$null
Write-Host "Disabling Teams:"
Reg add "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\Communications" /v "ConfigureChatAutoInstall" /t REG_DWORD /d "0" /f 2>&1>$null
Write-Host "Disabling Sponsored Apps:"
Reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v "OemPreInstalledAppsEnabled" /t REG_DWORD /d "0" /f 2>&1>$null
Reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v "PreInstalledAppsEnabled" /t REG_DWORD /d "0" /f 2>&1>$null
Reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v "SilentInstalledAppsEnabled" /t REG_DWORD /d "0" /f 2>&1>$null
Reg add "HKLM\zSOFTWARE\Policies\Microsoft\Windows\CloudContent" /v "DisableWindowsConsumerFeatures" /t REG_DWORD /d "1" /f 2>&1>$null
Reg add "HKLM\zSOFTWARE\Microsoft\PolicyManager\current\device\Start" /v "ConfigureStartPins" /t REG_SZ /d "{`"pinnedList`": [{}]}" /f 2>&1>$null
Write-Host "Enabling Local Accounts on OOBE:"
Reg add "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\OOBE" /v "BypassNRO" /t REG_DWORD /d "1" /f 2>&1>$null
Copy-Item -Force .\autounattend.xml c:\scratchdir\Windows\System32\Sysprep\autounattend.xml
Write-Host "Disabling Reserved Storage:"
Reg add "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\ReserveManager" /v "ShippedWithReserves" /t REG_DWORD /d "0" /f 2>&1>$null
Write-Host "Disabling Chat icon:"
Reg add "HKLM\zSOFTWARE\Policies\Microsoft\Windows\Windows Chat" /v "ChatIcon" /t REG_DWORD /d "3" /f 2>&1>$null
Reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v "TaskbarMn" /t REG_DWORD /d "0" /f 2>&1>$null
Write-Host "Tweaking complete!"
Write-Host "Unmounting Registry..."
reg unload HKLM\zCOMPONENTS 2>&1>$null
reg unload HKLM\zDRIVERS 2>&1>$null
reg unload HKLM\zDEFAULT 2>&1>$null
reg unload HKLM\zNTUSER 2>&1>$null
reg unload HKLM\zSCHEMA 2>&1>$null
reg unload HKLM\zSOFTWARE 2>&1>$null
reg unload HKLM\zSYSTEM 2>&1>$null
Write-Host "Cleaning up image..."
dism /image:c:\scratchdir /Cleanup-Image /StartComponentCleanup /ResetBase
Write-Host "Cleanup complete."
Write-Host "Unmounting image..."
dism /unmount-image /mountdir:c:\scratchdir /commit
Write-Host "Exporting image..."
Dism /Export-Image /SourceImageFile:c:\tiny11\sources\install.wim /SourceIndex:$image_index /DestinationImageFile:c:\tiny11\sources\install2.wim /compress:max
Remove-Item c:\tiny11\sources\install.wim
Rename-Item c:\tiny11\sources\install2.wim install.wim
Write-Host "Windows image completed. Continuing with boot.wim."
Start-Sleep 2
Write-Host "Mounting boot image:"
dism /mount-image /imagefile:c:\tiny11\sources\boot.wim /index:2 /mountdir:c:\scratchdir
Write-Host "Loading registry..."
reg load HKLM\zCOMPONENTS "c:\scratchdir\Windows\System32\config\COMPONENTS" >$null
reg load HKLM\zDEFAULT "c:\scratchdir\Windows\System32\config\default" >$null
reg load HKLM\zNTUSER "c:\scratchdir\Users\Default\ntuser.dat" >$null
reg load HKLM\zSOFTWARE "c:\scratchdir\Windows\System32\config\SOFTWARE" >$null
reg load HKLM\zSYSTEM "c:\scratchdir\Windows\System32\config\SYSTEM" >$null
Write-Host "Bypassing system requirements(on the setup image):"
Reg add "HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache" /v "SV1" /t REG_DWORD /d "0" /f 2>&1>$null
Reg add "HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache" /v "SV2" /t REG_DWORD /d "0" /f 2>&1>$null
Reg add "HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache" /v "SV1" /t REG_DWORD /d "0" /f 2>&1>$null
Reg add "HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache" /v "SV2" /t REG_DWORD /d "0" /f 2>&1>$null
Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassCPUCheck" /t REG_DWORD /d "1" /f 2>&1>$null
Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassRAMCheck" /t REG_DWORD /d "1" /f 2>&1>$null
Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassSecureBootCheck" /t REG_DWORD /d "1" /f 2>&1>$null
Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassStorageCheck" /t REG_DWORD /d "1" /f 2>&1>$null
Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassTPMCheck" /t REG_DWORD /d "1" /f 2>&1>$null
Reg add "HKLM\zSYSTEM\Setup\MoSetup" /v "AllowUpgradesWithUnsupportedTPMOrCPU" /t REG_DWORD /d "1" /f 2>&1>$null
Write-Host "Tweaking complete! "
Write-Host "Unmounting Registry..."
reg unload HKLM\zCOMPONENTS 2>&1>$null
reg unload HKLM\zDRIVERS 2>&1>$null
reg unload HKLM\zDEFAULT 2>&1>$null
reg unload HKLM\zNTUSER 2>&1>$null
reg unload HKLM\zSCHEMA 2>&1>$null
reg unload HKLM\zSOFTWARE 2>&1>$null
reg unload HKLM\zSYSTEM 2>&1>$null
Write-Host "Unmounting image..."
dism /unmount-image /mountdir:c:\scratchdir /commit
Write-Host "the tiny11 image is now completed. Proceeding with the making of the ISO..."
Write-Host "Copying unattended file for bypassing MS account on OOBE..."
Copy-Item -Force .\autounattend.xml c:\tiny11\autounattend.xml
Write-Host "Creating ISO image..."
Start-Process -FilePath "oscdimg.exe" -ArgumentList "-m -o -u2 -udfver102 -bootdata:2#p0,e,bc:\tiny11\boot\etfsboot.com#pEF,e,bc:\tiny11\efi\microsoft\boot\efisys.bin c:\tiny11 .\tiny11.iso" -NoNewWindow -Wait

Write-Host "Creation completed! Press any key to exit the script..."
Write-Host "Performing Cleanup..."
Remove-Item -Recurse -Path C:\tiny11 -Force
Remove-Item -Recurse -Path C:\scratchdir -Force