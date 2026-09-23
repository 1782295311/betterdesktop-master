# ============================================================
#  BetterDesktop uninstaller (user-level, no admin required)
#
#  Steps (strict reverse order of the installer)
#    1) stop every component (desktop service gracefully first, so it restores the desktop)
#    2) Cli --system-integration unregister --all  (B route keys + menu snapshot + all autostart values)
#    3) Remove-AppxPackage BetterDesktop.ShellMenu  (A route sparse package)
#    4) recovery --clean-autostart            (legacy Run values from older builds)
#    5) delete the install folder + deployment.json  (user data kept unless -PurgeUserData)
#
#  Also, in step 2: delete the "BetterDesktop Core Ensure" scheduled task (S3-4 crash fallback).
#  Leaving it behind would make it keep firing every 5 minutes after the files are gone.
#    6) restart explorer
#
#  Order matters: the A-route package's ExternalLocation points AT the install folder, so it must
#  be removed before the folder is deleted; otherwise the shell is left with a broken package.
#
#  NOTE 1: keep this file pure ASCII (PowerShell 5.1 parses .ps1 as ANSI/GBK without a BOM).
#  NOTE 2: this script may live inside the folder it deletes. In that case it re-launches itself
#  from %TEMP% first, because PowerShell keeps the running script file open.
#  NOTE 3: our exes are GUI-subsystem, so all calls go through Start-Process -Wait (see Invoke-Tool).
#
#  Usage:
#    powershell -ExecutionPolicy Bypass -File scripts\uninstall-betterdesktop.ps1
#    powershell -ExecutionPolicy Bypass -File scripts\uninstall-betterdesktop.ps1 -KeepInstallFiles
#    powershell -ExecutionPolicy Bypass -File scripts\uninstall-betterdesktop.ps1 -DryRun
# ============================================================
[CmdletBinding()]
param(
    # Explicit install root (default: read deployment.json; fall back to newest app\* folder).
    [string]$InstallRoot = '',
    # Parent folder of per-build install dirs.
    [string]$InstallBase = '',
    # Keep settings.json / logs / clipboard data (default). Use -PurgeUserData for a full wipe.
    [switch]$PurgeUserData,
    # Keep program files (only unregister from the system). Useful to test the unregister path.
    [switch]$KeepInstallFiles,
    # Do not restart explorer at the end.
    [switch]$NoRestartExplorer,
    # Print the plan without changing anything.
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$product = 'BetterDesktop'
$packageName = 'BetterDesktop.ShellMenu'
$classicClsid = '{7B2E9C41-3D58-4F0A-9E6B-1A4C8D2F5E71}'
$sceneKeys = @('*', 'Directory', 'Directory\Background', 'DesktopBackground')
$startupValueNames = @('BetterDesktop.Tray', 'BetterDesktop.Watchdog', 'BetterDesktop')
# Scheduled task registered by core (S3-4). Cross-process literal: must match core/src/task.rs
# TASK_NAME and recovery/Program.cs CoreTaskName. verify-system-integration.ps1 cross-checks all four.
$coreTaskName = 'BetterDesktop Core Ensure'
$pointer = Join-Path $env:LOCALAPPDATA "$product\deployment.json"

# ---- log file ----
#
# Same gap the installer closed on 2026-09-22: this script printed progress to stdout ONLY, so a
# run that stops or hangs leaves no evidence at all. The uninstaller is worse than the installer in
# one respect - it deletes things - so "it said OK but the folder is still there" (a real incident
# on 2026-09-17) is exactly the shape a log makes diagnosable after the fact.
# Every progress line goes to stdout AND to
#   %LOCALAPPDATA%\BetterDesktop\logs\uninstall-<utc stamp>.log
# with an elapsed-seconds column: "where did it stop" is answered by the LAST line of the file.
# AppendAllText (open / append / close per line) is deliberate: a hung process must still leave its
# last line on disk. The logs folder itself may be created here, and it may be deleted later by
# -PurgeUserData - a missing log at the end is expected in that case, not a failure.
$script:LogFile = ''
$script:StepClock = [System.Diagnostics.Stopwatch]::StartNew()
$script:Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# Longest single child-tool wait (the reason NOT to use -Wait is in Invoke-Tool below).
$script:ToolTimeoutMs = 45000

function Start-Log {
    try {
        $dir = Join-Path $env:LOCALAPPDATA "$product\logs"
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $stamp = (Get-Date).ToUniversalTime().ToString('yyyy.MM.dd.HHmmss')
        $script:LogFile = Join-Path $dir "uninstall-$stamp.log"
        [IO.File]::WriteAllText($script:LogFile, '', $script:Utf8NoBom)
    }
    catch {
        $script:LogFile = ''
        Write-Host "[uninstall][warn] could not create a log file ($($_.Exception.Message)); progress goes to stdout only" -ForegroundColor Yellow
    }
}

# File only (Step / Warn handle stdout) - for lines that exist purely to give a human context.
function Write-Log([string]$level, [string]$message) {
    if (-not $script:LogFile) { return }
    try {
        $elapsed = [string]::Format('{0,8:N1}s', $script:StepClock.Elapsed.TotalSeconds)
        [IO.File]::AppendAllText(
            $script:LogFile,
            ("[{0}] [{1}] [{2}] {3}`r`n" -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $elapsed, $level, $message),
            $script:Utf8NoBom)
    }
    catch { }
}

function Step([string]$message) {
    Write-Log 'step' $message
    Write-Host "[uninstall] $message" -ForegroundColor Cyan
}
function Warn([string]$message) {
    Write-Log 'warn' $message
    Write-Host "[uninstall][warn] $message" -ForegroundColor Yellow
}
function Fail([string]$message) {
    Write-Log 'FAIL' $message
    Write-Host "[uninstall] FAILED: $message" -ForegroundColor Red
    # Print the log path too: reading that file is the first thing anyone does at a failure site.
    if ($script:LogFile) { Write-Host "[uninstall] log: $script:LogFile" -ForegroundColor Red }
    exit 1
}

Start-Log
Write-Log 'info' "uninstaller started; script=$PSCommandPath; installRoot='$InstallRoot'; keepFiles=$($KeepInstallFiles.IsPresent); purgeUserData=$($PurgeUserData.IsPresent); dryRun=$($DryRun.IsPresent)"

function Invoke-Tool([string]$exe, [string[]]$arguments, [switch]$Capture) {
    if (-not (Test-Path $exe)) { return $null }
    if ($Capture) {
        $tmp = [IO.Path]::GetTempFileName()
        try {
            # TRAP - do not "simplify" this back to `-Wait` (same one the installer hit on 2026-09-22):
            # `Start-Process -Wait` also waits for the redirected stdout stream to reach EOF, and a
            # long-lived grandchild inherits that handle - so a tool that starts a resident process
            # would hang this script forever. We mean "wait for this process", not "wait for this
            # stream". Wait for the process only, with a timeout.
            $p = Start-Process -FilePath $exe -ArgumentList $arguments -NoNewWindow -PassThru -RedirectStandardOutput $tmp
            if (-not $p.WaitForExit($script:ToolTimeoutMs)) {
                Warn "$exe did not exit within $([int]($script:ToolTimeoutMs / 1000))s; continuing without its output"
            }
            $out = ''
            # Still try to read the temp file after a timeout; PowerShell's async reader may hold it.
            try { if (Test-Path $tmp) { $out = Get-Content $tmp -Raw } } catch { }
            if (-not [string]::IsNullOrWhiteSpace($out)) {
                Write-Host $out.TrimEnd()
                # Child-tool output goes to the log too: it is often the only record of why an
                # unregister step reported a failure the script then continued past.
                Write-Log 'tool' $out.TrimEnd()
            }
            return @{ ExitCode = $p.ExitCode; Output = $out }
        }
        finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
    }

    $p = Start-Process -FilePath $exe -ArgumentList $arguments -NoNewWindow -Wait -PassThru
    return @{ ExitCode = $p.ExitCode; Output = '' }
}

function Get-RunningOrNull([string]$name) {
    try { return Get-Process -Name $name -ErrorAction SilentlyContinue } catch { return $null }
}

# All our component names (also used to wait for a clean exit before touching files).
# 'betterdesktop-core' is lower-case/hyphenated because that is the actual process name (= the
# cargo crate name). Leaving it out was a real defect: core stays running, keeps its exe mapped,
# and the install folder then cannot be deleted in step 5.
$componentNames = @('betterdesktop-core', 'BetterDesktop.Watchdog', 'BetterDesktop.Tray', 'BetterDesktop.Host',
                    'BetterDesktop.DesktopControl', 'BetterDesktop.Clipboard.Panel',
                    'BetterDesktop.Clipboard.Engine', 'BetterDesktop.Index.Engine',
                    'BetterDesktop.Capture', 'BetterDesktop.Settings')

# Wait until every component is really gone. A fixed sleep is not enough: a process that is still
# shutting down keeps its DLLs mapped, which makes the later folder delete fail (real incident).
function Wait-ProcessesGone([int]$timeoutMs = 8000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $alive = @()
        foreach ($n in $componentNames) { if (Get-RunningOrNull $n) { $alive += $n } }
        if ($alive.Count -eq 0) { return $true }
        Start-Sleep -Milliseconds 200
    }

    $left = @()
    foreach ($n in $componentNames) { if (Get-RunningOrNull $n) { $left += $n } }
    Warn ('these components did not exit in time: ' + ($left -join ', '))
    return $false
}

