# Tiny11 Builder

English | [简体中文](README.zh-CN.md)

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
