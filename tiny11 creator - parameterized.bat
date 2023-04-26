@echo off
cd /d "%~dp0"

setlocal EnableExtensions EnableDelayedExpansion

rem Use those parameters to adapt the script easier to other editions!
set TINY11DIR=C:\tiny11
set TINY11SCRATCHDIR=c:\scratchdir
set TINY11DISM=dism
set TINY11CLS=rem cls

rem Some examples:
rem set TINY11DISM="D:\WAIKs\11\Assessment and Deployment Kit\Deployment Tools\amd64\DISM\dism.exe"
rem set TINY11CLS=rem cls (for debugging)

rem Currently unused!
rem set TINY11LANG=en-us
rem set TINY11ARCH1=x64
rem set TINY11ARCH2=amd64
rem set TINY11WINVER1=11.0.22621.1
rem set TINY11WINVER2=10.0.22621.1265
rem set TINY11WINVER3=10.0.22621.1


title tiny11 builder alpha
echo Welcome to the tiny11 image creator!
timeout /t 3 /nobreak > nul
%TINY11CLS%

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

md %TINY11DIR%
md %TINY11DIR%\tmp
echo Copying Windows image...
xcopy.exe /E /I /H /R /Y /J %DriveLetter% %TINY11DIR% >nul
echo Copy complete!
sleep 2
%TINY11CLS%

echo Getting image information:
%TINY11DISM% /Get-WimInfo /wimfile:%TINY11DIR%\sources\install.wim
set index=
set /p index=Please enter the image index:
set "index=%index%"

echo Mounting Windows image. This may take a while.
echo.
md %TINY11SCRATCHDIR%
%TINY11DISM% /mount-image /imagefile:%TINY11DIR%\sources\install.wim /index:%index% /mountdir:%TINY11SCRATCHDIR%

echo Mounting complete! Performing removal of applications...

call :remove_provisioned_packages_from_list
echo Removing of system apps complete! Now proceeding to removal of system packages...
timeout /t 1 /nobreak > nul
%TINY11CLS%

call :remove_packages_from_list

echo Removing Edge:
rd "%TINY11SCRATCHDIR%\Program Files (x86)\Microsoft\Edge" /s /q
rd "%TINY11SCRATCHDIR%\Program Files (x86)\Microsoft\EdgeUpdate" /s /q

echo Removing OneDrive:
takeown /f %TINY11SCRATCHDIR%\Windows\System32\OneDriveSetup.exe
icacls %TINY11SCRATCHDIR%\Windows\System32\OneDriveSetup.exe /grant S-1-5-32-544:F /T /C
del /f /q /s "%TINY11SCRATCHDIR%\Windows\System32\OneDriveSetup.exe"

echo Removal complete!
timeout /t 2 /nobreak > nul
%TINY11CLS%

call :load_registry

call :bypass_system_requirements

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
copy /y %~dp0autounattend.xml %TINY11SCRATCHDIR%\Windows\System32\Sysprep\autounattend.xml

echo Disabling Reserved Storage:
Reg add "HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\ReserveManager" /v "ShippedWithReserves" /t REG_DWORD /d "0" /f >nul 2>&1

echo Disabling Chat icon:
Reg add "HKLM\zSOFTWARE\Policies\Microsoft\Windows\Windows Chat" /v "ChatIcon" /t REG_DWORD /d "3" /f >nul 2>&1
Reg add "HKLM\zNTUSER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v "TaskbarMn" /t REG_DWORD /d "0" /f >nul 2>&1

echo Tweaking complete!

call :unmount_registry

echo Cleaning up image...
%TINY11DISM% /image:%TINY11SCRATCHDIR% /Cleanup-Image /StartComponentCleanup /ResetBase

echo Cleanup complete.
echo PRESS KEY TO CONTINUE
pause

echo Unmounting image...
%TINY11DISM% /unmount-image /mountdir:%TINY11SCRATCHDIR% /commit

echo Exporting image...
%TINY11DISM% /Export-Image /SourceImageFile:%TINY11DIR%\sources\install.wim /SourceIndex:%index% /DestinationImageFile:%TINY11DIR%\sources\install2.wim /compress:max
del %TINY11DIR%\sources\install.wim
ren %TINY11DIR%\sources\install2.wim install.wim
echo Windows image completed. Continuing with boot.wim.
timeout /t 2 /nobreak > nul
%TINY11CLS%

