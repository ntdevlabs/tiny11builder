#!/usr/bin/env bash
#
# tiny11maker.sh — macOS port of tiny11maker.ps1
#
# Builds a trimmed-down Windows 11 install ISO from a stock Windows 11 ISO,
# using open-source tools available via Homebrew.
#
# IMPORTANT: This port intentionally skips DISM's `/Cleanup-Image
# /StartComponentCleanup /ResetBase` step because that operation is implemented
# inside the Windows servicing stack and has no cross-platform equivalent. As a
# result, the resulting ISO will be larger than what the original PowerShell
# script produces on Windows (the WinSxS component store is not pruned).
# Everything else — appx package removal, Edge/OneDrive removal, registry
# tweaks, autounattend injection, recovery-compression export, ISO repack — is
# faithfully reproduced.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LOG_FILE="$SCRIPT_DIR/tiny11_$(date +%Y%m%d_%H%M%S).log"

# ---------- usage / args ----------

usage() {
  cat <<'EOF'
Usage: tiny11maker.sh -s <source> [-o <output.iso>] [-i <index>] [-w <work_dir>] [-y]

  -s SOURCE   Path to a Windows 11 ISO file, OR a directory whose top level
              contains the extracted ISO contents (sources/, boot/, efi/, ...).
  -o OUTPUT   Output ISO path. Default: ./tiny11.iso
  -i INDEX    install.wim image index to keep. If omitted, you'll be prompted
              after the available SKUs are listed.
  -w WORK     Work directory. Holds ~30 GB of intermediates. Default: ./tiny11-work
  -y          Skip confirmation prompts (non-interactive).

Requires: wimlib, hivex, xorriso, p7zip.
Install with: brew install wimlib hivex xorriso p7zip
EOF
}

SOURCE=""
OUTPUT_ISO="$SCRIPT_DIR/tiny11.iso"
INDEX=""
WORK_DIR="$SCRIPT_DIR/tiny11-work"
ASSUME_YES=0

while getopts "s:o:i:w:yh" opt; do
  case "$opt" in
    s) SOURCE="$OPTARG" ;;
    o) OUTPUT_ISO="$OPTARG" ;;
    i) INDEX="$OPTARG" ;;
    w) WORK_DIR="$OPTARG" ;;
    y) ASSUME_YES=1 ;;
    h) usage; exit 0 ;;
    *) usage; exit 1 ;;
  esac
done

if [[ -z "$SOURCE" ]]; then
  usage
  exit 1
fi

# ---------- logging helpers ----------

log()  { printf '%s %s\n' "$(date +%H:%M:%S)" "$*"; }
warn() { printf '%s WARN: %s\n' "$(date +%H:%M:%S)" "$*" >&2; }
die()  { printf '%s ERR:  %s\n' "$(date +%H:%M:%S)" "$*" >&2; exit 1; }

confirm() {
  if (( ASSUME_YES )); then return 0; fi
  read -r -p "$1 [y/N] " ans
  [[ "$ans" =~ ^[yY]([eE][sS])?$ ]]
}