$script:explorerRestarted = $false
function Restart-Explorer {
    Step 'restarting explorer (needed: the shell loads our DLL in-proc) ...'
    & taskkill.exe /f /im explorer.exe 2>$null | Out-Null
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 100
        if (-not (Get-RunningOrNull 'explorer')) { break }
    }
    Start-Process explorer.exe | Out-Null
    $script:explorerRestarted = $true
}

function Resolve-InstallRoot {
    if (-not [string]::IsNullOrWhiteSpace($InstallRoot)) { return $InstallRoot }
    if (Test-Path $pointer) {
        try {
            $json = Get-Content $pointer -Raw | ConvertFrom-Json
            if ($json.installRoot) { return [string]$json.installRoot }
        }
        catch { Warn "deployment.json unreadable: $($_.Exception.Message)" }
    }

    $base = if ([string]::IsNullOrWhiteSpace($InstallBase)) {
        Join-Path $env:LOCALAPPDATA "$product\app"
    } else { $InstallBase }
    if (-not (Test-Path $base)) { return '' }
    $newest = Get-ChildItem $base -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $newest) { return '' }
    return $newest.FullName
}

# Refuse to recursively delete anything outside the product data roots.
# Real risk: -InstallRoot accepts any path and deployment.json can be corrupted or hand-edited;
# "Remove-Item $root -Recurse -Force" on C:\ or on the user profile would destroy user data.
function Test-SafeDeletePath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    try { $full = [IO.Path]::GetFullPath($Path) } catch { return $false }
    foreach ($r in @((Join-Path $env:LOCALAPPDATA $product), (Join-Path $env:APPDATA $product))) {
        if ([string]::IsNullOrWhiteSpace($r)) { continue }
        $rootFull = ([IO.Path]::GetFullPath($r)).TrimEnd('\') + '\'
        if ($full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

# ---- resolve target + self re-launch from %TEMP% if we are inside it ----
$root = Resolve-InstallRoot
if ($root -and -not (Test-Path $root)) {
    Warn "install root recorded in deployment.json no longer exists: $root"
    $root = ''
}

Step ("install root: " + $(if ($root) { $root } else { '(not found - only unregistering)' }))

if ($root -and -not $env:BDT_UNINSTALL_TEMP) {
    $selfInRoot = $PSCommandPath -and $PSCommandPath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)
    if ($selfInRoot -and -not $KeepInstallFiles) {
        $tempCopy = Join-Path $env:TEMP 'bdt-uninstall.ps1'
        Copy-Item $PSCommandPath $tempCopy -Force
        Step "re-launching from $tempCopy (so the install folder can be deleted)"
        $env:BDT_UNINSTALL_TEMP = '1'
        $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $tempCopy)
        if ($PurgeUserData) { $argList += '-PurgeUserData' }
        if ($NoRestartExplorer) { $argList += '-NoRestartExplorer' }
        if ($DryRun) { $argList += '-DryRun' }
        Start-Process powershell -ArgumentList $argList | Out-Null
        exit 0
    }
}

if ($DryRun) {
    Write-Host ''
    Write-Host '[uninstall] DRY RUN - nothing was changed. Planned steps:' -ForegroundColor Yellow
    Write-Host '  1. stop components (desktop service --stop first: it restores the desktop)'
    Write-Host '  2. Cli --system-integration unregister --all'
    Write-Host "  3. Remove-AppxPackage $packageName"
    Write-Host '  4. recovery --clean-autostart'
    Write-Host ("  5. delete: " + $(if ($KeepInstallFiles) { '(skipped: -KeepInstallFiles)' } else { "$root + $pointer" }))
    Write-Host ("  6. purge user data: " + [bool]$PurgeUserData)
    Write-Host ("  7. restart explorer: " + (-not $NoRestartExplorer))
    Write-Host '  8. drop our tray icon entries (HKCU\Control Panel\NotifyIconSettings)'
    exit 0
}

$cli = if ($root) { Join-Path $root 'BetterDesktop.Cli.exe' } else { '' }
$desktopExe = if ($root) { Join-Path $root 'BetterDesktop.DesktopControl.exe' } else { '' }
$recoveryExe = if ($root) { Join-Path $root 'BetterDesktop.Recovery.exe' } else { '' }

# ---- 0. stop the lifecycle owner FIRST ----
# core is the supervisor: it (a) pulls supervised components back within seconds and (b) is itself
# restarted by the 'BetterDesktop Core Ensure' scheduled task every 5 minutes. Stopping the other
# components while a supervisor is still alive means racing a respawn; and leaving core alive keeps
# betterdesktop-core.exe mapped, which makes the install-folder delete in step 5 fail (the folder
# looks empty but cannot be removed).
# Order: delete the task by name (so nothing can start core again) -> stop core -> then the rest.
Step "removing the core ensure task ($coreTaskName) before stopping anything..."
try {
    $out = & schtasks /delete /tn $coreTaskName /f 2>&1
    if ($LASTEXITCODE -eq 0) { Step "core ensure task removed: $coreTaskName" }
    else { Warn "core ensure task not present or not removable: $coreTaskName" }
}
catch { Warn "core ensure task removal failed: $($_.Exception.Message)" }

$coreProcs = Get-RunningOrNull 'betterdesktop-core'
if ($coreProcs) {
    Step "  stopping core ($($coreProcs.Count) instance(s))"
    $coreProcs | Stop-Process -Force -ErrorAction SilentlyContinue
}

# ---- 1. stop every component (graceful first) ----
Step 'stopping components...'
if ($desktopExe -and (Test-Path $desktopExe)) {
    # Graceful stop restores the explorer icon layer and the taskbar (strong kill would not).
    try { Invoke-Tool $desktopExe @('--stop') | Out-Null } catch { Warn "desktop service --stop failed: $($_.Exception.Message)" }
}
$agentExe = if ($root) { Join-Path $root 'BetterDesktop.Agent.exe' } else { '' }
if ($agentExe -and (Test-Path $agentExe)) {
    try { Invoke-Tool $agentExe @('--stop') | Out-Null } catch { Warn "agent --stop failed: $($_.Exception.Message)" }
}

foreach ($name in $componentNames) {
    $procs = Get-RunningOrNull $name
    if ($procs) {
        Step "  stopping $name ($($procs.Count) instance(s))"
        $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    }
}
[void](Wait-ProcessesGone)

# ---- 2. unregister from the system ----
$unregistered = $false
if ($cli -and (Test-Path $cli)) {
    Step 'unregistering (B route keys + snapshot + autostart)...'
    $res = Invoke-Tool $cli @('--system-integration', 'unregister', '--all') -Capture
    if ($null -ne $res -and $res.ExitCode -eq 0) { $unregistered = $true }
    else { Warn "Cli unregister returned $(if ($res) { $res.ExitCode } else { 'null' })" }
}

if (-not $unregistered) {
    # Fallback: no Cli available (already deleted / partial install). Remove what we know about,
    # so the shell is not left pointing at a DLL that is about to disappear.
    Warn 'Cli unavailable: falling back to direct registry cleanup.'
    try {
        foreach ($scene in $sceneKeys) {
            Remove-Item -Path "HKCU:\Software\Classes\$scene\shellex\ContextMenuHandlers\BetterDesktop" -Recurse -Force -ErrorAction SilentlyContinue
        }
        Remove-Item -Path "HKCU:\Software\Classes\CLSID\$classicClsid" -Recurse -Force -ErrorAction SilentlyContinue
        Step 'registry keys removed (B route)'
    }
    catch { Warn "registry cleanup failed: $($_.Exception.Message)" }
}

try {
    $snapshot = Join-Path $env:APPDATA "$product\shellmenu.json"
    if (Test-Path $snapshot) { Remove-Item $snapshot -Force; Step 'menu snapshot removed' }
}
catch { Warn "snapshot removal failed: $($_.Exception.Message)" }

foreach ($value in $startupValueNames) {
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $value -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' -Name $value -ErrorAction SilentlyContinue
}
Step 'autostart values removed'

# ---- 2c. core ensure task (S3-4) ----
# Already removed in STEP 0 - it must happen before the processes are stopped, otherwise the task
# restarts core in the middle of the uninstall. Kept as a pointer here so the removal is not
# duplicated, and so the reason it lives in step 0 stays discoverable from this spot.
# Removal is by NAME via schtasks, deliberately NOT via `Cli --core task unregister`: that path
# "ensures core" first, i.e. it would start the very process this uninstaller is removing - and
# after step 5 the CLI is gone, so the removal must not depend on any BetterDesktop binary.
# Deleting by name generates no task definition, so it is not a second implementation of the task.

# ---- 3. A route package ----
Step "removing appx package $packageName ..."
try {
    $existing = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
        Remove-AppxPackage -Package $existing.PackageFullName
        Step 'appx package removed'
    }
    else {
        Step 'appx package not present (nothing to remove)'
    }
}
catch { Warn "appx package removal failed: $($_.Exception.Message)" }

