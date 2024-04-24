# tiny11builder

Scripts to build a trimmed-down Windows 11 image - now in **PowerShell**!

Tiny11 builder, now completely overhauled.
After more than a year (for which I am so sorry) of no updates, tiny11 builder is now a much more complete and flexible solution - one script fits all. Also, it is a steppingstone for an even more fleshed-out solution.

You can now use it on ANY Windows 11 release (not just a specific build), as well as ANY language or architecture.
This is made possible thanks to the much-improved scripting capabilities of PowerShell, compared to the older Batch release.

Since it is written in PowerShell, you need to set the execution policy to  `Unrestricted`, so that you could run the script.
If you haven't done this before, make sure to run `Set-ExecutionPolicy unrestricted` as administrator in PowerShell before running the script, otherwise it would just crash.

This is a script created to automate the build of a streamlined Windows 11 image, similar to tiny11.
My main goal is to use only Microsoft utilities like DISM, and no utilities from external sources. The only executable included is **oscdimg.exe**, which is provided in the Windows ADK and it is used to create bootable ISO images. 
Also included is an unattended answer file, which is used to bypass the Microsoft Account on OOBE and to deploy the image with the `/compact` flag.
It's open-source, **so feel free to add or remove anything you want!** Feedback is also much appreciated.

**Instructions:**
  1. Download Windows 11 from the Microsoft website (<https://www.microsoft.com/software-download/windows11>)
  2. Mount the downloaded ISO image using Windows Explorer.
  3. Select the drive letter where the image is mounted (only the letter, no colon (:))
  4. Select the SKU that you want the image to be based.
  5. Sit back and relax :)
  6. When the image is completed, you will see it in the folder where the script was extracted, with the name tiny11.iso

**What is removed:**
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

**Known issues:**
  1. Although Edge is removed, there are some remnants in the Settings. But the app in itself is deleted. You can install any browser using WinGet (after you update the app using Microsoft Store). If you want Edge, Copilot and Web Search back, simply install Edge using Winget: `winget install edge`. Note: You might have to update Winget before using Microsoft Store.
  2. Outlook and Dev Home might reappear after some time.
  3. If you are using this script on arm64, you might see a glimpse of an error while running the script. This is caused by the fact that the arm64 image doesn't have OneDriveSetup.exe included in the System32 folder.

**Features to be implemented:**
  - disabling telemetry
  - more ad suppression
  - improved language and arch detection
  - more flexibility in what to keep and what to delete
  - maybe a GUI???

**Suggestions / Off-Topic** (CAUTION! The following is not related to the project can be dangerous if used blindly!:
  - Optimizing Tiny11 Images storage. Supprusingly, binary diff between Orignal and Tiny11 images is pretty small (~90Mb).
    You can store one or more binary diffs along only one base image using rdiff (<https://github.com/librsync/librsync>)
    - To create the diff:
      ```
      rdiff signature orig.iso orig.iso.sig
      rdiff delta tiny11.iso orig.sig tiny11.iso.rdiff
      del orig.sig
      ```
    - To restore the image using rdiff:
      ```
      rdiff patch orig.iso tiny11.iso tiny11.iso.rdiff
      ```

  - Optimazing final install even more. With manual install you have more control and can squeeze a few more hunders of Mbs from initial image. Here is an example:
      - When first windows Setup screen appears, press (Shift-F10) to open terminal
      - Partition your drive. Example of adding a new windows partition on a free space at the end of the drive
        with already installed EFI OS (Windows / Linux):
          ```
          diskpart
          list disk
          sel disk 0 [Choose you disk]
          sel par 1
          assign letter=e
          create par pri
          list par
          sel par 3 [Choose you partition]
          format quick compress
          assign letter=c
          ```
      - Install windows:
          ```
          dism /apply-image /imagefile:d:\sources\install.wim /index:1 /applydir:c:\ /compact
          bcdboot c:\windows /s e: /f UEFI
          ```
      - Close terminal, Quit Windows Setup
      - Let windows reboot a few times and ask you questions to initialize your new Windows install

  - Things to do after install:
    - If you installed windows manually or on existing partition, you won't have Recovery Partition (WRE). If you are planning on enabling Bitlocker, Windows will create WRE partition automatically. You can disable that behaviour by disabling Recovery:
      ```
      reagentc /disable
      ```
    - Even though Tiny11 installed in "Compact" mode, only pre-imaged windows components will be compressed. You might consider compressing the whole C: drive by using corespondng option in drive properties window.
    - If you prefer using password for Bitlocker instead of auto-decryption using TPM (and you should!), you can do the followng:
      - Disable TPM in Bios (CAUTON! Changing BIOS settings might cause irreversible damage to you hardware and data)
      - Enable non-TPM (password) encryption:
        - gpedit.msc (Local Group Policy Editor)
        - Navigate to: Computer Configuration/Administrative Templates/Windows Components/BitLocker Drive Encryption/Operating System Drive/Require additional authentication at startup
        - Choose (*) Enabled
        - Choose [v] Allow BitLocker without a compatible TPM
        - If the drive is no encrypted, enable Bitlocker as usual
        - If the drive is already encrypted with TPM, replace TPM with Password for bitlocker:
          ```
          manage-bde -status
          manage-bde -protectors -delete C: -t TPM
          manage-bde -protectors -add C: -password
          manage-bde -status
          ```
 
And that's pretty much it for now!
Thanks for trying it and let me know how you like it!

