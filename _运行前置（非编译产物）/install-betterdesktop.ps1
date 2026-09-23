# ============================================================
#  BetterDesktop installer (user-level, no admin required)
#
#  Steps (in order)
#    1) validate the source folder (a half release must never be installed)
#    2) stop our running components (tray / desktop service / host / engines)
#       NOTE: agent and watchdog were RETIRED in S4-4 (2026-09-19) - their duties moved into core
#       (core/src/supervisor.rs, hotkeys.rs, shellmenu.rs). The stop step below still tries
#       BetterDesktop.Agent.exe when present, on purpose: an OLD install may still be running it,
#       and leaving it alive would fight core for the same components.
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

# Required files are NOT listed here any more (2026-09-21).
# Source of truth: scripts/manifests/required-files.json, which travels INSIDE the release folder
# next to this script (copied there by publish.ps1 via the launcher project), so the installer
# validates a payload against the manifest that payload itself ships. See Get-MissingRequired.

# ---- log file ----
#
# This installer used to print progress to stdout ONLY - and it is the only script in this product
# without a log file (launcher / core / tray / desktop-control all write one). Real incident
# 2026-09-21: run in the background, the installer HUNG after the copy had finished; the only
# evidence was "the target folder stopped growing" - no way to tell which step it was stuck in,
# and therefore no way to fix it. Every progress line now goes to stdout AND to
#   %LOCALAPPDATA%\BetterDesktop\logs\install-<utc stamp>.log
# with an elapsed-seconds column, so "where did it hang" is answered by the LAST line of the file.
#
# AppendAllText (open / append / close per line) is deliberate: a hung process must still leave its
# last line on disk. A buffered writer or Start-Transcript would lose exactly the evidence needed.
$script:LogFile = ''
$script:StepClock = [System.Diagnostics.Stopwatch]::StartNew()
$script:Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# Longest single child-tool wait (the reason NOT to use -Wait is in Invoke-Tool below).
# 45s: a normal call returns within 15s, while a STUCK child must never hang the install forever -
# that is exactly the shape of the 2026-09-22 incident (the installer sat at 17.5s, silently, for hours).
$script:ToolTimeoutMs = 45000

