# tiny11builder

Scripts to build a trimmed-down Windows 11 image.

This is a script to automate the build of a streamlined Windows 11 image, similar to tiny11.
My main goal is to use only Microsoft utilities like DISM, and nothing external. The only executable included is oscdimg.exe, which is provided in the Windows ADK and it is used to create bootable ISO images. Also included is an unattended answer file, which is used to bypass the MS account on OOBE and to deploy the image with the /compact flag.
It's open-source, so feel free to add or remove anything you want! Feedback is also much appreciated.

As of now, only build 22621.525 (the one that can be downloaded from the Microsoft website), 22621.1265 (the latest public build) and 25300 (latest Insider build as of now) are supported.

# but do NOT use the oscdimg.exe as the signing is missing a proper timestamp. Always use official Microsoft tools from their website. oscdimg.exe is included in the Windows ADK Package located @ https://learn.microsoft.com/en-us/windows-hardware/get-started/adk-install

Why should you not trust digital signatures - Check out the latest hack from 3cx and it exploiting them in this article: https://www.bleepingcomputer.com/news/microsoft/10-year-old-windows-bug-with-opt-in-fix-exploited-in-3cx-attack/

Instructions:

1. Download Windows 11 22621.1265 from UUPDump or 22621.525 or 25300 from the Microsoft website (<https://www.microsoft.com/software-download/windows11>)
2. Mount the downloaded ISO image using Windows Explorer.
3. For .1265, run tiny11 creator.bat as administrator. For .525 or 25300, use the aptly-named script (also as administrator).
4. Select the drive letter where the image is mounted (only the letter, no colon (:))
5. Select the SKU that you want the image to be based.
6. Sit back and relax :)
7. When the image is completed, you will see it in the folder where the script was extracted, with the name tiny11.iso

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
