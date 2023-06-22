@echo off
cls

setlocal EnableExtensions EnableDelayedExpansion
title tiny11 builder (v1.0)
echo tiny11 creator (v1.0)

echo Administrative permissions required to run this script. 
    
net session >nul 2>&1
if %errorLevel% == 0 (
	echo Success: Administrative permissions confirmed.
) else (
	echo Failure: Current permissions inadequate. Open your terminal in Admin mode.
	exit /b
)

REM get all command line parameters
for %%A in (%*) do (
	set "argument=%%A"

	if /i "!argument:/h=!" neq "!argument!" (
		echo Creates small stripped down version of Windows ISO
		echo.
		echo "tiny11 creator.bat" [/it] [/spc] 
		echo         /it          	Install tools from tools directory.
		echo         /spc         	Skip Package Cleanup will not delete and clean packages from iso image.
		echo         /do            Defender Out... Use this if you use Windows off the internet and don't run funny apps.
		echo         /d:letter    	Specify iso image letter that image was mounted to. ex: '-d c' where c is letter of the drive.
		echo         /i:idx      	Specify image index in the iso that you want to install. ex: '-ii 2' will use second image from original iso.
		exit /b
	)
	if /i "!argument:/d:=!" neq "!argument!" (
		REM Extract the value after "/d:"
		set "DriveLetter=!argument:/d:=!"
	)
	if /i "!argument:/i:=!" neq "!argument!" (
		set "index=!argument:/i:=!"
	)
	if /i "!argument:/spc=!" neq "!argument!" (
		echo Skipping package cleanup phase
		set SkipPackageCleanup=1
	)
	if /i "!argument:/it=!" neq "!argument!" (
		set InstallTools=1
	)
	if /i "!argument:/do=!" neq "!argument!" (
		set DefenderOut=1
	)
	rem WARNING!!!!!!
	rem this next flag is experimental and is not supported yet. 
	rem moreover on some OS flavors OS got into infinite loop after setup and never finished setup
	rem some important packagegot deleted which we need to add to exception list
	if /i "!argument:/dap=!" neq "!argument!" (
		set DeleteAllPackages=1
	)
)

if not defined DriveLetter (
	set /p DriveLetter=Please enter the drive letter for the Windows 11 image: 
)
set DriveLetter=%DriveLetter%:\

if not exist "%DriveLetter%\sources\boot.wim" (
	echo.Can't find Windows OS Installation files in the specified Drive Letter..
	echo.
	echo.Please enter the correct DVD Drive Letter..
	exit /b
)
if not exist "%DriveLetter%\sources\install.wim" (
	echo.Can't find Windows OS Installation files in the specified Drive Letter..
	echo.
	echo.Please enter the correct DVD Drive Letter..
	exit /b
)

rem Setting variables
set tinyTempDir=c:\tinyTemp
set mountDir=c:\tinyMountDir
md "%mountDir%"
set wimFile=%tinyTempDir%\sources\install.wim
set bootFile=%tinyTempDir%\sources\boot.wim
set packagesFile=%tinyTempDir%\PackagesCache.txt

set ERRORLEVEL=0
echo Copying Windows image...
xcopy.exe %DriveLetter% %tinyTempDir% /E /I /H /R /Y /J /D /Q
if %ERRORLEVEL% NEQ 0 (
  echo ERROR: Failed to copy Windows Image to %tinyTempDir% directory from %DriveLetter%
  goto :eof
)
echo Copy complete!

rem Check if old package cache exists, if so delete it
if exist "%packagesFile%" (
	echo.Deleting PackagesCache file
    del /f "%packagesFile%"
)

if not defined index (
	echo Getting image information:
	dism /Get-WimInfo /wimfile:"%wimFile%"

	set /p index=Please enter the image index:
)
set index=%index%

echo.
echo Mounting Windows image. This may take a while...
dism /mount-image /imagefile:"%wimFile%" /index:%index% /MountDir:"%mountDir%"

rem Check if the image is mounted correctly
dism /Get-MountedWimInfo 
rem Check the return code
if %errorlevel% neq 0 (
    echo ERROR: Failed to retrieve mounted image information. Exiting...
    exit /b
)
echo Mounting complete! Performing removal of applications...

if defined DefenderOut (
rem Dism /online /Disable-Feature /FeatureName:Windows-Defender /Remove /NoRestart /quiet
	call :DeletePackages Windows-Defender
)

if defined SkipPackageCleanup (
	goto :skipPackageCleanup
)

