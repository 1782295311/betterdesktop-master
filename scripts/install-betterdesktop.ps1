# ============================================================
#  BetterDesktop installer (user-level, no admin required)
#
#  Steps (in order)
#    1) validate the source folder (a half release must never be installed)
#    2) stop our running components (watchdog / tray / desktop service / agent / host / engines)
#    3) copy everything to %LOCALAPPDATA%\BetterDesktop\app\<build>
#    4) write %LOCALAPPDATA%\BetterDesktop\deployment.json  ("which install is current")
#    5) Cli --system-integration register  (B route COM extension + tray autostart, Run+StartupApproved)
#    5b) Cli --core task register  (core crash fallback; the installer half of "register from both places")
#    6) A route (Win11 new-menu sparse package) via pack-shellmenu-msix.ps1
#    7) start the tray (which brings up agent + desktop service)
#    8) restart explorer so the shell reloads context-menu handlers
#    9) print the resulting state (Cli --system-integration status)
#
#  Why user-level: everything we register lives in HKCU (Classes / Run / StartupApproved),
#  same as the rest of the repo. No elevation, no UAC.
#
#  NOTE 1: keep this file pure ASCII. Windows PowerShell 5.1 parses .ps1 as ANSI/GBK unless the
#  file has a BOM, so non-ASCII literals break at parse time.
#  NOTE 2: our Cli.exe is a GUI-subsystem executable. PowerShell does NOT wait for GUI apps,
#  so every call must go through Start-Process -Wait (see Invoke-Tool) or $LASTEXITCODE lies.
#
#  Usage:
#    powershell -ExecutionPolicy Bypass -File scripts\install-betterdesktop.ps1
#    powershell -ExecutionPolicy Bypass -File scripts\install-betterdesktop.ps1 -Source dist\BetterDesktop-2026.09.17.2140
#    powershell -ExecutionPolicy Bypass -File scripts\install-betterdesktop.ps1 -DryRun
# ============================================================
[CmdletBinding()]
param(
    # Release folder to install. Default: newest dist\BetterDesktop-* .
    [string]$Source = '',
    # Parent folder of per-build install dirs.
    [string]$InstallBase = '',
    # Do not register the tray for autostart (context menu registration still happens).
    [switch]$NoAutostart,
    # A route (Win11 new menu sparse package): auto | signed | loose | skip.
    [ValidateSet('auto', 'signed', 'loose', 'skip')]
    [string]$Msix = 'auto',
    # Do not restart explorer at the end (the right-click menu shows up only after a restart).
    [switch]$NoRestartExplorer,
    # Files are already placed at the target by an external installer (Inno Setup):
    # skip the copy step, treat the source folder as the install root.
    [switch]$SkipCopy,
    # Print the plan without changing anything.
    [switch]$DryRun,
    # Treat missing runtime prerequisites as a hard failure instead of a warning
    # (for unattended installs that must not ship a build that cannot start).
    [switch]$RequirePrereqs
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$product = 'BetterDesktop'
$packageName = 'BetterDesktop.ShellMenu'
$classicClsid = '{7B2E9C41-3D58-4F0A-9E6B-1A4C8D2F5E71}'
$trayValueName = 'BetterDesktop.Tray'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startupApprovedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'

# Files that MUST exist in the source folder (mirrors publish.ps1 $required; keep in sync).
$required = @(
    # 2026-09-18 launcher: the single entry point users double-click.
    'BetterDesktop.exe',
    'BetterDesktop.Host.exe',
    'BetterDesktop.Cli.exe',
    'BetterDesktop.DesktopControl.exe',
    'BetterDesktop.Settings.exe',
    'BetterDesktop.Tray.exe',
    'BetterDesktop.Updater.exe',
    'BetterDesktop.Recovery.exe',
    'convert-engine.exe',
    'cordis.yml',
    'native\BetterDesktopShellMenu.dll'
)

function Step([string]$message) { Write-Host "[install] $message" -ForegroundColor Cyan }
function Warn([string]$message) { Write-Host "[install][warn] $message" -ForegroundColor Yellow }
function Fail([string]$message) {
    Write-Host "[install] FAILED: $message" -ForegroundColor Red
    exit 1
}

# Run an executable to completion. Mandatory for our GUI-subsystem exes (PowerShell does not
# wait for those, so '& exe' would race ahead with a stale $LASTEXITCODE).
function Invoke-Tool([string]$exe, [string[]]$arguments, [switch]$Capture) {
    if (-not (Test-Path $exe)) { return $null }
    if ($Capture) {
        $tmp = [IO.Path]::GetTempFileName()
        try {
            $p = Start-Process -FilePath $exe -ArgumentList $arguments -NoNewWindow -Wait -PassThru -RedirectStandardOutput $tmp
            $out = ''
            if (Test-Path $tmp) { $out = Get-Content $tmp -Raw }
            if (-not [string]::IsNullOrWhiteSpace($out)) { Write-Host $out.TrimEnd() }
            return @{ ExitCode = $p.ExitCode; Output = $out }
        }
        finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
    }

    $p = Start-Process -FilePath $exe -ArgumentList $arguments -NoNewWindow -Wait -PassThru
    return @{ ExitCode = $p.ExitCode; Output = '' }
}

function Get-MissingRequired([string]$dir) {
    $missing = @()
    foreach ($file in $required) {
        if (-not (Test-Path (Join-Path $dir $file))) { $missing += $file }
    }
    return $missing
}

function Resolve-Source {
    if (-not [string]::IsNullOrWhiteSpace($Source)) {
        if (-not (Test-Path $Source)) { Fail "source folder not found: $Source" }
        return (Resolve-Path $Source).Path
    }

    $distRoot = Join-Path $repoRoot 'dist'
    if (-not (Test-Path $distRoot)) { Fail 'no dist folder; run scripts\publish.ps1 first (or pass -Source)' }

    # Newest COMPLETE release, not just newest: an interrupted publish leaves a half folder behind
    # (real incident 2026-09-17: dist\BetterDesktop-2026.09.17.1354 had Host+Agent only and was
    # picked as "the newest", so a plain install failed with a confusing message).
    $incomplete = @()
    foreach ($candidate in (Get-ChildItem $distRoot -Directory -Filter 'BetterDesktop-*' |
                                Sort-Object LastWriteTime -Descending)) {
        if ((Get-MissingRequired $candidate.FullName).Count -eq 0) {
            if ($incomplete.Count -gt 0) {
                Warn ('skipped incomplete release folder(s): ' + ($incomplete -join ', '))
            }
            return $candidate.FullName
        }
        $incomplete += $candidate.Name
    }

    Fail ('no complete release in dist (incomplete: ' + ($incomplete -join ', ') + '); run scripts\publish.ps1 or pass -Source')
}

function Get-RunningOrNull([string]$name) {
    try { return Get-Process -Name $name -ErrorAction SilentlyContinue } catch { return $null }
}

# Refuse to recursively delete anything outside the product data roots.
# deployment.json can be corrupted or hand-edited, and a previous install root is deleted blindly below;
# "Remove-Item <path> -Recurse -Force" on C:\ or the user profile would destroy user data.
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

$componentNames = @('BetterDesktop.Watchdog', 'BetterDesktop.Tray', 'BetterDesktop.Host',
                    'BetterDesktop.DesktopControl', 'BetterDesktop.Clipboard.Panel',
                    'BetterDesktop.Clipboard.Engine', 'BetterDesktop.Index.Engine',
                    'BetterDesktop.Capture', 'BetterDesktop.Settings')

# A fixed sleep is not enough: a component that is still shutting down keeps its DLLs mapped,
# which makes the folder copy/delete fail later (same trap as the uninstaller).
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

function Restart-Explorer {
    Step 'restarting explorer...'
    & taskkill.exe /f /im explorer.exe 2>$null | Out-Null
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 100
        if (-not (Get-RunningOrNull 'explorer')) { break }
    }
    Start-Process explorer.exe | Out-Null
}

