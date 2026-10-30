# Tiny11 Builder 0.2

This is the local 0.2.0 source/package update. It does not imply a GitHub
release, an issue closure, or certification of every output ISO.

## Changes to show

- **Clearer navigation:** the sidebar can advance to the next valid step
  using the same checks as the main action. An uninspected image or missing
  risk acknowledgement gets a specific explanation instead of a silent click.
  Clicking Custom offers manual choices or JSON recipe loading; both Custom
  cards and the inline loader share the validated import path. Loading never
  starts a build or preserves old risk acknowledgements.
- **Preset transparency:** Tiny11 and Core show their complete read-only
  removal groups, declared targets and Core effects. This is a preset plan,
  not a claim that every target exists in the chosen image.
- **Complete inventory export:** save every loaded component to UTF-8 text,
  independent of search filters and removal selections. Nothing is uploaded.
- **Interface languages:** English and an initial Simplified Chinese catalog,
  with external validated JSON files for translators. Changing the interface
  language does not change the ISO language or removal selections. Tool logs
  and identifiers remain original data. Translated expanders explicitly update
  their accessible names as well as their visible headers.
- **Offline updates:** add selected local CAB/MSU files in prerequisite order.
  They are privately copied and hash-checked, then passed to DISM without
  disabling applicability checks. Pending actions still protect Core and
  component-store pruning.
- **Setup and servicing:** the legacy Setup answer file requests product-key
  UI without inserting a key. DISM inherits required existing privileges, which
  are restored on the parent after launch. Tool selection accepts valid
  Microsoft embedded or catalog signatures and trusted timestamps; it still
  rejects unsigned or tampered tools.
- **Edge leftovers:** matching browser, updater and known WebView uninstall
  entries are removed only after their corresponding offline files are gone.
- **Portable Custom recipes:** `recipes/cd700-custom.json` now applies actual
  removal groups to any supported source, not a prepared-image copy. Local
  ISO/workspace/output paths and selected edition remain yours. Loaded groups
  are checked in Components and survive source/edition changes.
- **Simpler Custom settings:** one checkbox rebuilds the selected ISO's stock
  `boot.wim` as a maximum-compressed, bootable Setup index; GUI and native
  driver files are retained. Image preservation and external installer inputs
  were removed. A byte limit appears only when a recipe specifies it, with
  a remove action. Tiny11/Core presets and the three-mode design are unchanged.
  CD size is an extreme-recipe goal, not a guaranteed result or default.
- **23H2 export identity:** accept known enablement-family metadata transitions
  such as 22621 to 22631 while still checking edition, installation type,
  architecture and servicing revision. Reports use the actual exported version.
