# pack-shellmenu-msix.ps1 - stage + pack + sign + register the sparse MSIX that gives
# BetterDesktopShellMenu a **package identity**, which is the only way a context menu
# command can reach the Windows 11 first-level context menu.
#
# Why this is needed:
#   Win11 promotes only commands declared by a *packaged* app through the manifest
#   extension windows.fileExplorerContextMenus. A classic
#   HKCR\...\shellex\ContextMenuHandlers handler (route B) is always pushed into
#   "Show more options", no matter how it is written.
#
# Why *sparse*:
#   A sparse package carries ONLY the manifest + logo assets. The DLL stays in the
#   "external location" (the host output dir), so rebuilding the native DLL does not
#   require repacking. The certificate exists only to satisfy package signing.
#
# Output:
#   packages/shell/shell-context-menu/native/build/msix/BetterDesktop.ShellMenu_<ver>.msix
#   packages/shell/shell-context-menu/native/build/msix/cert/BetterDesktop.pfx|.cer
#
# NOTE: keep this file pure ASCII (PowerShell 5.1 reads .ps1 as ANSI; non-ASCII breaks parsing).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\pack-shellmenu-msix.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\pack-shellmenu-msix.ps1 -ExternalLocation <dir>
#   powershell -ExecutionPolicy Bypass -File scripts\pack-shellmenu-msix.ps1 -SkipInstall

[CmdletBinding()]
param(
    [string]$ExternalLocation = '',
    [string]$CertSubject = 'CN=BetterDesktop',
    [string]$PfxPassword = 'BetterDesktop',
    [string]$Configuration = 'Debug',
    [switch]$Loose,
    [switch]$SkipInstall
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path $PSScriptRoot -Parent
$nativeDir  = Join-Path $repoRoot 'packages\shell\shell-context-menu\native'
$manifestIn = Join-Path $nativeDir 'AppxManifest.xml'
$msixRoot   = Join-Path $nativeDir 'build\msix'
$stageDir   = Join-Path $msixRoot 'stage'
$certDir    = Join-Path $msixRoot 'cert'
$pfxPath    = Join-Path $certDir 'BetterDesktop.pfx'
$cerPath    = Join-Path $certDir 'BetterDesktop.cer'
$packageName = 'BetterDesktop.ShellMenu'

if (-not (Test-Path $manifestIn)) { throw "manifest not found: $manifestIn" }

if ([string]::IsNullOrWhiteSpace($ExternalLocation)) {
    $ExternalLocation = Join-Path $repoRoot "host\bin\x64\$Configuration\net8.0-windows10.0.19041.0"
}
if (-not (Test-Path $ExternalLocation)) { throw "external location not found: $ExternalLocation" }

# The manifest resolves these relative to the external location root.
$dllInExternal  = Join-Path $ExternalLocation 'native\BetterDesktopShellMenu.dll'
$exeInExternal  = Join-Path $ExternalLocation 'BetterDesktop.Cli.exe'
foreach ($required in @($dllInExternal, $exeInExternal)) {
    if (-not (Test-Path $required)) { throw "external location is missing a declared file: $required" }
}

# ---------------------------------------------------------------------------
# Locate SDK tools (MakeAppx / SignTool): pick the highest installed version.
# ---------------------------------------------------------------------------
function Find-SdkTool([string]$name) {
    $kitsRoot = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (-not (Test-Path $kitsRoot)) { return '' }
    $hit = Get-ChildItem $kitsRoot -Recurse -Filter $name -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like '*\x64\*' } |
        Sort-Object FullName |
        Select-Object -Last 1
    if ($null -eq $hit) { return '' }
    return $hit.FullName
}

$makeAppx = Find-SdkTool 'makeappx.exe'
$signTool = Find-SdkTool 'signtool.exe'
if ([string]::IsNullOrWhiteSpace($makeAppx)) { throw 'makeappx.exe not found (install the Windows SDK)' }
if ([string]::IsNullOrWhiteSpace($signTool)) { throw 'signtool.exe not found (install the Windows SDK)' }
Write-Host "[msix] makeappx : $makeAppx"
Write-Host "[msix] signtool : $signTool"

