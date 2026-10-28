# Tiny11 Builder: usage and development

English | [简体中文](usage.zh-CN.md)

Run the commands below from the repository root. For downloads, the video and
screenshots, see the [project page](../README.md).

Optional [project support](support.md) is available from **Support development**
in the sidebar and on the successful-build page. The choices open only when
clicked, never interrupt a build, and never unlock features. Payments happen
on the selected provider's website, not inside the app.

**WinUI 3 desktop application with a C# image builder.** Its step-by-step layout
uses the [S9 WoA installer](https://github.com/ntdevlabs/exynos9810-woa) as a reference.
It does not wrap or execute the PowerShell builders. The original scripts remain
unchanged below for compatibility.

## What the app does

| Mode | Behavior |
| --- | --- |
| Tiny11 | The fixed original-style removal set: bundled apps, Edge/WebView2 and OneDrive binaries, hardware-check settings, reserved-storage and automatic-encryption settings. Keeps Update, Defender, WinRE, Store and shared runtimes. Choose Custom to edit removals. |
| Tiny11 Core | Applies changes before resetting the component base. Keeps common-controls/GDI+/CRT/servicing files, deletes other WinSxS component folders, removes WinRE and disables Windows Update and Defender. These changes cannot be reversed. Use only for test VMs. |
| Custom | Starts with no removals selected. Choose individual removal groups or list preinstalled apps, protected system apps, optional features, language capabilities, CBS packages, services, drivers and WinSxS component folders. Features and services also offer disable-only. Core's irreversible removals are a separate, unchecked option with the same warning and acknowledgement. |

The presets are original-style, not command-for-command copies of the scripts.
Tiny11 cleans superseded components without `/ResetBase`, preserving the
ability to uninstall existing updates. Core does reset the base, retains
additional detected runtime/servicing families, and removes WinRE without
creating the original script's empty placeholder. These compatibility choices
are unchanged by the reliability update; further reductions require separate
Setup/boot testing.

The six-step wizard provides:

- A full-width welcome page with clickable build-mode cards. Selecting a card
  sets its mode and opens ISO selection. Cards stack in narrow windows, and a
  large faint number (1-6) fades out when the page scrolls down. Back is at the
  upper-left, with Alt+Left support. All forward actions, including Start and
  Build, are at the lower-right. There is no footer strip or redundant step count.
- One-click download of Microsoft's latest published Windows 11 ISO using the
  host architecture and display language. Unsupported languages fall back to
  English with an explicit status message. No URL or save dialog is required;
  downloads go into the selected workspace's `downloads` directory. Microsoft
  availability/region blocks are reported rather than bypassed.
  Transient connection resets and busy-server responses are retried a bounded
  number of times with visible status. After a secure-handshake reset the app
  can retry with TLS 1.2, still using Windows certificate validation. Certificate,
  DNS, proxy and permission failures are not bypassed. The failure notice links
  to Microsoft's download page; a local ISO remains an option. Interrupted
  ISO body transfers retain their partial file for retry/resume.
- Official Microsoft ISO downloads with speed/progress, strong-ETag resumable
  partial files, ISO structure checks, and optional verification against a
  published SHA-256. Only Microsoft's ISO hosts are accepted, including every
  redirect. Expiring query tokens are not persisted; resume metadata keeps only
  the Microsoft resource host/path, ETag and length.
- Local ISO and extracted installation-media input, WIM/ESD detection, edition
  selection, and automatic read-only native ISO attachment. Windows 11 Pro is
  selected when found; otherwise index 1 is selected. Restored recipes preserve
  their saved edition if it still exists.
- Three build-mode cards: Tiny11, Tiny11 Core and Custom. Individual keep/remove
  controls are shown only in Custom. Core requires an explicit agreement dialog
  before proceeding; cancellation leaves Core unapproved.
  A full image inventory requires an upfront expert warning and a separate risk
  acknowledgement before a build.
- Core and Custom WinSxS/component-payload removals always use the legacy
  Windows installer, including on 24H2 and newer media. The private Setup
  boot image starts `wpeinit.exe`, then its matching `sources\setup.exe`
  (`setup_old.exe` when present), instead of the newer SetupHost workflow.
  This is automatic, not a hardware-bypass option; no installer from a
  different Windows release is downloaded. Media without the classic
  executable fails explicitly. A resumed Core job also receives this change
  even if its final installation image was already compressed.
  This configures the booted ISO's WinPE shell using
  [Winpeshl.ini](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/winpeshlini-reference-launching-an-app-when-winpe-starts);
  it is not an in-place upgrade workaround.
- Optional .NET 3.5 from matching `sources\sxs`, compact installation, and
  best-effort local-account setup. No account, password, product key, automatic
  logon or EULA acceptance is injected.
- Automatic image-tool detection and compatible-version checks. oscdimg is
  found locally or downloaded from Microsoft's symbol server with an exact
  SHA-256 pin. No tool-selection controls or script execution-policy changes.
- Illustrated installation-option cards with switches, a structured review,
  and expandable build stages with pending, running, completed, skipped and
  failed operations. App, feature and capability removals show exact identities.
  Console-style progress bars stay in the disk log, not in the interface.
  Overall progress follows stage boundaries, not a time estimate. Items are
  grouped under their stage; a failed item is not counted again as a second
  failure for its stage. Unstarted work reads **Not run** after stopping.
  The build page separates overall progress, the current operation, output
  details and expandable operation history with more room between sections.
- Architecture-correct setup XML, UEFI boot media on x64 and ARM64, BIOS boot
  entry on x64 when the source includes it, and recovery ESD, maximum WIM or fast WIM
  compression.
- Recipe import/export, live logs, safe-boundary cancellation, persistent job
  state, verified saved progress for **Resume build**, an output SHA-256, a JSON
  report, and separate interrupted-workspace discard/recovery.

## Turbo mode and smaller images

The settings page has independent **Turbo mode**, **Remove redundant image
data**, and **Omit .NET 3.5 setup payload** switches. All default to off so
existing recipes and fixed Tiny11/Core removal sets keep their behavior.

| Setting | What changes | Tradeoff |
| --- | --- | --- |
| Turbo mode | Serialized feature/capability batches with state verification, up to eight copy workers and reduced log I/O. | More CPU/I/O and temporary space; actual benefit depends on the image and host. |
| Turbo with Core or Fast WIM output | Uses a fast-compressed writable intermediate WIM instead of recompressing large Core changes into an LZX working image. | Initial conversion can be slower and the working WIM is larger. Final compression remains as selected. |
| Recovery ESD | Solid recovery compression for the final edition. | Smallest Microsoft-tool output, usually the slowest compression stage. Turbo does not silently replace it. |
| Maximum WIM | Strong mountable WIM compression. | Easier to service, usually larger than recovery ESD. |
| Fast WIM | Faster final compression. | Larger ISO; use when build time matters more than storage. |
| Remove redundant image data | Removes downloaded update cache, temporary files, archived CBS/DISM logs, crash dumps and Windows Error Reporting archives/queues. | Logical removed bytes are recorded; they are not a prediction of compressed ISO savings. |
| Omit .NET 3.5 setup payload | Does not stage `sources\sxs` into the output ISO. .NET 3.5 can still be enabled during the build from the original source. | Adding it afterward needs other matching media or Windows Update; Core cannot add features afterward. |

Builds and inventory use normal mounts by default, including in Turbo.
Lazy mounting (`/Optimize`) is not automatically tried when a tool version
changes: one failed production probe on this PC consumed over twelve minutes.
The development opt-in retains the known-error fallback and versioned
`mount-optimization.json` advisory cache. An invalid or inaccessible advisory
cache disables lazy mounting with a diagnostic rather than stopping the build.
Turbo does not parallelize servicing against one
mounted image, disable host antivirus, alter OS power settings, or remove
integrity checking. The batch tracks every feature and reads back its state;
a failed batch is reported as failed, not as individual successes.

Capability removal is verified in both Standard and Turbo. **Uninstall Pending**
means Windows accepted the uninstall but still needs to finish it at first boot.
Serviceable builds retain the pending payloads and continue with a visible
warning; the per-capability report and operation details say **deferred**, not
absent. Superseded-component cleanup and selected Edge/WebView2 file removal
are skipped while these removals are pending. **Install Pending**, still-installed,
unknown, missing or conflicting post-removal states remain errors.
Core and Custom component-store pruning still refuse pending removals.

This can occur on previously serviced or UUP-converted media, including media
created with updates and .NET 3.5 already integrated. It does not require deleting
`pending.xml`, reverting servicing actions, or changing the UUP app list. A Core
build needs a settled source without pending operations; a serviceable image
can preserve them for first boot. Changing compression does not resolve them.

The cleaner uses a fixed allowlist. It preserves the cache/log root directories,
current `CBS.log` and `dism.log`, update databases, registry files, driver
repositories, Windows Installer cache, and servicing manifests. Download and
temporary payloads are retained when `pending.xml` or `reboot.xml` indicates
pending servicing, or when the offline registry has servicing/update reboot
markers. If a missing or corrupt hive prevents verification, sensitive targets
are retained with a warning; this is not reported as successful removal.
Registry inspection uses a private hive/log copy so loading it cannot replay
transactions into the image. Other I/O/access errors remain failures.
Unknown symlinks and junctions are refused before a cleanup
target is deleted. It does not scan the running PC for things to delete.

Component cleanup runs after removals but before enabling .NET Framework 3.5:
offline feature changes can leave work that completes only when Windows first
boots. If cleanup returns `0x800F0806` (pending operations), a serviceable
Tiny11/Custom build keeps that work and reports cleanup as **Skipped** with a
warning; the image may be larger. Other cleanup errors remain fatal. Pending
actions are never deleted or reverted to get past this error.
When that exact pending-state error occurs, selected Edge/WebView2 file removal
is also skipped for the run, because its WinSxS files may be referenced by
pending servicing. The report records that those files were kept; it does not
claim the requested removal succeeded. These warnings survive resume.
When Edge's files are actually removed, the matching Microsoft Edge entry is
also removed from both offline installed-program registry locations. Exact
identity and publisher checks keep unrelated software, Edge Update and WebView2
registrations outside this browser-entry cleanup. If file removal fails or is
deferred, the entry is retained. The running PC's registry is never targeted.
Core and explicit Custom component-payload removal also check pending files,
package states and feature states immediately before deleting component files,
even when cleanup is off. If enabling .NET 3.5 leaves pending work, those
destructive builds stop with an explanation rather than removing files needed
at first boot. Build without .NET 3.5 or use a serviceable image in that case.

Both Standard and Turbo delete offline entries through backup/restore handles without changing
their ACLs or clearing read-only attributes shared by remaining hard links.
Both handle long image paths
and reject non-image reparse points. Every build records per-operation duration,
per-stage duration in `timings.json`, setup bytes copied/omitted, and cleanup
file/byte counts in the report. Failure reports retain timings too.

Common optimizations apply in either mode: source WIM compression is retained
when not using a fast working image; duplicate targets are handled once;
already-removed payloads are skipped; preflight only re-queries the selected
edition; unnecessary feature inventories are omitted; setup files are copied
largest-first; and same-volume ISO publication renames rather than copies.
Logs stay private to the job and scratch files use its NTFS workspace.
The progress timer runs only during operations and processes bounded batches,
instead of waking continuously while the app is idle.

Before the shared deletion path was adopted, a three-run 4,000-file benchmark
measured the old ACL-rewriting path at 3.55-4.12 seconds and native deletion at
0.69-0.75 seconds. This is not a comparison of the current two modes. Real
977-file ISO staging used 1.74-5.90 seconds with four workers and 1.68-1.85
seconds with eight; results are cache- and host-dependent, not a universal
speedup promise. Omitting this image's optional .NET payload avoided copying
71,656,916 bytes (about 68.3 MiB).

An end-to-end 26H2 x64 Core build with the same removal set and recovery ESD
compression completed in **31 minutes 44 seconds**, compared with an earlier
**90 minutes 31 seconds** run on this PC. Core pruning took 37 seconds instead
of about 4 minutes 38 seconds; commit took 3 minutes 54 seconds instead of about
29 minutes 38 seconds. Initial conversion to a fast working WIM was slower
(3 minutes 8 seconds rather than 33 seconds). These are measured development
runs with different cache/system load, not a guaranteed speed multiplier.
Optimized lazy mounting was not used because this host's provider rejected it.

The resulting Turbo ISO was **3,259,584,512 bytes (3.04 GiB)**, compared with
3,331,207,168 bytes previously. About 68.3 MiB was saved by omitting the optional
setup payload; this clean Microsoft source had no extra cache files to remove.
No cache savings are claimed for that build. The ISO's Pro edition, build,
compression and SHA-256 were verified; VM boot certification remains separate.

**This is an image builder, not an installer or a live-system debloater.** It
never intentionally targets the running Windows installation, formats a disk or
modifies the source media. Only a newly created working copy is serviced.
Existing output ISOs are refused, not overwritten.

Offline registry editing temporarily mounts copied image hives under uniquely
named `HKLM\Tiny11Builder_Offline_<guid>` keys and unloads them explicitly.
Existing host registry settings are not edited. A hive load/unload failure stops
the operation and reports the exact temporary key instead of hiding the error.
Per-job hive journals let workspace recovery unload only that job's temporary
keys if the app is interrupted before the hives are unloaded.
DWORD and string writes use the native `RegSetValueExW` API on handles opened
with scoped backup/restore privileges, and every written value is read back.
Widgets are disabled with Microsoft's documented
`SOFTWARE\Policies\Microsoft\Dsh\AllowNewsAndInterests=0` policy. The protected
`Explorer\Advanced\TaskbarDa` preference is deliberately not written: newer
hosts can reject it even through the native API. Host protection services are
not disabled. The policy applies to Widgets and its taskbar content; support
still varies by Windows edition.

Search highlights use the machine policy
`SOFTWARE\Policies\Microsoft\Windows\Windows Search\EnableDynamicContentInWSB=0`.
The protected default-user `SearchSettings\IsDynamicSearchBoxEnabled`
preference is not edited. Required policy writes are still verified; access
errors are not silently ignored and no host registry protection is disabled.

Core payload deletion uses extended-length Windows paths so long
WinSxS filenames do not fail at the legacy 260-character limit. Symbolic-link
and junction restrictions are unchanged.

The acceleration uses Microsoft's documented
[/Mount-Image /Optimize](https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism-image-management-command-line-options-s14)
and
[multi-feature /Disable-Feature](https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism-operating-system-package-servicing-command-line-options)
options. Feature batches target the default Foundation package; individual
expert servicing actions remain serialized.

Offline image files may legitimately use WIM/WOF reparse tags. Those two
non-name-surrogate image-provider tags are accepted inside the owned image;
ordinary symbolic links and junctions are still refused. Source, workspace and
output paths retain their stricter reparse-point checks.

## Windows 11 build compatibility

The engine accepts **Windows 11 client builds 22621 (22H2) and newer**, including
23H2, 24H2, 25H2, 26H2 and Insider/Canary media, in **x64 and ARM64**, without a
hard-coded maximum build number. Image architecture, edition and version are
read from DISM, not inferred from the ISO filename. Package/capability/feature
identities come from the mounted image, so missing items are logged as *not
present*, rather than guessed.
Edition names are decoded using the OEM code page Windows image tools write,
with UTF byte-order marks recognized explicitly. Cyrillic text is not repaired
by replacing fonts or guessing edition names. Invalid encoded output fails
explicitly rather than silently substituting characters.

Acceptance is **not a certification that every build and every custom removal
combination boots**. This new implementation needs VM validation against
representative media for each release/channel. Unit tests cover build metadata
and simulated pipelines across release and newer/Canary-style build numbers;
the opt-in integration test exercises real media read-only.

Windows 10, including 22H2, can build Windows 11 images using compatible
Microsoft deployment tools; the host's release number is not the requirement.
The app checks the actual servicing providers rather than only the version of
the `dism.exe` launcher. Known enablement releases share servicing bases:
23H2 (22631) uses 22621; 25H2 (26200) and 26H2 (26300) use 26100.
A 26100 tool is therefore not rejected merely because a 26H2 image reports 26300.
Keep Windows/ADK servicing updates current.

Microsoft's [ADK support list](https://learn.microsoft.com/windows-hardware/get-started/adk-install)
includes 24H2, 25H2 and 26H2 for the 26100 ADK;
[DISM supported platforms](https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism-supported-platforms)
documents using Windows 11 deployment tools on Windows 10.
Unknown newer/Canary builds are not folded into these families and may still
require a preview ADK or newer tool providers. The app stops before
modifications when its tools do not meet that servicing base. It detects
installed tools automatically; there is no manual tool picker.

### Windows 10 reports image tools 10.0.19041

For image build **26300**, tools **10.0.19041.3636** are not the same case as
the earlier false rejection of 26100 tools. Windows 10 can stay installed,
but its inbox servicing tools are older than this image's servicing base.

1. Open the [Microsoft ADK page](https://learn.microsoft.com/windows-hardware/get-started/adk-install)
   using **Get image tools** in the app's error notice.
2. Install the ADK that Microsoft lists for Windows 11 24H2/25H2/26H2
   (26100 family), selecting **Deployment Tools**, and apply its servicing
   updates. The separate Windows PE add-on is not required to modify the
   `boot.wim` already present in a complete ISO.
3. Close and reopen Tiny11 Builder so it detects the installed tools, then
   start the build again with your original ISO.

Do not replace files in `Windows\System32`, download a standalone DISM EXE,
or remove pending-operation files. The app needs a complete compatible
toolset, not a renamed launcher.

Advanced component inventory is read-only and checks the actual listing
operations instead of imposing the writable-build version gate or output
space requirements. If the installed tools cannot query a newer image,
the app stops the inventory, discards its owned mount and offers **Get image
tools**, linking to Microsoft's ADK page. Listing an image successfully is
not permission to modify it with older tools: build and resume preflight still
require compatible servicing providers.

A saved image's hash, edition, architecture, installation type and complete
version remain verified on resume. Its localized display name is not treated
as an identity: fixing incorrectly decoded text does not invalidate unchanged
saved image bytes.
Microsoft also decides which stable/Insider ISOs are available: the app does not
claim to download unpublished Canary releases or use unofficial UUP mirrors.
The main Download button resolves the current public release automatically.
The optional Other official downloads section still accepts manual links for
other languages or Insider media. Microsoft may require you to sign in on its
Insider page; the app does not handle your credentials.

**Policies are build/edition dependent.** Successful offline registry writes are
verified, but that does not prove that Home, Pro, Enterprise or a future build
honors every policy. Required diagnostics remain in the Balanced profile.
Windows feature updates can restore apps and settings. Local-account and
hardware-check settings are best effort, not guaranteed bypasses on new builds.
No modification bypasses Windows licensing or activation.

**Core and expert warnings:** Core has no usable update/component servicing or
recovery image and disables antivirus protection. A future build with an unknown
runtime layout is refused before Core pruning starts. Expert mode deliberately
allows removal of protected and boot-critical items; there is no dependency
solver. DISM can refuse protected CBS package removal, in which case the build
fails explicitly. Direct system-app/WinSxS payload removal is separately exposed
as an unsupported operation. Removing a service deletes its offline registry
registration; shared `svchost.exe` and other shared binaries are never blindly
deleted. Test aggressive images in an isolated VM and keep known-good media.

## Build and run

Requirements: Windows 10 2004 or newer as the host, .NET 8 SDK and a Windows SDK
compatible with the target in the project. The app selects the newest installed
Microsoft servicing tool and prepares oscdimg automatically. A sufficiently new
Windows host or ADK is still required for newer image builds. The Windows App
SDK and .NET runtime are self-contained in published application folders.

Installed tools require a trusted Microsoft signature. Microsoft's symbol-server
copy of oscdimg omits its embedded certificate, so only the exact approved
2.56 binary is accepted: SHA-256
`2000160B2C5044691B2F9A0AC308E5207F273D4880A572457AF16D05886BA861`.
Downloads are restricted to the approved Microsoft symbol-server resource and
its observed Microsoft blob redirect. A different checksum or redirect stops
acquisition without execution. This is not a general unsigned-tool exception.

```powershell
dotnet build .\Tiny11Builder.sln -c Release
dotnet publish .\src\Tiny11Builder.App\Tiny11Builder.App.csproj -c Release -r win-x64 -p:Platform=x64 -o .\artifacts\Tiny11Builder-x64
.\artifacts\Tiny11Builder-x64\Tiny11Builder.exe
```

For ARM64 hosts:

```powershell
dotnet publish .\src\Tiny11Builder.App\Tiny11Builder.App.csproj -c Release -r win-arm64 -p:Platform=ARM64 -o .\artifacts\Tiny11Builder-arm64
```

The app **always requires administrator privileges at startup**. Windows asks
for UAC approval (or an administrator account's credentials) before opening it;
declining means the app does not start. This is embedded in the executable,
not an optional restart button or a UAC bypass. Launch `Tiny11Builder.exe`, not
the managed DLL. Builds still require explicit confirmation and risk
acknowledgements. File/folder pickers work in elevated apps.

## GitHub source and release packages

Commit the source, tests, solution, scripts, README, NOTICE and `.github`
configuration. Generated `bin`, `obj`, `artifacts`, test results and Windows
images are ignored. Do not include your working media, recipes with personal
paths or build logs in a public upload.

The GitHub Actions workflow runs the tests on Windows and builds self-contained
x64 and ARM64 ZIPs. It uploads workflow artifacts only; it does not publish a
GitHub Release, change permissions, or bundle Windows ISOs/ADK tools.
Native tests require an elevated Windows runner. Real-image tests remain opt-in.

To build the same archives locally from an elevated PowerShell:

```powershell
.\scripts\package.ps1 -Architecture x64
.\scripts\package.ps1 -Architecture arm64
```

Archives and SHA-256 files appear in `artifacts\release`. The script verifies
the executable architecture, embedded administrator manifest and bundled
runtime, and includes dependency license notices. It refuses to overwrite an
existing archive for that version. Extract the entire ZIP into a folder and
open `Tiny11Builder.exe`; copying just the EXE is not sufficient.

The current app version is **0.1.3**. Packages are unsigned,
not Microsoft-certified Windows images. Review [NOTICE.md](../NOTICE.md) before
public redistribution: this upstream checkout has no standalone license file,
and this rewrite does not assign a license to someone else's source.
The existing `origin` points to the upstream project; choose your own GitHub
repository or fork before pushing. Nothing in the packaging script pushes code.

The default workspace is **`C:\tiny11`**, with output ISOs there as well.
Use an **NTFS workspace**, typically with 40-70 GiB free. ReFS, FAT32 and network
workspaces are refused because of WIM mounting constraints. Output cannot be
FAT32. A workspace on the same drive as output is supported. Network paths,
alternate data streams, reparse points in source/workspace/output and system
directories as output/workspace are rejected. Input must include `boot.wim`,
`install.wim` or `install.esd`, and UEFI boot media; split `install.swm` input is
not supported. Exporting one edition avoids copying a multi-edition install
image only to delete it afterward.

## Logs, recipes and recovery

State and cached tools live in `%LOCALAPPDATA%\Tiny11Builder`; working media
defaults to `C:\tiny11` and can be changed. Each operation creates a unique
`tiny11-<guid>` subdirectory. A build
keeps `job.json`, `recipe.json`, `build.log`, `dism.log`, `operations.json`,
`timings.json` and `report.json`.
`operations.json` also records failed builds, including the exact operation and
error detail. The raw disk log preserves exceptions and console output.
On success, working
media is removed unless **Keep working media** is selected; logs and reports
remain. On failure, working files are retained, and the app gives their path.
Recipes contain local paths and selections, so review them before sharing.
Switching build modes replaces the previous removal selections. Custom starts
with no removal groups, advanced actions or Core removals selected. Individual
component actions add to selected groups: to keep a component, leave both its
group and its individual entry unchecked. Saved recipes retain the selected
mode and exact removals. Older recipes are still accepted; modified Core
recipes load as Custom with Core removals enabled, never silently discarded.

Cancellation does **not** kill an active DISM mount/commit process. It waits for
the operation to finish, then discards this job's mount at a safe boundary.
Closing while an operation is running is blocked. ISO creation and download
streams can be cancelled. A completed stage is recorded before cancellation
at the following boundary. Working files and logs are kept on failure/cancel.
Failed notices provide **Open logs**. The red **Cancel safely** button stays at
the upper-right while work is running. Clicking it first asks for confirmation;
Keep building/Continue is the default and sends no cancellation. Confirming
keeps the same dialog open with a progress bar while the current operation
finishes, the owned mount is discarded and logs are saved. The waiting bar is
indeterminate, not a guessed shutdown percentage.
After the run fully settles the dialog shows its actual result. Failed
cleanup or required recovery is shown as an error, never as a safe completion.
If the operation finished while the confirmation was open, nothing is
cancelled. Escape and closing the app are blocked during cancellation cleanup.

After a successful installation-image commit, the job saves `checkpoint.json`
with the original recipe hash, image identity, image hash and a hashed manifest
of staged setup files. After final compression, a second checkpoint allows
retrying ISO creation without recompressing. Checkpoints are flushed and
replaced atomically while the job's exclusive lock is held. Setup-image
editing uses a private copy, committed and verified before replacement.

Use **Resume build** and select the stopped job's `job.json`, including after
restarting the app. The engine verifies the checkpoint, hashes, tools and job
ownership before continuing. It refuses changed removal choices, corrupt or
stale progress, active jobs and unsafe paths. The original source is no longer
needed after a valid commit. The UI restores the original recipe with freshly
located tools; if the output already exists, choose a new ISO name. The Resume
confirmation defaults to Cancel and repeats the applicable Core/custom risks.

Older interrupted jobs without a checkpoint cannot be resumed automatically,
even if they happen to contain a committed WIM. They are not adopted or deleted
when selected for Resume. Keep their working files if you need them.

Publication never overwrites an existing ISO. Cross-volume copies are flushed
and hash-checked before the final rename. On an output race, the verified ISO
is kept at the job's `output.iso` and the build remains resumable. A success
report is written only after publication. Post-publication log/report/cleanup
problems are shown as warnings, not as a false failure of the published ISO.

If the app/PC crashes, use **Recover workspace**, select that job's `job.json`,
and explicitly confirm discarding its working copy. Recovery refuses active
locked jobs and completed jobs. If saved progress exists, the UI offers
**Resume instead**, never automatic deletion of the committed image.
Recovery only addresses that job's mount and staging
directories; it never uses global DISM mount cleanup and preserves the source,
logs, recipe and existing output.

## Tests

Run the tests from an elevated terminal. Native file and registry tests require
Windows backup and restore privileges.

```powershell
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj
```

A small native image-provider check captures a synthetic directory into its
own temporary WIM, deletes an offline component, commits, and remounts read-only
to verify the retained hard link's content, permissions and read-only state.
It does not use your Windows ISO:

```powershell
$env:TINY11_NATIVE_WIM_CHECK = '1'
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Shared_deletion_survives_a_synthetic_WIM_commit_when_configured -- RunConfiguration.EnvironmentVariables.TINY11_NATIVE_WIM_CHECK=1
Remove-Item Env:\TINY11_NATIVE_WIM_CHECK
```

Legacy installer integration can be checked against a real ISO. This test
copies only `boot.wim` into a disposable job, applies the startup configuration,
commits, remounts read-only, and checks the saved configuration and image count.
It leaves the original ISO unchanged; it does not certify Windows Setup in a VM:

```powershell
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Legacy_startup_survives_a_real_Setup_image_commit_when_configured -- RunConfiguration.EnvironmentVariables.TINY11_SETUP_ISO_CHECK=C:\media\Windows11.iso
```

The real-image test is opt-in and requires a valid Windows 11 WIM. It mounts the
WIM **read-only**, lists its components, and tests policy and service changes on
separate copies of its registry files. It then discards the mount and checks
that the source timestamp is unchanged:

```powershell
$env:TINY11_TEST_WIM = 'C:\media\sources\install.wim'
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Read_only_real_Windows_image_integration_when_configured
```

The app is thin WinUI presentation over `src\Tiny11Builder.Core`; tests run
without the GUI. Live Microsoft catalog and tool checks are opt-in:

```powershell
$env:TINY11_TEST_DOWNLOADS = '1'
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Live_official_catalog_and_pinned_tool_when_configured
```

This resolves x64 and ARM64 ISO links and downloads/verifies oscdimg without
executing it or downloading a full Windows ISO. Windows media and deployment
executables are not redistributed by this repository.

The writable-hive regression is also opt-in. It applies the full Core policy
set to a temporary mount, verifies the Widgets policy, then discards changes:

```powershell
$env:TINY11_PERMISSION_WIM = 'C:\media\working.wim'
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Writable_mount_permission_regression_when_configured
```

Use a working WIM copy for this test. Full Core ISO verification reads an
explicit saved recipe, writes a separate ISO under `C:\tiny11`, and does not
claim VM boot validation:

```powershell
$env:TINY11_BUILD_RECIPE = 'C:\tiny11\tiny11-job\recipe.json'
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Full_real_Core_build_when_configured
```

An end-to-end build of Windows 11 Pro 26H2 x64, build 26300.9457, completed with
the original Core removal set, compact installation and recovery ESD
compression. The resulting ISO was 3,331,207,168 bytes (3.10 GiB) and contained
one installation edition at index 1. Source media was 8.31 GiB and multi-edition,
so that size comparison includes exporting only Pro as well as removal and
compression. VM boot validation remains separate from ISO creation.

Opt-in Turbo benchmarks:

```powershell
$env:TINY11_TURBO_BENCHMARK = '1'
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Native_file_deletion_benchmark_when_configured

$env:TINY11_TURBO_TEST_WIM = 'C:\media\working.wim'
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Real_Turbo_mount_staging_and_cleanup_benchmark_when_configured

$env:TINY11_BUILD_RECIPE = 'C:\tiny11\tiny11-job\recipe.json'
$env:TINY11_TEST_TURBO = '1'
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Full_real_Core_build_when_configured
```

The last command uses a separate `C:\tiny11\turbo-verification` workspace and
`C:\tiny11\Tiny11Core-26300-turbo.iso` output. It retains recovery compression,
enables Turbo and redundant-data cleanup, and omits the optional setup payload.
Use a working-copy WIM for the mount benchmark, and let each test discard its
mount before ending the process.

---

# Original PowerShell tiny11builder
*Scripts to build a trimmed-down Windows 11 image - now in **PowerShell**!*

## Introduction :
The original PowerShell scripts create modified Windows 11 installation images
using DISM and oscdimg. `tiny11maker.ps1` keeps update support;
`tiny11Coremaker.ps1` removes it.

You can now use it on ANY Windows 11 release (not just a specific build), as well as ANY language or architecture.
This is made possible thanks to the much-improved scripting capabilities of PowerShell, compared to the older Batch release.

The scripts use DISM recovery compression and **oscdimg.exe** from the Windows
ADK to create the ISO. The included unattended answer file configures compact
installation and local-account setup. Local-account bypasses may not work on
newer Windows builds.

**tiny11 Core** removes component-store files and Windows recovery support, and
disables Windows Update and Defender. You cannot add languages, updates or
features afterward. Use it only for test VMs.

---

## ⚠️ Script versions:
- **tiny11maker.ps1** : The regular script, which removes a lot of bloat but keeps the system serviceable. You can add languages, updates, and features post-creation. This is the recommended script for regular use.
- ⚠️ **tiny11coremaker.ps1** : The core script, which removes even more bloat but also removes the ability to service the image. You cannot add languages, updates, or features post-creation. This is recommended for quick testing or development use.

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
