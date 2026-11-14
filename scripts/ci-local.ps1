<#
.SYNOPSIS
  Umbra's CI, run on this PC: every job of .github/workflows/ci.yml and widgets.yml, plus optional captures for review.

.DESCRIPTION
  GitHub Actions isn't used for CI any more: this script is the CI. It checks out the commit in a throwaway git worktree
  (your working tree and your installed Umbra are never touched) and runs, in parallel where it can:
    linux    Docker mcr.microsoft.com/dotnet/sdk:10.0: build Umbra.Linux.slnf -c Release -warnaserror, test it, then
             linux/test/ci.sh (smoke under Xvfb + openbox, sandbox, catalog; captures and umbra.log in linux\)
    node     Docker node:22: widget/SDK/release tests, relay tests, check-versions, catalog --check
    windows  this PC: build Umbra.slnx -c Release -warnaserror, then test it (WinForms/WebView2 can't run in a Linux container)
    bump     this PC: check-bump.mjs against origin/main (changed widgets and themes bump their version)
    smoke    this PC: scripts/smoke.ps1 against a portable copy of the Release build (its own pipe, so it runs next to
             the installed Umbra and never quits it)
  -Capture then starts that portable copy once more with a throwaway data folder holding the commit's widgets and saves
  `umbractl capture` PNGs (only the widget's or Settings' own WebView, never the desktop).

  A PR from a fork is refused unless -TrustFork: its code runs on this PC, so read its diff first.

  With -Status it posts the commit status "local-ci" (pending, then success or failure) through gh, which is the check
  branch protection on main requires. Needs Docker Desktop running (Linux containers), the .NET 10 SDK, Node and gh.

.EXAMPLE
  powershell -File scripts/ci-local.ps1                       # the current HEAD
  powershell -File scripts/ci-local.ps1 -Pr 338 -Status       # a PR's head, and report the result on GitHub
  powershell -File scripts/ci-local.ps1 -Pr 338 -Capture settings:general,clock
#>
param(
    [string]$Ref = 'HEAD',
    [int]$Pr,
    [switch]$Status,
    [string[]]$Capture = @(),
    [string]$OutDir,
    [switch]$Keep,
    [switch]$TrustFork
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Native([string]$exe) {
    # Runs git/gh/docker and returns trimmed stdout; throws on a non-zero exit.
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $out = (& $exe @args 2>$null | Out-String).Trim(); $code = $LASTEXITCODE } finally { $ErrorActionPreference = $old }
    if ($code) { throw "$exe $($args -join ' ') failed ($code)" }
    $out
}

# What to test
if ($Pr) {
    # It builds and runs the PR's code on this PC: a fork's PR only after someone read its diff.
    $fork = Native gh pr view $Pr --json isCrossRepository -q .isCrossRepository
    if ($fork -eq 'true' -and -not $TrustFork) {
        throw "PR #$Pr comes from a fork, and CI runs its code on this PC. Read the diff (gh pr diff $Pr), then rerun with -TrustFork."
    }
    Native git fetch -q origin "pull/$Pr/head" | Out-Null
    $sha = Native git rev-parse FETCH_HEAD
} else { $sha = Native git rev-parse "$Ref^{commit}" }
$short = $sha.Substring(0, 7)
if (-not $OutDir) { $OutDir = Join-Path $root "dist\ci\$short" }
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
Native git fetch -q origin main | Out-Null
$repo = if ($Status) { Native gh repo view --json nameWithOwner -q .nameWithOwner }

function Post-Status([string]$state, [string]$text) {
    if (-not $Status) { return }
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    & gh api "repos/$repo/statuses/$sha" -f "state=$state" -f 'context=local-ci' -f "description=$text" 2>$null | Out-Null
    $ErrorActionPreference = $old
    if ($LASTEXITCODE) { Write-Host "  (could not post the local-ci status to GitHub)" -ForegroundColor Yellow }
}

Write-Host "Umbra local CI for $short$(if ($Pr) { " (PR #$Pr)" })" -ForegroundColor Cyan
Write-Host "Logs and captures: $OutDir"
Post-Status pending 'Running on the maintainer''s PC'

$docker = $true
try { Native docker version --format '{{.Server.Os}}' | Out-Null } catch { $docker = $false }

$wt = Join-Path ([IO.Path]::GetTempPath()) "umbra-ci-$short-$([Guid]::NewGuid().ToString('N').Substring(0, 6))"
Native git worktree add -q --detach $wt $sha | Out-Null

# Start-Process with files for stdout/stderr: parallel, and no PowerShell 5.1 stderr-to-ErrorRecord trouble.
function Start-Logged([string]$name, [string]$exe, [string[]]$argv, [string]$cwd) {
    $quoted = $argv | ForEach-Object { if ($_ -match '[\s;]' -or $_ -eq '') { '"' + $_ + '"' } else { $_ } }
    $log = Join-Path $OutDir "$name.log"
    $p = Start-Process -FilePath $exe -ArgumentList $quoted -WorkingDirectory $cwd -NoNewWindow -PassThru `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    $null = $p.Handle # keeps ExitCode readable in Windows PowerShell
    @{ Name = $name; Proc = $p; Log = $log; Start = Get-Date }
}

$results = [ordered]@{}
# A job with a Limit (seconds from its start) is killed when it runs over, its container too, and counts as failed.
function Finish($job, [string]$pattern) {
    $timedOut = $false
    if ($job.Limit) {
        $left = [Math]::Max(0, [int]($job.Limit * 1000 - ((Get-Date) - $job.Start).TotalMilliseconds))
        if (-not $job.Proc.WaitForExit($left)) {
            $timedOut = $true
            if ($job.Container) {
                $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
                & docker kill $job.Container *> $null
                $ErrorActionPreference = $old
            }
            if (-not $job.Proc.WaitForExit(15000)) { $job.Proc.Kill() }
        }
    }
    $job.Proc.WaitForExit()
    if (Test-Path "$($job.Log).err") { Get-Content "$($job.Log).err" | Add-Content $job.Log; Remove-Item "$($job.Log).err" }
    $ok = -not $timedOut -and $job.Proc.ExitCode -eq 0
    $lines = @(Get-Content $job.Log | Where-Object { $_ -cmatch $pattern } | ForEach-Object { $_.Trim() })
    if ($timedOut) { $lines = @("timed out after $([int]($job.Limit / 60)) min, killed") + $lines }
    $secs = [int]((Get-Date) - $job.Start).TotalSeconds
    $results[$job.Name] = @{ Ok = $ok; Lines = $lines; Secs = $secs }
    $mark = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("{0,-8} {1} ({2} s)" -f $job.Name, $mark, $secs) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
    $lines | Select-Object -First 8 | ForEach-Object { Write-Host "         $_" }
}

try {
    # The Docker jobs copy the checkout (without bin/obj) into the container, so the Windows build can run beside them.
    $copy = "mkdir /w && cd /src && tar --exclude=.git --exclude=bin --exclude=obj --exclude=node_modules -cf - . | tar -xf - -C /w && cd /w"
    $linuxSh = @(
        'set -e', $copy,
        'echo "image: $(. /etc/os-release; echo $PRETTY_NAME), dotnet $(dotnet --version)"',
        # src/Umbra.Linux compiles its sandbox web-process extension (native/umbra-webext.c) with gcc against WebKitGTK 6.
        'apt-get update -qq && apt-get install -y -qq --no-install-recommends gcc pkg-config libwebkitgtk-6.0-dev >/dev/null',
        'dotnet build Umbra.Linux.slnf -c Release -warnaserror',
        'dotnet test Umbra.Linux.slnf -c Release --no-build --logger "console;verbosity=normal"',
        # The headless harness (#198): smoke under Xvfb + openbox, the hostile-widget sandbox suite, every catalog widget.
        # Captures and umbra.log go to $OutDir\linux (mounted at /out). linux/test/ci.sh holds the logic. It needs a Debug
        # host (UMBRA_RELEASE_URL is a Debug-only hook): only the two apps it starts are built again, not the tests.
        'if [ -f linux/test/ci.sh ]; then',
        '  chmod +x linux/test/*.sh # a Windows checkout may not carry the executable bit',
        '  apt-get install -y -qq --no-install-recommends xvfb xauth openbox dbus dbus-x11 python3 xdotool bubblewrap fonts-dejavu-core >/dev/null',
        '  dotnet build src/Umbra.Linux -warnaserror -v q -nologo && dotnet build src/Umbra.Cli -warnaserror -v q -nologo',
        "  UMBRA_CI_SHA=$short UMBRA_CI_CONTAINER=1 linux/test/ci.sh --no-build /out",
        'fi'
    ) -join "`n"
    $nodeSh = @(
        'set -e', $copy, 'echo "node $(node -v)"',
        'node --test tests/widgets/*.test.mjs tests/sdk/*.test.mjs tests/release/*.test.mjs',
        'node --test tests/relay/*.test.mjs',
        'node scripts/check-versions.mjs',
        'node scripts/catalog.mjs --check'
    ) -join "`n"
    [IO.File]::WriteAllText((Join-Path $OutDir 'linux.sh'), $linuxSh + "`n")
    [IO.File]::WriteAllText((Join-Path $OutDir 'node.sh'), $nodeSh + "`n")

    $jobs = @()
    if ($docker) {
        $linuxOut = New-Item -ItemType Directory -Force (Join-Path $OutDir 'linux')
        foreach ($j in @(@('linux', 'mcr.microsoft.com/dotnet/sdk:10.0'), @('node', 'node:22'))) {
            $mounts = @('-v', "${wt}:/src:ro", '-v', "${OutDir}:/ci:ro")
            if ($j[0] -eq 'linux') { $mounts += @('-v', "$($linuxOut.FullName):/out") }
            $name = "umbra-ci-$short-$($j[0])-$([Guid]::NewGuid().ToString('N').Substring(0, 6))"
            $job = Start-Logged $j[0] 'docker' (@('run', '--rm', '--name', $name) + $mounts + @($j[1], 'bash', "/ci/$($j[0]).sh")) $wt
            $job.Container = $name; $job.Limit = 45 * 60 # a hung container must not leave local-ci pending forever
            $jobs += $job
        }
    } else {
        Write-Host 'Docker is not running: start Docker Desktop. The linux and node jobs are marked FAIL.' -ForegroundColor Red
        $results['linux'] = @{ Ok = $false; Lines = @('Docker is not running'); Secs = 0 }
        $results['node'] = @{ Ok = $false; Lines = @('Docker is not running'); Secs = 0 }
    }
    $bump = Start-Logged 'bump' 'node' @('scripts/check-bump.mjs', 'origin/main') $wt

    # windows: build, then test (sequential, in the worktree)
    $build = Start-Logged 'windows' 'dotnet' @('build', 'Umbra.slnx', '-c', 'Release', '-warnaserror') $wt
    $build.Proc.WaitForExit()
    $buildOk = $build.Proc.ExitCode -eq 0
    if ($buildOk) {
        Copy-Item $build.Log (Join-Path $OutDir 'windows-build.log')
        $test = Start-Logged 'windows' 'dotnet' @('test', 'Umbra.slnx', '-c', 'Release', '--no-build') $wt
        $test.Start = $build.Start
        Finish $test 'Warning\(s\)|Error\(s\)|^(Passed|Failed)!|^\s+Failed '
        $wb = @(Get-Content (Join-Path $OutDir 'windows-build.log') | Where-Object { $_ -match 'Warning\(s\)|Error\(s\)' } | ForEach-Object { $_.Trim() })
        $results['windows'].Lines = $wb + $results['windows'].Lines
    } else { Finish $build 'Warning\(s\)|Error\(s\)| error | warning ' }

    # smoke: a portable copy of the Release build (Umbra.exe + umbractl.exe + portable.txt)
    if ($buildOk) {
        $app = Join-Path $OutDir 'app'
        Copy-Item (Join-Path $wt 'src\Umbra\bin\Release\net10.0-windows10.0.19041.0') $app -Recurse
        Get-ChildItem (Join-Path $wt 'src\Umbra.Cli\bin\Release\net10.0') | Where-Object { -not (Test-Path (Join-Path $app $_.Name)) } |
            Copy-Item -Destination $app -Recurse
        New-Item (Join-Path $app 'portable.txt') -ItemType File | Out-Null
        # The commit's own smoke script once it knows portable copies; before that, this one.
        $smokePs = Join-Path $wt 'scripts\smoke.ps1'
        if (-not (Select-String -Path $smokePs -Pattern 'portable.txt' -Quiet)) { $smokePs = Join-Path $PSScriptRoot 'smoke.ps1' }
        $smoke = Start-Logged 'smoke' 'powershell' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $smokePs, '-Umbra', "$app\Umbra.exe",
            '-Ctl', "$app\umbractl.exe", '-OutDir', (Join-Path $OutDir 'smoke')) $wt
        Finish $smoke 'FAIL|SKIPPED|passed\.'
    } else { $results['smoke'] = @{ Ok = $false; Lines = @('not run: the Windows build failed'); Secs = 0 } }

    foreach ($j in $jobs) { Finish $j '^(PASS|FAIL|SKIPPED)  |^Linux CI|^image:|^node v|Warning\(s\)|Error\(s\)|^(Passed|Failed)!|^\s+Failed |^# (pass|fail) |^not ok|^Versions|^OK:|^ERROR|error:' }
    Finish $bump '\S'

    # Captures for review: the commit's widgets in a throwaway data folder, then settings:<page> and widget ids.
    if ($Capture.Count -and $buildOk) { try {
        $capFailed = @()
        $capDir = New-Item -ItemType Directory -Force (Join-Path $OutDir 'captures')
        $data = Join-Path ([IO.Path]::GetTempPath()) "umbra-ci-data-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Force (Join-Path $data 'widgets') | Out-Null
        $targets = @($Capture | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
        foreach ($t in $targets | Where-Object { $_ -notlike 'settings:*' }) {
            $src = Join-Path $wt "widgets\$t"
            if (Test-Path $src) { Copy-Item $src (Join-Path $data "widgets\$t") -Recurse }
        }
        $env:UMBRA_DATA_DIR = $data
        $ctl = Join-Path $app 'umbractl.exe'
        $p = Start-Process (Join-Path $app 'Umbra.exe') -ArgumentList '--background' -PassThru
        try {
            $up = $false
            for ($i = 0; $i -lt 120 -and -not $up; $i++) { & $ctl status *> $null; $up = $LASTEXITCODE -eq 0; if (-not $up) { Start-Sleep -Milliseconds 500 } }
            if (-not $up) { throw 'the portable copy did not answer umbractl status' }
            Start-Sleep -Seconds 4 # let the pages render
            foreach ($t in $targets) {
                $png = Join-Path $capDir (($t -replace '[:#]', '-') + '.png')
                $out = (& $ctl capture $t $png 2>&1 | Out-String).Trim()
                if ($LASTEXITCODE) { Write-Host "capture $t failed: $out" -ForegroundColor Red; $capFailed += "${t}: $out" } else { Write-Host "capture  $png" }
            }
        } finally {
            & $ctl quit *> $null
            if (-not $p.WaitForExit(15000)) { $p.Kill() }
            Remove-Item Env:\UMBRA_DATA_DIR
            Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
        }
        $results['capture'] = @{ Ok = $capFailed.Count -eq 0; Lines = $capFailed; Secs = 0 }
    } catch { $results['capture'] = @{ Ok = $false; Lines = @($_.Exception.Message); Secs = 0 } } }
}
catch {
    # Anything unexpected still ends as a failed local-ci status, never a pending one.
    $results['ci-local'] = @{ Ok = $false; Lines = @($_.Exception.Message); Secs = 0 }
    Write-Host "ci-local: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    if (-not $Keep) {
        $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        & git worktree remove --force $wt 2>$null
        $ErrorActionPreference = $old
    }
}

# Summary (also written to summary.md for a PR comment)
$failed = @($results.Keys | Where-Object { -not $results[$_].Ok })
$md = @("### Local CI for $short", '', '| Job | Result | Details |', '|---|---|---|')
foreach ($k in $results.Keys) {
    $r = $results[$k]
    $md += "| $k | $(if ($r.Ok) { 'pass' } else { '**FAIL**' }) | $((($r.Lines | Select-Object -First 6) -join '<br>') -replace '\|', '/') |"
}
$md | Set-Content (Join-Path $OutDir 'summary.md') -Encoding utf8

if ($failed.Count) {
    Post-Status failure ("Failed: " + ($failed -join ', '))
    Write-Host "`nLocal CI FAILED: $($failed -join ', ') (logs in $OutDir)" -ForegroundColor Red
    exit 1
}
Post-Status success ("linux, node, windows, bump, smoke passed on " + [Environment]::MachineName)
Write-Host "`nLocal CI passed." -ForegroundColor Green
exit 0