# ---- 1. source validation (fail loudly; never install a half release) ----
$src = Resolve-Source
Step "source: $src"

$missing = Get-MissingRequired $src
if ($missing.Count -gt 0) {
    Fail ('source folder is incomplete, missing: ' + ($missing -join ', '))
}

$hostExe = Join-Path $src 'BetterDesktop.Host.exe'
$fileVersion = (Get-Item $hostExe).VersionInfo.FileVersion
# $fileVersion becomes a path segment below ($target = Join-Path $installBase $fileVersion) and that target
# is later recursively deleted, so it must never be trusted blindly: an exe advertising "..\.." as its
# version would make the installer delete directories above the install base.
if ($fileVersion -notmatch '^[0-9]+(\.[0-9]+){1,3}$') {
    if (-not [string]::IsNullOrWhiteSpace($fileVersion)) {
        Warn "host exe reports an unusable FileVersion ('$fileVersion'); falling back to a timestamp"
    }
    $fileVersion = (Get-Date).ToUniversalTime().ToString('yyyy.MM.dd.HHmm')
}
$prodVersion = '1.3.0'
$informational = (Get-Item $hostExe).VersionInfo.ProductVersion
if ($informational -match '^([0-9]+\.[0-9]+\.[0-9]+)') { $prodVersion = $Matches[1] }