if defined DeleteAllPackages (
	echo WARNING: DeleteAllPackages is a special parameter that tries to nuke all packages from the image
	echo This is experimental feature and should not be used.
	call :DeletePackages DeleteAllPackages
	goto :SkipPackageCleanup
)

rem Call the subroutine to delete the specified packages
call :DeletePackages Cortana
rem There are _4 _3 and other packages of 549981C3F5F1 out there
call :DeletePackages 549981C3F5F1
call :DeletePackages Photos
call :DeletePackages Teams
call :DeletePackages Clipchamp
call :DeletePackages BingNews
call :DeletePackages BingWeather
call :DeletePackages GamingApp
call :DeletePackages GetHelp
call :DeletePackages Getstarted
call :DeletePackages MicrosoftOfficeHub
call :DeletePackages MicrosoftSolitaireCollection
call :DeletePackages MicrosoftStickyNotes
call :DeletePackages Paint
call :DeletePackages WindowsCamera
call :DeletePackages windowscommunications
call :DeletePackages YourPhone
call :DeletePackages MicrosoftCorporationII
call :DeletePackages People
call :DeletePackages PowerAutomateDesktop
call :DeletePackages Todos
call :DeletePackages WindowsAlarms
call :DeletePackages windowscommunicationsapps
call :DeletePackages WindowsFeedbackHub
call :DeletePackages WindowsMaps
call :DeletePackages WindowsSoundRecorder
call :DeletePackages Xbox
call :DeletePackages XboxGamingOverlay
call :DeletePackages XboxGameOverlay
call :DeletePackages XboxSpeechToTextOverlay
call :DeletePackages YourPhone
call :DeletePackages ZuneMusic
call :DeletePackages ZuneVideo
call :DeletePackages MicrosoftFamily
call :DeletePackages QuickAssist
call :DeletePackages MicrosoftTeams
call :DeletePackages Face
call :DeletePackages StepsRecorder
call :DeletePackages Wallpaper
call :DeletePackages ApplicationModel-Sync

echo Removing of system apps complete! Now proceeding to removal of system packages...
call :DeletePackages InternetExplorer
call :DeletePackages LA57
call :DeletePackages MediaPlayer
call :DeletePackages TabletPCMath
call :DeletePackages DirectX-Database
call :DeletePackages LanguageFeatures-Handwriting
call :DeletePackages LanguageFeatures-OCR
call :DeletePackages LanguageFeatures-Speech
call :DeletePackages LanguageFeatures-TextToSpeech
call :DeletePackages PowerShell-ISE
call :DeletePackages DiagTrack

rem Remove folders with applications
call :DeleteDirectory "%mountDir%\Windows\DiagTrack"
call :DeleteDirectory "%mountDir%\Windows\InboxApps"
call :DeleteDirectory "%mountDir%\Program Files (x86)\Windows Mail"
call :DeleteDirectory "%mountDir%\Program Files (x86)\Windows Photo Viewer"
call :DeleteDirectory "%mountDir%\Program Files (x86)\Internet Explorer"
call :DeleteDirectory "%mountDir%\Program Files (x86)\Microsoft"
call :DeleteDirectory "%mountDir%\Program Files\Windows Mail"
call :DeleteDirectory "%mountDir%\Program Files\Windows Photo Viewer"
call :DeleteDirectory "%mountDir%\Program Files\Internet Explorer"

:skipPackageCleanup

echo Configuring Windows Features.
echo Removing telemetry and other non essencial features from the image...
call :DisableAndRemoveFeature "DiagTrack"
call :DisableAndRemoveFeature "CompatTel"
call :DisableAndRemoveFeature "DirectPlay"
call :DisableAndRemoveFeature "FaxServicesClientPackage"
call :DisableAndRemoveFeature "Internet-Explorer-Optional-x64"
call :DisableAndRemoveFeature "LegacyComponents"
call :DisableAndRemoveFeature "MediaPlayback"
call :DisableAndRemoveFeature "Microsoft-Hyper-V-All"
call :DisableAndRemoveFeature "Microsoft-Hyper-V-Management-Clients"
call :DisableAndRemoveFeature "Microsoft-Hyper-V-Management-PowerShell"
call :DisableAndRemoveFeature "Microsoft-Hyper-V-Tools-All"
call :DisableAndRemoveFeature "Printing-Foundation-InternetPrinting-Client"
call :DisableAndRemoveFeature "Printing-XPSServices-Features"
call :DisableAndRemoveFeature "ScanManagementConsole"
call :DisableAndRemoveFeature "SearchEngine-Client-Package"
call :DisableAndRemoveFeature "TelnetClient"
call :DisableAndRemoveFeature "TFTP"
call :DisableAndRemoveFeature "TIFFIFilter"
call :DisableAndRemoveFeature "WindowsMediaPlayer"
call :DisableAndRemoveFeature "WorkFolders-Client"
call :DisableAndRemoveFeature "Xps-Foundation-Xps-Viewer"