# ---- 4. legacy autostart values from older builds ----
if ($recoveryExe -and (Test-Path $recoveryExe)) {
    try { Invoke-Tool $recoveryExe @('--clean-autostart') | Out-Null; Step 'recovery --clean-autostart done' }
    catch { Warn "recovery failed: $($_.Exception.Message)" }
}

# ---- 5. delete files ----
if (-not $KeepInstallFiles) {
    # Explorer must be restarted BEFORE deleting: our B-route extension is loaded **in-proc** by the
    # shell, so as long as explorer is alive it keeps native\BetterDesktopShellMenu.dll mapped and the
    # folder delete fails (real incident 2026-09-17: uninstall "succeeded" but left every file behind).
    if (-not $NoRestartExplorer) { Restart-Explorer }

    if ($root -and (Test-Path $root) -and (Test-SafeDeletePath $root)) {
        Step "deleting $root"
        $deleted = $false
        for ($i = 0; $i -lt 10; $i++) {
            try {
                Remove-Item $root -Recurse -Force -ErrorAction Stop
                $deleted = $true
                break
            }
            catch {
                Start-Sleep -Milliseconds 500
            }
        }
        if (-not $deleted) {
            Warn "could not delete the install folder (files still locked): $root"
            Warn 'close any remaining BetterDesktop processes (or restart Windows) and delete it manually.'
        }
    }
    elseif ($root -and (Test-Path $root)) {
        Warn "refusing to delete a path outside the product folder: $root"
        Warn 'pass a valid -InstallRoot (or fix deployment.json), then remove the folder manually if needed.'
    }

    if (Test-Path $pointer) {
        Remove-Item $pointer -Force -ErrorAction SilentlyContinue
        Step 'deployment.json removed'
    }
}

