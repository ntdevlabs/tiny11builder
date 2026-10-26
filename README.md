# Tiny11 Builder

Build a smaller Windows 11 ISO with a native WinUI app. Download an official
Windows image or use your own, choose what to remove, and create your ISO.

## Download

[**Get the latest version**](https://github.com/ntdevlabs/tiny11builder/releases/latest)

Choose **x64** for Intel/AMD PCs or **ARM64** for Windows on ARM. Extract the
entire ZIP and open `Tiny11Builder.exe`. Administrator approval is required.

## Choose your build

| Mode | What it does |
| --- | --- |
| Tiny11 | Removes bundled apps and unwanted features while keeping updates, Defender and recovery. |
| Tiny11 Core | Trims further by removing servicing and recovery support and disabling Defender. |
| Custom | Choose individual apps, components, features and services to remove or keep. |

Turbo mode speeds up image processing. Interrupted builds can resume from saved
progress after the image is committed or compressed. Core and component-store
removals automatically use the legacy Windows installer.

**Core cannot be updated or serviced. Protected Custom removals can break
Windows. Test your ISO in a VM before installing it.**

## Video

[**Watch on YouTube**](https://youtu.be/LfEWIz5mQ8A)

## Screenshots

### Welcome

![Tiny11 Builder welcome screen with Tiny11, Tiny11 Core and Custom modes](docs/images/ui-welcome.png)

### Components

![Component choices and the Tiny11 Core warning](docs/images/ui-components.png)

### Build settings

![Build settings with Turbo mode and optional image cleanup](docs/images/ui-build-settings.png)

## Booting from Ventoy

ISOs built here boot from Ventoy in its default (non-wimboot) mode — no Rufus,
no extracted USB stick, no manual patching. If an older build stops at
**"A media driver your computer needs is missing"**, the wording is misleading:
it is not a missing disk driver, it means Setup never saw its installation
media. Rebuild the ISO; nothing about the Ventoy stick needs to change.

Ventoy does not extract a Windows ISO. It boots the ISO's own `boot.wim` and
injects `vtoyjump.exe` so the ISO can be mounted as a virtual drive before
`setup.exe` runs. Two builder behaviours broke that:

| Change | Why it broke Ventoy |
| --- | --- |
| The WinPE launch chain forced to a fixed `X:\sources\setup.exe` (`Setup\CmdLine` in the scripts, `winpeshl.ini` in the app) | Setup takes the boot RAM disk as its media root, finds no `install.wim` beside it and stops. Core only. |
| `oscdimg -udfver102` | UDF 1.02 cannot describe a file larger than 4 GB, so a trimmed `install.wim` is written with metadata that lean readers mis-parse. |

Both are fixed: the stock WinPE launch chain is left alone, so the boot image's
own `setup.exe` bootstrap locates the media before running that media's
`sources\setup.exe`, and ISOs are built with `-udfver150`. A build also refuses
to emit an ISO whose `sources\` has no usable `install.wim`/`install.esd`,
because that failure looks identical to this one.

If a rebuilt ISO still fails, press `Shift+F10` at the error and run `diskpart`,
then `list volume`. No virtual DVD drive means `vtoyjump` never ran;
`X:\Windows\System32\ventoy\vtoype.log` records why a mount failed. `Ctrl+W` at
the Ventoy menu switches to wimboot mode, a different boot path. Also check that
Ventoy is 1.1.07 or newer and that its data partition is NTFS rather than exFAT.

## More information

[Usage, compatibility and build instructions](docs/usage.md) ·
[Attribution and distribution notes](NOTICE.md)

The original PowerShell builders remain in this repository.

## Support the project

Tiny11 Builder is free. If it saved you time, you can help fund development,
testing across Windows releases and fixes for reported problems.

**[Monthly support](https://github.com/sponsors/ntdevlabs)** ·
**[One-time donation](https://ko-fi.com/ntdev)**

Prefer another service? [Patreon](https://www.patreon.com/ntdev) ·
[PayPal](https://paypal.me/ntdev2)

No donation is needed to use any feature. Reporting reproducible bugs,
testing builds in a VM and sharing the [demo](https://youtu.be/LfEWIz5mQ8A)
help too.
