@echo off

setlocal

:: Define the builds to modify
set "builds=19041 19042"

:: Define the language and architecture
set "lang=en-us"
set "arch=x64"

:: Remove OneDrive
echo Removing OneDrive...
start /wait "" "%SystemRoot%\SysWOW64\OneDriveSetup.exe" /uninstall
echo OneDrive removed.

:: Remove Cortana
echo Removing Cortana...
PowerShell -Command "Get-AppxPackage -allusers Microsoft.549981C3F5F10 | Remove-AppxPackage"
echo Cortana removed.

:: Remove Microsoft Teams (personal)
echo Removing Microsoft Teams (personal)...
PowerShell -Command "Get-AppxPackage -allusers Microsoft.Todos | Remove-AppxPackage"
echo Microsoft Teams (personal) removed.

:: Remove Edge
echo Removing Edge...
PowerShell -Command "Get-AppxPackage -allusers Microsoft.MicrosoftEdge | Remove-AppxPackage"
PowerShell -Command "Get-AppxPackage -allusers Microsoft.MicrosoftEdge.DevToolsClient | Remove-AppxPackage"
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband" /v "Favorites" /f
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband" /v "FavoritesChanges" /f
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband" /v "Pinned" /f
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband" /v "LayoutCycle" /f
echo Edge removed.

:: Remove Edge icon from taskbar (optional)
echo Removing Edge icon from taskbar...
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband" /v "Favorites" /f
echo Edge icon removed from taskbar.

:: Remove remnants in Settings (optional)
echo Removing remnants in Settings...
PowerShell -Command "Get-AppxPackage -allusers Microsoft.MicrosoftEdge.Stable | Remove-AppxPackage"
echo Remnants in Settings removed.

:: End
echo Done.

pause
