@echo off
rem cd /d "%~dp0" && ( if exist "%temp%\getadmin.vbs" del "%temp%\getadmin.vbs" ) && fsutil dirty query %systemdrive% 1>nul 2>nul || (  echo Set UAC = CreateObject^("Shell.Application"^) : UAC.ShellExecute "wt", "cmd.exe /k cd ""%~sdp0"" && %~s0 %params%", "", "runas", 1 >> "%temp%\getadmin.vbs" && "%temp%\getadmin.vbs" && exit /B )
setlocal EnableExtensions EnableDelayedExpansion

title tiny11 builder alpha
echo Welcome to the tiny11 image creator!
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
dism /image:%HOMEDRIVE%\scratchdir /Get-ProvisionedAppxPackages | find "PackageName:" | find /V "Microsoft.WindowsStore" | find /V "DesktopAppInstaller" | find /V "Microsoft.HEIFImageExtension" | find /V "Microsoft.HEVCVideoExtension" | find /V "Microsoft.MicrosoftStickyNotes" | find /V "Microsoft.Paint" | find /V "Microsoft.RawImageExtension" | find /V "Microsoft.ScreenSketch" | find /V "Microsoft.SecHealthUI" | find /V "Microsoft.StorePurchaseApp" | find /V "Microsoft.VP9VideoExtensions" | find /V "Microsoft.WebMediaExtensions" | find /V "Microsoft.WebpImageExtension" | find /V "Microsoft.Windows.Photos" | find /V "Microsoft.WindowsCalculator" | find /V "Microsoft.WindowsCamera" | find /V "Microsoft.WindowsNotepad" | find /V "Microsoft.WindowsTerminal" | find /V "Microsoft.XboxIdentityProvider" | find /V "MicrosoftWindows.Client.WebExperience" > %tempfile%.1
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

echo Removing of system apps complete! Now proceeding to removal of system packages...
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

rem echo Removing Edge:
rem rd "%HOMEDRIVE%\scratchdir\Program Files (x86)\Microsoft\Edge" /s /q
rem rd "%HOMEDRIVE%\scratchdir\Program Files (x86)\Microsoft\EdgeUpdate" /s /q
echo Removing OneDrive:
takeown /f %HOMEDRIVE%\scratchdir\Windows\System32\OneDriveSetup.exe
icacls %HOMEDRIVE%\scratchdir\Windows\System32\OneDriveSetup.exe /grant Administrators:F /T /C
del /f /q /s "%HOMEDRIVE%\scratchdir\Windows\System32\OneDriveSetup.exe"
echo Removal complete!
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
rem copy /y %~dp0autounattend.xml %HOMEDRIVE%\scratchdir\Windows\System32\Sysprep\autounattend.xml
echo Disabling Reserved Storage
reg add "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\ReserveManager" /v "ShippedWithReserves" /t REG_DWORD /d "0" /f >nul 2>&1
echo Enabeling drive compression
reg add "HKLM\zSYSTEM\CurrentControlSet\Policies" /v "Ntfsenablecompression" /t REG_DWORD /d "1" /f >nul 2>&1
echo Disabeling hybernation
reg add "HKLM\zSYSTEM\CurrentControlSet\Control\Power" /v "HibernateEnabledDefault" /t REG_DWORD /d "0" /f >nul 2>&1
echo Disabling Chat icon
reg add "HKLM\zSOFTWARE\Policies\Microsoft\Windows\Windows Chat" /v "ChatIcon" /t REG_DWORD /d "3" /f >nul 2>&1
reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v "TaskbarMn" /t REG_DWORD /d "0" /f >nul 2>&1
echo Tweaking complete!
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
echo Tweaking complete!
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
echo the tiny11 image is now completed. Proceeding with the making of the ISO...
rem echo Copying unattended file for bypassing MS account on OOBE...
rem copy /y %~dp0autounattend.xml %tiny11%\autounattend.xml
echo.
echo Creating ISO image...
%~dp0oscdimg.exe -m -o -u2 -udfver102 -bootdata:2#p0,e,b%tiny11%\boot\etfsboot.com#pEF,e,b%tiny11%\efi\microsoft\boot\efisys.bin %tiny11% %~dp0tiny11.iso
echo Creation completed! Press any key to exit the script...
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