function Start-Log {
    try {
        $dir = Join-Path $env:LOCALAPPDATA "$product\logs"
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $stamp = (Get-Date).ToUniversalTime().ToString('yyyy.MM.dd.HHmmss')
        $script:LogFile = Join-Path $dir "install-$stamp.log"
        [IO.File]::WriteAllText($script:LogFile, '', $script:Utf8NoBom)
    }
    catch {
        $script:LogFile = ''
        Write-Host "[install][warn] could not create a log file ($($_.Exception.Message)); progress goes to stdout only" -ForegroundColor Yellow
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
    Write-Host "[install] $message" -ForegroundColor Cyan
}
function Warn([string]$message) {
    Write-Log 'warn' $message
    Write-Host "[install][warn] $message" -ForegroundColor Yellow
}
function Fail([string]$message) {
    Write-Log 'FAIL' $message
    Write-Host "[install] FAILED: $message" -ForegroundColor Red
    # Print the log path too: reading that file is the first thing anyone does at a failure site.
    if ($script:LogFile) { Write-Host "[install] log: $script:LogFile" -ForegroundColor Red }
    exit 1
}

Start-Log
Write-Log 'info' "installer started; script=$PSCommandPath; source='$Source'; dryRun=$($DryRun.IsPresent)"

# Run an executable to completion. Mandatory for our GUI-subsystem exes (PowerShell does not
# wait for those, so '& exe' would race ahead with a stale $LASTEXITCODE).
function Invoke-Tool([string]$exe, [string[]]$arguments, [switch]$Capture) {
    if (-not (Test-Path $exe)) { return $null }
    if ($Capture) {
        $tmp = [IO.Path]::GetTempFileName()
        try {
            # TRAP - do not "simplify" this back to `-Wait`.
            # `Start-Process -Wait` waits not only for the process to exit but also for the redirected
            # stdout stream to reach EOF - and a LONG-LIVED GRANDCHILD INHERITS that handle.
            # Measured 2026-09-22: `Cli --core task register` first ensures core (it starts the resident
            # betterdesktop-core.exe itself); core inherits the handle and never exits, so the handle
            # never closes and the installer hung FOREVER, its last log line being
            # "registering the core ensure task...". The log proved it: core's start time is the same
            # second as that last line, while the CLI process was gone and the installer was not.
            # We mean "wait for this process", not "wait for this stream" - the latter is held hostage
            # by a process we deliberately leave running. So wait for the process only, with a timeout.
            $p = Start-Process -FilePath $exe -ArgumentList $arguments -NoNewWindow -PassThru -RedirectStandardOutput $tmp
            if (-not $p.WaitForExit($script:ToolTimeoutMs)) {
                Warn "$exe did not exit within $([int]($script:ToolTimeoutMs / 1000))s; continuing without its output"
            }
            $out = ''
            # Still try to read the temp file after a timeout; PowerShell's async reader may hold it.
            try { if (Test-Path $tmp) { $out = Get-Content $tmp -Raw } } catch { }
            if (-not [string]::IsNullOrWhiteSpace($out)) {
                Write-Host $out.TrimEnd()
                # Child-tool output goes to the log too: the CLI's message is often only half of
                # why a registration did not happen, and this file is the only place both halves meet.
                Write-Log 'tool' $out.TrimEnd()
            }
            return @{ ExitCode = $p.ExitCode; Output = $out }
        }
        finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
    }

    $p = Start-Process -FilePath $exe -ArgumentList $arguments -NoNewWindow -Wait -PassThru
    return @{ ExitCode = $p.ExitCode; Output = '' }
}

# Missing files in $dir, read from the manifest that ships inside $dir.
#
# Every manifest problem is reported AS a missing item instead of being swallowed: a check that
# cannot fail is worse than no check ("a half release must never be installed" is this script's
# whole reason for step 1). A missing/unparsable/truncated manifest therefore fails the install
# with a message naming the manifest, not with a silent pass over an empty list.
function Get-MissingRequired([string]$dir) {
    $manifestPath = Join-Path $dir 'required-files.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        # The manifest is itself a required file, so its absence IS an incomplete source.
        return @('required-files.json')
    }

    try {
        $files = @((Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json).files)
    }
    catch {
        return @("required-files.json (unparsable: $($_.Exception.Message))")
    }

    $requiredMain = @($files | Where-Object { $_.stage -eq 'main' } | ForEach-Object { $_.path })
    if ($requiredMain.Count -lt 10) {
        return @("required-files.json (only $($requiredMain.Count) 'main' entries - manifest damaged)")
    }

    $missing = @()
    foreach ($file in $requiredMain) {
        if (-not (Test-Path (Join-Path $dir $file))) { $missing += $file }
    }
    return $missing
}

