<#
.SYNOPSIS
  End-to-end smoke test: starts a real Umbra with a throwaway data folder and drives it with umbractl.

.DESCRIPTION
  status, list, new + capture, configure, data, logs, remove, quit. Used by CI (scripts/ci-local.ps1, the smoke job of .github/workflows/ci.yml)
  and runnable locally after `dotnet build Umbra.slnx -c Release`.

  It refuses to run while another Umbra is running (one instance per user, and it would quit it), unless -Umbra is a
  portable copy (portable.txt next to Umbra.exe): that has its own pipe, so scripts/ci-local.ps1 runs it beside your Umbra.
  The data folder (UMBRA_DATA_DIR) is a fresh temp folder, so your settings and widgets are never touched.
  If the WebView2 runtime is missing or cannot start, the capture step is reported as SKIPPED (a warning),
  not a failure; every other step still has to pass.

.EXAMPLE
  powershell -File scripts/smoke.ps1
  powershell -File scripts/smoke.ps1 -Configuration Debug -OutDir C:\temp\smoke
#>
param(
    [string]$Configuration = 'Release',
    [string]$Umbra,
    [string]$Ctl,
    [string]$OutDir,
    [int]$StartTimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Umbra) { $Umbra = Join-Path $root "src\Umbra\bin\$Configuration\net10.0-windows10.0.19041.0\Umbra.exe" }
if (-not $Ctl) { $Ctl = Join-Path $root "src\Umbra.Cli\bin\$Configuration\net10.0\umbractl.exe" }
if (-not $OutDir) { $OutDir = Join-Path $root 'dist\smoke' }
foreach ($p in $Umbra, $Ctl) { if (-not (Test-Path $p)) { throw "Not found: $p (build first: dotnet build Umbra.slnx -c $Configuration)" } }
# A portable copy (portable.txt next to Umbra.exe) has its own pipe and single-instance name, so it runs beside an installed Umbra.
if (-not (Test-Path (Join-Path (Split-Path $Umbra) 'portable.txt')) -and (Get-Process -Name Umbra -ErrorAction SilentlyContinue)) {
    throw 'Umbra is already running. Exit it first (this test starts its own instance and quits it at the end).'
}
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

$dataDir = Join-Path ([IO.Path]::GetTempPath()) ("umbra-smoke-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $dataDir | Out-Null
$env:UMBRA_DATA_DIR = $dataDir
$env:UMBRA_EXE = $Umbra
$script:failures = @()
$script:proc = $null

function Step($name) { Write-Host "== $name" }
function Pass($msg) { Write-Host "   ok: $msg" }
function Fail($msg) { Write-Host "   FAIL: $msg"; $script:failures += $msg }
function Skip($msg) {
    Write-Host "   SKIPPED: $msg"
    if ($env:GITHUB_ACTIONS) { Write-Host "::warning title=Smoke test::$msg" }
}

# Runs umbractl; returns @{ Code; Out } with stdout and stderr combined.
function Ctl {
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $out = (& $Ctl @args 2>&1 | Out-String).Trim(); $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $old }
    @{ Code = $code; Out = $out }
}

function Expect($r, $what) {
    if ($r.Code -ne 0) { Fail "$what exited $($r.Code): $($r.Out)"; return $false }
    return $true
}

