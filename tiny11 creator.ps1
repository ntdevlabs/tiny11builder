Write-Output "Welcome to the tiny11 image creator!"
$DriveLetter = Read-Host -Prompt "Please enter the drive letter for the Windows 11 image"

if ( -not ( Test-Path -Path $DriveLetter":\sources\boot.wim" -PathType Leaf ) ) {
	Write-Output "Can't find Windows OS Installation files in the specified Drive Letter.."
	Write-Output ""
	Write-Output "Please enter the correct DVD Drive Letter.."
	exit
}

if ( -not ( Test-Path -Path $DriveLetter":\sources\install.wim" -PathType Leaf ) ) {
	Write-Output "Can't find Windows OS Installation files in the specified Drive Letter.."
	Write-Output ""
	Write-Output "Please enter the correct DVD Drive Letter.."
	exit
}

md "c:\tiny11" >$null 2>&1
Write-Output "Copying Windows image..."
xcopy.exe /E /I /H /R /Y /J $DriveLetter":" c:\tiny11 >$null
Write-Output "Copy complete!"
Start-Sleep -Seconds 2

Write-Output "Getting image information:"
dism /Get-WimInfo /wimfile:c:\tiny11\sources\install.wim
$index = Read-Host -Prompt "Please enter the image index"
Write-Output "Mounting Windows image. This may take a while."
md "c:\scratchdir" >$null 2>&1
dism /mount-image /imagefile:c:\tiny11\sources\install.wim /index:$index /mountdir:c:\scratchdir
Write-Output "Mounting complete! Performing removal of applications..."
$appxfind = "Clipchamp.Clipchamp", "Microsoft.BingNews", "Microsoft.BingWeather", "Microsoft.GamingApp", `
	"Microsoft.GetHelp", "Microsoft.Getstarted", "Microsoft.MicrosoftOfficeHub", `
	"Microsoft.MicrosoftSolitaireCollection", "Microsoft.People", "Microsoft.PowerAutomateDesktop", `
	"Microsoft.Todos", "Microsoft.WindowsAlarms", "microsoft.windowscommunicationsapps", `
	"Microsoft.WindowsFeedbackHub", "Microsoft.WindowsMaps", "Microsoft.WindowsSoundRecorder", `
	"Microsoft.Xbox.TCUI", "Microsoft.XboxGamingOverlay", "Microsoft.XboxGameOverlay", `
	"Microsoft.XboxSpeechToTextOverlay", "Microsoft.YourPhone", "Microsoft.ZuneMusic", "Microsoft.ZuneVideo", `
	"MicrosoftCorporationII.MicrosoftFamily", "MicrosoftCorporationII.QuickAssist", "MicrosoftTeams", `
	"Microsoft.549981C3F5F10"
$appxlist = dism /image:c:\scratchdir /Get-ProvisionedAppxPackages | Select-String -Pattern 'PackageName : ' -CaseSensitive -SimpleMatch
$appxlist = $appxlist -split "PackageName : " | where {$_}

foreach ( $appxlookup in $appxfind ) {
	Write-Output "Finding $appxlookup ..."
	$appxremove = $appxlist | Select-String -Pattern $appxlookup -CaseSensitive -SimpleMatch
	foreach ( $appx in $appxremove ) {
		Write-Output "Removing $appx"
		dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:$appx >$null
	}
	if ( -not ( $appxremove ) ) {
		Write-Output "$appxlookup was not found!"
	}
}

Write-Output "Removing of system apps complete! Now proceeding to removal of system packages..."
Start-Sleep -Seconds 1

$packagefind = "Microsoft-Windows-InternetExplorer-Optional-Package", "Microsoft-Windows-Kernel-LA57-FoD-Package", `
	"Microsoft-Windows-LanguageFeatures-Handwriting", "Microsoft-Windows-LanguageFeatures-OCR", `
	"Microsoft-Windows-LanguageFeatures-Speech", "Microsoft-Windows-LanguageFeatures-TextToSpeech", `
	"Microsoft-Windows-MediaPlayer-Package", "Microsoft-Windows-TabletPCMath-Package", `
	"Microsoft-Windows-Wallpaper-Content-Extended-FoD-Package"
$packagelist = dism /image:c:\scratchdir /Get-Packages /Format:List | Select-String -Pattern 'Package Identity : ' -CaseSensitive -SimpleMatch
$packagelist = $packagelist -split "Package Identity : " | where {$_}

