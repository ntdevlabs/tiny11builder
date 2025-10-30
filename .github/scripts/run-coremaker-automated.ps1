<#
.SYNOPSIS
    Automated wrapper for tiny11Coremaker.ps1
.DESCRIPTION
    This script automates the interactive prompts in tiny11Coremaker.ps1
#>
param(
    [Parameter(Mandatory=$true)]
    [string]$ISODrive,
    
    [Parameter(Mandatory=$false)]
    [bool]$EnableDotNet35 = $false
)

$ErrorActionPreference = 'Continue'  # Change to Continue to allow script to continue on errors

# Use GITHUB_WORKSPACE if available, otherwise calculate from PSScriptRoot
if ($env:GITHUB_WORKSPACE) {
    $scriptRoot = $env:GITHUB_WORKSPACE
} else {
    $scriptRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}

$scriptPath = Join-Path $scriptRoot "tiny11Coremaker.ps1"

if (-not (Test-Path $scriptPath)) {
    Write-Error "Script not found: $scriptPath"
    Write-Host "PSScriptRoot: $PSScriptRoot"
    Write-Host "scriptRoot: $scriptRoot"
    Write-Host "Current location: $(Get-Location)"
    exit 1
}

Write-Host "Starting automated tiny11Coremaker script"
Write-Host "ISO Drive: $ISODrive"
Write-Host "Enable .NET 3.5: $EnableDotNet35"

# Read the script content
$scriptContent = Get-Content $scriptPath -Raw -ErrorAction Stop

# Fix missing $ScratchDisk variable (should be $mainOSDrive)
$scriptContent = $scriptContent -replace '\$ScratchDisk', '$mainOSDrive'

# Disable admin restart check by commenting out the exit
$scriptContent = $scriptContent -replace 'if \(!\s*\$myWindowsPrincipal\.IsInRole\(\$adminRole\)\)', 'if ($false -and !$myWindowsPrincipal.IsInRole($adminRole))  # Disabled for GitHub Actions'

# Disable execution policy check by commenting out the exit
$scriptContent = $scriptContent -replace 'if \(\(Get-ExecutionPolicy\) -eq ''Restricted''\)', 'if ($false -and (Get-ExecutionPolicy) -eq ''Restricted'')  # Disabled for GitHub Actions'

# Add error checking after oscdimg command to verify ISO was created
# Match the oscdimg command line ending with tiny11.iso
$oscdimgPattern = '(&\s*"\$OSCDIMG"[^`r`n]*tiny11\.iso[^`r`n]*)'
$oscdimgReplacement = @"
`$1
if (`$LASTEXITCODE -ne 0 -and `$LASTEXITCODE -ne `$null) {
    Write-Error "oscdimg failed with exit code: `$LASTEXITCODE"
    exit 1
}

# Verify ISO file was created
`$expectedIsoPath = "`$PSScriptRoot\tiny11.iso"
Start-Sleep -Seconds 2
if (-not (Test-Path `$expectedIsoPath)) {
    Write-Error "ISO file was not created at: `$expectedIsoPath"
    Write-Host "Checking for ISO in alternative locations..."
    Get-ChildItem -Path `$PSScriptRoot -Filter "*.iso" -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "Found ISO: `$(`$_.FullName)"
    }
    exit 1
}

Write-Host "ISO file verified: `$expectedIsoPath"
Write-Host "ISO size: `$([math]::Round((Get-Item `$expectedIsoPath).Length / 1GB, 2)) GB"
"@
$scriptContent = $scriptContent -replace $oscdimgPattern, $oscdimgReplacement

# Create a temporary script with auto-answers
$ISODriveLetter = $ISODrive -replace ':', ''
$dotNetAnswer = if ($EnableDotNet35) { "y" } else { "n" }

$tempScriptHeader = @"
`$ErrorActionPreference = 'Continue'  # Continue on errors to see full output

# Override Read-Host BEFORE any script execution to ensure all prompts are handled
function Read-Host {
    param([string]`$Prompt)
    
    Write-Host "`$Prompt"
    
    # Auto-answer execution policy prompt (yes/no) - handle empty prompt too
    if (`$Prompt -eq "" -or `$Prompt -match "change it to RemoteSigned" -or `$Prompt -match "Execution Policy" -or `$Prompt -match "Restricted") {
        Write-Host "Auto-answering: yes"
        return "yes"
    }
    
    # Auto-answer continue prompt (y/n)
    if (`$Prompt -match "Do you want to continue") {
        Write-Host "Auto-answering: y"
        return "y"
    }
    
    # Auto-answer drive letter prompt
    if (`$Prompt -match "enter the drive letter") {
        Write-Host "Auto-answering: $ISODriveLetter"
        return "$ISODriveLetter"
    }
    
    # Auto-answer image index prompt
    if (`$Prompt -match "enter the image index") {
        Write-Host "Auto-answering: 1"
        return "1"
    }
    
    # Auto-answer .NET 3.5 prompt
    if (`$Prompt -match "enable .NET 3.5") {
        Write-Host "Auto-answering: $dotNetAnswer"
        return "$dotNetAnswer"
    }
    
    # Auto-answer Press Enter prompt
    if (`$Prompt -match "Press") {
        return ""
    }
    
    # Default: return empty or "yes" for empty prompts
    if (`$Prompt -eq "") {
        return "yes"
    }
    return ""
}

# Override $myInvocation to prevent admin restart
`$script:myInvocation = @{
    MyCommand = @{
        Definition = `$PSCommandPath
    }
}

"@

$tempScriptPath = Join-Path $env:TEMP "tiny11coremaker-automated-$(Get-Date -Format 'yyyyMMddHHmmss').ps1"

# Write header first
$tempScriptHeader | Out-File -FilePath $tempScriptPath -Encoding UTF8

# Append script content
$scriptContent | Out-File -FilePath $tempScriptPath -Append -Encoding UTF8

try {
    # Change to script directory
    Push-Location $scriptRoot
    Write-Host "Changed to directory: $(Get-Location)"
    Write-Host "Running script: $tempScriptPath"
    
    # Run the modified script and capture exit code
    & $tempScriptPath
    $scriptExitCode = $LASTEXITCODE
    
    # Check if script failed (PowerShell sets $LASTEXITCODE for native commands)
    # If script exited normally, $LASTEXITCODE might be null or 0
    if ($scriptExitCode -ne 0 -and $scriptExitCode -ne $null) {
        Write-Error "Script exited with code: $scriptExitCode"
        exit $scriptExitCode
    }
    
    # Verify ISO was actually created (double-check)
    $expectedIsoPath = Join-Path $scriptRoot "tiny11.iso"
    if (-not (Test-Path $expectedIsoPath)) {
        Write-Error "Script completed but ISO file not found at: $expectedIsoPath"
        Write-Host "Searching for ISO files..."
        Get-ChildItem -Path $scriptRoot -Filter "*.iso" -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
            Write-Host "Found: $($_.FullName)"
        }
        exit 1
    }
    
    Write-Host "Build completed successfully. ISO found at: $expectedIsoPath"
} catch {
    Write-Error "Error running script: $_"
    Write-Error "Exception: $($_.Exception.Message)"
    Write-Error "Line: $($_.InvocationInfo.ScriptLineNumber)"
    exit 1
} finally {
    Pop-Location
    # Cleanup temp script
    Remove-Item -Path $tempScriptPath -Force -ErrorAction SilentlyContinue
}

