# Tiny11 Builder 0.2.0

A feature and reliability update for the WinUI builder.

## What's new

- **Offline updates:** integrate local CAB/MSU files in prerequisite order before
  removals. Packages are privately staged and hash-checked. Pending servicing
  remains visible in reports and protects files needed at first boot.
- **Portable Custom recipes:** click Custom to choose manually or load JSON.
  Imported groups appear checked; blank recipe paths keep your selected ISO,
  edition, workspace and output. Source changes retain the removal selections.
- **More precise removals:** inspect individual files, driver packages, fonts,
  resources and payload folders. Six Custom-only groups cover 32-bit support,
  desktop .NET, IMEs, speech/OCR, printer/scanner drivers and bundled media.
- **Smaller stock boot image:** Custom can rebuild the source's `boot.wim` as
  one maximum-compressed, bootable Setup index, retaining Setup and native drivers.
- **Extreme recipe:** `recipes/cd700-custom.json` supplies 37 checked removal
  groups and an optional 700,000,000-byte whole-ISO limit. It works as a portable
  profile, not a prepared-image copy or a fourth build mode.
- **Preset details:** inspect the complete read-only Tiny11/Core removal plans.
- **Complete inventory export:** save all loaded components to UTF-8 text,
  regardless of filters or selections.
- **Interface languages:** English and Simplified Chinese, plus validated external
  JSON catalogs for additional translations. See [the translator guide](translation.md).

## Improvements and fixes

- Sidebar and primary navigation share validation and show clear prerequisites.
- Custom settings no longer expose image preservation or an external installer
  picker. Recipe-specific size limits can be removed without editing JSON.
- New-build workspace preflight uses twice the source ISO's file size, plus
  selected update staging, instead of the large expanded-image estimate.
- Fixed the Core/legacy Setup "cannot read the ProductKey setting" failure
  without inserting a product key.
- Fixed required-privilege inheritance when launching DISM, including the
  disabled-privilege case associated with error 1314.
- Improved trusted Microsoft image-tool detection for catalog signatures,
  timestamped certificates and Windows Kits publishers; unsigned/tampered
  tools are still rejected.
- Fixed false final-image failures when enablement releases report different
  build numbers, such as 22621 becoming 22631.
- Extended guarded removal of stale Edge Update and WebView installed-program
  entries when their corresponding offline files are gone.
- Improved sidebar scrolling and translated accessibility labels.

## Downloads and use

Choose **x64** for Intel/AMD PCs or **ARM64** for Windows on ARM. Extract the
entire ZIP and run `Tiny11Builder.exe` with administrator approval. Both packages
are self-contained; current compatible Microsoft image tools may still be required.
SHA-256 sidecars accompany the ZIPs.

Existing ISOs and installation USBs are not patched by updating the app.
Rebuild affected output from the original Windows media.

Tiny11 and Tiny11 Core preset selections are unchanged. Core is nonserviceable
and removes update/recovery support and antivirus protection. Protected Custom
removals can break boot, applications, input methods and devices. Test output
in an isolated VM. The extreme recipe does **not** guarantee CD size or boot
compatibility for every source. Unknown Insider builds require compatible tools;
Windows Server is unsupported. Packages are unsigned.

See [usage and compatibility](usage.md) and [distribution notices](../NOTICE.md).
All features remain free.

## Support

[GitHub Sponsors](https://github.com/sponsors/ntdevlabs) |
[Patreon](https://www.patreon.com/ntdev) |
[Ko-fi](https://ko-fi.com/ntdev) |
[PayPal](https://paypal.me/ntdev2)