foreach ( $packagelookup in $packagefind ) {
	Write-Output "Finding $packagelookup ..."
	$packageremove = $packagelist | Select-String -Pattern $packagelookup -CaseSensitive -SimpleMatch
	foreach ( $package in $packageremove ) {
		Write-Output "Removing $package"
		dism /image:c:\scratchdir /Remove-Package /PackageName:$package >$null
	}
	if ( -not ( $packageremove ) ) {
		Write-Output "$packagelookup was not found!"
	}
}

Write-Output "Removing Edge:"
Remove-Item -Recurse -Force "C:\scratchdir\Program Files (x86)\Microsoft\Edge"
Remove-Item -Recurse -Force "C:\scratchdir\Program Files (x86)\Microsoft\EdgeUpdate"
Write-Output "Removing OneDrive:"
takeown /f C:\scratchdir\Windows\System32\OneDriveSetup.exe
icacls C:\scratchdir\Windows\System32\OneDriveSetup.exe /grant Administrators:F /T /C
Remove-Item -Force "C:\scratchdir\Windows\System32\OneDriveSetup.exe"
Write-Output "Removal complete!"
Start-Sleep -Seconds 2

Write-Output "Loading registry..."
reg load HKLM\zCOMPONENTS "c:\scratchdir\Windows\System32\config\COMPONENTS" >$null
reg load HKLM\zDEFAULT "c:\scratchdir\Windows\System32\config\default" >$null
reg load HKLM\zNTUSER "c:\scratchdir\Users\Default\ntuser.dat" >$null
reg load HKLM\zSOFTWARE "c:\scratchdir\Windows\System32\config\SOFTWARE" >$null
reg load HKLM\zSYSTEM "c:\scratchdir\Windows\System32\config\SYSTEM" >$null
Write-Output "Bypassing system requirements(on the system image):"
			Reg add "HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache" /v "SV1" /t REG_DWORD /d "0" /f >$null 2>&1
			Reg add "HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache" /v "SV2" /t REG_DWORD /d "0" /f >$null 2>&1
			Reg add "HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache" /v "SV1" /t REG_DWORD /d "0" /f >$null 2>&1
			Reg add "HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache" /v "SV2" /t REG_DWORD /d "0" /f >$null 2>&1
			Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassCPUCheck" /t REG_DWORD /d "1" /f >$null 2>&1
			Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassRAMCheck" /t REG_DWORD /d "1" /f >$null 2>&1
			Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassSecureBootCheck" /t REG_DWORD /d "1" /f >$null 2>&1
			Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassStorageCheck" /t REG_DWORD /d "1" /f >$null 2>&1
			Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassTPMCheck" /t REG_DWORD /d "1" /f >$null 2>&1
			Reg add "HKLM\zSYSTEM\Setup\MoSetup" /v "AllowUpgradesWithUnsupportedTPMOrCPU" /t REG_DWORD /d "1" /f >$null 2>&1
Write-Output "Disabling Teams:"
Reg add "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\Communications" /v "ConfigureChatAutoInstall" /t REG_DWORD /d "0" /f >$null 2>&1
Write-Output "Disabling Sponsored Apps:"
Reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v "OemPreInstalledAppsEnabled" /t REG_DWORD /d "0" /f >$null 2>&1
			Reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v "PreInstalledAppsEnabled" /t REG_DWORD /d "0" /f >$null 2>&1
			Reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v "SilentInstalledAppsEnabled" /t REG_DWORD /d "0" /f >$null 2>&1
			Reg add "HKLM\zSOFTWARE\Policies\Microsoft\Windows\CloudContent" /v "DisableWindowsConsumerFeatures" /t REG_DWORD /d "1" /f >$null 2>&1
			Reg add "HKLM\zSOFTWARE\Microsoft\PolicyManager\current\device\Start" /v "ConfigureStartPins" /t REG_SZ /d "{\"pinnedList\": [{}]}" /f >$null 2>&1
