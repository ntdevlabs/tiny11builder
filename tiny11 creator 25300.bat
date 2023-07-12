echo off
setlocal EnableExtensions EnableDelayedExpansion

title tiny11 builder alpha
echo Welcome to the tiny11 image creator!
timeout /t 3 /nobreak > nul
cls

set DriveLetter=
set /p DriveLetter=Please enter the drive letter for the Windows 11 image: 
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
md c:\tiny11
echo Copying Windows image...
xcopy.exe /E /I /H /R /Y /J %DriveLetter% c:\tiny11 >nul
echo Copy complete!
sleep 2
cls
echo Getting image information:
dism /Get-WimInfo /wimfile:c:\tiny11\sources\install.wim
set index=
set /p index=Please enter the image index:
set "index=%index%"
echo Mounting Windows image. This may take a while.
echo.
md c:\scratchdir
dism /mount-image /imagefile:c:\tiny11\sources\install.wim /index:%index% /mountdir:c:\scratchdir
echo Mounting complete! Performing removal of applications...
echo Removing Clipchamp...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Clipchamp.Clipchamp_2.5.15.0_neutral_~_yxz26nhyzhsrt
echo Removing News...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.BingNews_4.12.21001.0_neutral_~_8wekyb3d8bbwe
echo Removing Weather...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.BingWeather_4.53.50023.0_neutral_~_8wekyb3d8bbwe
echo Removing Xbox...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.GamingApp_2021.427.138.0_neutral_~_8wekyb3d8bbwe
echo Removing GetHelp...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.GetHelp_10.2206.2011.0_neutral_~_8wekyb3d8bbwe
echo Removing GetStarted...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.Getstarted_2021.2204.1.0_neutral_~_8wekyb3d8bbwe
echo Removing Office Hub...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.MicrosoftOfficeHub_18.2204.1141.0_neutral_~_8wekyb3d8bbwe
echo Removing Solitaire...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.MicrosoftSolitaireCollection_4.12.3171.0_neutral_~_8wekyb3d8bbwe
echo Removing PeopleApp...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.People_2020.901.1724.0_neutral_~_8wekyb3d8bbwe
echo Removing PowerAutomate...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.PowerAutomateDesktop_10.0.3735.0_neutral_~_8wekyb3d8bbwe
echo Removing ToDo...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.Todos_2.54.42772.0_neutral_~_8wekyb3d8bbwe
echo Removing Alarms...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.WindowsAlarms_2022.2210.9.0_neutral_~_8wekyb3d8bbwe
echo Removing Mail...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:microsoft.windowscommunicationsapps_16005.14326.20544.0_neutral_~_8wekyb3d8bbwe
echo Removing Feedback Hub...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.WindowsFeedbackHub_2022.106.2230.0_neutral_~_8wekyb3d8bbwe
echo Removing Maps...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.WindowsMaps_2022.2208.6.0_neutral_~_8wekyb3d8bbwe
echo Removing Sound Recorder...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.WindowsSoundRecorder_2021.2208.27.0_neutral_~_8wekyb3d8bbwe
echo Removing XboxTCUI...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.Xbox.TCUI_1.23.28004.0_neutral_~_8wekyb3d8bbwe
echo Removing XboxGamingOverlay...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.XboxGamingOverlay_2.622.3232.0_neutral_~_8wekyb3d8bbwe
echo Removing XboxGameOverlay...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.XboxGameOverlay_1.47.2385.0_neutral_~_8wekyb3d8bbwe
echo Removing XboxSpeechToTextOverlay...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.XboxSpeechToTextOverlay_1.17.29001.0_neutral_~_8wekyb3d8bbwe
echo Removing Your Phone...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.YourPhone_1.22022.147.0_neutral_~_8wekyb3d8bbwe
echo Removing Music...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.ZuneMusic_11.2210.33.0_neutral_~_8wekyb dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.ZuneVideo_2019.22120.10031.0_neutral_~_8wekyb3d8bbwe
echo Removing Family...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:MicrosoftCorporationII.MicrosoftFamily_2022.507.447.0_neutral_~_8wekyb3d8bbwe
echo Removing QuickAssist...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:MicrosoftCorporationII.QuickAssist_2022.825.2016.0_neutral_~_8wekyb3d8bbwe
echo Removing Cortana...
dism /image:c:\scratchdir /Remove-ProvisionedAppxPackage /PackageName:Microsoft.549981C3F5F10_4.2204.13303.0_neutral_~_8wekyb3d8bbwe

