# build-core.ps1 - build the BetterDesktop Rust core (the single resident process).
#
# Output: core/target/<profile>/betterdesktop-core.exe
#         (collected by scripts/publish.ps1, hot-deployed by scripts/deploy-core.ps1)
#
# NOTE: keep this file pure ASCII (PowerShell 5.1 reads .ps1 as ANSI; non-ASCII breaks parsing).
#
# Usage:
#   pwsh -File scripts\build-core.ps1
#   pwsh -File scripts\build-core.ps1 -Profile debug
#
# Why an explicit script instead of letting publish.ps1 run cargo:
#   That is the standing rule in this repo - BetterDesktop.Shell.Convert.csproj says "cargo builds
#   are an EXPLICIT step (this repo does not run cargo for you; engine/ and engine-index/ follow the
#   same rule)". So publish.ps1 only *collects* products; building them stays an explicit action.
#   This script is that action for core, so the action has a name instead of a hand-typed command.

[CmdletBinding()]
param(
    [ValidateSet('release', 'debug')]
    [string]$Profile = 'release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$coreDir = Join-Path $repoRoot 'core'
$manifest = Join-Path $coreDir 'Cargo.toml'

if (-not (Test-Path $manifest)) {
    throw "core crate not found: $manifest"
}

# cargo is often NOT on PATH in this repo's environments - resolve it explicitly and fail with an
# actionable message instead of a bare "command not found".
$cargo = $null
$onPath = Get-Command cargo -ErrorAction SilentlyContinue
if ($onPath) {
    $cargo = $onPath.Source
}
else {
    $fallback = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
    if (Test-Path $fallback) { $cargo = $fallback }
}
if (-not $cargo) {
    throw "cargo not found. Install Rust (https://rustup.rs) or add %USERPROFILE%\.cargo\bin to PATH."
}

# Not $args: that is an automatic variable in PowerShell and must not be assigned to.
$cargoArgs = @('build', '--manifest-path', $manifest)
if ($Profile -eq 'release') { $cargoArgs += '--release' }

Write-Host "[core] $cargo $($cargoArgs -join ' ')" -ForegroundColor Cyan
& $cargo @cargoArgs
$code = $LASTEXITCODE
if ($code -ne 0) {
    # The most confusing cargo failure here is a locked output file: if the target binary is running
    # from this very path (a debug run started by hand, not the installed copy), the linker cannot
    # replace it and cargo reports an opaque error. Name that cause explicitly when it applies.
    $running = @(Get-Process -Name 'betterdesktop-core' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        throw "cargo build failed (exit $code). betterdesktop-core is running (pid $((($running | ForEach-Object { $_.Id }) -join ', '))): if it runs from $coreDir\target\$Profile, the linker cannot replace a locked exe - stop it and retry."
    }
    throw "cargo build failed (exit $code)"
}

$exe = Join-Path $coreDir "target\$Profile\betterdesktop-core.exe"
if (-not (Test-Path $exe)) {
    throw "build succeeded but product is missing: $exe"
}

$fi = Get-Item $exe
Write-Host ("[core] OK  {0}  {1:N0} bytes  {2}" -f $fi.Name, $fi.Length, $fi.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')) -ForegroundColor Green
