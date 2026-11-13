# Builds the Linux release tarballs in Docker (#197): dist\umbra-<version>-linux-<arch>.tar.gz plus .sha256, for the same
# GitHub release as the Windows installer. Each arch builds in linux/package.Dockerfile (Ubuntu 24.04 + .NET 10 SDK +
# WebKitGTK 6 dev files) through linux/container-package.sh, from a read-only mount of this checkout.
#   powershell -File scripts/linux-package.ps1               (x64 and arm64)
#   powershell -File scripts/linux-package.ps1 -Arch x64
# Needs Docker Desktop (Linux containers). arm64 runs under QEMU emulation, which Docker Desktop includes; it is slow
# (several minutes). Called by build.ps1 -Linux.
# TODO(#196): the Flatpak bundle (io.github.aminechr54.Umbra.flatpak) once the manifest and metainfo exist.
param([string[]]$Arch = @('x64', 'arm64'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$dist = Join-Path $root 'dist'
New-Item $dist -ItemType Directory -Force | Out-Null
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw "Docker is needed for the Linux tarballs (Docker Desktop, Linux containers)" }
$platforms = @{ x64 = 'linux/amd64'; arm64 = 'linux/arm64' }

foreach ($a in $Arch) {
  $platform = $platforms[$a]
  if (-not $platform) { throw "Unknown arch '$a' (x64 or arm64)" }
  $image = "umbra-linux-package:$a"
  Write-Host "Linux $a ($platform)" -ForegroundColor Cyan
  docker build --platform $platform -t $image -f (Join-Path $root 'linux\package.Dockerfile') (Join-Path $root 'linux')
  if ($LASTEXITCODE) { throw "docker build ($a) failed" }
  # The script is read through tr: a Windows checkout may have given it CRLF line endings.
  docker run --rm --platform $platform -v "${root}:/src:ro" -v "${dist}:/out" -v "umbra-nuget-${a}:/root/.nuget/packages" $image `
    bash -c "tr -d '\r' < /src/linux/container-package.sh > /tmp/package.sh && bash /tmp/package.sh /src /out $a"
  if ($LASTEXITCODE) { throw "Linux packaging ($a) failed" }

  $tar = Join-Path $dist "umbra-$version-linux-$a.tar.gz"
  $sum = "$tar.sha256"
  if (-not (Test-Path $tar) -or -not (Test-Path $sum)) { throw "missing $tar or its .sha256" }
  $expected = ((Get-Content $sum -Raw).Trim() -split '\s+')[0]
  $actual = (Get-FileHash $tar -Algorithm SHA256).Hash
  if ($expected -ne $actual) { throw "$sum does not match $tar" }
  Write-Host "Built $tar ($([math]::Round((Get-Item $tar).Length / 1MB, 1)) MB)" -ForegroundColor Green
}