Write-Output "Enabling Local Accounts on OOBE:"
Reg add "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\OOBE" /v "BypassNRO" /t REG_DWORD /d "1" /f >$null 2>&1
copy $pwd\autounattend.xml c:\scratchdir\Windows\System32\Sysprep\autounattend.xml
Write-Output "Disabling Reserved Storage:"
Reg add "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\ReserveManager" /v "ShippedWithReserves" /t REG_DWORD /d "0" /f >$null 2>&1
Write-Output "Disabling Chat icon:"
Reg add "HKLM\zSOFTWARE\Policies\Microsoft\Windows\Windows Chat" /v "ChatIcon" /t REG_DWORD /d "3" /f >$null 2>&1
Reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v "TaskbarMn" /t REG_DWORD /d "0" /f >$null 2>&1
Write-Output "Tweaking complete!"
Write-Output "Unmounting Registry..."
reg unload HKLM\zCOMPONENTS >$null 2>&1
reg unload HKLM\zDRIVERS >$null 2>&1
reg unload HKLM\zDEFAULT >$null 2>&1
reg unload HKLM\zNTUSER >$null 2>&1
reg unload HKLM\zSCHEMA >$null 2>&1
reg unload HKLM\zSOFTWARE >$null 2>&1
reg unload HKLM\zSYSTEM >$null 2>&1
Write-Output "Cleaning up image..."
dism /image:c:\scratchdir /Cleanup-Image /StartComponentCleanup /ResetBase
Write-Output "Cleanup complete."
Write-Output "Unmounting image..."
dism /unmount-image /mountdir:c:\scratchdir /commit
Write-Output "Exporting image..."
Dism /Export-Image /SourceImageFile:c:\tiny11\sources\install.wim /SourceIndex:$index /DestinationImageFile:c:\tiny11\sources\install2.wim /compress:max
Remove-Item c:\tiny11\sources\install.wim
Rename-Item c:\tiny11\sources\install2.wim install.wim
Write-Output "Windows image completed. Continuing with boot.wim."
Start-Sleep -Seconds 2

Write-Output "Mounting boot image:"
dism /mount-image /imagefile:c:\tiny11\sources\boot.wim /index:2 /mountdir:c:\scratchdir
Write-Output "Loading registry..."
reg load HKLM\zCOMPONENTS "c:\scratchdir\Windows\System32\config\COMPONENTS" >$null
reg load HKLM\zDEFAULT "c:\scratchdir\Windows\System32\config\default" >$null
reg load HKLM\zNTUSER "c:\scratchdir\Users\Default\ntuser.dat" >$null
reg load HKLM\zSOFTWARE "c:\scratchdir\Windows\System32\config\SOFTWARE" >$null
reg load HKLM\zSYSTEM "c:\scratchdir\Windows\System32\config\SYSTEM" >$null
Write-Output "Bypassing system requirements(on the setup image):"
			Reg add "HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache" /v "SV1" /t REG_DWORD /d "0" /f >$null 2>&1
			Reg add "HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache" /v "SV2" /t REG_DWORD /d "0" /f >$null 2>&1
			Reg add "HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache" /v "SV1" /t REG_DWORD /d "0" /f >$null 2>&1
			Reg add "HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache" /v "SV2" /t REG_DWORD /d "0" /f >$null 2>&1
			Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassCPUCheck" /t REG_DWORD /d "1" /f >$null 2>&1
			Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassRAMCheck" /t REG_DWORD /d "1" /f >$null 2>&1
			Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassSecureBootCheck" /t REG_DWORD /d "1" /f >$null 2>&1
			Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassStorageCheck" /t REG_DWORD /d "1" /f >$null 2>&1
			Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassTPMCheck" /t REG_DWORD /d "1" /f >$null 2>&1
			Reg add "HKLM\zSYSTEM\Setup\MoSetup" /v "AllowUpgradesWithUnsupportedTPMOrCPU" /t REG_DWORD /d "1" /f >$null 2>&1
Write-Output "Tweaking complete!"
Write-Output "Unmounting Registry..."
reg unload HKLM\zCOMPONENTS >$null 2>&1
reg unload HKLM\zDRIVERS >$null 2>&1
reg unload HKLM\zDEFAULT >$null 2>&1
reg unload HKLM\zNTUSER >$null 2>&1
reg unload HKLM\zSCHEMA >$null 2>&1
reg unload HKLM\zSOFTWARE >$null 2>&1
reg unload HKLM\zSYSTEM >$null 2>&1
Write-Output "Unmounting image..."
dism /unmount-image /mountdir:c:\scratchdir /commit 

Write-Output "the tiny11 image is now completed. Proceeding with the making of the ISO..."
Write-Output "Copying unattended file for bypassing MS account on OOBE..."
Copy-Item $pwd\autounattend.xml c:\tiny11\autounattend.xml
Write-Output ""
Write-Output "Creating ISO image..."
& $pwd\oscdimg.exe -m -o -u2 -udfver102 -bootdata:2#p0,e,bc:\tiny11\boot\etfsboot.com#pEF,e,bc:\tiny11\efi\microsoft\boot\efisys.bin c:\tiny11 $pwd\tiny11.iso
Write-Output "Creation completed! Press any key to exit the script..."
pause 
Write-Output "Performing Cleanup..."
Remove-Item -Recurse -Force "c:\scratchdir"
Remove-Item -Recurse -Force "c:\tiny11"