echo Enabling features like writing to PDF. This will take a bit...
dism /image:%mountDir% /Enable-Feature /FeatureName:"Printing-Foundation-LPDPrintService" /NoRestart > nul
dism /image:%mountDir% /Enable-Feature /FeatureName:"Printing-Foundation-LPRPortMonitor" /NoRestart > nul
dism /image:%mountDir% /Enable-Feature /FeatureName:"Printing-PrintToPDFServices-Features" /NoRestart > nul
dism /image:%mountDir% /Enable-Feature /FeatureName:"Printing-Foundation-Features" /NoRestart > nul
echo Windows Features have now been configured.

echo Removing OneDrive:
takeown /f %mountDir%\Windows\System32\OneDriveSetup.exe
icacls %mountDir%\Windows\System32\OneDriveSetup.exe /grant Administrators:F /T /C
del /f /q /s "%mountDir%\Windows\System32\OneDriveSetup.exe"
echo Removal complete!

echo Copying unattended file for bypassing MS account on OOBE
md %mountDir%\Windows\Setup\Scripts
copy /y %~dp0autounattend.xml %tinyTempDir%\autounattend.xml > nul
copy /y %~dp0autounattend.xml %mountDir%\Windows\System32\Sysprep\unattend.xml > nul
copy /y %~dp0autounattend.xml %mountDir%\autounattend.xml > nul
copy /y %~dp0firststartup.ps1 %mountDir%\Windows\Setup\Scripts\firststartup.ps1 > nul
echo Setting up SetupComplete.cmd to perform tasks like remove Edge Icons etc after firt logon.
echo WARNING: This will not work if you are using wrong/no Product Key for your Windows 
echo          check this KB: https://learn.microsoft.com/en-us/troubleshoot/mem/configmgr/os-deployment/os-deployment-task-sequence-not-continue
timeout /t 5 /nobreak > nul
echo.powershell "Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope CurrentUser -Force" > %mountDir%\Windows\Setup\Scripts\SetupComplete.cmd
echo.powershell "& c:\Windows\Setup\Scripts\FirstStartup.ps1" >> %mountDir%\Windows\Setup\Scripts\SetupComplete.cmd

rem ****************************************************************************
rem if you want to embed tools into the iso, for example Chris Titus's winutil.ps1
rem create tools directory where the script is and it will download latest version of winutil.ps1
rem ****************************************************************************
if defined InstallTools (
	echo Download latest winutil.ps1
	powershell -ExecutionPolicy Bypass -Command "Invoke-WebRequest -Uri 'https://christitus.com/win' -OutFile '%mountDir%\Windows\Setup\Scripts\winutil.ps1'"
	call :GetWinGet
)

echo Loading registry...
reg load HKLM\zCOMPONENTS "%mountDir%\Windows\System32\config\COMPONENTS" > nul
reg load HKLM\zDEFAULT "%mountDir%\Windows\System32\config\default" > nul
reg load HKLM\zNTUSER "%mountDir%\Users\Default\ntuser.dat" > nul
reg load HKLM\zSOFTWARE "%mountDir%\Windows\System32\config\SOFTWARE" > nul
reg load HKLM\zSYSTEM "%mountDir%\Windows\System32\config\SYSTEM" > nul

echo Bypassing system requirements(on the system image)
reg add "HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache" /v "SV1" /t REG_DWORD /d "0" /f > nul 2>&1
reg add "HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache" /v "SV2" /t REG_DWORD /d "0" /f > nul 2>&1
reg add "HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache" /v "SV1" /t REG_DWORD /d "0" /f > nul 2>&1
reg add "HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache" /v "SV2" /t REG_DWORD /d "0" /f > nul 2>&1
reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassCPUCheck" /t REG_DWORD /d "1" /f > nul 2>&1
reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassRAMCheck" /t REG_DWORD /d "1" /f > nul 2>&1
reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassSecureBootCheck" /t REG_DWORD /d "1" /f > nul 2>&1
reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassStorageCheck" /t REG_DWORD /d "1" /f > nul 2>&1
reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassTPMCheck" /t REG_DWORD /d "1" /f > nul 2>&1
reg add "HKLM\zSYSTEM\Setup\MoSetup" /v "AllowUpgradesWithUnsupportedTPMOrCPU" /t REG_DWORD /d "1" /f > nul 2>&1