echo Removing of system apps complete! Now proceeding to removal of system packages...
timeout /t 1 /nobreak > nul
cls
echo Removing Internet Explorer...
dism /image:c:\scratchdir /Remove-Package /PackageName:Microsoft-Windows-InternetExplorer-Optional-Package~31bf3856ad364e35~amd64~en-US~11.0.25300.1000 > nul
echo Removing LA57:
dism /image:c:\scratchdir /Remove-Package /PackageName:Microsoft-Windows-Kernel-LA57-FoD-Package~31bf3856ad364e35~amd64~~10.0.25300.1000 > nul
echo Removing Handwriting:
dism /image:c:\scratchdir /Remove-Package /PackageName:Microsoft-Windows-LanguageFeatures-Handwriting-en-us-Package~31bf3856ad364e35~amd64~~10.0.25300.1000 > nul
echo Removing OCR:
dism /image:c:\scratchdir /Remove-Package /PackageName:Microsoft-Windows-LanguageFeatures-OCR-en-us-Package~31bf3856ad364e35~amd64~~10.0.25300.1000 > nul
echo Removing Speech:
dism /image:c:\scratchdir /Remove-Package /PackageName:Microsoft-Windows-LanguageFeatures-Speech-en-us-Package~31bf3856ad364e35~amd64~~10.0.25300.1000 > nul
echo Removing TTS:
dism /image:c:\scratchdir /Remove-Package /PackageName:Microsoft-Windows-LanguageFeatures-TextToSpeech-en-us-Package~31bf3856ad364e35~amd64~~10.0.25300.1000 > nul
echo Removing Media Player Legacy:
dism /image:c:\scratchdir /Remove-Package /PackageName:Microsoft-Windows-MediaPlayer-Package~31bf3856ad364e35~amd64~~10.0.25300.1000 > nul
dism /image:c:\scratchdir /Remove-Package /PackageName:Microsoft-Windows-MediaPlayer-Package~31bf3856ad364e35~wow64~en-US~10.0.25300.1000 > nul
dism /image:c:\scratchdir /Remove-Package /PackageName:Microsoft-Windows-MediaPlayer-Package~31bf3856ad364e35~amd64~~10.0.25300.1000 > nul
dism /image:c:\scratchdir /Remove-Package /PackageName:Microsoft-Windows-MediaPlayer-Package~31bf3856ad364e35~wow64~~10.0.25300.1000 > nul
echo Removing Tablet PC Math:
dism /image:c:\scratchdir /Remove-Package /PackageName:Microsoft-Windows-TabletPCMath-Package~31bf3856ad364e35~amd64~~10.0.25300.1000 > nul
echo Removing Wallpapers:
dism /image:c:\scratchdir /Remove-Package /PackageName:Microsoft-Windows-Wallpaper-Content-Extended-FoD-Package~31bf3856ad364e35~amd64~~10.0.25300.1000 > nul

echo Removing Teams Registry Entries...
reg delete "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\Communications" /f > nul
echo Disabling Sponsored Apps:
reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v "OemPreInstalledAppsEnabled" /t REG_DWORD /d "0" /f > nul
reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v "PreInstalledAppsEnabled" /t REG_DWORD /d "0" /f > nul
reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v "SilentInstalledAppsEnabled" /t REG_DWORD /d "0" /f > nul
reg add "HKLM\zSOFTWARE\Policies\Microsoft\Windows\CloudContent" /v "DisableWindowsConsumerFeatures" /t REG_DWORD /d "1" /f > nul
reg add "HKLM\zSOFTWARE\Microsoft\PolicyManager\current\device\Start" /v "ConfigureStartPins" /t REG_SZ /d "{\"pinnedList\": [{}]}" /f > nul

echo Tweaking complete!
echo Unmounting Registry...
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
echo Unmounting image...
dism /unmount-image /mountdir:c:\scratchdir /



