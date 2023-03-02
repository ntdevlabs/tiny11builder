@echo off
cd /d "%~dp0" && ( if exist "%temp%\getadmin.vbs" del "%temp%\getadmin.vbs" ) && fsutil dirty query %systemdrive% 1>nul 2>nul || (  echo Set UAC = CreateObject^("Shell.Application"^) : UAC.ShellExecute "wt", "cmd.exe /k cd ""%~sdp0"" && %~s0 %params%", "", "runas", 1 >> "%temp%\getadmin.vbs" && "%temp%\getadmin.vbs" && exit /B )
setlocal EnableExtensions EnableDelayedExpansion

title tiny11 builder beta
echo Welcome to the tiny11 image creator
timeout /t 3 /nobreak >nul
cls

set tiny11=%HOMEDRIVE%\tiny11
set scratchdir=%HOMEDRIVE%\scratchdir
set tempfile=%TMP%\cleanappx-%RANDOM%
set driveletter=
set /p driveletter=Please enter the drive letter for the Windows 11 image: 
set "driveletter=%driveletter%:"
echo.
if not exist "%driveletter%\sources\boot.wim" (
	echo. Can't find Windows OS Installation files in the specified Drive Letter...
	echo.
	echo. Please enter the correct DVD Drive Letter...
	goto :stop
)

if not exist "%driveletter%\sources\install.wim" (
	echo. Can't find Windows OS Installation files in the specified Drive Letter...
	echo.
	echo. Please enter the correct DVD Drive Letter...
	goto :stop
)
md %tiny11%
echo Copying Windows image...
xcopy /E /I /H /R /Y /J %driveletter% %tiny11% >nul
echo Copy complete!
sleep 2
cls
echo Getting image information:
dism /Get-WimInfo /wimfile:%tiny11%\sources\install.wim
set index=
set /p index=Please enter the image index: 
set "index=%index%"
echo Mounting Windows image. This may take a while.
echo.
md %HOMEDRIVE%\scratchdir
dism /mount-image /imagefile:%tiny11%\sources\install.wim /index:%index% /mountdir:%HOMEDRIVE%\scratchdir
cls

echo Mounting complete! Performing removal of applications...
dism /image:%HOMEDRIVE%\scratchdir /Get-ProvisionedAppxPackages | findstr /C:"Clipchamp.Clipchamp" /C:"Microsoft.BingNews" /C:"Microsoft.BingWeather" /C:"Microsoft.GamingApp" /C:"Microsoft.GetHelp" /C:"Microsoft.Getstarted" /C:"Microsoft.MicrosoftOfficeHub" /C:"Microsoft.MicrosoftSolitaireCollection" /C:"Microsoft.People" /C:"Microsoft.PowerAutomateDesktop" /C:"Microsoft.Todos" /C:"Microsoft.WindowsAlarms" /C:"microsoft.windowscommunicationsapps" /C:"Microsoft.WindowsFeedbackHub" /C:"Microsoft.WindowsMaps" /C:"Microsoft.WindowsSoundRecorder" /C:"Microsoft.Xbox.TCUI" /C:"Microsoft.XboxGamingOverlay" /C:"Microsoft.XboxGameOverlay" /C:"Microsoft.XboxSpeechToTextOverlay" /C:"Microsoft.YourPhone" /C:"Microsoft.ZuneMusic" /C:"Microsoft.ZuneVideo" /C:"MicrosoftCorporationII.MicrosoftFamily" /C:"MicrosoftCorporationII.QuickAssist" /C:"MicrosoftTeams" /C:"Microsoft.549981C3F5F10" > %tempfile%.1
> %tempfile%.2 (
  for /f "tokens=*" %%a in (%tempfile%.1) do (
    call :strip2 %%a
  )
)
for /f "tokens=*" %%a in (%tempfile%.2) do (
  echo Removing %%a
  dism /image:%HOMEDRIVE%\scratchdir /Remove-ProvisionedAppxPackage /PackageName:%%a >nul
)
cls

