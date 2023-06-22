# Set the global error action preference to continue
$ErrorActionPreference = "Continue"
function Remove-RegistryValue
{
    param (
        [Parameter(Mandatory = $true)]
        [string]$RegistryPath,

        [Parameter(Mandatory = $true)]
        [string]$ValueName
    )

    # Check if the registry path exists
    if (Test-Path -Path $RegistryPath)
    {
        $registryValue = Get-ItemProperty -Path $RegistryPath -Name $ValueName -ErrorAction SilentlyContinue

        # Check if the registry value exists
        if ($registryValue)
        {
            # Remove the registry value
            Remove-ItemProperty -Path $RegistryPath -Name $ValueName -Force
            Write-Host "Registry value '$ValueName' removed from '$RegistryPath'."
        }
        else
        {
            Write-Host "Registry value '$ValueName' not found in '$RegistryPath'."
        }
    }
    else
    {
        Write-Host "Registry path '$RegistryPath' not found."
    }
}

# figure this out later how to set updates to security only
#Import-Module -Name PSWindowsUpdate; 
#Stop-Service -Name wuauserv
#Set-WUSettings -MicrosoftUpdateEnabled -AutoUpdateOption 'Never'
#Start-Service -Name wuauserv

$taskbarPath = "$env:AppData\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"
# Delete all files in the Taskbar directory
Get-ChildItem -Path $taskbarPath -File | Remove-Item -Force

Remove-RegistryValue -RegistryPath "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband" -ValueName "FavoritesRemovedChanges"
Remove-RegistryValue -RegistryPath "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband" -ValueName "FavoritesChanges"
Remove-RegistryValue -RegistryPath "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband" -ValueName "Favorites"

# Delete Edge Icon from desktop
$desktopPath = [Environment]::GetFolderPath('Desktop')
$edgeShortcutFiles = Get-ChildItem -Path $desktopPath -Filter "*Edge*.lnk"
# Check if Edge shortcuts exist on the desktop
if ($edgeShortcutFiles) 
{
    foreach ($shortcutFile in $edgeShortcutFiles) 
    {
        # Remove each Edge shortcut
        Remove-Item -Path $shortcutFile.FullName -Force
        Write-Host "Edge shortcut '$($shortcutFile.Name)' removed from the desktop."
    }
}

# Restart the explorer process
Stop-Process -Name explorer -Force
Start-Process explorer

if (Test-Path 'C:\Windows\Setup\Scripts\winutil.ps1') 
{ 
#    Invoke-Expression -Command "winget install --id nomacs"
    Invoke-Expression -Command "C:\Windows\Setup\Scripts\winutil.ps1"
}