reg add "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\Search" /v "SearchboxTaskbarMode" /t REG_DWORD /d "0" /f > nul 2>&1

echo Disabling Teams
reg add "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\Communications" /v "ConfigureChatAutoInstall" /t REG_DWORD /d 0 /f > nul 2>&1
reg add "HKLM\Software\Policies\Microsoft\Windows\Windows Chat" /v ChatIcon /t REG_DWORD /d 2 /f > nul 2>&1

echo Setting all services to start manually
reg add "HKLM\zSOFTWARE\CurrentControlSet\Services" /v Start /t REG_DWORD /d 3 /f

echo Disabling Sponsored Apps
reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v "OemPreInstalledAppsEnabled" /t REG_DWORD /d "0" /f > nul 2>&1
reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v "PreInstalledAppsEnabled" /t REG_DWORD /d "0" /f > nul 2>&1
reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v "SilentInstalledAppsEnabled" /t REG_DWORD /d "0" /f > nul 2>&1
reg add "HKLM\zSOFTWARE\Policies\Microsoft\Windows\CloudContent" /v "DisableWindowsConsumerFeatures" /t REG_DWORD /d "1" /f > nul 2>&1
reg add "HKLM\zSOFTWARE\Microsoft\PolicyManager\current\device\Start" /v "ConfigureStartPins" /t REG_SZ /d "{\"pinnedList\": [{}]}" /f > nul 2>&1

echo Enabling Local Accounts on OOBE
reg add "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\OOBE" /v "BypassNRO" /t REG_DWORD /d "1" /f > nul 2>&1
echo Creating a directory that allows to bypass Wifi setup
md %mountDir%\Windows\System32\OOBE\BYPASSNRO

echo Disabling Reserved Storage
reg add "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\ReserveManager" /v "ShippedWithReserves" /t REG_DWORD /d "0" /f > nul 2>&1

echo Disabling Chat icon
reg add "HKLM\zSOFTWARE\Policies\Microsoft\Windows\Windows Chat" /v "ChatIcon" /t REG_DWORD /d "3" /f > nul 2>&1
reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v "TaskbarMn" /t REG_DWORD /d "0" /f > nul 2>&1

echo Changing theme to dark. This only works on Activated Windows
reg add "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize" /v "AppsUseLightTheme" /t REG_DWORD /d "0" /f 
reg add "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize" /v "SystemUsesLightTheme" /t REG_DWORD /d "0" /f

echo Unmounting registry...
reg unload HKLM\zCOMPONENTS > nul 2>&1
reg unload HKLM\zDRIVERS > nul 2>&1
reg unload HKLM\zDEFAULT > nul 2>&1
reg unload HKLM\zNTUSER > nul 2>&1
reg unload HKLM\zSCHEMA > nul 2>&1
reg unload HKLM\zSOFTWARE > nul 2>&1
reg unload HKLM\zSYSTEM > nul 2>&1

echo Cleaning up image...
dism /image:%mountDir% /Cleanup-Image /StartComponentCleanup /ResetBase

echo Unmounting image...
dism /unmount-image /mountdir:%mountDir% /commit

echo Exporting image...
dism /Export-Image /SourceImageFile:%tinyTempDir%\sources\install.wim /SourceIndex:%index% /DestinationImageFile:%tinyTempDir%\sources\install2.wim /compress:max

del %tinyTempDir%\sources\install.wim
ren %tinyTempDir%\sources\install2.wim install.wim
echo Windows image completed. Continuing with boot.wim.

rem ****************************************************************************
echo Mounting boot image:
rem ****************************************************************************
dism /mount-image /imagefile:%tinyTempDir%\sources\boot.wim /index:2 /mountdir:%mountDir%

echo Loading registry...
reg load HKLM\zCOMPONENTS "%mountDir%\Windows\System32\config\COMPONENTS" > nul
reg load HKLM\zDEFAULT "%mountDir%\Windows\System32\config\default" > nul
reg load HKLM\zNTUSER "%mountDir%\Users\Default\ntuser.dat" > nul
reg load HKLM\zSOFTWARE "%mountDir%\Windows\System32\config\SOFTWARE" > nul
reg load HKLM\zSYSTEM "%mountDir%\Windows\System32\config\SYSTEM" > nul

