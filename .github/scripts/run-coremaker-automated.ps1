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

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptPath = Join-Path $scriptRoot "tiny11Coremaker.ps1"

if (-not (Test-Path $scriptPath)) {
    Write-Error "Script not found: $scriptPath"
    exit 1
}

Write-Host "Starting automated tiny11Coremaker script"
Write-Host "ISO Drive: $ISODrive"
Write-Host "Enable .NET 3.5: $EnableDotNet35"

# Read the script content
$scriptContent = Get-Content $scriptPath -Raw -ErrorAction Stop

# Fix missing $ScratchDisk variable (should be $mainOSDrive)
$scriptContent = $scriptContent -replace '\$ScratchDisk', '$mainOSDrive'

# Create a temporary script with auto-answers
$ISODriveLetter = $ISODrive -replace ':', ''
$dotNetAnswer = if ($EnableDotNet35) { "y" } else { "n" }

$tempScriptHeader = @"
`$ErrorActionPreference = 'Stop'

# Override Read-Host to auto-answer prompts
function Read-Host {
    param([string]`$Prompt)
    
    Write-Host "`$Prompt"
    
    # Auto-answer execution policy prompt (yes/no)
    if (`$Prompt -eq "" -or `$Prompt -match "change it to RemoteSigned") {
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
    
    # Default: return empty
    return ""
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
    # Run the modified script
    & $tempScriptPath
} catch {
    Write-Error "Error running script: $_"
    throw
} finally {
    Pop-Location
    # Cleanup temp script
    Remove-Item -Path $tempScriptPath -Force -ErrorAction SilentlyContinue
}

