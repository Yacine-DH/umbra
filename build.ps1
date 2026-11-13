# Builds the Umbra installer: dist\UmbraSetup-<version>.exe
#   powershell -ExecutionPolicy Bypass -File build.ps1
# Needs the .NET 10 SDK, Node and Inno Setup 6 (winget install JRSoftware.InnoSetup).
# -Linux also builds the Linux tarballs in Docker (scripts/linux-package.ps1): dist\umbra-<version>-linux-<x64|arm64>.tar.gz + .sha256.
param([switch]$Linux)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'src\Umbra\Umbra.csproj'
$cli = Join-Path $root 'src\Umbra.Cli\Umbra.Cli.csproj'
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$publish = Join-Path $root 'dist\publish'

Write-Host "Umbra $version" -ForegroundColor Cyan

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=none -o $publish
if ($LASTEXITCODE) { throw "dotnet publish failed" }
# umbractl.exe (CLI + MCP server) sits next to Umbra.exe and shares its runtime files.
dotnet publish $cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=none -o $publish
if ($LASTEXITCODE) { throw "dotnet publish (umbractl) failed" }
# Umbra.Updater.exe (the update window) is one self-contained file: Umbra copies it to temp before the installer overwrites the install folder.
$updater = Join-Path $root 'src\Umbra.Updater\Umbra.Updater.csproj'
dotnet publish $updater -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o (Join-Path $root 'dist\updater')
if ($LASTEXITCODE) { throw "dotnet publish (updater) failed" }
Copy-Item (Join-Path $root 'dist\updater\Umbra.Updater.exe') $publish

# Make sure the community catalog is valid before shipping
node (Join-Path $root 'scripts\catalog.mjs') --check
if ($LASTEXITCODE) { throw "widget catalog is invalid or out of date (run: node scripts/catalog.mjs)" }

$iscc = @(
  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

& $iscc "/DAppVersion=$version" "/DPublishDir=$publish" (Join-Path $root 'installer\Umbra.iss')
if ($LASTEXITCODE) { throw "Inno Setup failed" }

# Portable zip: the same publish folder plus the portable.txt marker (data then lives in <folder>\data).
$zip = Join-Path $root "dist\Umbra-$version-portable.zip"
$stage = Join-Path $root 'dist\portable\Umbra'
if (Test-Path (Split-Path $stage)) { Remove-Item (Split-Path $stage) -Recurse -Force }
Copy-Item $publish $stage -Recurse
New-Item (Join-Path $stage 'portable.txt') -ItemType File | Out-Null
if (Test-Path $zip) { Remove-Item $zip -Force }
# Not Compress-Archive: in Windows PowerShell 5.1 it stores "Umbra\x.dll" with backslashes, which unzip, macOS and
# Linux tools extract as flat files named "Umbra\x.dll". Write the entries with "/" (the zip separator) in any PowerShell.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$base = (Split-Path $stage) + '\'
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
  foreach ($f in Get-ChildItem $stage -Recurse -File) {
    $entry = $f.FullName.Substring($base.Length).Replace('\', '/')
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal)
  }
} finally { $archive.Dispose() }
$check = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
  $bad = @($check.Entries | Where-Object { $_.FullName.Contains('\') -or -not $_.FullName.StartsWith('Umbra/') })
  if ($bad.Count) { throw "portable zip has $($bad.Count) bad entry path(s), e.g. $($bad[0].FullName)" }
  $count = $check.Entries.Count
} finally { $check.Dispose() }
Remove-Item (Split-Path $stage) -Recurse -Force
Write-Host "Built $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB, $count files)" -ForegroundColor Green

$setup = Join-Path $root "dist\UmbraSetup-$version.exe"
Write-Host "Built $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)" -ForegroundColor Green

if ($Linux) { & (Join-Path $root 'scripts\linux-package.ps1') }
