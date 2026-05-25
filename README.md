# tiny11builder
*Scripts to build a trimmed-down Windows 11 image - now in **PowerShell**!*

## Introduction :
Tiny11 builder, now completely overhauled. <br> After more than a year (for which I am so sorry) of no updates, tiny11 builder is now a much more complete and flexible solution - one script fits all. Also, it is a steppingstone for an even more fleshed-out solution.

You can now use it on ANY Windows 11 release (not just a specific build), as well as ANY language or architecture.
This is made possible thanks to the much-improved scripting capabilities of PowerShell, compared to the older Batch release.

This is a script created to automate the build of a streamlined Windows 11 image, similar to tiny10.
The script has also been updated to use DISM's recovery compression, resulting in a much smaller final ISO size, and no utilities from external sources. The only other executable included is **oscdimg.exe**, which is provided in the Windows ADK and it is used to create bootable ISO images. 
Also included is an unattended answer file, which is used to bypass the Microsoft Account on OOBE and to deploy the image with the `/compact` flag.
It's open-source, **so feel free to add or remove anything you want!** Feedback is also much appreciated.

Also, for the very first time, **introducing tiny11 core builder**! A more powerful script, designed for a quick and dirty development testbed. Just the bare minimum, none of the fluff. 
This script generates a significantly reduced Windows 11 image. However, **it's not suitable for regular use due to its lack of serviceability - you can't add languages, updates, or features post-creation**. tiny11 Core is not a full Windows 11 substitute but a rapid testing or development tool, potentially useful for VM environments.

---

