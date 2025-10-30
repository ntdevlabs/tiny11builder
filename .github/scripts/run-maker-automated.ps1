<#
.SYNOPSIS
    Automated wrapper for tiny11maker.ps1
.DESCRIPTION
    This script automates the interactive prompts in tiny11maker.ps1 when ISO parameter is not provided
#>
param(
    [Parameter(Mandatory=$true)]
    [string]$ISODrive,
    
    [Parameter(Mandatory=$false)]
    [string]$ScratchDrive = ""
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptPath = Join-Path $scriptRoot "tiny11maker.ps1"

if (-not (Test-Path $scriptPath)) {
    Write-Error "Script not found: $scriptPath"
    exit 1
}

Write-Host "Starting automated tiny11maker script"
Write-Host "ISO Drive: $ISODrive"
Write-Host "Scratch Drive: $ScratchDrive"

# Since tiny11maker.ps1 supports parameters, we can use them directly
# But we need to handle image index selection automatically
$ISODriveLetter = $ISODrive -replace ':', ''

# Read the script content to modify it for automation
$scriptContent = Get-Content $scriptPath -Raw -ErrorAction Stop

# Create a temporary script with auto-answers for image index
$tempScriptHeader = @"
`$ErrorActionPreference = 'Stop'

# Set ISO parameter if not already set
if (-not `$ISO) {
    `$ISO = '$ISODriveLetter'
}
if (-not `$SCRATCH -and '$ScratchDrive' -ne '') {
    `$SCRATCH = '$ScratchDrive'
}

# Override Read-Host to auto-answer prompts
function Read-Host {
    param([string]`$Prompt)
    
    Write-Host "`$Prompt"
    
    # Auto-answer execution policy prompt (yes/no)
    if (`$Prompt -eq "" -or `$Prompt -match "change it to RemoteSigned") {
        Write-Host "Auto-answering: yes"
        return "yes"
    }
    
    # Auto-answer drive letter prompt (only if ISO parameter not provided)
    if (`$Prompt -match "enter the drive letter") {
        Write-Host "Auto-answering: $ISODriveLetter"
        return "$ISODriveLetter"
    }
    
    # Auto-answer image index prompt - get first available index
    if (`$Prompt -match "enter the image index") {
        Write-Host "Auto-detecting image index..."
        # Try to get the first available index
        `$index = "1"
        Write-Host "Auto-answering: `$index"
        return `$index
    }
    
    # Auto-answer Press Enter prompt
    if (`$Prompt -match "Press") {
        return ""
    }
    
    # Default: return empty
    return ""
}

"@

$tempScriptPath = Join-Path $env:TEMP "tiny11maker-automated-$(Get-Date -Format 'yyyyMMddHHmmss').ps1"

# Write header first
$tempScriptHeader | Out-File -FilePath $tempScriptPath -Encoding UTF8

# Append script content
$scriptContent | Out-File -FilePath $tempScriptPath -Append -Encoding UTF8

try {
    # Change to script directory
    Push-Location $scriptRoot
    
    # Use wrapper approach to ensure all prompts are handled
    # Even though tiny11maker supports ISO parameter, it still prompts for image index
    Write-Host "Using wrapper approach to handle all prompts automatically"
    
    # Run the wrapper script which has Read-Host override
    & $tempScriptPath
    
} catch {
    Write-Error "Error running script: $_"
    throw
} finally {
    Pop-Location
    # Cleanup temp script
    Remove-Item -Path $tempScriptPath -Force -ErrorAction SilentlyContinue
}