function Resolve-Source {
    if (-not [string]::IsNullOrWhiteSpace($Source)) {
        if (-not (Test-Path $Source)) { Fail "source folder not found: $Source" }
        return (Resolve-Path $Source).Path
    }

    # Packaged layout: this script is shipped INSIDE the release folder (publish.ps1 copies it next
    # to the exes), so the folder it lives in IS the payload. Needed since 2026-09-22 because the
    # hand-off zip gets unzipped by someone who does NOT have this repo: without this branch a plain
    # run ends with "no dist folder; run scripts\publish.ps1 first" - advice that cannot be followed
    # outside the repo (there is no repo, and no publish script, inside the zip).
    # The folder is validated exactly like any other source below, so this is a convenience, not a
    # bypass of the completeness check.
    if ((Get-MissingRequired $PSScriptRoot).Count -eq 0) { return (Resolve-Path $PSScriptRoot).Path }

    $distRoot = Join-Path $repoRoot 'dist'
    if (-not (Test-Path $distRoot)) { Fail 'no dist folder and this script does not sit in a complete release folder; pass -Source <release folder>' }

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

# Processes that must let go of the install folder before the copy, by process NAME (so a copy
# running from ANY location is stopped - a hand-started dist build counts too).
#
# 2026-09-21: 'BetterDesktop' (the launcher) was MISSING from this list, and it can hold WPF
# satellite assemblies open (real repro: reinstall aborted with
# "Access to the path '...\zh-Hans\PresentationCore.resources.dll' is denied" - a message that
# points at a resource DLL nobody can connect to "the launcher window is still open"). It is a
# WPF app whose boot-report window stays up, so the trap is easy to hit in normal use:
# install -> try the app -> install again -> abort.
# BetterDesktop.Cli is listed for the same class of reason: a CLI left over from a manual run
# keeps BetterDesktop.Cli.dll mapped, and the installer itself spawns the CLI later on.
# betterdesktop-core is NOT here on purpose - it needs the fallback disarmed first, so it is
# stopped separately below (see the core block in step 2).
$componentNames = @('BetterDesktop.Watchdog', 'BetterDesktop.Tray', 'BetterDesktop.Host',
                    'BetterDesktop.DesktopControl', 'BetterDesktop.Clipboard.Panel',
                    'BetterDesktop.Clipboard.Engine', 'BetterDesktop.Index.Engine',
                    'BetterDesktop.Capture', 'BetterDesktop.Settings',
                    'BetterDesktop', 'BetterDesktop.Cli')

# A fixed sleep is not enough: a component that is still shutting down keeps its DLLs mapped,
# which makes the folder copy/delete fail later (same trap as the uninstaller).
function Wait-ProcessesGone([int]$timeoutMs = 8000, [string[]]$Only = @()) {
    # -Only narrows the wait to what the caller has actually stopped yet. core is stopped on its own
    # (disarming the fallback task has to happen first), and waiting for the FULL component list at
    # that moment warns about components that were never asked to exit yet - a warning that says the
    # opposite of the truth, which is worse than no warning.
    $names = if ($Only.Count -gt 0) { $Only } else { $componentNames }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $alive = @()
        foreach ($n in $names) { if (Get-RunningOrNull $n) { $alive += $n } }
        if ($alive.Count -eq 0) { return $true }
        Start-Sleep -Milliseconds 200
    }

    $left = @()
    foreach ($n in $names) { if (Get-RunningOrNull $n) { $left += $n } }
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

# ---- 1b. module-stage payload: WARN, never fail (mirrors the engines block below) ----
#
# publish.ps1's release folder does NOT contain the four feature-module exes (Capture /
# Clipboard.Panel / Clipboard.Engine / Index.Engine) - they come from publish-modules.ps1's
# modules 02-05. The launcher, however, REQUIRES all of them. So a payload can pass step 1
# (main-stage only) and still install an INCOMPLETE product.
#
# 2026-09-21, real machine: that is exactly what happened, and it was invisible - the launcher
# reported "20 required files found" because ComponentPaths.Find fell back to
# %LOCALAPPDATA%\BetterDesktop, where stale copies from an older deployment happened to sit.
# A missing feature was reported as present by leftovers. Say it out loud here instead.
# Complete source: the "01-main" module folder (publish-modules.ps1 makes it self-contained).
$moduleMissing = @()
$manifestPath = Join-Path $src 'required-files.json'
if (Test-Path -LiteralPath $manifestPath) {
    try {
        $moduleFiles = @((Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json).files |
            Where-Object { $_.stage -eq 'modules' })
        if ($moduleFiles.Count -eq 0) {
            Warn "required-files.json lists no 'modules' entries - the manifest lost a stage; module gaps cannot be detected"
        }
        foreach ($m in $moduleFiles) {
            if (-not (Test-Path (Join-Path $src $m.path))) { $moduleMissing += "$($m.label) ($($m.path))" }
        }
    }
    catch {
        $moduleMissing = @("(manifest unreadable: $($_.Exception.Message))")
    }
}
if ($moduleMissing.Count -gt 0) {
    Warn ("this payload is missing $($moduleMissing.Count) feature module file(s): " + ($moduleMissing -join ', '))
    Warn 'those features will not work after installing. Use the 01-main module folder as the source (it is self-contained), or add the 02-05 module folders.'
}
else {
    Step 'feature modules present (the payload carries all modules)'
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
# What this build actually needs, measured rather than assumed:
#   * .NET: NOTHING since 2026-09-18. The release is SELF-CONTAINED (scheme A) and carries
#     coreclr.dll / hostfxr.dll / PresentationFramework.dll next to the exes. The check below stays
#     for the case of a foreign, framework-dependent payload.
#   * VC++ 2015-2022 x64: SHIPPED INSIDE the package since 2026-09-22 (publish.ps1 copies redist\
#     into the release root). VCRUNTIME140.dll is a real import of betterdesktop-core.exe - see the
#     block further down for how that was measured and what it means when the file is absent.
# A gap here shows up on a clean machine as "it installed but nothing works" with no explanation,
# so both are checked here, with exact download links, before anything is touched.
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

# VC++ 2015-2022 x64: SHIPPED INSIDE this package since 2026-09-22 (publish.ps1 copies redist\ into
# the release root next to this script), so a clean machine needs nothing installed.
#
# Why it matters at all - measured 2026-09-22 by scanning the import names of our binaries:
#   betterdesktop-core.exe IMPORTS VCRUNTIME140.dll, and it is the only one that does
#   (the Rust engines, the native shell-menu DLL and every .NET component do not).
#   core is the lifecycle owner: without this DLL the product does not run AT ALL - no tray, no
#   hotkeys, no supervision - so this is a hard prerequisite.
#
# This block used to claim "our own binaries link the CRT statically since 2026-09-18 (scheme D) ...
# Informational, never fatal" and told the user only that "a few third-party conversions may fail.
# Everything else is unaffected". Both halves were wrong, and the second one sent the tester looking
# in the wrong place for what is actually a dead product. Keep the three cases apart:
#   a) the file ships in the payload          -> nothing to install (normal case since 2026-09-22)
#   b) not shipped but present on the machine -> the loader finds it in System32
#   c) neither                                -> FATAL, this build cannot start
$vcShipped = Test-Path (Join-Path $src 'vcruntime140.dll')
if ($vcShipped) {
    Step 'VC++ runtime is shipped with this package (nothing to install)'
}
else {
    $vcOk = $false
    try {
        $vc = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64' -ErrorAction Stop
        if ($vc.Installed -eq 1) { $vcOk = $true }
    }
    catch { $vcOk = $false }
    if (-not $vcOk) {
        # some machines carry the DLLs without the redist registry key
        $vcOk = (Test-Path (Join-Path $env:WINDIR 'System32\vcruntime140.dll'))
    }
    if (-not $vcOk) {
        $fatalProblems.Add('VC++ 2015-2022 x64 runtime is missing AND this package does not ship vcruntime140.dll -> betterdesktop-core.exe cannot start, so nothing will work. Install https://aka.ms/vs/17/release/vc_redist.x64.exe and run this installer again.')
    }
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
    Write-Host '  1. stop components (tray / desktop service / host / engines; agent+watchdog were retired)'
    Write-Host "  2. copy $src -> $target"
    Write-Host "  3. write $pointer"
    Write-Host '  4. Cli --system-integration register'
    Write-Host '  4b. Cli --core task register (core crash fallback)'
    Write-Host "  5. A route (msix): $Msix"
    # Was 'start tray': core owns the notification area now (the .NET tray was retired in S4-4 and
    # the code below starts betterdesktop-core.exe). Keep the plan text equal to what actually runs.
    Write-Host ('  6. start core (resident control plane; owns the tray icon)' + $(if ($NoAutostart) { ' (autostart NOT registered)' } else { ' (autostart registered)' }))
    Write-Host "  7. restart explorer: $(-not $NoRestartExplorer)"
    Write-Host '  8. drop stale tray icon entries (HKCU\Control Panel\NotifyIconSettings)'
    exit 0
}

# ---- 2. stop our running components (graceful first, then force) ----
Step 'stopping running components...'

# core FIRST, and disarming the fallback is part of stopping it (2026-09-21).
#
# Two separate traps, both hit for real when reinstalling into the SAME build folder:
#   1) core keeps its OWN exe open => Remove-Item aborts with
#      "Access to the path '...\betterdesktop-core.exe' is denied". core is not in
#      $componentNames, so the loop below never touches it.
#   2) killing core without disarming the crash fallback lets the EVERY-5-MINUTES task
#      resurrect the OLD core from the OLD path *during* the copy - and since step 5b talks
#      to whatever core answers, the task would then be re-registered to the OLD path.
# The disarming flag IS existing machinery (core/src/task.rs): core refuses --from-task
# launches while it exists, and any EXPLICIT start clears it again - which is exactly what the
# CLI does in step 5b (it ensures core first) and what step 7 does. So: no new mechanism, and
# no leftover state if the install finishes.
$coreStoppedFlag = Join-Path $env:LOCALAPPDATA "$product\core-stopped.flag"
$coreProcs = @(Get-Process -Name 'betterdesktop-core' -ErrorAction SilentlyContinue)
if ($coreProcs.Count -gt 0) {
    Step "  stopping betterdesktop-core ($($coreProcs.Count) instance(s)); it holds its own exe open"
    try { [IO.File]::WriteAllText($coreStoppedFlag, 'install in progress: core lets go of its exe') }
    catch { Warn "could not write the core stop flag ($($_.Exception.Message)); the fallback task may relaunch the OLD core mid-install" }
    foreach ($p in $coreProcs) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    [void](Wait-ProcessesGone 3000 -Only @('betterdesktop-core'))
}

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
        $removed = $false
        try {
            Remove-Item $target -Recurse -Force -ErrorAction Stop
            $removed = $true
        }
        catch {
            # Same in-proc lock as the uninstaller: explorer maps native\BetterDesktopShellMenu.dll from
            # the install folder, so reinstalling into the SAME build folder fails until explorer restarts.
            Warn "old install folder is locked ($($_.Exception.Message)); restarting explorer and retrying"
            Restart-Explorer
            Start-Sleep -Milliseconds 500
            try {
                Remove-Item $target -Recurse -Force -ErrorAction Stop
                $removed = $true
            }
            catch {
                # NEVER abort here. Wiping the folder first is a convenience, not a requirement - and
                # bailing out leaves a HALF-DELETED install, which is strictly worse than the problem
                # being solved. Real incident 2026-09-21: this second Remove-Item had no catch, so the
                # script died with the product folder already gutted and the copy never ran - a broken
                # install, and the only message was an "access denied" on some unrelated file.
                # Copying over the leftovers keeps the product whole; the warning names what is left.
                Warn "could not fully remove the old install folder: $($_.Exception.Message)"
                Warn 'copying over the leftovers instead (stale extra files may remain, but the product stays complete).'
            }
        }
        if ($removed) { Step 'old install folder removed' }
    }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -Path (Join-Path $src '*') -Destination $target -Recurse -Force

    # belt and braces: the release folder is already clean, but never ship PDB/XML from a hand-made folder
    Get-ChildItem -Path $target -Recurse -Include *.pdb,*.xml -File -ErrorAction SilentlyContinue | Remove-Item -Force
}

