# Builds the Linux release tarballs in Docker (#197): dist\umbra-<version>-linux-<arch>.tar.gz plus .sha256, for the same
# GitHub release as the Windows installer. Each arch builds in an image of linux/package.Dockerfile (Ubuntu 24.04 + .NET 10
# SDK + WebKitGTK 6 dev files) through linux/container-package.sh, from a read-only mount of this checkout.
#   powershell -File scripts/linux-package.ps1               (x64 and arm64)
#   powershell -File scripts/linux-package.ps1 -Arch x64
#   powershell -File scripts/linux-package.ps1 -Arch arm64 -Qemu
# Needs Docker Desktop (Linux containers). Both arches build as linux/amd64: arm64 is published with -r linux-arm64 and
# only the sandbox extension is cross-compiled (aarch64-linux-gnu-gcc); linux/package.sh refuses an extension of the wrong
# architecture. -Qemu instead emulates a whole arm64 container (slow, the .NET SDK under QEMU is unsupported and can hang),
# so each packaging run has a time limit and is retried once. Called by build.ps1 -Linux.
# TODO(#196): the Flatpak bundle (io.github.aminechr54.Umbra.flatpak) once the manifest and metainfo exist.
param([string[]]$Arch = @('x64', 'arm64'), [switch]$Qemu, [int]$TimeoutMinutes = 45)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$dist = Join-Path $root 'dist'
New-Item $dist -ItemType Directory -Force | Out-Null
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw "Docker is needed for the Linux tarballs (Docker Desktop, Linux containers)" }

foreach ($a in $Arch) {
  if ($a -notin @('x64', 'arm64')) { throw "Unknown arch '$a' (x64 or arm64)" }
  $emulated = $Qemu -and $a -eq 'arm64'
  $platform = if ($emulated) { 'linux/arm64' } else { 'linux/amd64' }
  $target = if ($emulated) { 'native' } else { $a }
  $image = "umbra-linux-package:$target-$($platform.Replace('/', '-'))"
  Write-Host "Linux $a (image target $target, $platform)" -ForegroundColor Cyan
  docker build --platform $platform --target $target -t $image -f (Join-Path $root 'linux\package.Dockerfile') (Join-Path $root 'linux')
  if ($LASTEXITCODE) { throw "docker build ($a) failed" }

  # The script is read through tr: a Windows checkout may have given it CRLF line endings (.gitattributes should prevent it).
  # `timeout` inside the container bounds a hung build; one retry after a failure.
  $script = "tr -d '\r' < /src/linux/container-package.sh > /tmp/package.sh && timeout $($TimeoutMinutes * 60) bash /tmp/package.sh /src /out $a"
  $ok = $false
  foreach ($attempt in 1, 2) {
    docker run --rm --platform $platform -v "${root}:/src:ro" -v "${dist}:/out" -v "umbra-nuget-${target}:/root/.nuget/packages" $image bash -c $script
    if (-not $LASTEXITCODE) { $ok = $true; break }
    Write-Warning "Linux packaging ($a) failed or timed out (attempt $attempt)"
  }
  if (-not $ok) { throw "Linux packaging ($a) failed twice" }

  $tar = Join-Path $dist "umbra-$version-linux-$a.tar.gz"
  $sum = "$tar.sha256"
  if (-not (Test-Path $tar) -or -not (Test-Path $sum)) { throw "missing $tar or its .sha256" }
  $expected = ((Get-Content $sum -Raw).Trim() -split '\s+')[0]
  $actual = (Get-FileHash $tar -Algorithm SHA256).Hash
  if ($expected -ne $actual) { throw "$sum does not match $tar" }
  Write-Host "Built $tar ($([math]::Round((Get-Item $tar).Length / 1MB, 1)) MB)" -ForegroundColor Green
}