echo Removing of system apps complete. Now proceeding to removal of system packages...
timeout /t 1 /nobreak >nul
dism /image:%HOMEDRIVE%\scratchdir /Get-Packages | findstr /C:"Microsoft-Windows-InternetExplorer-Optional-Package" /C:"Microsoft-Windows-Kernel-LA57-FoD-Package" /C:"Microsoft-Windows-InternetExplorer-Optional-Package" /C:"Microsoft-Windows-Kernel-LA57-FoD-Package" /C:"Microsoft-Windows-LanguageFeatures-Handwriting-" /C:"Microsoft-Windows-LanguageFeatures-OCR-" /C:"Microsoft-Windows-LanguageFeatures-Speech-" /C:"Microsoft-Windows-LanguageFeatures-TextToSpeech-" /C:"Microsoft-Windows-MediaPlayer-Package" /C:"Microsoft-Windows-TabletPCMath-Package" /C:"Microsoft-Windows-Wallpaper-Content-Extended-FoD-Package" > %tempfile%.1
> %tempfile%.2 (
  for /f "tokens=*" %%a in (%tempfile%.1) do (
    call :strip3 %%a
  )
)
for /f "tokens=*" %%a in (%tempfile%.2) do (
  echo Removing %%a
  dism /image:%HOMEDRIVE%\scratchdir /Remove-Package /PackageName:%%a >nul
)
cls

timeout /t 2 /nobreak >nul
cls
echo Loading registry...
reg load HKLM\zCOMPONENTS "%HOMEDRIVE%\scratchdir\Windows\System32\config\COMPONENTS" >nul
reg load HKLM\zDEFAULT "%HOMEDRIVE%\scratchdir\Windows\System32\config\default" >nul
reg load HKLM\zNTUSER "%HOMEDRIVE%\scratchdir\Users\Default\ntuser.dat" >nul
reg load HKLM\zSOFTWARE "%HOMEDRIVE%\scratchdir\Windows\System32\config\SOFTWARE" >nul
reg load HKLM\zSYSTEM "%HOMEDRIVE%\scratchdir\Windows\System32\config\SYSTEM" >nul
echo Bypassing system requirements (on the system image)
		reg add "HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache" /v "SV1" /t REG_DWORD /d "0" /f >nul 2>&1
		reg add "HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache" /v "SV2" /t REG_DWORD /d "0" /f >nul 2>&1
		reg add "HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache" /v "SV1" /t REG_DWORD /d "0" /f >nul 2>&1
		reg add "HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache" /v "SV2" /t REG_DWORD /d "0" /f >nul 2>&1
		reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassCPUCheck" /t REG_DWORD /d "1" /f >nul 2>&1
		reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassRAMCheck" /t REG_DWORD /d "1" /f >nul 2>&1
		reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassSecureBootCheck" /t REG_DWORD /d "1" /f >nul 2>&1
		reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassStorageCheck" /t REG_DWORD /d "1" /f >nul 2>&1
		reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassTPMCheck" /t REG_DWORD /d "1" /f >nul 2>&1
		reg add "HKLM\zSYSTEM\Setup\MoSetup" /v "AllowUpgradesWithUnsupportedTPMOrCPU" /t REG_DWORD /d "1" /f >nul 2>&1
