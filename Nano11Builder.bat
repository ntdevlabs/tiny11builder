@echo off
setlocal EnableExtensions EnableDelayedExpansion

title Nano11 Builder
echo Welcome to the Nano11 image creator!
timeout /t 3 /nobreak > nul
cls

set DriveLetter=
set /p DriveLetter=Please enter the drive letter for the Windows 11 image to modify: 
set "DriveLetter=%DriveLetter%:"
echo.
if not exist "%DriveLetter%\sources\boot.wim" (
	echo.Can't find Windows OS Installation files in the specified Drive Letter..
	echo.
	echo.Please enter the correct DVD Drive Letter..
	goto :Stop
)

if not exist "%DriveLetter%\sources\install.wim" (
	echo.Can't find Windows OS Installation files in the specified Drive Letter..
	echo.
	echo.Please enter the correct DVD Drive Letter..
	goto :Stop
)
md C:\nano11
echo Copying Windows image...
xcopy.exe /E /I /H /R /Y /J %DriveLetter% C:\nano11 >nul
echo Copy complete!
sleep 2
cls
echo Getting image information:
dism /Get-WimInfo /wimfile:c:\nano11\sources\install.wim
set index=
set /p index=Please enter the image index:
set "index=%index%"
echo Mounting Windows image. This may take a while.
echo.
md c:\scratchdir
dism /mount-image /imagefile:C:\nano11\sources\install.wim /index:%index% /mountdir:c:\scratchdir
echo Mounting complete! Performing removal of applications...
echo Removing Clipchamp...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Clipchamp.Clipchamp*
echo Removing Microsoft News...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.BingNews*
echo Removing Microsoft Weather...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.BingWeather*
echo Removing Xbox...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.GamingApp*
echo Removing GetHelp...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.GetHelp*
echo Removing GetStarted...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.Getstarted*
echo Removing Office...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.MicrosoftOfficeHub*
echo Removing Solitaire Collection...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.MicrosoftSolitaireCollection*
echo Removing People App...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.People*
echo Removing PowerAutomate...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.PowerAutomateDesktop*
echo Removing Microsoft To Do...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.Todos*
echo Removing Clock...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.WindowsAlarms*
echo Removing Mail...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:microsoft.windowscommunicationsapps*
echo Removing Feedback Hub...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.WindowsFeedbackHub*
echo Removing Maps...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.WindowsMaps*
echo Removing Sound Recorder...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.WindowsSoundRecorder*
echo Removing Xbox TCUI...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.Xbox.TCUI*e
echo Removing Xbox Gaming Overlay...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.XboxGamingOverlay*
echo Removing Xbox Game Overlay...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.XboxGameOverlay*
echo Removing XboxSpeechToTextOverlay...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.XboxSpeechToTextOverlay*
echo Removing Your Phone...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.YourPhone*
echo Removing Music...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.ZuneMusic*
echo Removing Video...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.ZuneVideo*
echo Removing Family...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:MicrosoftCorporationII.MicrosoftFamily*
echo Removing QuickAssist...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:MicrosoftCorporationII.QuickAssist*
echo Removing Teams...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:MicrosoftTeams*
echo Removing Cortana...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.549981C3F5F10*

echo Removing of system apps complete! Now proceeding to removal of system packages...
timeout /t 1 /nobreak > nul
cls
echo Removing Handwriting:
dism /image:c:\scratchdir /Remove-Package /PackageName:Microsoft-Windows-LanguageFeatures-Handwriting-en-us-Package~31bf3856ad364e35~amd64~~10.0.22621.1265 > nul
echo Removing OCR:
dism /image:c:\scratchdir /Remove-Package /PackageName:Microsoft-Windows-LanguageFeatures-OCR-en-us-Package~31bf3856ad364e35~amd64~~10.0.22621.1265 > nul
echo Removing Speech:
dism /image:c:\scratchdir /Remove-Package /PackageName:Microsoft-Windows-LanguageFeatures-Speech-en-us-Package~31bf3856ad364e35~amd64~~10.0.22621.1265 > nul
echo Removing TTS:
dism /image:c:\scratchdir /Remove-Package /PackageName:Microsoft-Windows-LanguageFeatures-TextToSpeech-en-us-Package~31bf3856ad364e35~amd64~~10.0.22621.1265 > nul
echo Removing Media Player Legacy:
dism /image:c:\scratchdir /Remove-Package /PackageName:Microsoft-Windows-MediaPlayer-Package~31bf3856ad364e35~amd64~~10.0.22621.1265 > nul
dism /image:c:\scratchdir /Remove-Package /PackageName:Microsoft-Windows-MediaPlayer-Package~31bf3856ad364e35~wow64~en-US~10.0.22621.1 > nul
dism /image:c:\scratchdir /Remove-Package /PackageName:Microsoft-Windows-MediaPlayer-Package~31bf3856ad364e35~amd64~~10.0.22621.1265 > nul
dism /image:c:\scratchdir /Remove-Package /PackageName:Microsoft-Windows-MediaPlayer-Package~31bf3856ad364e35~wow64~~10.0.22621.1 > nul

