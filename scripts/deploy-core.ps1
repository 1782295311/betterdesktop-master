# deploy-core.ps1 - build the Rust core and hot-deploy it to the current install root.
#
# NOTE: keep this file pure ASCII (PowerShell 5.1 reads .ps1 as ANSI; non-ASCII breaks parsing).
#
# Usage:
#   pwsh -File scripts\deploy-core.ps1
#   pwsh -File scripts\deploy-core.ps1 -SkipBuild          # deploy the existing product as-is
#   pwsh -File scripts\deploy-core.ps1 -InstallRoot <dir>  # override the install root
#
# Why this exists
#   core is a self-contained Rust binary with ZERO ProjectReference, so it can be deployed on its own
#   without a full publish (which is not runnable while packages/ has uncommitted work). Without
#   this, the running core is simply "whatever was deployed last" and edit -> run proves nothing.
#
# Why the steps are in this order (each one is load-bearing, see plan 13.24)
#   1. build            - before anything is stopped, so a compile error changes nothing.
#   2. disable the task  - 'BetterDesktop Core Ensure' runs every 5 minutes and would pull core up
#                          mid-copy, locking the destination file.
#   3. stop core         - releases the file lock on the destination.
#   4. copy              - ONLY betterdesktop-core.exe (+ BetterDesktop.ico). Never copy C# assemblies:
#                          hand-copying shared DLLs across builds is what broke the panel once.
#   5. start core        - the new binary takes over the tray icon, hotkeys, pipe and supervision.
#   6. restore the task  - back to the original state (the task already points at this same path, so
#                          no re-registration is needed).

[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [string]$InstallRoot = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent

function Step([string]$message) { Write-Host "[deploy-core] $message" -ForegroundColor Cyan }
function Warn([string]$message) { Write-Host "[deploy-core][warn] $message" -ForegroundColor Yellow }

# Cross-process literal: must match core/src/task.rs TASK_NAME (same value as in
# uninstall-betterdesktop.ps1 and recovery/Program.cs).
$coreTaskName = 'BetterDesktop Core Ensure'
$localAppData = Join-Path $env:LOCALAPPDATA 'BetterDesktop'

# ---- 0. who are we deploying to? ----
if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $pointer = Join-Path $localAppData 'deployment.json'
    if (-not (Test-Path $pointer)) { throw "no deployment.json at $pointer; pass -InstallRoot <dir>" }
    $InstallRoot = (Get-Content $pointer -Raw | ConvertFrom-Json).installRoot
}
if (-not (Test-Path $InstallRoot)) { throw "install root not found: $InstallRoot" }
Step "install root: $InstallRoot"

# ---- 1. build (first: a compile error must not stop anything) ----
$src = Join-Path $repoRoot 'core\target\release\betterdesktop-core.exe'
if (-not $SkipBuild) {
    # Invoke in-process on purpose: it works under both powershell 5.1 and pwsh 7 (calling a fixed
    # host name would break for whoever is not running it). build-core.ps1 throws on failure.
    & (Join-Path $PSScriptRoot 'build-core.ps1')
}
if (-not (Test-Path $src)) { throw "core product missing: $src (run without -SkipBuild)" }

$ico = Join-Path $repoRoot 'packages\entry\host\Assets\BetterDesktop.ico'
if (-not (Test-Path $ico)) { throw "tray icon missing: $ico" }

# ---- 2. disable the ensure task (nothing may start core behind our back) ----
$taskDisabled = $false
try {
    & schtasks /change /tn $coreTaskName /disable 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { $taskDisabled = $true; Step "ensure task disabled: $coreTaskName" }
    else { Warn "could not disable '$coreTaskName' (not registered?) - continuing" }
}
catch { Warn "could not disable '$coreTaskName': $($_.Exception.Message) - continuing" }

# ---- 3. stop core ----
$running = @(Get-Process -Name 'betterdesktop-core' -ErrorAction SilentlyContinue)
foreach ($p in $running) {
    Step "stopping core (pid $($p.Id))"
    $p | Stop-Process -Force -ErrorAction SilentlyContinue
}
if ($running.Count -gt 0) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 8000 -and (Get-Process -Name 'betterdesktop-core' -ErrorAction SilentlyContinue)) {
        Start-Sleep -Milliseconds 200
    }
    if (Get-Process -Name 'betterdesktop-core' -ErrorAction SilentlyContinue) {
        throw 'core did not exit; refusing to copy over a locked file'
    }
}

# ---- 4. copy ONLY the core product (exe + tray icon) ----
function Show([string]$path) {
    if (Test-Path $path) {
        $fi = Get-Item $path
        "{0}  {1:N0} bytes  {2}" -f $fi.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'), $fi.Length, $fi.Name
    }
    else { '(absent)' }
}
$dstExe = Join-Path $InstallRoot 'betterdesktop-core.exe'
Step "before: $(Show $dstExe)"
Copy-Item $src $dstExe -Force
Copy-Item $ico (Join-Path $InstallRoot 'BetterDesktop.ico') -Force
Step "after : $(Show $dstExe)"

# ---- 5. start core ----
Start-Process $dstExe -WorkingDirectory $InstallRoot | Out-Null
Start-Sleep -Milliseconds 1200
$newProc = @(Get-Process -Name 'betterdesktop-core' -ErrorAction SilentlyContinue)
if ($newProc.Count -gt 0) {
    Step "core running (pid $($newProc[0].Id))"
}
else {
    Warn "core did not start; check $localAppData\logs\core-*.log"
}

# ---- 6. restore the task ----
if ($taskDisabled) {
    & schtasks /change /tn $coreTaskName /enable 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { Step "ensure task re-enabled: $coreTaskName" }
    else { Warn "could not re-enable '$coreTaskName' - enable it manually the next time" }
}

Step 'done'