require_cmd() {
  local missing=()
  for c in "$@"; do
    command -v "$c" >/dev/null 2>&1 || missing+=("$c")
  done
  if (( ${#missing[@]} > 0 )); then
    die "Missing required tools: ${missing[*]}
  brew install wimlib hivex xorriso p7zip"
  fi
}

# applies a JSON list of registry ops (on stdin) to a single offline hive.
# the python helper uses libhivex directly via ctypes (Homebrew's hivex
# bottle omits hivexregedit; hivexsh's setval is destructive to siblings).
apply_hive() {
  local hive_file="$1" json_file="$2"
  if [[ ! -f "$hive_file" ]]; then
    warn "hive not found, skipping: $hive_file"
    return 0
  fi
  if ! python3 "$SCRIPT_DIR/tiny11_hive.py" "$hive_file" < "$json_file"; then
    warn "registry edits reported issues for $(basename "$hive_file") (continuing)"
  fi
}

# ---------- dependency check ----------

require_cmd wimlib-imagex xorriso 7z python3
if [[ ! -f "$SCRIPT_DIR/tiny11_hive.py" ]]; then
  die "tiny11_hive.py missing — expected next to tiny11maker.sh"
fi

# ---------- prep work dir ----------

# normalize WORK_DIR to absolute path
case "$WORK_DIR" in
  /*) : ;;
  *)  WORK_DIR="$(pwd)/$WORK_DIR" ;;
esac
mkdir -p "$WORK_DIR"

TINY_DIR="$WORK_DIR/tiny11"            # mirrors original $ScratchDisk\tiny11
SCRATCH_DIR="$WORK_DIR/scratchdir"      # mirrors original $ScratchDisk\scratchdir
APPLY_DIR="$SCRATCH_DIR/install"        # extracted install.wim
BOOT_APPLY_DIR="$SCRATCH_DIR/boot"      # extracted boot.wim (index 2)

if [[ -d "$TINY_DIR" || -d "$SCRATCH_DIR" ]]; then
  if confirm "Work dir already contains tiny11/ or scratchdir/. Remove and start fresh?"; then
    rm -rf "$TINY_DIR" "$SCRATCH_DIR"
  else
    die "Refusing to reuse non-empty work dir. Pass -w <dir> or remove existing content."
  fi
fi

mkdir -p "$TINY_DIR" "$SCRATCH_DIR"

# duplicate everything to the log file from this point on
exec > >(tee -a "$LOG_FILE") 2>&1

log "tiny11 image creator (macOS port)"
log "source : $SOURCE"
log "output : $OUTPUT_ISO"
log "work   : $WORK_DIR"
log "log    : $LOG_FILE"

# ---------- ensure autounattend.xml is present ----------

if [[ ! -f "$SCRIPT_DIR/autounattend.xml" ]]; then
  log "autounattend.xml not in script dir, downloading..."
  curl -fsSL -o "$SCRIPT_DIR/autounattend.xml" \
    https://raw.githubusercontent.com/ntdevlabs/tiny11builder/refs/heads/main/autounattend.xml
fi

# ---------- stage source into TINY_DIR ----------

if [[ -f "$SOURCE" ]]; then
  log "extracting ISO with 7z (this may take a few minutes)..."
  7z x -y -o"$TINY_DIR" "$SOURCE" >/dev/null
elif [[ -d "$SOURCE" ]]; then
  log "copying source directory to work dir..."
  # use rsync if available for progress, otherwise cp
  if command -v rsync >/dev/null 2>&1; then
    rsync -a "$SOURCE"/ "$TINY_DIR"/
  else
    cp -R "$SOURCE"/ "$TINY_DIR"/
  fi
else
  die "source not found or not a file/dir: $SOURCE"
fi

# make everything writable (mounted ISOs / read-only copies)
chmod -R u+w "$TINY_DIR" 2>/dev/null || true

# ---------- locate install.wim / install.esd ----------

INSTALL_WIM="$TINY_DIR/sources/install.wim"
INSTALL_ESD="$TINY_DIR/sources/install.esd"
BOOT_WIM="$TINY_DIR/sources/boot.wim"

if [[ ! -f "$BOOT_WIM" ]]; then
  die "boot.wim missing in source: expected at sources/boot.wim"
fi

if [[ ! -f "$INSTALL_WIM" && ! -f "$INSTALL_ESD" ]]; then
  die "neither install.wim nor install.esd found under sources/"
fi

# convert esd -> wim if needed
if [[ ! -f "$INSTALL_WIM" && -f "$INSTALL_ESD" ]]; then
  log "found install.esd; converting to install.wim..."
  wimlib-imagex info "$INSTALL_ESD"
  if [[ -z "$INDEX" ]]; then
    read -r -p "Please enter the image index to keep: " INDEX
  fi
  wimlib-imagex export "$INSTALL_ESD" "$INDEX" "$INSTALL_WIM" \
    --compress=LZX --check
  rm -f "$INSTALL_ESD"
fi

# ---------- pick / validate index ----------

log "install.wim contents:"
wimlib-imagex info "$INSTALL_WIM"

while ! wimlib-imagex info "$INSTALL_WIM" "$INDEX" >/dev/null 2>&1; do
  read -r -p "Please enter the image index: " INDEX
done
log "using image index: $INDEX"

# ---------- apply install.wim ----------

mkdir -p "$APPLY_DIR"
log "applying install.wim to scratch dir (this may take a while)..."
wimlib-imagex apply "$INSTALL_WIM" "$INDEX" "$APPLY_DIR"

# ---------- detect architecture ----------

ARCH="$(wimlib-imagex info "$INSTALL_WIM" "$INDEX" \
  | awk -F': *' '/^Architecture/ {print $2; exit}')"
log "architecture: ${ARCH:-unknown}"

# ---------- remove provisioned appx packages ----------

log "removing provisioned appx packages (folder-level)..."

APP_PREFIXES=(
  AppUp.IntelManagementandSecurityStatus
  Clipchamp.Clipchamp
  DolbyLaboratories.DolbyAccess
  DolbyLaboratories.DolbyDigitalPlusDecoderOEM
  Microsoft.BingNews
  Microsoft.BingSearch
  Microsoft.BingWeather
  Microsoft.Copilot
  Microsoft.Windows.CrossDevice
  Microsoft.GamingApp
  Microsoft.GetHelp
  Microsoft.Getstarted
  Microsoft.Microsoft3DViewer
  Microsoft.MicrosoftOfficeHub
  Microsoft.MicrosoftSolitaireCollection
  Microsoft.MicrosoftStickyNotes
  Microsoft.MixedReality.Portal
  Microsoft.MSPaint
  Microsoft.Office.OneNote
  Microsoft.OfficePushNotificationUtility
  Microsoft.OutlookForWindows
  Microsoft.Paint
  Microsoft.People
  Microsoft.PowerAutomateDesktop
  Microsoft.SkypeApp
  Microsoft.StartExperiencesApp
  Microsoft.Todos
  Microsoft.Wallet
  Microsoft.Windows.DevHome
  Microsoft.Windows.Copilot
  Microsoft.Windows.Teams
  Microsoft.WindowsAlarms
  Microsoft.WindowsCamera
  microsoft.windowscommunicationsapps
  Microsoft.WindowsFeedbackHub
  Microsoft.WindowsMaps
  Microsoft.WindowsSoundRecorder
  Microsoft.WindowsTerminal
  Microsoft.Xbox.TCUI
  Microsoft.XboxApp
  Microsoft.XboxGameOverlay
  Microsoft.XboxGamingOverlay
  Microsoft.XboxIdentityProvider
  Microsoft.XboxSpeechToTextOverlay
  Microsoft.YourPhone
  Microsoft.ZuneMusic
  Microsoft.ZuneVideo
  MicrosoftCorporationII.MicrosoftFamily
  MicrosoftCorporationII.QuickAssist
  MSTeams
  MicrosoftTeams
  Microsoft.549981C3F5F10
)

# return success if $1 matches any package prefix
pkg_matches() {
  local name="$1"
  local lc_name; lc_name="$(printf '%s' "$name" | tr '[:upper:]' '[:lower:]')"
  for p in "${APP_PREFIXES[@]}"; do
    local lc_p; lc_p="$(printf '%s' "$p" | tr '[:upper:]' '[:lower:]')"
    if [[ "$lc_name" == "${lc_p}_"* || "$lc_name" == *"${lc_p}"* ]]; then
      return 0
    fi
  done
  return 1
}

APPX_DIR="$APPLY_DIR/Program Files/WindowsApps"
REMOVED_PKG_NAMES=()
if [[ -d "$APPX_DIR" ]]; then
  while IFS= read -r -d '' pkg_dir; do
    pkg_name="$(basename "$pkg_dir")"
    if pkg_matches "$pkg_name"; then
      log "  removing appx: $pkg_name"
      rm -rf "$pkg_dir"
      REMOVED_PKG_NAMES+=("$pkg_name")
    fi
  done < <(find "$APPX_DIR" -mindepth 1 -maxdepth 1 -type d -print0)
else
  warn "WindowsApps directory not found; skipping appx folder removal"
fi

# ---------- remove Edge ----------

log "removing Edge directories..."
rm -rf "$APPLY_DIR/Program Files (x86)/Microsoft/Edge" \
       "$APPLY_DIR/Program Files (x86)/Microsoft/EdgeUpdate" \
       "$APPLY_DIR/Program Files (x86)/Microsoft/EdgeCore" \
       "$APPLY_DIR/Windows/System32/Microsoft-Edge-Webview" 2>/dev/null || true

# ---------- remove OneDrive setup ----------

log "removing OneDrive setup..."
rm -f "$APPLY_DIR/Windows/System32/OneDriveSetup.exe" 2>/dev/null || true

# ---------- delete scheduled task definition files ----------

log "deleting scheduled task definition files..."
TASKS_DIR="$APPLY_DIR/Windows/System32/Tasks"
rm -f "$TASKS_DIR/Microsoft/Windows/Application Experience/Microsoft Compatibility Appraiser" 2>/dev/null || true
rm -rf "$TASKS_DIR/Microsoft/Windows/Customer Experience Improvement Program" 2>/dev/null || true
rm -f "$TASKS_DIR/Microsoft/Windows/Application Experience/ProgramDataUpdater" 2>/dev/null || true
rm -f "$TASKS_DIR/Microsoft/Windows/Chkdsk/Proxy" 2>/dev/null || true
rm -f "$TASKS_DIR/Microsoft/Windows/Windows Error Reporting/QueueReporting" 2>/dev/null || true

# ---------- copy autounattend.xml into Sysprep ----------

mkdir -p "$APPLY_DIR/Windows/System32/Sysprep"
cp -f "$SCRIPT_DIR/autounattend.xml" \
      "$APPLY_DIR/Windows/System32/Sysprep/autounattend.xml"

# ---------- registry edits for install.wim ----------

log "applying registry edits to install.wim hives..."

SW_HIVE="$APPLY_DIR/Windows/System32/config/SOFTWARE"
SYS_HIVE="$APPLY_DIR/Windows/System32/config/SYSTEM"
DEFAULT_HIVE="$APPLY_DIR/Windows/System32/config/default"
NTUSER_HIVE="$APPLY_DIR/Users/Default/NTUSER.DAT"

# fall back to lowercase NTUSER.DAT if upper-case isn't present
[[ -f "$NTUSER_HIVE" ]] || NTUSER_HIVE="$APPLY_DIR/Users/Default/ntuser.dat"

# SOFTWARE hive ----------------------------------------------------------------
cat > "$WORK_DIR/sw.json" <<'EOF'
[
  {"action":"set_dword","path":"Policies\\Microsoft\\Windows\\CloudContent","name":"DisableWindowsConsumerFeatures","value":1},
  {"action":"set_dword","path":"Policies\\Microsoft\\Windows\\CloudContent","name":"DisableConsumerAccountStateContent","value":1},
  {"action":"set_dword","path":"Policies\\Microsoft\\Windows\\CloudContent","name":"DisableCloudOptimizedContent","value":1},
  {"action":"set_sz","path":"Microsoft\\PolicyManager\\current\\device\\Start","name":"ConfigureStartPins","value":"{\"pinnedList\": [{}]}"},
  {"action":"set_dword","path":"Policies\\Microsoft\\PushToInstall","name":"DisablePushToInstall","value":1},
  {"action":"set_dword","path":"Policies\\Microsoft\\MRT","name":"DontOfferThroughWUAU","value":1},
  {"action":"set_dword","path":"Microsoft\\Windows\\CurrentVersion\\OOBE","name":"BypassNRO","value":1},
  {"action":"set_dword","path":"Microsoft\\Windows\\CurrentVersion\\ReserveManager","name":"ShippedWithReserves","value":0},
  {"action":"set_dword","path":"Policies\\Microsoft\\Windows\\Windows Chat","name":"ChatIcon","value":3},
  {"action":"set_dword","path":"Policies\\Microsoft\\Windows\\OneDrive","name":"DisableFileSyncNGSC","value":1},
  {"action":"set_dword","path":"Policies\\Microsoft\\Windows\\DataCollection","name":"AllowTelemetry","value":0},
  {"action":"set_dword","path":"Microsoft\\Windows\\CurrentVersion\\WindowsUpdate\\Orchestrator\\UScheduler_Oobe\\OutlookUpdate","name":"workCompleted","value":1},
  {"action":"set_dword","path":"Microsoft\\Windows\\CurrentVersion\\WindowsUpdate\\Orchestrator\\UScheduler\\OutlookUpdate","name":"workCompleted","value":1},
  {"action":"set_dword","path":"Microsoft\\Windows\\CurrentVersion\\WindowsUpdate\\Orchestrator\\UScheduler\\DevHomeUpdate","name":"workCompleted","value":1},
  {"action":"del_key","path":"Microsoft\\WindowsUpdate\\Orchestrator\\UScheduler_Oobe\\OutlookUpdate"},
  {"action":"del_key","path":"Microsoft\\WindowsUpdate\\Orchestrator\\UScheduler_Oobe\\DevHomeUpdate"},
  {"action":"set_dword","path":"Policies\\Microsoft\\Windows\\WindowsCopilot","name":"TurnOffWindowsCopilot","value":1},
  {"action":"set_dword","path":"Policies\\Microsoft\\Edge","name":"HubsSidebarEnabled","value":0},
  {"action":"set_dword","path":"Policies\\Microsoft\\Windows\\Explorer","name":"DisableSearchBoxSuggestions","value":1},
  {"action":"set_dword","path":"Policies\\Microsoft\\Teams","name":"DisableInstallation","value":1},
  {"action":"set_dword","path":"Policies\\Microsoft\\Windows\\Windows Mail","name":"PreventRun","value":1},
  {"action":"del_key","path":"WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Microsoft Edge"},
  {"action":"del_key","path":"WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Microsoft Edge Update"}
]
EOF
apply_hive "$SW_HIVE" "$WORK_DIR/sw.json"

# SYSTEM hive ------------------------------------------------------------------
cat > "$WORK_DIR/sys.json" <<'EOF'
[
  {"action":"set_dword","path":"Setup\\LabConfig","name":"BypassCPUCheck","value":1},
  {"action":"set_dword","path":"Setup\\LabConfig","name":"BypassRAMCheck","value":1},
  {"action":"set_dword","path":"Setup\\LabConfig","name":"BypassSecureBootCheck","value":1},
  {"action":"set_dword","path":"Setup\\LabConfig","name":"BypassStorageCheck","value":1},
  {"action":"set_dword","path":"Setup\\LabConfig","name":"BypassTPMCheck","value":1},
  {"action":"set_dword","path":"Setup\\MoSetup","name":"AllowUpgradesWithUnsupportedTPMOrCPU","value":1},
  {"action":"set_dword","path":"ControlSet001\\Control\\BitLocker","name":"PreventDeviceEncryption","value":1},
  {"action":"set_dword","path":"ControlSet001\\Services\\dmwappushservice","name":"Start","value":4}
]
EOF
apply_hive "$SYS_HIVE" "$WORK_DIR/sys.json"

# DEFAULT hive -----------------------------------------------------------------
cat > "$WORK_DIR/default.json" <<'EOF'
[
  {"action":"set_dword","path":"Control Panel\\UnsupportedHardwareNotificationCache","name":"SV1","value":0},
  {"action":"set_dword","path":"Control Panel\\UnsupportedHardwareNotificationCache","name":"SV2","value":0}
]
EOF
apply_hive "$DEFAULT_HIVE" "$WORK_DIR/default.json"

# NTUSER hive (Users\Default\ntuser.dat) ---------------------------------------
cat > "$WORK_DIR/ntuser.json" <<'EOF'
[
  {"action":"set_dword","path":"Control Panel\\UnsupportedHardwareNotificationCache","name":"SV1","value":0},
  {"action":"set_dword","path":"Control Panel\\UnsupportedHardwareNotificationCache","name":"SV2","value":0},
  {"action":"set_dword","path":"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager","name":"OemPreInstalledAppsEnabled","value":0},
  {"action":"set_dword","path":"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager","name":"PreInstalledAppsEnabled","value":0},
  {"action":"set_dword","path":"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager","name":"SilentInstalledAppsEnabled","value":0},
  {"action":"set_dword","path":"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager","name":"ContentDeliveryAllowed","value":0},
  {"action":"set_dword","path":"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager","name":"FeatureManagementEnabled","value":0},
  {"action":"set_dword","path":"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager","name":"PreInstalledAppsEverEnabled","value":0},
  {"action":"set_dword","path":"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager","name":"SoftLandingEnabled","value":0},
  {"action":"set_dword","path":"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager","name":"SubscribedContentEnabled","value":0},
  {"action":"set_dword","path":"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager","name":"SubscribedContent-310093Enabled","value":0},
  {"action":"set_dword","path":"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager","name":"SubscribedContent-338388Enabled","value":0},
  {"action":"set_dword","path":"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager","name":"SubscribedContent-338389Enabled","value":0},
  {"action":"set_dword","path":"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager","name":"SubscribedContent-338393Enabled","value":0},
  {"action":"set_dword","path":"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager","name":"SubscribedContent-353694Enabled","value":0},
  {"action":"set_dword","path":"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager","name":"SubscribedContent-353696Enabled","value":0},
  {"action":"set_dword","path":"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager","name":"SystemPaneSuggestionsEnabled","value":0},
  {"action":"del_key","path":"Software\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager\\Subscriptions"},
  {"action":"del_key","path":"Software\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager\\SuggestedApps"},
  {"action":"set_dword","path":"Software\\Microsoft\\Windows\\CurrentVersion\\AdvertisingInfo","name":"Enabled","value":0},
  {"action":"set_dword","path":"Software\\Microsoft\\Windows\\CurrentVersion\\Privacy","name":"TailoredExperiencesWithDiagnosticDataEnabled","value":0},
  {"action":"set_dword","path":"Software\\Microsoft\\Speech_OneCore\\Settings\\OnlineSpeechPrivacy","name":"HasAccepted","value":0},
  {"action":"set_dword","path":"Software\\Microsoft\\Input\\TIPC","name":"Enabled","value":0},
  {"action":"set_dword","path":"Software\\Microsoft\\InputPersonalization","name":"RestrictImplicitInkCollection","value":1},
  {"action":"set_dword","path":"Software\\Microsoft\\InputPersonalization","name":"RestrictImplicitTextCollection","value":1},
  {"action":"set_dword","path":"Software\\Microsoft\\InputPersonalization\\TrainedDataStore","name":"HarvestContacts","value":0},
  {"action":"set_dword","path":"Software\\Microsoft\\Personalization\\Settings","name":"AcceptedPrivacyPolicy","value":0},
  {"action":"set_dword","path":"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced","name":"TaskbarMn","value":0}
]
EOF
apply_hive "$NTUSER_HIVE" "$WORK_DIR/ntuser.json"

# ---------- documented gap: component cleanup ----------

log ""
log "===================================================================="
log "SKIPPED: Windows component-store cleanup (DISM /StartComponentCleanup"
log "         /ResetBase) has no macOS equivalent. The output ISO will be"
log "         larger than what the Windows version of this script produces."
log "===================================================================="
log ""

# ---------- capture install.wim ----------

# preserve original name by capturing to a temp file and swapping
NEW_INSTALL_WIM="$TINY_DIR/sources/install_new.wim"

log "capturing modified tree to a fresh install.wim (LZMS, non-solid)..."
# non-solid is required so we can split the wim later if it exceeds the
# ISO 9660 Level 3 per-file ceiling of 4 GiB. solid compression saves
# ~10-15% size but wimlib-imagex split refuses solid archives.
wimlib-imagex capture "$APPLY_DIR" "$NEW_INSTALL_WIM" \
  "tiny11" "tiny11 streamlined Windows 11 image" \
  --compress=LZMS

rm -f "$INSTALL_WIM"
mv "$NEW_INSTALL_WIM" "$INSTALL_WIM"

# free the scratch space for the install image
rm -rf "$APPLY_DIR"

# ---------- split install.wim if it exceeds ISO 9660 Level 3's 4 GiB limit ----------

# Homebrew's xorriso bottle has no UDF support, so the bootable ISO must use
# ISO 9660 (Level 3), which caps individual files at just under 4 GiB. If our
# install.wim is over that, split it into install.swm parts -- the Windows
# installer recognizes split wims natively (same convention Microsoft uses for
# FAT32-formatted boot USBs).
INSTALL_WIM_SIZE=$(stat -f%z "$INSTALL_WIM" 2>/dev/null || stat -c%s "$INSTALL_WIM")
ISO_FILE_LIMIT=$((4 * 1024 * 1024 * 1024 - 1))
if (( INSTALL_WIM_SIZE > ISO_FILE_LIMIT )); then
  log "install.wim is $((INSTALL_WIM_SIZE / 1024 / 1024)) MiB; splitting to install.swm (parts <3500 MiB)..."
  wimlib-imagex split "$INSTALL_WIM" "$TINY_DIR/sources/install.swm" 3500
  rm -f "$INSTALL_WIM"
  log "split parts:"
  ls -lh "$TINY_DIR/sources/"install*.swm
fi

# ---------- boot.wim modifications ----------

log "processing boot.wim..."
mkdir -p "$BOOT_APPLY_DIR"
wimlib-imagex apply "$BOOT_WIM" 2 "$BOOT_APPLY_DIR"

BOOT_SW_HIVE="$BOOT_APPLY_DIR/Windows/System32/config/SOFTWARE"
BOOT_SYS_HIVE="$BOOT_APPLY_DIR/Windows/System32/config/SYSTEM"
BOOT_DEFAULT_HIVE="$BOOT_APPLY_DIR/Windows/System32/config/default"
BOOT_NTUSER_HIVE="$BOOT_APPLY_DIR/Users/Default/NTUSER.DAT"
[[ -f "$BOOT_NTUSER_HIVE" ]] || BOOT_NTUSER_HIVE="$BOOT_APPLY_DIR/Users/Default/ntuser.dat"

cat > "$WORK_DIR/boot_sys.json" <<'EOF'
[
  {"action":"set_dword","path":"Setup\\LabConfig","name":"BypassCPUCheck","value":1},
  {"action":"set_dword","path":"Setup\\LabConfig","name":"BypassRAMCheck","value":1},
  {"action":"set_dword","path":"Setup\\LabConfig","name":"BypassSecureBootCheck","value":1},
  {"action":"set_dword","path":"Setup\\LabConfig","name":"BypassStorageCheck","value":1},
  {"action":"set_dword","path":"Setup\\LabConfig","name":"BypassTPMCheck","value":1},
  {"action":"set_dword","path":"Setup\\MoSetup","name":"AllowUpgradesWithUnsupportedTPMOrCPU","value":1}
]
EOF

cat > "$WORK_DIR/boot_default.json" <<'EOF'
[
  {"action":"set_dword","path":"Control Panel\\UnsupportedHardwareNotificationCache","name":"SV1","value":0},
  {"action":"set_dword","path":"Control Panel\\UnsupportedHardwareNotificationCache","name":"SV2","value":0}
]
EOF

apply_hive "$BOOT_DEFAULT_HIVE" "$WORK_DIR/boot_default.json"
apply_hive "$BOOT_NTUSER_HIVE"  "$WORK_DIR/boot_default.json"
apply_hive "$BOOT_SYS_HIVE"     "$WORK_DIR/boot_sys.json"

# write modified hives back to image 2 of boot.wim via `wimupdate`
log "writing modified hives back to boot.wim image 2..."
update_cmds="$WORK_DIR/boot_update.txt"
{
  echo "add \"$BOOT_DEFAULT_HIVE\" \"Windows/System32/config/default\""
  echo "add \"$BOOT_SYS_HIVE\"     \"Windows/System32/config/SYSTEM\""
  if [[ -f "$BOOT_NTUSER_HIVE" ]]; then
    rel_ntuser="Users/Default/$(basename "$BOOT_NTUSER_HIVE")"
    echo "add \"$BOOT_NTUSER_HIVE\" \"$rel_ntuser\""
  fi
} > "$update_cmds"
wimlib-imagex update "$BOOT_WIM" 2 < "$update_cmds"

rm -rf "$BOOT_APPLY_DIR"

# ---------- copy autounattend.xml to ISO root ----------

cp -f "$SCRIPT_DIR/autounattend.xml" "$TINY_DIR/autounattend.xml"

# ---------- build bootable ISO ----------

log "building bootable ISO with xorriso..."

ETFSBOOT="$TINY_DIR/boot/etfsboot.com"
EFISYS="$TINY_DIR/efi/microsoft/boot/efisys.bin"

if [[ ! -f "$ETFSBOOT" ]]; then
  die "BIOS boot file missing: $ETFSBOOT"
fi
if [[ ! -f "$EFISYS" ]]; then
  die "UEFI boot file missing: $EFISYS"
fi

# Relative paths inside the staging dir, as required by xorriso boot args
ETFSBOOT_REL="${ETFSBOOT#"$TINY_DIR/"}"
EFISYS_REL="${EFISYS#"$TINY_DIR/"}"

xorriso -as mkisofs \
  -iso-level 3 \
  -full-iso9660-filenames \
  -J -joliet-long \
  -volid "CCCOMA_X64FRE_EN-US_DV9" \
  -eltorito-boot "$ETFSBOOT_REL" \
  -no-emul-boot \
  -boot-load-size 8 \
  -boot-info-table \
  -eltorito-alt-boot \
  -eltorito-platform efi \
  -no-emul-boot \
  -eltorito-boot "$EFISYS_REL" \
  -isohybrid-gpt-basdat \
  -o "$OUTPUT_ISO" \
  "$TINY_DIR"

log ""
log "ISO created: $OUTPUT_ISO"
log ""

# ---------- cleanup ----------

if confirm "Remove work directory ($WORK_DIR)?"; then
  rm -rf "$WORK_DIR"
  log "work dir removed"
else
  log "work dir kept at $WORK_DIR"
fi

log "done."
