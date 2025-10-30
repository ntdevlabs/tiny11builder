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

$ErrorActionPreference = 'Continue'  # Change to Continue to allow script to continue on errors

# Use GITHUB_WORKSPACE if available, otherwise calculate from PSScriptRoot
if ($env:GITHUB_WORKSPACE) {
    $scriptRoot = $env:GITHUB_WORKSPACE
} else {
    $scriptRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}

$scriptPath = Join-Path $scriptRoot "tiny11maker.ps1"

if (-not (Test-Path $scriptPath)) {
    Write-Error "Script not found: $scriptPath"
    Write-Host "PSScriptRoot: $PSScriptRoot"
    Write-Host "scriptRoot: $scriptRoot"
    Write-Host "Current location: $(Get-Location)"
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

# Disable admin restart check by commenting out the exit
$scriptContent = $scriptContent -replace 'if \(!\s*\$myWindowsPrincipal\.IsInRole\(\$adminRole\)\)', 'if ($false -and !$myWindowsPrincipal.IsInRole($adminRole))  # Disabled for GitHub Actions'

# Disable execution policy check by commenting out the exit
$scriptContent = $scriptContent -replace 'if \(\(Get-ExecutionPolicy\) -eq ''Restricted''\)', 'if ($false -and (Get-ExecutionPolicy) -eq ''Restricted'')  # Disabled for GitHub Actions'

# Create a temporary script with auto-answers for image index
$tempScriptHeader = @"
`$ErrorActionPreference = 'Continue'  # Continue on errors to see full output

# Set ISO parameter if not already set
if (-not `$ISO) {
    `$ISO = '$ISODriveLetter'
}
if (-not `$SCRATCH -and '$ScratchDrive' -ne '') {
    `$SCRATCH = '$ScratchDrive'
}

# Override Read-Host BEFORE any script execution to ensure all prompts are handled
function Read-Host {
    param([string]`$Prompt)
    
    Write-Host "`$Prompt"
    
    # Auto-answer execution policy prompt (yes/no) - handle empty prompt too
    if (`$Prompt -eq "" -or `$Prompt -match "change it to RemoteSigned" -or `$Prompt -match "Execution Policy" -or `$Prompt -match "Restricted") {
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

$tempScriptPath = Join-Path $env:TEMP "tiny11maker-automated-$(Get-Date -Format 'yyyyMMddHHmmss').ps1"

# Write header first
$tempScriptHeader | Out-File -FilePath $tempScriptPath -Encoding UTF8

# Append script content
$scriptContent | Out-File -FilePath $tempScriptPath -Append -Encoding UTF8

try {
    # Change to script directory
    Push-Location $scriptRoot
    Write-Host "Changed to directory: $(Get-Location)"
    Write-Host "Using wrapper approach to handle all prompts automatically"
    Write-Host "Running script: $tempScriptPath"
    
    # Run the wrapper script which has Read-Host override
    & $tempScriptPath
    $scriptExitCode = $LASTEXITCODE
    
    if ($scriptExitCode -ne 0 -and $scriptExitCode -ne $null) {
        Write-Error "Script exited with code: $scriptExitCode"
        exit $scriptExitCode
    }
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

