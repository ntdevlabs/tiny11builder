@echo off
setlocal EnableExtensions EnableDelayedExpansion

title Tiny11 Builder (Alpha)
echo Welcome to the Tiny11 builder!
timeout /t 3 /nobreak > nul
cls

set DriveLetter=
set /p "DriveLetter=Please enter the drive letter for the Windows 11 image: "
set "DriveLetter=%DriveLetter:~0,1=%:"
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

md c:\tiny11
echo Copying Windows image...
xcopy.exe /E /I /H /R /Y /J %DriveLetter% c:\tiny11 >nul
echo Copy complete!
sleep 2
cls

echo Getting image information...
dism /Get-WimInfo /wimfile:c:\tiny11\sources\install.wim
set index=
set /p index=Please enter the image index:
set "index=%index%"

echo Mounting Windows image. This may take a while.
echo.
md c:\scratchdir
dism /mount-image /imagefile:c:\tiny11\sources\install.wim /index:%index% /mountdir:c:\scratchdir
echo Mounting complete! Performing removal of applications...

call :remove-appx "Clipchamp" "Clipchamp.Clipchamp_2.2.8.0_neutral_~_yxz26nhyzhsrt"
call :remove-appx "News" "Microsoft.BingNews_2022.507.446.0_neutral_~_8wekyb3d8bbwe"
call :remove-appx "Weather" "Microsoft.BingWeather_2022.507.447.0_neutral_~_8wekyb3d8bbwe"
call :remove-appx "Xbox" "Microsoft.GamingApp_2022.507.447.0_neutral_~_8wekyb3d8bbwe"
call :remove-appx "GetHelp" "Microsoft.GetHelp_2022.507.447.0_neutral_~_8wekyb3d8bbwe"
call :remove-appx "GetStarted" "Microsoft.Getstarted_2022.507.447.0_neutral_~_8wekyb3d8bbwe"
call :remove-appx "Office Hub" "Microsoft.MicrosoftOfficeHub_2022.507.447.0_neutral_~_8wekyb3d8bbwe"
call :remove-appx "Solitaire" "Microsoft.MicrosoftSolitaireCollection_2022.507.447.0_neutral_~_8wekyb3d8bbwe"
call :remove-appx "PeopleApp" "Microsoft.People_2022.507.446.0_neutral_~_8wekyb3d8bbwe"
call :remove-appx "PowerAutomate" "Microsoft.PowerAutomateDesktop_2022.507.446.0_neutral_~_8wekyb3d8bbwe"
call :remove-appx "ToDo" "Microsoft.Todos_2022.507.447.0_neutral_~_8wekyb3d8bbwe"
call :remove-appx "Alarms" "Microsoft.WindowsAlarms_2022.507.446.0_neutral_~_8wekyb3d8bbwe"
call :remove-appx "Mail" "microsoft.windowscommunicationsapps_2022.507.447.0_neutral_~_8wekyb3d8bbwe"
call :remove-appx "Feedback Hub" "Microsoft.WindowsFeedbackHub_2022.507.447.0_neutral_~_8wekyb3d8bbwe"
call :remove-appx "Maps" "Microsoft.WindowsMaps_2022.507.447.0_neutral_~_8wekyb3d8bbwe"
call :remove-appx "Sound Recorder" "Microsoft.WindowsSoundRecorder_2022.507.447.0_neutral_~_8wekyb3d8bbwe"
call :remove-appx "XboxTCUI" "Microsoft.Xbox.TCUI_2022.507.446.0_neutral_~_8wekyb3d8bbwe"
call :remove-appx "XboxGamingOverlay" "Microsoft.XboxGamingOverlay_2022.507.447.0_neutral_~_8wekyb3d8bbwe"
call :remove-appx "XboxGameOverlay" "Microsoft.XboxGameOverlay_2022.507.446.0_neutral_~_8wekyb3d8bbwe"
call :remove-appx "XboxSpeechToTextOverlay" "Microsoft.XboxSpeechToTextOverlay_2022.507.446.0_neutral_~_8wekyb3d8bbwe"
call :remove-appx "Your Phone" "Microsoft.YourPhone_2022.507.447.0_neutral_~_8wekyb3d8bbwe"
call :remove-appx "Music" "Microsoft.ZuneMusic_2022.507.447.0_neutral_~_8wekyb3d8bbwe"
call :remove-appx "Video" "Microsoft.ZuneVideo_2022.507.446.0_neutral_~_8wekyb3d8bbwe"
call :remove-appx "Family" "MicrosoftCorporationII.MicrosoftFamily_2022.507.447.0_neutral_~_8wekyb3d8bbwe"
call :remove-appx "QuickAssist" "MicrosoftCorporationII.QuickAssist_2022.507.446.0_neutral_~_8wekyb3d8bbwe"
call :remove-appx "Teams" "MicrosoftTeams_23002.403.1788.1930_x64__8wekyb3d8bbwe"
call :remove-appx "Cortana" "Microsoft.549981C3F5F10_4.2204.13303.0_neutral_~_8wekyb3d8bbwe"