echo Removing OneDrive:
takeown /f C:\scratchdir\Windows\System32\OneDriveSetup.exe
icacls C:\scratchdir\Windows\System32\OneDriveSetup.exe /grant Administrators:F /T /C
del /f /q /s "C:\scratchdir\Windows\System32\OneDriveSetup.exe"
echo Removal complete!
timeout /t 2 /nobreak > nul
cls
echo Loading registry...
reg load HKLM\zCOMPONENTS "c:\scratchdir\Windows\System32\config\COMPONENTS" >nul
reg load HKLM\zDEFAULT "c:\scratchdir\Windows\System32\config\default" >nul
reg load HKLM\zNTUSER "c:\scratchdir\Users\Default\ntuser.dat" >nul
reg load HKLM\zSOFTWARE "c:\scratchdir\Windows\System32\config\SOFTWARE" >nul
reg load HKLM\zSYSTEM "c:\scratchdir\Windows\System32\config\SYSTEM" >nul
echo Bypassing system requirements(on the system image):
			Reg add "HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache" /v "SV1" /t REG_DWORD /d "0" /f >nul 2>&1
			Reg add "HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache" /v "SV2" /t REG_DWORD /d "0" /f >nul 2>&1
			Reg add "HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache" /v "SV1" /t REG_DWORD /d "0" /f >nul 2>&1
			Reg add "HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache" /v "SV2" /t REG_DWORD /d "0" /f >nul 2>&1
			Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassCPUCheck" /t REG_DWORD /d "1" /f >nul 2>&1
			Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassRAMCheck" /t REG_DWORD /d "1" /f >nul 2>&1
			Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassSecureBootCheck" /t REG_DWORD /d "1" /f >nul 2>&1
			Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassStorageCheck" /t REG_DWORD /d "1" /f >nul 2>&1
			Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassTPMCheck" /t REG_DWORD /d "1" /f >nul 2>&1
			Reg add "HKLM\zSYSTEM\Setup\MoSetup" /v "AllowUpgradesWithUnsupportedTPMOrCPU" /t REG_DWORD /d "1" /f >nul 2>&1
echo Disabling reserved storage
			Reg add "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\ReserveManager" /t REG_DWORD /D "0" /f >nul 2>&1
echo Disabling Teams:
Reg add "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\Communications" /v "ConfigureChatAutoInstall" /t REG_DWORD /d "0" /f >nul 2>&1
echo Disabling Sponsored Apps:
Reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v "OemPreInstalledAppsEnabled" /t REG_DWORD /d "0" /f >nul 2>&1
			Reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v "PreInstalledAppsEnabled" /t REG_DWORD /d "0" /f >nul 2>&1
			Reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v "SilentInstalledAppsEnabled" /t REG_DWORD /d "0" /f >nul 2>&1
			Reg add "HKLM\zSOFTWARE\Policies\Microsoft\Windows\CloudContent" /v "DisableWindowsConsumerFeatures" /t REG_DWORD /d "1" /f >nul 2>&1
			Reg add "HKLM\zSOFTWARE\Microsoft\PolicyManager\current\device\Start" /v "ConfigureStartPins" /t REG_SZ /d "{\"pinnedList\": [{}]}" /f >nul 2>&1