echo Removing Edge and WebView2
		rd /s /q "%HOMEDRIVE%\scratchdir\Program Files (x86)\Microsoft\Edge" >nul
		rd /s /q "%HOMEDRIVE%\scratchdir\Program Files (x86)\Microsoft\EdgeUpdate" >nul
		rd /s /q "%HOMEDRIVE%\scratchdir\Program Files (x86)\Microsoft\EdgeWebView" >nul
		rd /s /q "%HOMEDRIVE%\scratchdir\Program Files (x86)\Microsoft\EdgeCore" >nul
		reg delete "HKLM\zSOFTWARE\WOW6432Node\Microsoft\EdgeUpdate" /f >nul 2>&1
		reg delete "HKLM\zSOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft Edge" /f /v "NoRemove" >nul 2>&1
		reg delete "HKLM\zSOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft Edge Update" /f /v "NoRemove" >nul 2>&1
		reg delete "HKLM\zSOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft EdgeWebView" /f /v "NoRemove" >nul 2>&1
		reg add "HKLM\zSOFTWARE\Microsoft\EdgeUpdate" /f /v InstallDefault /d 0 /t reg_dword >nul 2>&1
		reg add "HKLM\zSOFTWARE\Microsoft\EdgeUpdate" /f /v Install{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062} /d 0 /t reg_dword >nul 2>&1
		reg add "HKLM\zSOFTWARE\Microsoft\EdgeUpdate" /f /v Install{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5} /d 0 /t reg_dword >nul 2>&1
		reg add "HKLM\zSOFTWARE\Microsoft\EdgeUpdate" /f /v DoNotUpdateToEdgeWithChromium /d 1 /t reg_dword >nul 2>&1
		reg add "HKLM\zSOFTWARE\Policies\Microsoft\EdgeUpdate" /f /v InstallDefault /d 0 /t reg_dword >nul 2>&1
		reg add "HKLM\zSOFTWARE\Policies\Microsoft\EdgeUpdate" /f /v Install{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062} /d 0 /t reg_dword >nul 2>&1
		reg add "HKLM\zSOFTWARE\Policies\Microsoft\EdgeUpdate" /f /v Install{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5} /d 0 /t reg_dword >nul 2>&1
		reg add "HKLM\zSOFTWARE\Policies\Microsoft\EdgeUpdate" /f /v DoNotUpdateToEdgeWithChromium /d 1 /t reg_dword >nul 2>&1
echo Removing OneDrive
		takeown /f %HOMEDRIVE%\scratchdir\Windows\System32\OneDriveSetup.exe >nul
		icacls %HOMEDRIVE%\scratchdir\Windows\System32\OneDriveSetup.exe /grant *S-1-5-32-544:F /T /C >nul
		del /f /q /s "%HOMEDRIVE%\scratchdir\Windows\System32\OneDriveSetup.exe" >nul
		reg delete "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\Run" /v "OneDriveSetup" /f >nul 2>&1
echo Removal complete!
echo Disabling Teams
reg add "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\Communications" /v "ConfigureChatAutoInstall" /t REG_DWORD /d "0" /f >nul 2>&1
echo Disabling Sponsored Apps
		reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v "OemPreInstalledAppsEnabled" /t REG_DWORD /d "0" /f >nul 2>&1
		reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v "PreInstalledAppsEnabled" /t REG_DWORD /d "0" /f >nul 2>&1
		reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v "SilentInstalledAppsEnabled" /t REG_DWORD /d "0" /f >nul 2>&1
		reg add "HKLM\zSOFTWARE\Policies\Microsoft\Windows\CloudContent" /v "DisableWindowsConsumerFeatures" /t REG_DWORD /d "1" /f >nul 2>&1
		reg add "HKLM\zSOFTWARE\Microsoft\PolicyManager\current\device\Start" /v "ConfigureStartPins" /t REG_SZ /d "{\"pinnedList\": [{}]}" /f >nul 2>&1
