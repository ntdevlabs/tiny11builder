[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64',
    [string]$RestoreSource = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Tiny11Builder.App\Tiny11Builder.App.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$version = [string]$projectXml.Project.PropertyGroup.Version
$name = "Tiny11Builder-$version-win-$Architecture"
$releases = Join-Path $root 'artifacts\release'
$archive = Join-Path $releases "$name.zip"
$checksum = "$archive.sha256"
if ((Test-Path $archive) -or (Test-Path $checksum)) {
    throw "Release already exists: $archive. Move it elsewhere before packaging this version again."
}
$stage = Join-Path $root ('artifacts\package-' + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $stage $name
New-Item -ItemType Directory -Path $publish -Force | Out-Null
try {
    $arguments = @('publish', $project, '-c', 'Release', '-r', "win-$Architecture",
        "-p:Platform=$Architecture", '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $publish, '--nologo')
    if ($RestoreSource) { $arguments += @("-p:RestoreSources=$RestoreSource", '-p:NuGetAudit=false') }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Publishing win-$Architecture failed." }

    $sdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $mt = Get-ChildItem -LiteralPath $sdkBin -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\mt.exe' } |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (!$mt) { throw 'Windows SDK mt.exe is required to verify the packaged administrator manifest.' }
    $manifest = Join-Path $stage 'embedded.manifest'
    & $mt -nologo "-inputresource:$publish\Tiny11Builder.exe;#1" "-out:$manifest"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot extract the executable manifest.' }
    [xml]$embedded = Get-Content -LiteralPath $manifest -Raw
    $level = $embedded.SelectSingleNode("//*[local-name()='requestedExecutionLevel']")
    if (!$level -or $level.level -ne 'requireAdministrator' -or $level.uiAccess -ne 'false') {
        throw 'The executable does not require administrator approval.'
    }
    $executable = [IO.File]::ReadAllBytes((Join-Path $publish 'Tiny11Builder.exe'))
    $pe = [BitConverter]::ToInt32($executable, 60)
    $expected = if ($Architecture -eq 'x64') { 0x8664 } else { 0xAA64 }
    if ([BitConverter]::ToUInt16($executable, $pe + 4) -ne $expected) { throw 'Incorrect executable architecture.' }
    foreach ($dependency in @('Tiny11Builder.Core.dll', 'coreclr.dll', 'Microsoft.UI.Xaml.dll')) {
        if (!(Test-Path (Join-Path $publish $dependency))) { throw "Missing self-contained dependency: $dependency" }
    }
    Copy-Item -LiteralPath (Join-Path $root 'README.md'), (Join-Path $root 'NOTICE.md') -Destination $publish
    Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination (Join-Path $publish 'docs') -Recurse
    $cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $licenses = Join-Path $publish 'licenses'
    $deps = Get-Content -LiteralPath (Join-Path $publish 'Tiny11Builder.deps.json') -Raw | ConvertFrom-Json
    $target = $deps.targets.PSObject.Properties[$deps.runtimeTarget.name].Value
    foreach ($library in $deps.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package' -and $library.Name -notlike 'runtimepack.*') { continue }
        $assets = $target.PSObject.Properties[$library.Name].Value
        if ((!$assets -or !($assets.runtime -or $assets.native -or $assets.runtimeTargets -or $assets.resources)) -and
            $library.Name -notlike 'Microsoft.WindowsAppSDK*') { continue }
        $parts = $library.Name -split '/', 2
        $id = $parts[0] -replace '^runtimepack\.', ''
        $package = Join-Path $cache ($id.ToLowerInvariant() + '\' + $parts[1])
        $notices = Get-ChildItem -LiteralPath $package -File |
            Where-Object { $_.Name -match '(?i)license|third.?party.?notices|notice' }
        if (!$notices) {
            # Some SDK packs specify a license URL in their NuGet metadata instead of shipping a text file.
            $notices = Get-ChildItem -LiteralPath $package -Filter '*.nuspec' -File
            if (!$notices) { throw "Dependency license metadata not found: $($library.Name)" }
        }
        $destination = Join-Path $licenses "$id-$($parts[1])"
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        $notices | Copy-Item -Destination $destination
    }
    New-Item -ItemType Directory -Path $releases -Force | Out-Null
    [IO.Compression.ZipFile]::CreateFromDirectory($publish, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    [IO.File]::WriteAllText($checksum, "$hash  $name.zip`n", [Text.Encoding]::ASCII)
    Write-Output "Release: $archive"
    Write-Output "SHA-256: $hash"
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