echo Enabling Local Accounts on OOBE:
Reg add "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\OOBE" /v "BypassNRO" /t REG_DWORD /d "1" /f >nul 2>&1
copy /y %~dp0autounattend.xml c:\scratchdir\Windows\System32\Sysprep\autounattend.xml
echo Disabling Reserved Storage:
Reg add "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\ReserveManager" /v "ShippedWithReserves" /t REG_DWORD /d "0" /f >nul 2>&1
echo Disabling Chat icon:
Reg add "HKLM\zSOFTWARE\Policies\Microsoft\Windows\Windows Chat" /v "ChatIcon" /t REG_DWORD /d "3" /f >nul 2>&1
Reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v "TaskbarMn" /t REG_DWORD /d "0" /f >nul 2>&1
echo Tweaking complete!
echo Unmounting Registry...
reg unload HKLM\zCOMPONENTS >nul 2>&1
reg unload HKLM\zDRIVERS >nul 2>&1
reg unload HKLM\zDEFAULT >nul 2>&1
reg unload HKLM\zNTUSER >nul 2>&1
reg unload HKLM\zSCHEMA >nul 2>&1
reg unload HKLM\zSOFTWARE >nul 2>&1
reg unload HKLM\zSYSTEM >nul 2>&1
echo Replacing Wallpapers
del C:\ScratchDir\Windows\Web\Wallpaper\Windows\img19.jpg
copy /y %~dp0wallpaperdark.jpg c:\scratchdir\Windows\Web\Wallpaper\Windows\img19.jpg
copy /y %~dp0wallpaperlight.jpg c:\scratchdir\Windows\Web\Wallpaper\Windows\img0.jpg
echo Cleaning up image...
dism /image:c:\scratchdir /Cleanup-Image /StartComponentCleanup /ResetBase
echo Cleanup complete.
echo Unmounting image...
dism /unmount-image /mountdir:c:\scratchdir /commit
echo Exporting image...
Dism /Export-Image /SourceImageFile:c:\nano11\sources\install.wim /SourceIndex:%index% /DestinationImageFile:c:\nano11\sources\install2.wim /compress:max
del c:\nano11\sources\install.wim
ren c:\nano11\sources\install2.wim install.wim
echo Windows image completed. Continuing with boot.wim.
timeout /t 2 /nobreak > nul
cls
echo Mounting boot image:
dism /mount-image /imagefile:c:\nano11\sources\boot.wim /index:2 /mountdir:c:\scratchdir
echo Loading registry...
reg load HKLM\zCOMPONENTS "c:\scratchdir\Windows\System32\config\COMPONENTS" >nul
reg load HKLM\zDEFAULT "c:\scratchdir\Windows\System32\config\default" >nul
reg load HKLM\zNTUSER "c:\scratchdir\Users\Default\ntuser.dat" >nul
reg load HKLM\zSOFTWARE "c:\scratchdir\Windows\System32\config\SOFTWARE" >nul
reg load HKLM\zSYSTEM "c:\scratchdir\Windows\System32\config\SYSTEM" >nul
echo Bypassing system requirements(on the setup image):
			Reg add "HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache" /v "SV1" /t REG_DWORD /d "0" /f >nul 2>&1
			Reg add "HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache" /v "SV2" /t REG_DWORD /d "0" /f >nul 2>&1
			Reg add "HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache" /v "SV1" /t REG_DWORD /d "0" /f >nul 2>&1
			Reg add "HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache" /v "SV2" /t REG_DWORD /d "0" /f >nul 2>&1
			Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassCPUCheck" /t REG_DWORD /d "1" /f >nul 2>&1
			Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassRAMCheck" /t REG_DWORD /d "1" /f >nul 2>&1
			Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassSecureBootCheck" /t REG_DWORD /d "1" /f >nul 2>&1
			Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassStorageCheck" /t REG_DWORD /d "1" /f >nul 2>&1
			Reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassTPMCheck" /t REG_DWORD /d "1" /f >nul 2>&1
			Reg add "HKLM\zSYSTEM\Setup\MoSetup" /v "AllowUpgradesWithUnsupportedTPMOrCPU" /t REG_DWORD /d "1" /f >nul 2>&1
echo Tweaking complete! 
echo Unmounting Registry...
reg unload HKLM\zCOMPONENTS >nul 2>&1
reg unload HKLM\zDRIVERS >nul 2>&1
reg unload HKLM\zDEFAULT >nul 2>&1
reg unload HKLM\zNTUSER >nul 2>&1
reg unload HKLM\zSCHEMA >nul 2>&1
reg unload HKLM\zSOFTWARE >nul 2>&1
reg unload HKLM\zSYSTEM >nul 2>&1
echo Unmounting image...
dism /unmount-image /mountdir:c:\scratchdir /commit 
cls
echo the nano11 image is now completed. Proceeding with the making of the ISO...
echo Copying unattended file for bypassing MS account on OOBE...
copy /y %~dp0autounattend.xml c:\nano11\autounattend.xml
echo.
echo Creating ISO image...
%~dp0oscdimg.exe -m -o -u2 -udfver102 -bootdata:2#p0,e,bc:\nano11\boot\etfsboot.com#pEF,e,bc:\nano11\efi\microsoft\boot\efisys.bin c:\nano11 %~dp0nano11.iso
echo Creation completed! Press any key to exit the script...
pause 
echo Performing Cleanup...
rd c:\nano11 /s /q 
rd c:\scratchdir /s /q 
echo Creation Complete.
pause





