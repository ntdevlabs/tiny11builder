# tiny11builder

Scripts to build a trimmed-down Windows 11 image - now in PowerShell!
<br>
Tiny11 builder, now completely overhauled.
</br>
After more than a year (for which I am so sorry) of no updates, tiny11 builder is now a much more complete and flexible solution - one script fits all. Also, it is a steppingstone for an even more fleshed-out solution.
<br>
You can now use it on ANY Windows 11 release (not just a specific build), as well as ANY language or architecture.
This is made possible thanks to the much-improved scripting capabilities of PowerShell, compared to the older Batch release.
</br>
Since it is written in PowerShell, make sure to set the execution policy to Unrestricted, so that you could run the script.
If you haven't done this before, make sure to run:
<br> Set-ExecutionPolicy unrestricted </br>
as administrator in PowerShell before running the script, otherwise it would just crash.



This is a script created to automate the build of a streamlined Windows 11 image, similar to tiny11.
My main goal is to use only Microsoft utilities like DISM, and no utilities from external sources. The only executable included is oscdimg.exe, which is provided in the Windows ADK and it is used to create bootable ISO images. 
Also included is an unattended answer file, which is used to bypass the Microsoft Account on OOBE and to deploy the image with the /compact flag.
It's open-source, so feel free to add or remove anything you want! Feedback is also much appreciated.

Instructions:

1. Download Windows 11 from the Microsoft website (<https://www.microsoft.com/software-download/windows11>)
2. Mount the downloaded ISO image using Windows Explorer.
3. Select the drive letter where the image is mounted (only the letter, no colon (:))
4. Select the SKU that you want the image to be based.
5. Sit back and relax :)
6. When the image is completed, you will see it in the folder where the script was extracted, with the name tiny11.iso

What is removed:

- Clipchamp
- News
- Weather
- Xbox (although Xbox Identity provider is still here, so it should be possible to be reinstalled with no issues)
- GetHelp
- GetStarted
- Office Hub
- Solitaire
- PeopleApp
- PowerAutomate
- ToDo
- Alarms
- Mail and Calendar
- Feedback Hub
- Maps
- Sound Recorder
- Your Phone
- Media Player
- QuickAssist
- Internet Explorer
- Tablet PC Math
- Edge
- OneDrive

Known issues:

1. Although Edge is removed, there are some remnants in the Settings. But the app in itself is deleted. You can install any browser using WinGet (after you update the app using Microsoft Store)
2. Outlook and Dev Home might reappear after some time.
3. If you are using this script on arm64, you might see a glimpse of an error while running the script. This is caused by the fact that the arm64 image doesn't have OneDriveSetup.exe included in the System32 folder.

And that's pretty much it for now!
Thanks for trying it and let me know how you like it!