$installBase = if ([string]::IsNullOrWhiteSpace($InstallBase)) {
    Join-Path $env:LOCALAPPDATA "$product\app"
} else { $InstallBase }
$target = Join-Path $installBase $fileVersion
$pointer = Join-Path $env:LOCALAPPDATA "$product\deployment.json"

Step "target : $target"
Step "version: $prodVersion build=$fileVersion"

# ---- 1b. runtime prerequisites (.NET 8 Desktop Runtime + VC++ 2015-2022 x64) ----
# The release is FRAMEWORK-DEPENDENT: BetterDesktop.Host.runtimeconfig.json asks for
# Microsoft.WindowsDesktop.App 8.0.0 and the package ships no runtime files (no coreclr.dll /
# hostfxr.dll / PresentationFramework.dll), so a machine without .NET 8 Desktop Runtime cannot start
# the app at all. The Rust engines (clipboard / index / convert) and the native shell-menu DLL link
# the MSVC CRT dynamically (no crt-static is configured, and vcruntime140.dll is not shipped), so they
# need the VC++ 2015-2022 Redistributable (x64).
# On a clean machine either gap shows up as "it installed but does not work" with no explanation,
# so check both here, with the exact download links, before touching anything.
$fatalProblems = New-Object System.Collections.Generic.List[string]
$softNotes = New-Object System.Collections.Generic.List[string]

# .NET: only relevant for FRAMEWORK-DEPENDENT builds. Since 2026-09-18 the release is
# SELF-CONTAINED (scheme A: dotnet publish -r win-x64 -p:SelfContained=true) and carries
# coreclr.dll / hostfxr.dll next to the exes, so nothing has to be installed.
$selfContained = (Test-Path (Join-Path $src 'hostfxr.dll')) -or (Test-Path (Join-Path $src 'coreclr.dll'))
if ($selfContained) {
    Step 'self-contained build detected (.NET runtime is bundled; nothing to install)'
}
else {
    $desktopRuntimeFound = $false
    $sharedRoot = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.WindowsDesktop.App'
    if (Test-Path $sharedRoot) {
        $desktopRuntimeFound = (Get-ChildItem $sharedRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like '8.*' } | Select-Object -First 1) -ne $null
    }
    if (-not $desktopRuntimeFound) {
        # fall back to the CLI listing (covers DOTNET_ROOT / non-default locations)
        try {
            $listing = & dotnet --list-runtimes 2>$null
            if ($listing) {
                $desktopRuntimeFound = ($listing | Where-Object { $_ -like 'Microsoft.WindowsDesktop.App 8.*' } | Select-Object -First 1) -ne $null
            }
        }
        catch { $desktopRuntimeFound = $false }
    }
    if (-not $desktopRuntimeFound) {
        $fatalProblems.Add('.NET 8 Desktop Runtime missing and this build is NOT self-contained -> the app cannot start. Install "Desktop Runtime x64" from https://dotnet.microsoft.com/download/dotnet/8.0')
    }
}