# ---- 3b. optional engines (engines\) ----
# 2026-09-20 convert-lite migration: the document / table / ebook families no longer need any external
# engine (the Rust lite core converts them IN-PROCESS), so pandoc / libreoffice / calibre are no longer
# shipped.
# 2026-09-22 OCR migration: OCR is now the in-house Rust engine (engine-ocr), so the external tesseract
# tree is gone too. What is still installed here: ocr (our engine's runtime + PP-OCRv6 models),
# poppler (PDF) and ffmpeg (audio/video - shipped, but its menu entries are hidden this round).
# Engines are located at <AppContext.BaseDirectory>\engines\... (shell-convert). The tree ships as the
# "06-*" module next to this folder; a full dist may also carry engines\ directly. Missing engines is a
# Warn, never a Fail - conversion keeps working, only OCR/PDF rendering would be unavailable.
# NOTE (2026-09-20): BetterDesktop.Cli.csproj no longer copies engines\ into the build output, so a full
# dist no longer contains it - the 06-* module is now the only carrier.
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
    Warn 'no optional engines found (looked for engines\ here or in the parent folder / 06-* module)'
    Warn 'format conversion still works (the Rust lite core is in-process); only OCR (PP-OCRv6 runtime/models) and PDF rendering (poppler) will be unavailable'
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