echo Removal of system apps complete! Now proceeding to removal of system packages...
timeout /t 1 /nobreak > nul
cls

call :remove-pkg "Internet Explorer"
call :remove-pkg "" "Microsoft-Windows-InternetExplorer-Optional-Package~31bf3856ad364e35~amd64~en-US~11.0.22621.1"
call :remove-pkg "" "Microsoft-Windows-InternetExplorer-Optional-Package~31bf3856ad364e35~amd64~~11.0.22621.1265"
call :remove-pkg "LA57" "Microsoft-Windows-Kernel-LA57-FoD-Package~31bf3856ad364e35~amd64~~10.0.22621.1265"
call :remove-pkg "Handwriting" "Microsoft-Windows-LanguageFeatures-Handwriting-en-us-Package~31bf3856ad364e35~amd64~~10.0.22621.1265"
call :remove-pkg "OCR" "Microsoft-Windows-LanguageFeatures-OCR-en-us-Package~31bf3856ad364e35~amd64~~10.0.22621.1265"
call :remove-pkg "Speech" "Microsoft-Windows-LanguageFeatures-Speech-en-us-Package~31bf3856ad364e35~amd64~~10.0.22621.1265"
call :remove-pkg "TTS" "Microsoft-Windows-LanguageFeatures-TextToSpeech-en-us-Package~31bf3856ad364e35~amd64~~10.0.22621.1265"
call :remove-pkg "Media Player Legacy"
call :remove-pkg "" "Microsoft-Windows-MediaPlayer-Package~31bf3856ad364e35~amd64~~10.0.22621.1265"
call :remove-pkg "" "Microsoft-Windows-MediaPlayer-Package~31bf3856ad364e35~wow64~en-US~10.0.22621.1"
call :remove-pkg "" "Microsoft-Windows-MediaPlayer-Package~31bf3856ad364e35~amd64~~10.0.22621.1265"
call :remove-pkg "" "Microsoft-Windows-MediaPlayer-Package~31bf3856ad364e35~wow64~~10.0.22621.1"
call :remove-pkg "Tablet PC Math" "Microsoft-Windows-TabletPCMath-Package~31bf3856ad364e35~amd64~~10.0.22621.1265"
call :remove-pkg "Wallpapers" "Microsoft-Windows-Wallpaper-Content-Extended-FoD-Package~31bf3856ad364e35~amd64~~10.0.22621.1265"

call :remove-txt "Edge"
rd "C:\scratchdir\Program Files (x86)\Microsoft\Edge" /s /q
rd "C:\scratchdir\Program Files (x86)\Microsoft\EdgeUpdate" /s /q
call :remove-txt "OneDrive"
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

echo Disabling Teams...
Reg add "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\Communications" /v "ConfigureChatAutoInstall" /t REG_DWORD /d "0" /f >nul 2>&1

echo Disabling Sponsored Apps..
Reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v "OemPreInstalledAppsEnabled" /t REG_DWORD /d "0" /f >nul 2>&1
			Reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v "PreInstalledAppsEnabled" /t REG_DWORD /d "0" /f >nul 2>&1
			Reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v "SilentInstalledAppsEnabled" /t REG_DWORD /d "0" /f >nul 2>&1
			Reg add "HKLM\zSOFTWARE\Policies\Microsoft\Windows\CloudContent" /v "DisableWindowsConsumerFeatures" /t REG_DWORD /d "1" /f >nul 2>&1
			Reg add "HKLM\zSOFTWARE\Microsoft\PolicyManager\current\device\Start" /v "ConfigureStartPins" /t REG_SZ /d "{\"pinnedList\": [{}]}" /f >nul 2>&1