echo Enabling Local Accounts on OOBE
reg add "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\OOBE" /v "BypassNRO" /t REG_DWORD /d "1" /f >nul 2>&1
copy /y %~dp0autounattend.xml %HOMEDRIVE%\scratchdir\Windows\System32\Sysprep\autounattend.xml >nul
echo Disabling Reserved Storage
reg add "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\ReserveManager" /v "ShippedWithReserves" /t REG_DWORD /d "0" /f >nul 2>&1
echo Disabling Chat icon
reg add "HKLM\zSOFTWARE\Policies\Microsoft\Windows\Windows Chat" /v "ChatIcon" /t REG_DWORD /d "3" /f >nul 2>&1
reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v "TaskbarMn" /t REG_DWORD /d "0" /f >nul 2>&1
echo Tweaking complete
echo Unmounting registry...
reg unload HKLM\zCOMPONENTS >nul 2>&1
reg unload HKLM\zDRIVERS >nul 2>&1
reg unload HKLM\zDEFAULT >nul 2>&1
reg unload HKLM\zNTUSER >nul 2>&1
reg unload HKLM\zSCHEMA >nul 2>&1
reg unload HKLM\zSOFTWARE >nul 2>&1
reg unload HKLM\zSYSTEM >nul 2>&1
echo Cleaning up image...
dism /image:%HOMEDRIVE%\scratchdir /Cleanup-Image /StartComponentCleanup /ResetBase
echo Cleanup complete.
echo Unmounting image...
dism /unmount-image /mountdir:%HOMEDRIVE%\scratchdir /commit
echo Exporting image...
dism /Export-Image /SourceImageFile:%tiny11%\sources\install.wim /SourceIndex:%index% /DestinationImageFile:%tiny11%\sources\install2.wim /compress:max
del %tiny11%\sources\install.wim
ren %tiny11%\sources\install2.wim install.wim
echo Windows image completed. Continuing with boot.wim.
timeout /t 2 /nobreak >nul
cls
echo Mounting boot image
dism /mount-image /imagefile:%tiny11%\sources\boot.wim /index:2 /mountdir:%HOMEDRIVE%\scratchdir
echo Loading registry...
reg load HKLM\zCOMPONENTS "%HOMEDRIVE%\scratchdir\Windows\System32\config\COMPONENTS" >nul
reg load HKLM\zDEFAULT "%HOMEDRIVE%\scratchdir\Windows\System32\config\default" >nul
reg load HKLM\zNTUSER "%HOMEDRIVE%\scratchdir\Users\Default\ntuser.dat" >nul
reg load HKLM\zSOFTWARE "%HOMEDRIVE%\scratchdir\Windows\System32\config\SOFTWARE" >nul
reg load HKLM\zSYSTEM "%HOMEDRIVE%\scratchdir\Windows\System32\config\SYSTEM" >nul
echo Bypassing system requirements (on the setup image)
		reg add "HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache" /v "SV1" /t REG_DWORD /d "0" /f >nul 2>&1
		reg add "HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache" /v "SV2" /t REG_DWORD /d "0" /f >nul 2>&1
		reg add "HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache" /v "SV1" /t REG_DWORD /d "0" /f >nul 2>&1
		reg add "HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache" /v "SV2" /t REG_DWORD /d "0" /f >nul 2>&1
		reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassCPUCheck" /t REG_DWORD /d "1" /f >nul 2>&1
		reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassRAMCheck" /t REG_DWORD /d "1" /f >nul 2>&1
		reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassSecureBootCheck" /t REG_DWORD /d "1" /f >nul 2>&1
		reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassStorageCheck" /t REG_DWORD /d "1" /f >nul 2>&1
		reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassTPMCheck" /t REG_DWORD /d "1" /f >nul 2>&1
		reg add "HKLM\zSYSTEM\Setup\MoSetup" /v "AllowUpgradesWithUnsupportedTPMOrCPU" /t REG_DWORD /d "1" /f >nul 2>&1
echo Tweaking complete
echo Unmounting registry...
reg unload HKLM\zCOMPONENTS >nul 2>&1
reg unload HKLM\zDRIVERS >nul 2>&1
reg unload HKLM\zDEFAULT >nul 2>&1
reg unload HKLM\zNTUSER >nul 2>&1
reg unload HKLM\zSCHEMA >nul 2>&1
reg unload HKLM\zSOFTWARE >nul 2>&1
reg unload HKLM\zSYSTEM >nul 2>&1
echo Unmounting image...
dism /unmount-image /mountdir:%HOMEDRIVE%\scratchdir /commit
cls
echo The tiny11 image is now completed. Proceeding with the making of the ISO...
echo Copying unattended file for bypassing MS account on OOBE...
copy /y %~dp0autounattend.xml %tiny11%\autounattend.xml >nul
echo.
echo Creating ISO image...
%~dp0oscdimg.exe -m -o -u2 -udfver102 -bootdata:2#p0,e,b%tiny11%\boot\etfsboot.com#pEF,e,b%tiny11%\efi\microsoft\boot\efisys.bin %tiny11% %~dp0tiny11.iso
echo Creation completed. Press any key to exit the script...
pause 
echo Performing Cleanup...
rd %tiny11% /s /q
rd %HOMEDRIVE%\scratchdir /s /q
goto :eof

:strip2
echo %2
exit /b 1

:strip3
echo %3
exit /b 1