echo Bypassing system requirements(on the setup image)
reg add "HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache" /v "SV1" /t REG_DWORD /d "0" /f > nul 2>&1
reg add "HKLM\zDEFAULT\Control Panel\UnsupportedHardwareNotificationCache" /v "SV2" /t REG_DWORD /d "0" /f > nul 2>&1
reg add "HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache" /v "SV1" /t REG_DWORD /d "0" /f > nul 2>&1
reg add "HKLM\zNTUSER\Control Panel\UnsupportedHardwareNotificationCache" /v "SV2" /t REG_DWORD /d "0" /f > nul 2>&1
reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassCPUCheck" /t REG_DWORD /d "1" /f > nul 2>&1
reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassRAMCheck" /t REG_DWORD /d "1" /f > nul 2>&1
reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassSecureBootCheck" /t REG_DWORD /d "1" /f > nul 2>&1
reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassStorageCheck" /t REG_DWORD /d "1" /f > nul 2>&1
reg add "HKLM\zSYSTEM\Setup\LabConfig" /v "BypassTPMCheck" /t REG_DWORD /d "1" /f > nul 2>&1
reg add "HKLM\zSYSTEM\Setup\MoSetup" /v "AllowUpgradesWithUnsupportedTPMOrCPU" /t REG_DWORD /d "1" /f > nul 2>&1

echo Unmounting registry...
reg unload HKLM\zCOMPONENTS > nul 2>&1
reg unload HKLM\zDRIVERS > nul 2>&1
reg unload HKLM\zDEFAULT > nul 2>&1
reg unload HKLM\zNTUSER > nul 2>&1
reg unload HKLM\zSCHEMA > nul 2>&1
reg unload HKLM\zSOFTWARE > nul 2>&1
reg unload HKLM\zSYSTEM > nul 2>&1

:CleanUp

echo Unmounting boot image...
dism /unmount-image /mountdir:%mountDir% /commit 
echo The tiny11 image is now completed. Proceeding with the making of the ISO...
echo Creating ISO image...
%~dp0oscdimg.exe -m -o -u2 -udfver102 -bootdata:2#p0,e,b%tinyTempDir%\boot\etfsboot.com#pEF,e,b%tinyTempDir%\efi\microsoft\boot\efisys.bin %tinyTempDir% %~dp0tiny11.iso
echo Creation completed! Press any key to exit the script...
echo Performing Cleanup
rd %tinyTempDir% /s /q 
rd %mountDir% /s /q
echo Done. That's it! Have a cookie.
goto :eof

rem  ********************************************************
ren |                                                        |
rem |                     Functions section                  |
rem |                                                        |
rem  ********************************************************

rem *******************************************************
rem Define the subroutine to search and delete packages
rem Subroutine to remove packages matching the provided search mask
rem Usage: call :DeletePackages "searchMask"
rem *******************************************************
:DeletePackages
	rem  Cache the packages in a file (if not already cached)
	if not exist "%packagesFile%" (
		rem Enable delayed variable expansion
		setlocal enabledelayedexpansion

		rem Iterate over the package names obtained from dism
		for /f "tokens=2 delims=:" %%a in ('dism /image:%mountDir% /Get-Packages ^| findstr /i /c:"Package Identity"') do (
			rem Trim whitespace from the package name
			set trimmedPackageName=%%~a
			call :trimWhitespace "%%~a" trimmedPackageName
			rem Use the trimmed package name for further processing
			rem echo.Trimmed Package Identity: "!trimmedPackageName!"
			echo.!trimmedPackageName!>>"%packagesFile%"
		)
		for /f "tokens=2 delims=:" %%a in ('dism /image:%mountDir% /Get-ProvisionedAppxPackages ^| findstr /i /c:"PackageName"') do (
			rem Trim whitespace from the package name
			set trimmedPackageName=%%~a
			call :trimWhitespace "%%~a" trimmedPackageName
			rem Use the trimmed package name for further processing
			rem echo.Trimmed PackageName: "!trimmedPackageName!"
			echo.!trimmedPackageName!>>"%packagesFile%"
		)
		echo Packge cache is created %packagesFile%!
	)

	rem Get the search mask from the first parameter
	set "searchMask=%~1"

	echo Trying to remove '%searchMask%'
	rem Search for the line containing the search mask in the temporary file
	set "packageLine="
	for /f "usebackq delims=" %%a in ("%packagesFile%") do (

		if "%searchMask%"=="DeleteAllPackages" (
			rem DeleteAllPackages tries to remove all packes from the ISO

			:yesNoForTotalRemoval
				set /P c=Are you sure you want to continue[Y/N]?
				if /I "%c%" EQU "Y" goto :TotalRemoval
				if /I "%c%" EQU "N" goto :eof
			goto :yesNoForTotalRemoval

			:TotalRemoval
			echo Are you sure you want to do this? This is experimental feature and will probably not work.
			echo Removing '%%a'
			dism /image:%mountDir% /Remove-Package /PackageName:%%a /NoRestart
			dism /image:%mountDir% /Remove-ProvisionedAppxPackage /PackageName:%%a /NoRestart
		) else (
			echo %%a | findstr /i /c:"%searchMask%" >nul
			rem If no matching line is found, display an error message

			if not errorlevel 1 (
				rem echo Found '%%a'. Removing it from the imange...
				dism /image:%mountDir% /Remove-Package /PackageName:%%a /NoRestart > nul
				dism /image:%mountDir% /Remove-ProvisionedAppxPackage /PackageName:%%a /NoRestart > nul
				echo Deleted package %%a
			)
		)

	)
