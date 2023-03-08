# tiny11builder

Scripts to build a trimmed-down Windows 11 image.

This is a script to automate the build of a streamlined Windows 11 image, similar to tiny11.
My main goal is to use only Microsoft utilities like DISM, and nothing external. The only executable included is oscdimg.exe, which is provided in the Windows ADK (<https://learn.microsoft.com/fr-fr/windows-hardware/get-started/adk-install#download-the-adk-for-windows-11-version-22h2>) and it is used to create bootable ISO images. Also included is an unattended answer file, which is used to bypass the MS account on OOBE and to deploy the image with the /compact flag.
It's open-source, so feel free to add or remove anything you want! Feedback is also much appreciated.

Current and new Windows 11 builds are supported, but may need some small adjustments. Please report issue or create pull requests if you're able to patch some issues.

Instructions:

1. Download latest Windows 11 iso from Microsoft website (<https://www.microsoft.com/software-download/windows11>). As an alternative you can use the automated Windows Iso Downloader tool (<https://github.com/ianis58/WindowsIsoDownloader>).
2. Place the downloaded file in C:\windows11.iso (be sure to rename it so that it match that filename).
3. Open a Powershell terminal with admin rights and run the following commands:
```
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\tiny11creator.ps1
```
4. Sit back and relax :) (it runs for 13 minutes approximately on my old-but-decent laptop)
5. When the image is completed, you will see it in c:\tiny11.iso

What is removed:
Clipchamp,
News,
Weather,
Xbox (although Xbox Identity provider is still here, so it should be possible to be reinstalled with no issues),
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

Known issues:

1. Microsoft Teams (personal) and Cortana are still here. If you find a way to remove them before I find one, feel free to help!
2. Although Edge is removed, the icon and a ghost of its taskbar pin are still available. Also, there are some remnants in the Settings. But the app in itself is deleted.
3. The script is rather inflexible, as in only the builds specified can be modified. This is because with each new build Microsoft also updates the inbox apps included. If one tries to use other builds, it will work with varying degrees of success, but some things like the removal of Edge and OneDrive as well as bypassing system requirements or other patches will always be applied.
4. Only en-us x64 is supported as of now. This can be easily fixable by the end user, just by replacing every instance of en-us with the language needed (like ro-RO and so on), and every x64 instance with arm64.

And that's pretty much it for now!
Thanks for trying it and let me know how you like it!