# VC++ 2015-2022 x64: our own binaries link the CRT statically since 2026-09-18 (scheme D),
# so this is now only relevant for a few THIRD-PARTY conversion engines (ffmpeg / LibreOffice)
# that still import VCRUNTIME140.dll. Informational, never fatal.
$vcOk = $false
try {
    $vc = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64' -ErrorAction Stop
    if ($vc.Installed -eq 1) { $vcOk = $true }
}
catch { $vcOk = $false }
if (-not $vcOk) {
    # some machines carry the DLLs without the redist registry key
    $vcOk = (Test-Path (Join-Path $env:WINDIR 'System32\vcruntime140.dll')) -or
            (Test-Path (Join-Path $env:WINDIR 'System32\msvcp140.dll'))
}
if (-not $vcOk) {
    $softNotes.Add('Microsoft Visual C++ 2015-2022 Redistributable (x64) not found: a few third-party conversions (ffmpeg / LibreOffice based) may fail. Everything else is unaffected. Install https://aka.ms/vs/17/release/vc_redist.x64.exe if you need those formats.')
}

foreach ($note in $softNotes) { Warn $note }
if ($fatalProblems.Count -gt 0) {
    foreach ($problem in $fatalProblems) { Warn $problem }
    Warn 'install the missing runtime, then run this installer again'
    if ($RequirePrereqs) { Fail 'missing runtime prerequisites (see warnings above)' }
}
elseif ($softNotes.Count -eq 0) {
    Step 'runtime prerequisites OK'
}

if ($DryRun) {
    Write-Host ''
    Write-Host '[install] DRY RUN - nothing was changed. Planned steps:' -ForegroundColor Yellow
    Write-Host '  1. stop components (watchdog / tray / desktop service / agent / host / engines)'
    Write-Host "  2. copy $src -> $target"
    Write-Host "  3. write $pointer"
    Write-Host '  4. Cli --system-integration register'
    Write-Host '  4b. Cli --core task register (core crash fallback)'
    Write-Host "  5. A route (msix): $Msix"
    Write-Host ('  6. start tray' + $(if ($NoAutostart) { ' (autostart NOT registered)' } else { ' (autostart registered)' }))
    Write-Host "  7. restart explorer: $(-not $NoRestartExplorer)"
    exit 0
}

# ---- 2. stop our running components (graceful first, then force) ----
Step 'stopping running components...'

# Desktop service first: its graceful stop restores the explorer icon layer / taskbar.
$desktopExe = Join-Path $env:LOCALAPPDATA "$product\DesktopControl\BetterDesktop.DesktopControl.exe"
if (-not (Test-Path $desktopExe)) { $desktopExe = Join-Path $env:LOCALAPPDATA "$product\BetterDesktop.DesktopControl.exe" }
if (Test-Path $desktopExe) {
    try { Invoke-Tool $desktopExe @('--stop') | Out-Null } catch { Warn "desktop service --stop failed: $($_.Exception.Message)" }
}

$agentExe = Join-Path $src 'BetterDesktop.Agent.exe'
if (Test-Path $agentExe) {
    try { Invoke-Tool $agentExe @('--stop') | Out-Null } catch { Warn "agent --stop failed: $($_.Exception.Message)" }
}

# Watchdog first in the kill loop: it would otherwise relaunch what we just stopped.
foreach ($name in $componentNames) {
    $procs = Get-RunningOrNull $name
    if ($procs) {
        Step "  stopping $name ($($procs.Count) instance(s))"
        $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    }
}
[void](Wait-ProcessesGone)