goto :eof

rem *******************************************************
rem Function to take over and delete directory
rem  Usage: call :DeleteDirectory "directory_path"
rem   directory_path: The path of the directory to be deleted.
rem *******************************************************
:DeleteDirectory
	setlocal enabledelayedexpansion
	echo Deleting "%~1"
	if exist "%~1" (
		takeown /r /f "%~1" > nul
		icacls  "%~1" /grant Administrators:F /T /C > nul
		rd  "%~1" /s /q
	)
goto :eof

rem *******************************************************
rem Function to trim leading and trailing whitespace from a string
rem Usage: call :TrimWhitespace inputString outputVariable
rem *******************************************************
:trimWhitespace
	setlocal enabledelayedexpansion

	rem Retrieve the input string
	set "input=%~1"

	rem Trim leading whitespace
	for /f "tokens=* delims= " %%a in ("%input%") do (
		set "input=%%a"
	)

	rem Trim trailing whitespace
	for /f "tokens=* delims= " %%a in ("%input%") do (
		set "input=%%a"
	)

	rem Set the output variable
	endlocal & set "%~2=%input%"
goto :eof


:Trim
	SetLocal EnableDelayedExpansion
	set Params=%*
	for /f "tokens=1*" %%a in ("!Params!") do EndLocal & set %1=%%b
exit /b


rem *******************************************************
rem Function to disable a feature
rem Usage: call :DisableAndRemoveFeature FeatureName
rem *******************************************************
:DisableAndRemoveFeature
	setlocal

	set "featureName=%~1"
	rem Execute the DISM command to disable and remove the feature
	echo Disabling feature %featureName%
	dism /image:%mountDir% /Disable-Feature /FeatureName:"%featureName%" /Remove /NoRestart > nul

	rem Restore the previous environment
	endlocal
goto :eof


:GetWinGet

	echo on
	echo Injecting WinGet into Windows image.
	echo $progressPreference = 'silentlyContinue' > getwinget.ps1
	echo $latestWingetMsixBundleUri = $(Invoke-RestMethod https://api.github.com/repos/microsoft/winget-cli/releases/latest).assets.browser_download_url ^| Where-Object {$_.EndsWith(".msixbundle")} >> getwinget.ps1
	echo $latestWingetMsixBundle = $latestWingetMsixBundleUri.Split("/")[-1] >> getwinget.ps1
	echo Write-Information "Downloading winget to artifacts directory..." >> getwinget.ps1
	echo Invoke-WebRequest -Uri $latestWingetMsixBundleUri -OutFile "./winget.msixbundle" >> getwinget.ps1
	echo Invoke-WebRequest -Uri https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx -OutFile Microsoft.VCLibs.x64.14.00.Desktop.appx >> getwinget.ps1
	powershell -executionpolicy bypass -file getwinget.ps1

	REM TODO check if it was downloaded and only than inject it
	if exist "winget.msixbundle" (
		dism.exe /image:%mountDir% /Add-ProvisionedAppxPackage /PackagePath:Microsoft.VCLibs.x64.14.00.Desktop.appx /SkipLicense
		dism.exe /image:%mountDir% /Add-ProvisionedAppxPackage /PackagePath:winget.msixbundle /SkipLicense
	)

	del getwinget.ps1

	echo off

goto :eof