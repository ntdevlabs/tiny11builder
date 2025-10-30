# Build Flow Synchronization Check

## Workflow → Scripts → Artifact Flow

### 1. Workflow Inputs → Scripts Parameters
✅ **ISO URL** → Download → Mount → Drive Letter → Passed to scripts as `ISODrive`
✅ **Script Type** → Routes to correct wrapper script
✅ **Enable .NET 3.5** → Passed to `run-coremaker-automated.ps1` only
✅ **Scratch Drive** → Passed to `run-maker-automated.ps1`

### 2. Script Execution Flow

#### tiny11maker flow:
```
Workflow (line 190-208)
  ↓ ISODrive="D:", ScratchDrive=""
  ↓
run-maker-automated.ps1
  ↓ Sets $ISO = "D", $SCRATCH = "" (if provided)
  ↓ Overrides Read-Host for prompts
  ↓
tiny11maker.ps1
  ↓ Uses $ISO parameter → No drive letter prompt
  ↓ Still prompts for image index → Auto-answered "1"
  ↓ Creates ISO at: $PSScriptRoot\tiny11.iso
```

#### tiny11Coremaker flow:
```
Workflow (line 210-217)
  ↓ ISODrive="D:", EnableDotNet35=false
  ↓
run-coremaker-automated.ps1
  ↓ Fixes $ScratchDisk → $mainOSDrive
  ↓ Overrides Read-Host for all prompts
  ↓
tiny11Coremaker.ps1
  ↓ Prompts for drive letter → Auto-answered
  ↓ Prompts for image index → Auto-answered "1"
  ↓ Prompts for .NET 3.5 → Auto-answered from parameter
  ↓ Creates ISO at: $PSScriptRoot\tiny11.iso
```

### 3. ISO Output Location

**Scripts create ISO at:**
- `$PSScriptRoot\tiny11.iso` (repo root)

**Workflow looks for ISO at:**
- `$PSScriptRoot\tiny11.iso` (repo root)
- ✅ **SYNCHRONIZED**

### 4. Path Resolution

**Wrapper Scripts:**
- `$PSScriptRoot` in wrapper = `.github\scripts\`
- `$scriptRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)` = repo root
- ✅ **CORRECT**

**Scripts called:**
- `tiny11maker.ps1` uses `$PSScriptRoot` = repo root (when called from wrapper)
- `tiny11Coremaker.ps1` uses `$PSScriptRoot` = repo root (when called from wrapper)
- ✅ **SYNCHRONIZED**

### 5. Variable Fixes

**tiny11Coremaker.ps1:**
- Line 559: Uses `$ScratchDisk` → Fixed in wrapper to `$mainOSDrive`
- ✅ **FIXED**

**tiny11maker.ps1:**
- Uses `$ScratchDisk` parameter → Correct
- ✅ **OK**

### 6. Artifact Upload

**Workflow:**
- Finds ISO → Sets `ISO_PATH` env var
- Uploads using `${{ env.ISO_PATH }}`
- ✅ **SYNCHRONIZED**

## Verification Checklist

- [x] Workflow mounts ISO correctly
- [x] Workflow passes drive letter correctly (with ":")
- [x] Wrapper scripts handle drive letter correctly (remove ":")
- [x] Wrapper scripts fix $ScratchDisk issue
- [x] All prompts are auto-answered
- [x] ISO output path matches between scripts and workflow
- [x] Artifact upload path is correct
- [x] Cleanup properly unmounts ISO

## Status: ✅ ALL SYNCHRONIZED