echo Mounting boot image:
%TINY11DISM% /mount-image /imagefile:%TINY11DIR%\sources\boot.wim /index:2 /mountdir:%TINY11SCRATCHDIR%

call :load_registry
call :bypass_system_requirements

echo Tweaking complete! 

call :unmount_registry

echo Unmounting image...
%TINY11DISM% /unmount-image /mountdir:%TINY11SCRATCHDIR% /commit 
%TINY11CLS%

echo the tiny11 image is now completed. Proceeding with the making of the ISO...
echo Copying unattended file for bypassing MS account on OOBE...
copy /y %~dp0autounattend.xml %TINY11DIR%\autounattend.xml
echo.

echo Remove tmp folder...
rd %TINY11DIR%\tmp /s /q

call :create_iso
echo Creation completed! Press any key to exit the script...
pause

echo Performing Cleanup...
rd %TINY11DIR% /s /q 
rd %TINY11SCRATCHDIR% /s /q 
echo Finished.
pause
exit

:create_iso
echo Creating ISO image...
%~dp0oscdimg.exe -m -o -u2 -udfver102 -bootdata:2#p0,e,b%TINY11DIR%\boot\etfsboot.com#pEF,e,b%TINY11DIR%\efi\microsoft\boot\efisys.bin %TINY11DIR% %~dp0tiny11.iso
goto :eof

:load_registry
echo Loading registry...
reg load HKLM\zCOMPONENTS "%TINY11SCRATCHDIR%\Windows\System32\config\COMPONENTS" >nul
reg load HKLM\zDEFAULT "%TINY11SCRATCHDIR%\Windows\System32\config\default" >nul
reg load HKLM\zNTUSER "%TINY11SCRATCHDIR%\Users\Default\ntuser.dat" >nul
reg load HKLM\zSOFTWARE "%TINY11SCRATCHDIR%\Windows\System32\config\SOFTWARE" >nul
reg load HKLM\zSYSTEM "%TINY11SCRATCHDIR%\Windows\System32\config\SYSTEM" >nul
goto :eof

:bypass_system_requirements
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
goto :eof

:unmount_registry
echo Unmounting Registry...
reg unload HKLM\zCOMPONENTS >nul 2>&1
reg unload HKLM\zDRIVERS >nul 2>&1
reg unload HKLM\zDEFAULT >nul 2>&1
reg unload HKLM\zNTUSER >nul 2>&1
reg unload HKLM\zSCHEMA >nul 2>&1
reg unload HKLM\zSOFTWARE >nul 2>&1
reg unload HKLM\zSYSTEM >nul 2>&1
goto :eof

:remove_provisioned_packages_from_list
powershell "Get-AppxProvisionedPackage -Path %TINY11SCRATCHDIR% | Select PackageName" > %TINY11DIR%\tmp\provisioned_packages_in_image.txt

for /f %%p in ('type remove_provisioned_packages.txt') do (
	for /f %%i in ('findstr %%p %TINY11DIR%\tmp\provisioned_packages_in_image.txt') do (
		call :remove_provisioned_package %%p %%i
	)
)
del /f %TINY11DIR%\tmp\provisioned_packages_in_image.txt
goto :eof

:remove_packages_from_list
Dism /Image:%TINY11SCRATCHDIR% /get-packages /format:Table > %TINY11DIR%\tmp\packages_in_image.txt

for /f %%p in ('type remove_packages.txt') do (
	for /f "delims= " %%i in ('findstr %%p %TINY11DIR%\tmp\packages_in_image.txt') do (
		call :remove_package %%p %%i
	)
)
del /f %TINY11DIR%\tmp\packages_in_image.txt
goto :eof

:remove_package
%TINY11DISM% /image:%TINY11SCRATCHDIR% /Remove-Package /PackageName:%2 > nul
goto :eof

:remove_provisioned_package
echo Removing provisioned package %1...
%TINY11DISM% /image:%TINY11SCRATCHDIR% /Remove-ProvisionedAppxPackage /PackageName: %2 > nul
goto:eof 