## ⚠️ Script versions:
- **tiny11maker.ps1** : The regular script, which removes a lot of bloat but keeps the system serviceable. You can add languages, updates, and features post-creation. This is the recommended script for regular use.
- ⚠️ **tiny11coremaker.ps1** : The core script, which removes even more bloat but also removes the ability to service the image. You cannot add languages, updates, or features post-creation. This is recommended for quick testing or development use.
- 🍎 **tiny11maker.sh** : Bash port of `tiny11maker.ps1` for macOS / Linux. Same removals and tweaks as the PowerShell version, with one documented limitation — see the [macOS / Linux instructions](#macos--linux-instructions) below.

## Instructions:
1. Download Windows 11 from the [Microsoft website](https://www.microsoft.com/software-download/windows11) or [Rufus](https://github.com/pbatard/rufus)
2. Mount the downloaded ISO image using Windows Explorer.
3. Open **PowerShell 5.1** as Administrator. 
5. Change the script execution policy :
```powershell
Set-ExecutionPolicy Bypass -Scope Process
```
> Using `-Scope Process` you keep your original policy intact as this change only lasts for the current PowerShell session. 

6. Start the script :
```powershell
C:/path/to/your/tiny11/script.ps1 -ISO <letter> -SCRATCH <letter>
``` 
> You can see of the script by running the `get-help` command.

6. Select the drive letter where the image is mounted (only the letter, no colon (:))
7. Select the SKU that you want the image to be based.
8. Sit back and relax :)
9. When the image is completed, you will see it in the folder where the script was extracted, with the name tiny11.iso

---

## macOS / Linux instructions:

`tiny11maker.sh` is a bash port of `tiny11maker.ps1`. It performs the same modifications using open-source userland tools — no Windows host required.

### Dependencies (macOS)

```bash
brew install wimlib hivex xorriso p7zip
```

On Linux, install the equivalents (`wimlib`, `libhivex-bin` / `hivex`, `xorriso`, `p7zip-full`) via your package manager.

### Usage

```bash
./tiny11maker.sh -s <source> [-o <output.iso>] [-i <index>] [-w <work_dir>] [-y]
```

Where `<source>` is either a Windows 11 ISO file *or* a directory containing the extracted ISO contents (i.e. with `sources/`, `boot/`, `efi/` at its top level).

Example — full path, prompted for SKU index, output next to the script:

```bash
./tiny11maker.sh -s ~/Downloads/Win11_24H2_English_x64.iso
```

Example — pre-pick index 6 (Pro), custom output path, non-interactive:

```bash
./tiny11maker.sh -s ~/Downloads/Win11.iso -i 6 -o ~/Desktop/tiny11.iso -y
```

The script writes a timestamped log next to itself (e.g. `tiny11_20251225_133742.log`) and uses ~30 GB of temporary disk space in the work directory.

### ⚠️ Differences vs. the PowerShell version

1. **No WinSxS component-store pruning.** `DISM /Cleanup-Image /StartComponentCleanup /ResetBase` is implemented inside Windows' servicing stack and has no cross-platform equivalent. The resulting ISO will be larger than what the PowerShell script produces on Windows (in testing against Windows 11 25H2 x64: 6.7 GB output vs. 7.9 GB source — about half the savings the Windows version delivers).
2. **`install.wim` may be split into `install.swm` files.** Homebrew's `xorriso` bottle is built without UDF support, so the ISO uses plain ISO 9660 Level 3 (per-file ceiling: 4 GiB). If the recaptured `install.wim` is larger than that, the script auto-splits it into `install.swm`/`install2.swm`. Windows' installer recognizes split wims natively — this is the same approach Microsoft itself uses for FAT32 boot USBs.
3. **Compression is non-solid LZMS.** The PowerShell script uses `/Compress:recovery` (solid LZMS). `wimlib-imagex split` rejects solid archives, so the bash port uses non-solid LZMS to stay splittable. Output is ~10-15% larger than solid would produce.
4. **Provisioned appx removal is folder-level.** The script deletes matched package directories under `Program Files\WindowsApps`, which prevents installation for new users. The staged-app registry entries aren't fully pruned the way `DISM /Remove-ProvisionedAppxPackage` would; Windows handles the missing paths gracefully on first boot.
5. **Offline registry edits use `libhivex` via Python ctypes.** Homebrew's `hivex` bottle omits `hivexregedit` (a Perl-bindings tool). The bash port ships a small `tiny11_hive.py` helper that calls `libhivex` directly to do surgical per-value edits — no Perl, no Python pip deps beyond the stdlib.

---

## What is removed:
<table>
  <tbody>
    <tr>
      <th>Tiny11maker</th>
      <th>Tiny11coremaker</th>
    </tr>
    <tr>
      <td>
        <ul>
          <li>Clipchamp</li>
          <li>News</li>
          <li>Weather</li>
          <li>Xbox</li>
          <li>GetHelp</li>
          <li>GetStarted</li>
          <li>Office Hub</li>
          <li>Solitaire</li>
          <li>PeopleApp</li>
          <li>PowerAutomate</li>
          <li>ToDo</li>
          <li>Alarms</li>
          <li>Mail and Calendar</li>
          <li>Feedback Hub</li>
          <li>Maps</li>
          <li>Sound Recorder</li>
          <li>Your Phone</li>
          <li>Media Player</li>
          <li>QuickAssist</li>
          <li>Internet Explorer</li>
          <li>Tablet PC Math</li>
          <li>Edge</li>
          <li>OneDrive</li>
        </ul>
      </td>
      <td>
        <ul>
          <li>all from regular tiny +</li>
          <li>Windows Component Store (WinSxS)</li>
          <li>Windows Defender (only disabled, can be enabled back if needed)</li>
          <li>Windows Update (wouldn't work without WinSxS, enabling it would put the system in a state of failure)</li>
          <li>WinRE</li>
        </ul>
      </td>
    </tr>
  </tbody>
</table>

Keep in mind that **you cannot add back features in tiny11 core**! <br>
You will be asked during image creation if you want to enable .net 3.5 support!

---

## Known issues:
- Although Edge is removed, there are some remnants in the Settings, but the app in itself is deleted. 
- You might have to update Winget before being able to install any apps, using Microsoft Store.
- Outlook and Dev Home might reappear after some time. This is an ongoing battle, though the latest script update tries to prevent this more aggressively.
- If you are using this script on arm64, you might see a glimpse of an error while running the script. This is caused by the fact that the arm64 image doesn't have OneDriveSetup.exe included in the System32 folder.

---

## Features to be implemented:
- ~~disabling telemetry~~ (Implemented in the 04-29-24 release!)
- ~~more ad suppression~~ (Partially implemented in the 09-06-25 release!)
- improved language and arch detection
- more flexibility in what to keep and what to delete
- maybe a GUI???

And that's pretty much it for now!
## ❤️ Support the Project

If this project has helped you, please consider showing your support! A small donation helps me dedicate more time to projects like this.
Thank you!

**[Patreon](http://patreon.com/ntdev) | [PayPal](http://paypal.me/ntdev2) | [Ko-fi](http://ko-fi.com/ntdev)**
Thanks for trying it and let me know how you like it!