echo Enabling Local Accounts on OOBE...
Reg add "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\OOBE" /v "BypassNRO" /t REG_DWORD /d "1" /f >nul 2>&1
copy /y %~dp0autounattend.xml c:\scratchdir\Windows\System32\Sysprep\autounattend.xml

echo Disabling Reserved Storage...
Reg add "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\ReserveManager" /v "ShippedWithReserves" /t REG_DWORD /d "0" /f >nul 2>&1

echo Disabling Chat icon...
Reg add "HKLM\zSOFTWARE\Policies\Microsoft\Windows\Windows Chat" /v "ChatIcon" /t REG_DWORD /d "3" /f >nul 2>&1
Reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v "TaskbarMn" /t REG_DWORD /d "0" /f >nul 2>&1

echo Tweaking complete! Unmounting registry...
reg unload HKLM\zCOMPONENTS >nul 2>&1
reg unload HKLM\zDRIVERS >nul 2>&1
reg unload HKLM\zDEFAULT >nul 2>&1
reg unload HKLM\zNTUSER >nul 2>&1
reg unload HKLM\zSCHEMA >nul 2>&1
reg unload HKLM\zSOFTWARE >nul 2>&1
reg unload HKLM\zSYSTEM >nul 2>&1

echo Cleaning up image...
dism /image:c:\scratchdir /Cleanup-Image /StartComponentCleanup /ResetBase
echo Cleanup complete.
echo.

echo Unmounting image...
dism /unmount-image /mountdir:c:\scratchdir /commit
echo Exporting image...
Dism /Export-Image /SourceImageFile:c:\tiny11\sources\install.wim /SourceIndex:%index% /DestinationImageFile:c:\tiny11\sources\install2.wim /compress:max

del c:\tiny11\sources\install.wim
ren c:\tiny11\sources\install2.wim install.wim

echo Windows image completed. Continuing with boot.wim.
timeout /t 2 /nobreak > nul
cls

echo Mounting boot image:
dism /mount-image /imagefile:c:\tiny11\sources\boot.wim /index:2 /mountdir:c:\scratchdir

echo Loading registry...
reg load HKLM\zCOMPONENTS "c:\scratchdir\Windows\System32\config\COMPONENTS" >nul
reg load HKLM\zDEFAULT "c:\scratchdir\Windows\System32\config\default" >nul
reg load HKLM\zNTUSER "c:\scratchdir\Users\Default\ntuser.dat" >nul
reg load HKLM\zSOFTWARE "c:\scratchdir\Windows\System32\config\SOFTWARE" >nul
reg load HKLM\zSYSTEM "c:\scratchdir\Windows\System32\config\SYSTEM" >nul

echo Bypassing installation requirements...
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

echo Tweaking complete!  Unmounting Registry...
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
echo The Tiny11 image has been built. Building ISO...
echo Copying unattend file for bypassing MS account requirement on OOBE...
copy /y %~dp0autounattend.xml c:\tiny11\autounattend.xml
echo.

echo Creating ISO image...
%~dp0oscdimg.exe -m -o -u2 -udfver102 -bootdata:2#p0,e,bc:\tiny11\boot\etfsboot.com#pEF,e,bc:\tiny11\efi\microsoft\boot\efisys.bin c:\tiny11 %~dp0tiny11.iso

echo Creation completed! Press any key to exit the script...
pause
echo Cleaning up...
rd c:\tiny11 /s /q 
rd c:\scratchdir /s /q 
exit

:remove-appx
echo Removing %~1...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:%~2
exit /b

:remove-pkg
if not [%~1]==[] (echo Removing %~1...)
if not [%~2]==[] (dism /image:c:\scratchdir /Remove-Package /PackageName:%~2 > nul)
exit /b

:remove-txt
echo Removing %~1...
exit /b