# ---- 3. copy ----
if ($SkipCopy) {
    # External installer (Inno Setup) has already placed the files at the target folder.
    # The source folder IS the install root: pointer/registration below must reference it.
    $target = $src
    Step "copy skipped (-SkipCopy): files already at $target"
}
else {
    Step "copying to $target"
    if (Test-Path $target) {
        try {
            Remove-Item $target -Recurse -Force -ErrorAction Stop
        }
        catch {
            # Same in-proc lock as the uninstaller: explorer maps native\BetterDesktopShellMenu.dll from
            # the install folder, so reinstalling into the SAME build folder fails until explorer restarts.
            Warn "old install folder is locked ($($_.Exception.Message)); restarting explorer and retrying"
            Restart-Explorer
            Start-Sleep -Milliseconds 500
            Remove-Item $target -Recurse -Force -ErrorAction Stop
        }
    }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -Path (Join-Path $src '*') -Destination $target -Recurse -Force

    # belt and braces: the release folder is already clean, but never ship PDB/XML from a hand-made folder
    Get-ChildItem -Path $target -Recurse -Include *.pdb,*.xml -File -ErrorAction SilentlyContinue | Remove-Item -Force
}

# ---- 3b. format-conversion engines (engines\) ----
# 2026-09-18 real-machine bug: format conversion was ~80% unusable and most of its menu entries were
# MISSING. Every engine is located at <AppContext.BaseDirectory>\engines\... (shell-convert), but
# the main module deliberately excludes the 2.8GB engines tree (it ships as module 06) while this
# installer only copied the main module -> a clean install had NO engines -> pure-managed targets only
# reachable. Note ConvertMenuService HIDES unavailable targets entirely (it does not grey them out),
# which is exactly why the user reported "missing options" rather than "greyed options".
# Install them here from either layout: full dist (engines\ next to the component exes) or the module
# package (the 06-* module next to this folder). Missing engines is a Warn, never a Fail.
$enginesSource = ''
$srcParent = Split-Path $src -Parent
$enginesCandidates = New-Object System.Collections.Generic.List[string]
# full dist layout: engines\ next to the component exes (this folder or its parent)
$enginesCandidates.Add((Join-Path $src 'engines'))
$enginesCandidates.Add((Join-Path $srcParent 'engines'))
# module layout: the engines tree ships as the "06-..." module next to this folder.
# NOTE: this file must stay pure ASCII (Windows PowerShell 5.1 parses .ps1 as ANSI/GBK without a BOM),
# so the module folder is matched by its numeric prefix instead of its localized name.
Get-ChildItem -Path $srcParent -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like '06-*' } |
    ForEach-Object { $enginesCandidates.Add((Join-Path $_.FullName 'engines')) }

foreach ($candidate in $enginesCandidates) {
    if (Test-Path $candidate) { $enginesSource = $candidate; break }
}