# ---------------------------------------------------------------------------
# Version: 1.3.<dayOfYear>.<HHmm>. Re-registering the SAME version fails with
# 0x80073CF9, so the version is derived from the clock instead of being edited by hand.
# ---------------------------------------------------------------------------
$now     = Get-Date
$version = "1.3.$($now.DayOfYear).$([int]$now.ToString('HHmm'))"
Write-Host "[msix] version  : $version"

Add-Type -AssemblyName System.Drawing
function New-Logo([string]$path, [int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([System.Drawing.Color]::FromArgb(255, 30, 120, 216))
    $graphics.Dispose()
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

function Get-ManifestText([string]$version) {
    $text = Get-Content $manifestIn -Raw -Encoding UTF8
    $text = $text.TrimStart([char]0xFEFF)
    if ($text -notmatch 'Version="0\.0\.0\.0"') {
        throw 'AppxManifest.xml no longer carries the Version="0.0.0.0" placeholder'
    }
    return ($text -replace 'Version="0\.0\.0\.0"', "Version=`"$version`"")
}

# ---------------------------------------------------------------------------
# -Loose: developer-mode registration (no certificate, no msix).
#
# Why keep two routes at all:
#   0x800B0109 - a signed MSIX is validated by the AppX Deployment Service, which runs
#   as SYSTEM and therefore reads the **machine** store (LocalMachine\TrustedPeople),
#   NOT CurrentUser\TrustedPeople. Trusting the self-signed cert per-user is not enough;
#   the signed/sparse route needs one elevated trust step at install time.
#   -Loose sidesteps packaging entirely via Add-AppxPackage -Register, which is exactly
#   what Developer Mode exists for. Use it to iterate; use the signed route to ship.
# ---------------------------------------------------------------------------
if ($Loose) {
    $manifestText = Get-ManifestText $version

    # AllowExternalContent is mutually exclusive with -Register. Observed (2026-09-11):
    #   Add-AppxPackage -Register ... -> 0x80073CF9
    #   "must be installed using an external location"
    # Loose registration instead requires every declared file to sit inside the registered
    # folder. Our install dir already holds both BetterDesktop.Cli.exe and
    # native\BetterDesktopShellMenu.dll (it is the host build output), so dropping the
    # element is enough; nothing else in the manifest changes.
    $manifestText = $manifestText -replace '(?s)\s*<uap10:AllowExternalContent>true</uap10:AllowExternalContent>', ''

    Write-Host "[msix][loose] install dir: $ExternalLocation" -ForegroundColor Cyan

    New-Item -ItemType Directory -Force -Path (Join-Path $ExternalLocation 'Assets') | Out-Null
    New-Logo (Join-Path $ExternalLocation 'Assets\StoreLogo.png') 50
    New-Logo (Join-Path $ExternalLocation 'Assets\Square150x150Logo.png') 150
    New-Logo (Join-Path $ExternalLocation 'Assets\Square44x44Logo.png') 44

    $targetManifest = Join-Path $ExternalLocation 'AppxManifest.xml'
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($targetManifest, $manifestText, $utf8NoBom)

    $existing = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
        Write-Host "[msix][loose] removing previous registration $($existing.Version) ..." -ForegroundColor Cyan
        Remove-AppxPackage -Package $existing.PackageFullName
    }

    Add-AppxPackage -Register $targetManifest
    $installed = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
    if ($null -eq $installed) { throw 'registration reported success but the package is not present' }
    Write-Host "[msix][loose] registered: $($installed.PackageFullName)" -ForegroundColor Green
    Write-Host "[msix][loose] done. Restart explorer to pick up the new menu." -ForegroundColor Cyan
    return
}

# ---------------------------------------------------------------------------
# Certificate: self-signed code signing cert, public key trusted per-user
# (CurrentUser\TrustedPeople). Without that trust step Add-AppxPackage fails 0x800B0109.
# Identity/@Publisher in the manifest MUST equal $CertSubject.
# ---------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $certDir | Out-Null

$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $CertSubject -and $_.HasPrivateKey } |
    Select-Object -First 1
if ($null -eq $cert) {
    Write-Host "[msix] creating self-signed cert $CertSubject ..." -ForegroundColor Cyan
    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $CertSubject `
        -FriendlyName 'BetterDesktop ShellMenu (dev)' `
        -KeyUsage DigitalSignature `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}
Write-Host "[msix] cert       : $($cert.Thumbprint)"

$securePwd = ConvertTo-SecureString -String $PfxPassword -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePwd | Out-Null
Export-Certificate  -Cert $cert -FilePath $cerPath | Out-Null

$trusted = Get-ChildItem Cert:\CurrentUser\TrustedPeople |
    Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
if ($null -eq $trusted) {
    Write-Host "[msix] importing public key into CurrentUser\TrustedPeople ..." -ForegroundColor Cyan
    Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\CurrentUser\TrustedPeople' | Out-Null
}

# ---------------------------------------------------------------------------
# Stage: manifest (version substituted) + generated logo assets.
# ---------------------------------------------------------------------------
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $stageDir 'Assets') | Out-Null

$manifestText = Get-Content $manifestIn -Raw -Encoding UTF8
$manifestText = $manifestText.TrimStart([char]0xFEFF)
if ($manifestText -notmatch 'Version="0\.0\.0\.0"') {
    throw 'AppxManifest.xml no longer carries the Version="0.0.0.0" placeholder'
}
$manifestText = $manifestText -replace 'Version="0\.0\.0\.0"', "Version=`"$version`""

$stagedManifest = Join-Path $stageDir 'AppxManifest.xml'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($stagedManifest, $manifestText, $utf8NoBom)

New-Logo (Join-Path $stageDir 'Assets\StoreLogo.png') 50
New-Logo (Join-Path $stageDir 'Assets\Square150x150Logo.png') 150
New-Logo (Join-Path $stageDir 'Assets\Square44x44Logo.png') 44
Write-Host "[msix] staged     : $stageDir"

# ---------------------------------------------------------------------------
# Pack + sign. /nv skips validation - required because the real binaries live in the
# external location and are intentionally NOT inside the package.
# ---------------------------------------------------------------------------
$msixPath = Join-Path $msixRoot "$packageName`_$version.msix"
if (Test-Path $msixPath) { Remove-Item $msixPath -Force }

& $makeAppx pack /o /d $stageDir /nv /p $msixPath | Out-Host
if ($LASTEXITCODE -ne 0) { throw "makeappx failed (exit $LASTEXITCODE)" }

& $signTool sign /fd SHA256 /f $pfxPath /p $PfxPassword $msixPath | Out-Host
if ($LASTEXITCODE -ne 0) { throw "signtool failed (exit $LASTEXITCODE)" }
Write-Host "[msix] packed     : $msixPath" -ForegroundColor Green

# ---------------------------------------------------------------------------
# Register. Sparse package registration = Add-AppxPackage -ExternalLocation.
# ---------------------------------------------------------------------------
if ($SkipInstall) {
    Write-Host "[msix] -SkipInstall set; not registering." -ForegroundColor Yellow
    Write-Host "[msix] done."
    return
}

$existing = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    Write-Host "[msix] removing previous registration $($existing.Version) ..." -ForegroundColor Cyan
    Remove-AppxPackage -Package $existing.PackageFullName
}

Write-Host "[msix] registering with external location: $ExternalLocation" -ForegroundColor Cyan
Add-AppxPackage -Path $msixPath -ExternalLocation $ExternalLocation

$installed = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
if ($null -eq $installed) {
    throw 'registration reported success but the package is not present'
}
Write-Host "[msix] registered : $($installed.PackageFullName)"
Write-Host "[msix] install dir: $($installed.InstallLocation)"
Write-Host "[msix] done. If explorer was already running, restart it so the new menu is picked up." -ForegroundColor Cyan