# ---- 7. start core: the resident control plane ----
# core replaced the .NET tray. It owns the notification-area icon, the global hotkeys, the control
# pipe and the supervisor, so it is what must be running after an install. Starting the tray as well
# would put a SECOND icon in the notification area and resurrect the component core now supervises.
# Packages older than core (no core binary) keep the old behaviour through the fallback below.
$core = Join-Path $target 'betterdesktop-core.exe'
if (Test-Path $core) {
    Step "starting core: $core"
    Start-Process $core -WorkingDirectory $target | Out-Null
    Start-Sleep -Milliseconds 1200

    # Verify it really came up. A silent start failure (stale process holding the single-instance
    # mutex, missing dependency, crash on load) is indistinguishable from "not installed" for the
    # user - and core now owns the tray icon, i.e. the only visible proof that the install worked.
    $coreProc = Get-Process -Name 'betterdesktop-core' -ErrorAction SilentlyContinue
    if ($coreProc) {
        Step "core running (pid $($coreProc[0].Id)): tray icon, hotkeys and supervision are up"
    }
    else {
        Warn 'core did not start. Run betterdesktop-core.exe manually to see the failure, and check %LOCALAPPDATA%\BetterDesktop\logs\core-*.log'
    }
}
else {
    Warn 'betterdesktop-core.exe is not in this package: falling back to the legacy tray.'

    $tray = Join-Path $target 'BetterDesktop.Tray.exe'
    Step "starting tray: $tray"

    # Re-arm the one-time "where is my tray icon" hint (TrayApplicationContext.ShowFirstRunTrayHint):
    # Windows 11 folds NEWLY-appeared tray icons into the '^' overflow area, and the #1 user report we get is
    # "the tray app is not installed / not running" while it is in fact running with a hidden icon.
    # The hint is gated by a flag file and shown only once per install -> a reinstall must re-arm it,
    # otherwise an upgrading user is never told to look in the overflow area.
    # NOTE: core has no equivalent hint yet (known gap: a fresh core install on Win11 can still look
    # "missing" because the icon starts folded into '^').
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

# ---- 9b. tray icon registrations: take ours with us, drop the ones that are not this install ----
#
# Windows keys HKCU\Control Panel\NotifyIconSettings by the icon's ABSOLUTE exe path and NEVER
# garbage-collects dead entries. Measured 2026-09-21 on a real machine: 43 of 98 entries point at
# files that no longer exist. Our install folder carries the build number (app\<build>\) and dev
# runs come from bin\Debug / core\target\release, so every install adds ANOTHER permanent entry.
# User-reported symptom: the notification-area settings listed betterdesktop-core.exe twice plus a
# retired BetterDesktop.Tray, and nothing in this repo ever cleaned them up (the 2026-09-18 session
# judged the stale ones "harmless" - true for icon visibility, false for that list, which only grows).
#
# Rule: remove OURS when its file is gone, or when it lives outside the install root and the data
# dir. Matched by exe FILE NAME on purpose - a folder-based rule would miss the dev-tree entries.
$trayEntryPattern = '(?i)^(betterdesktop-core\.exe|BetterDesktop(\.[A-Za-z]+)*\.exe)$'
$dataRoot = Join-Path $env:LOCALAPPDATA $product
$iconBase = 'HKCU:\Control Panel\NotifyIconSettings'
$iconKept = 0
$iconRemoved = 0
if (Test-Path $iconBase) {
    foreach ($key in @(Get-ChildItem $iconBase -ErrorAction SilentlyContinue)) {
        $props = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue
        $iconPath = [string]$props.ExecutablePath
        if ([string]::IsNullOrWhiteSpace($iconPath)) { continue }
        if ((Split-Path $iconPath -Leaf) -notmatch $trayEntryPattern) { continue }

        $iconExists = Test-Path -LiteralPath $iconPath
        $underTarget = $iconPath.StartsWith($target, [StringComparison]::OrdinalIgnoreCase)
        $underData = $iconPath.StartsWith($dataRoot, [StringComparison]::OrdinalIgnoreCase)
        # [judgement fix 2026-09-22] `app\<build>\` IS under the data dir, so exempting "anything
        # under the data dir" also exempted EVERY PREVIOUS INSTALL - which is exactly what makes the
        # list grow one row per install (measured: kept 3 / removed 0, two of them being the previous
        # install's core and Tray). The exemption must exclude `app\`: loose exes at the data-dir root
        # and in the module subfolders may still be live, previous install folders never are.
        $underDataApp = $iconPath.StartsWith((Join-Path $dataRoot 'app'), [StringComparison]::OrdinalIgnoreCase)
        if ($iconExists -and ($underTarget -or ($underData -and -not $underDataApp))) { $iconKept++; continue }

        try {
            Remove-Item -LiteralPath $key.PSPath -Recurse -Force -ErrorAction Stop
            $iconRemoved++
        }
        catch { Warn "could not remove a stale tray entry ($iconPath): $($_.Exception.Message)" }
    }
}
Step "tray icon entries: kept $iconKept, removed $iconRemoved stale one(s)"

# ---- 10. report ----
Write-Host ''
Write-Host '=== INSTALL OK ===' -ForegroundColor Green
Write-Host "install root : $target"
Write-Host "version      : $prodVersion build=$fileVersion"
Write-Host "A route      : $msixMode"
Invoke-Tool $cli @('--system-integration', 'status') -Capture | Out-Null