if ([string]::IsNullOrWhiteSpace($enginesSource)) {
    Warn 'no conversion engines found (looked for engines\ here or in the parent folder / 06-* module)'
    Warn 'format conversion will only offer pure-managed image targets until that folder is copied into the install root'
}
else {
    $enginesTarget = Join-Path $target 'engines'
    $samePlace = $false
    try { $samePlace = ((Resolve-Path $enginesSource).Path.TrimEnd('\') -ieq (Resolve-Path $enginesTarget -ErrorAction SilentlyContinue).Path.TrimEnd('\')) } catch { $samePlace = $false }

    if ($samePlace) {
        Step 'conversion engines already in place'
    }
    else {
        Step "installing conversion engines (large, this may take a while): $enginesSource"
        New-Item -ItemType Directory -Path $enginesTarget -Force | Out-Null
        Copy-Item -Path (Join-Path $enginesSource '*') -Destination $enginesTarget -Recurse -Force
        Step 'conversion engines installed (format conversion / OCR available)'
    }
}

# ---- 4. deployment pointer (single source of truth for "where is the current install") ----
# Remember the previous install root first: after a successful install we delete it, otherwise every
# upgrade leaves an orphan copy behind (the pointer only ever records one root).
$previousRoot = ''
if (Test-Path $pointer) {
    try {
        $old = Get-Content $pointer -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($old.installRoot) { $previousRoot = [string]$old.installRoot }
    }
    catch { Warn "previous deployment.json unreadable: $($_.Exception.Message)" }
}

$record = [ordered]@{
    schemaVersion = 1
    product       = $product
    version       = $prodVersion
    build         = $fileVersion
    installRoot   = $target
    installedAt   = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
    msixMode      = $Msix
}
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
New-Item -ItemType Directory -Path (Split-Path $pointer -Parent) -Force | Out-Null
[IO.File]::WriteAllText($pointer, ($record | ConvertTo-Json -Depth 4), $utf8NoBom)
Step "pointer written: $pointer"

# ---- 5. B route + autostart (single implementation: Cli --system-integration) ----
$cli = Join-Path $target 'BetterDesktop.Cli.exe'
Step 'registering: context menu extension + tray autostart...'
$reg = Invoke-Tool $cli @('--system-integration', 'register') -Capture
if ($null -eq $reg) { Fail 'Cli.exe missing right after copy (unexpected)' }
if ($reg.ExitCode -ne 0) {
    Fail "system integration register failed (exit $($reg.ExitCode)); see %TEMP%\bdt-cli.log"
}

# ---- 5b. core ensure task (S3-4; decision "register from BOTH places", installer half) ----
# Why the installer must do this at all: core self-registers the task on its first start, so if the
# user never launches core after installing, the crash fallback would not exist at all.
#
# Why we ask the CLI instead of writing the task XML here: the definition lives in exactly ONE place
# (core/src/task.rs). A second copy in PowerShell would be the same disease this repo already paid
# for once - two implementations that each look right and never report the disagreement.
# `--core task register` goes through the control pipe; the CLI ensures core first, then asks it.
#
# NON-FATAL on purpose: core registers the task on its first start anyway, so a failure here degrades
# to "the fallback shows up at first launch" instead of aborting an otherwise good install.
Step 'registering the core ensure task (fallback; core also self-registers on first start)...'
$task = Invoke-Tool $cli @('--core', 'task', 'register') -Capture
if ($null -eq $task) {
    Warn 'core ensure task: could not run the CLI (Cli.exe missing?) - core will register it on first start'
}
elseif ($task.ExitCode -ne 0) {
    Warn "core ensure task NOT registered (CLI exit $($task.ExitCode)); core will register it on first start"
}
else {
    Step 'core ensure task ready (every 5 minutes, current user, no wake)'
}

if ($NoAutostart) {
    Remove-ItemProperty -Path $runKey -Name $trayValueName -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $startupApprovedKey -Name $trayValueName -ErrorAction SilentlyContinue
    Step 'autostart NOT registered (-NoAutostart)'
}

# ---- 6. A route: Win11 new-menu sparse package ----
$msixMode = 'skipped'
if ($Msix -eq 'skip') {
    Warn 'A route skipped by request (-Msix skip): Win11 new-menu first level will not show our entries (classic menu still works).'
}
else {
    $packer = Join-Path $PSScriptRoot 'pack-shellmenu-msix.ps1'
    if (-not (Test-Path $packer)) {
        Warn 'pack-shellmenu-msix.ps1 not found; A route skipped.'
    }
    else {
        if ($Msix -eq 'loose' -or $Msix -eq 'auto') {
            # loose = developer-mode registration (no certificate, no elevation) - the right fit
            # for a user-level installer. The signed route needs machine-level cert trust (elevation).
            Step 'A route: loose (developer mode) registration...'
            try {
                & powershell -NoProfile -ExecutionPolicy Bypass -File $packer -Loose -ExternalLocation $target
                if ($LASTEXITCODE -eq 0) { $msixMode = 'loose' }
            }
            catch { Warn "A route (loose) failed: $($_.Exception.Message)" }
        }

        if ($msixMode -eq 'skipped' -and ($Msix -eq 'signed' -or $Msix -eq 'auto')) {
            Step 'A route: signed registration...'
            try {
                & powershell -NoProfile -ExecutionPolicy Bypass -File $packer -ExternalLocation $target
                if ($LASTEXITCODE -eq 0) { $msixMode = 'signed' }
            }
            catch { Warn "A route (signed) failed: $($_.Exception.Message)" }
        }

        if ($msixMode -eq 'skipped') {
            Warn 'A route NOT registered: Win11 new-menu first level will not show our entries. B route (classic menu / "Show more options") still works.'
        }
    }
}

# rewrite the pointer with the actual A-route mode (read by --system-integration status)
$record.msixMode = $msixMode
[IO.File]::WriteAllText($pointer, ($record | ConvertTo-Json -Depth 4), $utf8NoBom)

# ---- 7. start the tray (it brings up agent + desktop service) ----
$tray = Join-Path $target 'BetterDesktop.Tray.exe'
Step "starting tray: $tray"

# Re-arm the one-time "where is my tray icon" hint (TrayApplicationContext.ShowFirstRunTrayHint):
# Windows 11 folds NEWLY-appeared tray icons into the '^' overflow area, and the #1 user report we get is
# "the tray app is not installed / not running" while it is in fact running with a hidden icon.
# The hint is gated by a flag file and shown only once per install -> a reinstall must re-arm it,
# otherwise an upgrading user is never told to look in the overflow area.
$trayHintFlag = Join-Path $env:LOCALAPPDATA 'BetterDesktop\tray-icon-hint.flag'
Remove-Item $trayHintFlag -Force -ErrorAction SilentlyContinue

Start-Process $tray | Out-Null
Start-Sleep -Milliseconds 700

# Verify it really came up. A silent start failure (stale process holding the single-instance mutex,
# missing dependency, crash on load) is indistinguishable from "not installed" for the user.
$trayProc = Get-Process -Name 'BetterDesktop.Tray' -ErrorAction SilentlyContinue
if ($trayProc) {
    Step "tray running (pid $($trayProc[0].Id))"
}
else {
    Warn 'tray did not start. Run BetterDesktop.Tray.exe manually to see the failure, and check %LOCALAPPDATA%\BetterDesktop\logs\tray.log'
}

# ---- 8. restart explorer so the shell reloads context-menu handlers ----
if (-not $NoRestartExplorer) { Restart-Explorer }

# ---- 9. remove the previous install root (deferred until after the explorer restart) ----
# Ordering matters: explorer holds the OLD native DLL in-proc until it restarts, so deleting the
# previous root before the restart fails (same trap as the uninstaller).
# Guard first: installRoot comes from deployment.json (untrusted, can be corrupted or hand-edited)
# and a recursive delete on it would otherwise be unguarded.
if ($previousRoot -and ($previousRoot -ne $target) -and (Test-Path $previousRoot) -and -not (Test-SafeDeletePath $previousRoot)) {
    Warn "refusing to remove the previous install root (outside the product folder): $previousRoot"
    $previousRoot = ''
}

if (-not $NoRestartExplorer -and $previousRoot -and ($previousRoot -ne $target) -and (Test-Path $previousRoot)) {
    Step "removing previous install: $previousRoot"
    $removed = $false
    for ($i = 0; $i -lt 10; $i++) {
        try {
            Remove-Item $previousRoot -Recurse -Force -ErrorAction Stop
            $removed = $true
            break
        }
        catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $removed) {
        Warn "could not remove the previous install (still locked): $previousRoot - delete it manually later."
    }
}

# ---- 10. report ----
Write-Host ''
Write-Host '=== INSTALL OK ===' -ForegroundColor Green
Write-Host "install root : $target"
Write-Host "version      : $prodVersion build=$fileVersion"
Write-Host "A route      : $msixMode"
Invoke-Tool $cli @('--system-integration', 'status') -Capture | Out-Null