if ($PurgeUserData) {
    $dataDirs = @((Join-Path $env:APPDATA $product), (Join-Path $env:LOCALAPPDATA $product))
    foreach ($dir in $dataDirs) {
        if (Test-Path $dir) {
            try { Remove-Item $dir -Recurse -Force; Step "purged $dir" }
            catch { Warn "could not purge $dir : $($_.Exception.Message)" }
        }
    }
}
else {
    Step 'user data kept (settings / logs / clipboard). Use -PurgeUserData for a full wipe.'
}

# ---- 6. restart explorer (skip if step 5 already did it) ----
if (-not $NoRestartExplorer -and -not $explorerRestarted) { Restart-Explorer }

# ---- 6b. tray icon registrations: take ours with us ----
#
# Same Windows behaviour the installer documents (step 9b there): HKCU\Control Panel\NotifyIconSettings
# is keyed by the icon's ABSOLUTE exe path and dead entries are NEVER garbage-collected. So an
# uninstall that leaves ours behind leaves a permanent ghost in the notification-area settings list -
# one more per install, which is what the user saw (betterdesktop-core.exe twice + a retired
# BetterDesktop.Tray).
#
# Scoped to the install root (plus entries whose file is already gone, which by definition are dead):
# a dev-tree entry (bin\Debug, core\target\release) belongs to that developer, not to this uninstall.
# When the install root is unknown we keep everything - guessing here would delete someone else's row.
$trayEntryPattern = '(?i)^(betterdesktop-core\.exe|BetterDesktop(\.[A-Za-z]+)*\.exe)$'
$iconBase = 'HKCU:\Control Panel\NotifyIconSettings'
$iconRemoved = 0
if (Test-Path $iconBase) {
    foreach ($key in @(Get-ChildItem $iconBase -ErrorAction SilentlyContinue)) {
        $props = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue
        $iconPath = [string]$props.ExecutablePath
        if ([string]::IsNullOrWhiteSpace($iconPath)) { continue }
        if ((Split-Path $iconPath -Leaf) -notmatch $trayEntryPattern) { continue }

        $iconExists = Test-Path -LiteralPath $iconPath
        $underRoot = $false
        if ($root) { $underRoot = $iconPath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) }
        if ($iconExists -and -not $underRoot) { continue }

        try {
            Remove-Item -LiteralPath $key.PSPath -Recurse -Force -ErrorAction Stop
            $iconRemoved++
        }
        catch { Warn "could not remove a tray entry ($iconPath): $($_.Exception.Message)" }
    }
}
Step "tray icon entries removed: $iconRemoved"

Write-Host ''
Write-Host '=== UNINSTALL OK ===' -ForegroundColor Green
Write-Host ("install folder : " + $(if ($KeepInstallFiles) { 'kept (-KeepInstallFiles)' } else { 'removed' }))
Write-Host ("menu extension : unregistered" + $(if ($unregistered) { ' (via Cli)' } else { ' (fallback registry cleanup)' }))
Write-Host 'autostart      : cleared'
Write-Host 'appx package   : removed (if it was present)'