- **Lower workspace requirement:** new builds require twice the source ISO's
  file size (or twice the extracted media's total files), plus selected update
  staging. The old expanded-image-based 40-84+ GiB gate is gone. Existing
  NTFS requirements and saved-job resume estimates remain.

## Issue coverage

| Reports | Outcome |
| --- | --- |
| #645, #648 | Changed the legacy Setup answer-file ProductKey UI contract. XML tests and isolated x64 UEFI/WinPE A/B runs cover the change: the missing-key case reproduces the exact old failure and the candidate instead requests interactive UI. Full installation and other firmware/architecture combinations are not certified. |
| #660 | Fixed disabled-privilege inheritance into the DISM child; synthetic WIM tests cover capture, mount, commit and remount. A right absent under account policy still needs an administrator. |
| #664 | Added catalog/timestamp/Kits-publisher verification, complete-tool-root selection and actionable diagnostics. No signature bypass; Windows Server is explicitly unsupported. The reporter's exact certificate was not supplied. |
| #646 | Shared validated navigation and visible prerequisites. |
| #662 | Read-only preset group and target details. |
| #661 | Complete local component export, tested with over 14,000 entries. |
| #651, #652, #656 | External translation support and a [translator guide](translation.md), preserving stable recipe/identifier values. |
| #665 | Ordered local CAB/MSU integration, with report and resume coverage. `.cap` files are not Windows servicing packages. |
| #659 | Extended the prior browser-entry cleanup to exact updater/WebView leftovers, guarded by file presence and publisher checks. |
| #658 | Documented that serviceable Tiny11 does not restrict Windows Update to security updates only. |
| #639, #640, #641, #642, #647, #650, #653 | Existing secure-download, supported-policy, read-only-inventory, encoding, compatible-tool and deferred-capability fixes retained and regression-tested. |
| #663 | External distribution permission remains a maintainer decision. The Scoop PR was closed pending permission; this update does not grant a new license or claim inclusion in Scoop. |
| #669 | Reproduced the source 22621 / export 22631 identity mismatch; known servicing-family equality now handles it without accepting different editions, architectures, revisions or unknown build families. |
| #638 follow-up | 26340 is accepted with matching/newer tools, not automatically treated as the 26100 family. Compatible preview tools may be needed; new explicit tests cover the boundary. |
| #670 | Reporter closed the launch report as resolved after identifying a Defender block. No host-security bypass was added. |
| #668 | Naming/permission request for another project remains a maintainer decision, not an application defect. No permission or license was granted. |

The application does not remove pending-operation data, bypass Windows
licensing, grant account privileges, disable certificate checks, or enable
unsupported Server builds to make these reports appear resolved.

### October 23 issue recheck

REST and GraphQL checks still show #665 as the newest issue, with the same
21 open reports. The new October 23 comment on #648 reports the same 26H2
ProductKey failure, already covered by the 0.2.0 change and the A/B evidence
below; it does not identify a separate failure. The earlier physical-device
follow-up on #645 is covered by the media-side answer-file change, not by
requiring a VM-only DVD-disconnection workaround. Existing 0.1.x output
must be rebuilt and installation USBs recreated; replacing the app alone
does not patch old media. No additional source fix or version bump was
needed for this recheck.

## Validation limits

The stock-boot reduction was exercised against a real 26300.9457 x64 ISO:
611,776,274 bytes became 597,781,118 bytes. A checked read-only remount
retained Setup, WinPE initialization and native storage files. A separate
network-free UEFI VM boot initialized the native Setup wizard with the
rebuilt index and the interactive-key answer file; no ProductKey regression
was present. This was a boot-to-wizard check, not a full installation.
The portable extreme recipe is tested for path binding, checked groups and
normal pipeline routing across build/architecture cases; no 700 MB or
universal boot result is claimed for arbitrary stock images.

The legacy Setup change has XML coverage and real Panther evidence from
isolated, network-free x64 UEFI VM boots using a private copy of Core media.
The original volume-channel configuration initialized Setup in both cases.
A second A/B scenario omitted `ei.cfg`/`pid.txt` only in that private fixture
to exercise missing-key validation: the old answer file produced exactly
"Windows cannot read the <ProductKey> setting from the unattend answer file."
The candidate selected `WillShowUI=Always`, set the product-key ShowUI flag
to 1, and reached the Setup wizard without that failure. No key, EULA
acceptance or automatic partitioning was added. This proves the reported
early failure path, not a completed installation or every OEM, edition,
BIOS and ARM64 combination. Original ISOs were not patched; test VMs and
mounts were removed.

Both app architectures compile and the navigation, catalog, preset, export
and update logic have regression coverage. After launch approval, the packaged
x64 app was exercised against a real 26300.9457 Pro ISO: English/Chinese
switching preserved the image and selections, sidebar navigation reached
Review, preset details and Core warnings were displayed, and insufficient
space/non-NTFS workspaces were correctly refused. No ISO build was started
through the UI. A stale expander accessibility name found during language
switching was corrected and given a regression test. This is not a complete
Narrator, keyboard-only or ARM64-runtime certification. No release or issue
closures were performed as part of this source update.

## Next video

Use a freshly extracted 0.2 package. Show the real flow rather than presenting
test fixtures as a completed Windows build:

1. Show the readable primary action and sidebar navigation, then inspect an ISO.
2. Expand the Tiny11 and Core preset plans and explain conditional targets.
3. Switch the interface to Simplified Chinese and back without changing the recipe.
4. In Custom, load a real inventory, filter it, and export the complete list.
5. Add legitimate matching update packages, reorder prerequisites, and review.
6. Record an actual validated build, its warnings/results and report.
7. In an isolated VM, show legacy Setup and the manual product-key/edition flow
   before claiming the product-key error is resolved on that media.

Keep all Core/protected-removal warnings, distinguish deferred removal from
absence, and avoid showing personal paths, tokens or unredacted user logs.
Retain the optional support links; no part of the demonstration requires a
donation or a paid feature.
