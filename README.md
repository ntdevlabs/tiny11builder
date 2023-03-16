# tiny11builder, PowerShell edition

This repository contains a PowerShell rewrite of the original, batch written, ntdevlabs/tiny11builder repository.

This script will:
* download the latest Windows 11 iso from Microsoft.
* build a trimmed-down Windows 11 installer iso.

Two executables are included:
-oscdimg.exe (it is part of the Windows ADK: <https://learn.microsoft.com/fr-fr/windows-hardware/get-started/adk-install#download-the-adk-for-windows-11-version-22h2>. It is used to create bootable ISO images.
-WindowsIsoDownloader.exe (you can find the source code and build it if you want: <https://github.com/ianis58/WindowsIsoDownloader>) (not directly included, but is downloaded by the PowerShell script).

Also included is autounattend.xml file:
-it bypass the need to connect to/create a Microsoft account during OOBE (Out Of the Box Experience, AKA the first startup setup wizard).
-it automatically accept the EULA (End User Licence Agreement) so it skip this step.
-it skip the product key step (if your PC was bought with Windows 10 or 11 on it and it has EFI/UEFI, the product key is stored in EFI/UEFI and is automatically used. Otherwise, you'll be able to set it after setup).

Instructions:

1. Download this repository as zip and unzip it where you want.
2. Open a Powershell as administrator, go to the extraction path, and run the following commands:
```
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\tiny11creator.ps1
```
3. Sit back and relax :) (it runs for 25 minutes approximately on my old-but-decent laptop). You might see some errors with RemoveWindowsPackage but it's not an issue (has to do with the order and dependencies of packages to remove).
4. The created Windows 11 installer iso is available in c:\tiny11.iso
Optional:
5. After setup and once connected to the internet, I recommend to install the package manager from Microsoft called winget. Run this command in PowerShell:
```
Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe
```
Then you can run one of these to get your favorite browser:
```
winget install Mozilla.Firefox
winget install Microsoft.Edge
winget install Opera.Opera
winget install Google.Chrome
```

What is removed:
Clipchamp,
News,
Weather,
Xbox,
GetHelp,
GetStarted,
Office Hub,
Solitaire,
PeopleApp,
PowerAutomate,
ToDo,
Alarms,
Mail and Calendar,
Feedback Hub,
Maps,
Sound Recorder,
Your Phone,
Media Player,
QuickAssist,
Internet Explorer,
LA57 support,
OCR for en-us,
Speech support,
TTS for en-us,
Media Player Legacy,
Tablet PC Math,
Wallpapers,
Edge,
OneDrive

Small tweaks:
-dark theme enabled by default.
-some file explorer config turned on: show hidden files, show known file extensions, ...
-taskbar aligned to the left.

Known issues:

1. Microsoft Teams is somehow still there. Feel free to create a PR if you can fix it!
2. Although Edge is removed, the desktop icon, a ghost of its taskbar pin, and some remnants in the Settings about it are still showing.