try {
    Step 'start Umbra in the background'
    $script:proc = Start-Process -FilePath $Umbra -ArgumentList '--background' -PassThru
    $deadline = (Get-Date).AddSeconds($StartTimeoutSeconds)
    $status = $null
    while ((Get-Date) -lt $deadline) {
        if ($script:proc.HasExited) { throw "Umbra exited early with code $($script:proc.ExitCode)" }
        $r = Ctl status
        if ($r.Code -eq 0) { $status = $r.Out | ConvertFrom-Json; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $status) { throw "Umbra did not answer 'umbractl status' within $StartTimeoutSeconds s" }
    Pass "status answered, version $($status.version)"

    Step 'list'
    $r = Ctl list
    if (Expect $r 'list') {
        $widgets = @($r.Out | ConvertFrom-Json)
        # Nothing is built in any more, so a fresh smoke data folder starts empty; 'new' below creates the widget.
        if ($r.Out.TrimStart().StartsWith('[')) { Pass "list returned an array ($($widgets.Count) widget(s))" }
        else { Fail "list did not return a JSON array: $($r.Out)" }
    }

    Step 'new + list'
    $id = 'smoke-widget'
    $r = Ctl new $id
    if (Expect $r 'new') {
        $r = Ctl list
        if ((Expect $r 'list') -and (@($r.Out | ConvertFrom-Json) | Where-Object { $_.id -eq $id })) { Pass "$id loaded" }
        else { Fail "$id is not in list after new" }
    }

    Step 'capture'
    $png = Join-Path $OutDir "$id.png"
    Remove-Item $png -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2   # let the page render
    $r = Ctl capture $id $png
    if ($r.Code -ne 0) {
        $log = Join-Path $dataDir 'umbra.log'
        $logText = if (Test-Path $log) { Get-Content $log -Raw } else { '' }
        if ($logText -match 'WebView2 init failed' -or $r.Out -match 'WebView2') {
            Skip "capture needs a working WebView2 and desktop, not available here: $($r.Out)"
        } else { Fail "capture exited $($r.Code): $($r.Out)" }
    } elseif (-not (Test-Path $png)) { Fail 'capture reported success but wrote no file' }
    else {
        $bytes = [IO.File]::ReadAllBytes($png)
        $sig = $bytes.Length -ge 8 -and $bytes[0] -eq 0x89 -and $bytes[1] -eq 0x50 -and $bytes[2] -eq 0x4E -and $bytes[3] -eq 0x47
        if ($bytes.Length -gt 1000 -and $sig) { Pass "$png ($($bytes.Length) bytes)" }
        else { Fail "capture is not a usable PNG ($($bytes.Length) bytes)" }
    }

    Step 'configure'
    $r = Ctl configure $id --scale 1.5
    if (Expect $r 'configure') {
        $w = @((Ctl list).Out | ConvertFrom-Json) | Where-Object { $_.id -eq $id }
        if ($w.scale -eq 1.5) { Pass 'scale 1.5 applied' } else { Fail "scale is $($w.scale), expected 1.5" }
    }
    $r = Ctl configure $id --scale 4
    if ($r.Code -eq 1) { Pass 'scale 4 rejected' } else { Fail "scale 4 should exit 1, got $($r.Code)" }

    Step 'data'
    $r = Ctl data $id count 42
    if (Expect $r 'data set') {
        $r = Ctl data $id count
        if ((Expect $r 'data get') -and $r.Out -match '42') { Pass 'stored and read back 42' } else { Fail "data get did not return 42: $($r.Out)" }
    }
    $null = Ctl data $id --clear

    Step 'logs'
    $r = Ctl logs 500
    if (Expect $r 'logs') {
        $bad = @($r.Out -split "`n" | Where-Object { $_ -match 'exception|Fatal|failed|unreadable|Pipe error|Skipping widget' })
        if ($bad.Count -eq 0) { Pass 'no errors in the log' }
        else {
            # WebView2 trouble is already reported as a skip by the capture step.
            $other = @($bad | Where-Object { $_ -notmatch 'WebView2 init failed' })
            if ($other.Count -gt 0) { Fail ("log has errors:`n" + ($other -join "`n")) } else { Skip 'log only has WebView2 init errors' }
        }
    }

    Step 'remove'
    $r = Ctl remove $id
    if (Expect $r 'remove') { Pass "$id removed" }
}
catch {
    Fail "unexpected: $($_.Exception.Message)"
}
finally {
    Step 'quit'
    $null = Ctl quit
    if ($script:proc) {
        if (-not $script:proc.WaitForExit(15000)) { Fail 'Umbra did not exit after quit'; $script:proc.Kill() }
        else { Pass 'Umbra exited' }
    }
    if (Test-Path (Join-Path $dataDir 'umbra.log')) { Copy-Item (Join-Path $dataDir 'umbra.log') (Join-Path $OutDir 'umbra.log') -Force }
    Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures.Count -gt 0) {
    Write-Host "`nSmoke test FAILED ($($script:failures.Count)):"
    $script:failures | ForEach-Object { Write-Host " - $_" }
    exit 1
}
Write-Host "`nSmoke test passed."
exit 0
