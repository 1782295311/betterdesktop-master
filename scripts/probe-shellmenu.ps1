# verify-shellmenu.ps1 - out-of-process probe for BetterDesktopShellMenu.dll.
#
# Loads the DLL in THIS PowerShell process (never in explorer), then:
#   1) resolves DllGetClassObject / DllCanUnloadNow exports,
#   2) DllGetClassObject(clsid, IID_IClassFactory) -> factory, then factory->CreateInstance(target IID)
#      for every declared CLSID (B path + A path),
#   3) checks negative cases (unknown CLSID -> CLASS_E_CLASSNOTAVAILABLE, unsupported IID -> E_NOINTERFACE),
#   4) releases every object and asks DllCanUnloadNow (must report S_OK).
#
# Rationale (plan D7): the single biggest risk is an in-proc crash inside explorer.
# Everything that can be proven without touching explorer is proven here first.
#
# NOTE: keep this file pure ASCII (PowerShell 5.1 reads .ps1 as ANSI; non-ASCII breaks parsing).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\probe-shellmenu.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\probe-shellmenu.ps1 -DllPath <path>

[CmdletBinding()]
param(
    [string]$DllPath = '',
    # Optional: path to a shellmenu.json to drive the menu-pipeline probe (config -> HMENU).
    # The probe installs it at the production path with backup/restore.
    [string]$MenuProbeConfig = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($DllPath)) {
    $DllPath = Join-Path $repoRoot 'packages\shell\shell-context-menu\native\BetterDesktopShellMenu.dll'
}

if (-not (Test-Path $DllPath)) {
    Write-Host "FAIL: DLL not found: $DllPath" -ForegroundColor Red
    exit 1
}

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class BdShellNative
{
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibraryW(string lpFileName);

    [DllImport("kernel32", SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32", SetLastError = true)]
    public static extern bool FreeLibrary(IntPtr hModule);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int DllGetClassObjectFn(IntPtr rclsid, IntPtr riid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int DllCanUnloadNowFn();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int CreateInstanceFn(IntPtr pcf, IntPtr pUnkOuter, IntPtr riid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint ReleaseFn(IntPtr punk);
}
"@

$CLSID_B_CLASSIC = '7B2E9C41-3D58-4F0A-9E6B-1A4C8D2F5E71'
$CLSID_A_FILES = '7B2E9C41-3D58-4F0A-9E6B-1A4C8D2F5E70'
$CLSID_A_DIR = '7B2E9C41-3D58-4F0A-9E6B-1A4C8D2F5E73'
$CLSID_A_BG = '7B2E9C41-3D58-4F0A-9E6B-1A4C8D2F5E72'
$IID_IUNKNOWN = '00000000-0000-0000-C000-000000000046'
$IID_ICLASSFACTORY = '00000001-0000-0000-C000-000000000046'

# CAUTION: these three are easy to mix up (verified against SDK 10.0.26100 shobjidl_core.h):
#   IContextMenu  = 000214E4   (NOT E6!)
#   IShellFolder  = 000214E6
#   IShellExtInit = 000214E8
$IID_ICONTEXTMENU = '000214E4-0000-0000-C000-000000000046'
$IID_ISHELLEXTINIT = '000214E8-0000-0000-C000-000000000046'
$IID_IEXPLORERCOMMAND = 'A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9'
$IID_IENUMEXPLORERCOMMAND = 'A88826F8-186F-4987-AADE-EA0CEF8FBFE8'

# HRESULT constants as signed int32. Built byte-by-byte on purpose: casting a large
# positive literal to [uint32]/[int] throws on some PowerShell versions.
function New-Hr([byte]$b0, [byte]$b1, [byte]$b2, [byte]$b3) {
    return [System.BitConverter]::ToInt32([byte[]]@($b0, $b1, $b2, $b3), 0)
}
$S_OK = [int]0
$E_NOINTERFACE = New-Hr 0x02 0x40 0x00 0x80             # 0x80004002
$CLASS_E_NOAGGREGATION = New-Hr 0x10 0x01 0x04 0x80     # 0x80040110
$CLASS_E_CLASSNOTAVAILABLE = New-Hr 0x11 0x01 0x04 0x80 # 0x80040111

function New-GuidPtr([string]$guid) {
    $ptr = [System.Runtime.InteropServices.Marshal]::AllocHGlobal(16)
    [System.Runtime.InteropServices.Marshal]::StructureToPtr([Guid]::Parse($guid), $ptr, $false)
    return $ptr
}

function Get-VtblFn([IntPtr]$punk, [int]$index, [Type]$delegateType) {
    $vtbl = [System.Runtime.InteropServices.Marshal]::ReadIntPtr($punk)
    $addr = [System.Runtime.InteropServices.Marshal]::ReadIntPtr($vtbl, $index * [IntPtr]::Size)
    return [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer($addr, $delegateType)
}

function Release-Unknown([IntPtr]$punk) {
    if ($punk -eq [IntPtr]::Zero) { return }
    $release = Get-VtblFn $punk 2 ([BdShellNative+ReleaseFn])
    [void]$release.Invoke($punk)
}

$failures = 0
function Report([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "  [ok]   $what" -ForegroundColor Green }
    else { Write-Host "  [FAIL] $what" -ForegroundColor Red; $script:failures++ }
}
function FmtHr([int]$hr) {
    $unsigned = [System.BitConverter]::ToUInt32([System.BitConverter]::GetBytes($hr), 0)
    return ("hr=0x{0:X8}" -f $unsigned)
}

Write-Host "== probe-shellmenu =="
Write-Host "dll: $DllPath"

$module = [BdShellNative]::LoadLibraryW($DllPath)
if ($module -eq [IntPtr]::Zero) {
    Write-Host "FAIL: LoadLibraryW failed (win32=$([Runtime.InteropServices.Marshal]::GetLastWin32Error()))" -ForegroundColor Red
    exit 1
}
Write-Host "LoadLibraryW: ok"

try {
    $getClassObjectAddr = [BdShellNative]::GetProcAddress($module, 'DllGetClassObject')
    $canUnloadNowAddr = [BdShellNative]::GetProcAddress($module, 'DllCanUnloadNow')
    Report ($getClassObjectAddr -ne [IntPtr]::Zero) 'export DllGetClassObject'
    Report ($canUnloadNowAddr -ne [IntPtr]::Zero) 'export DllCanUnloadNow'
    if ($getClassObjectAddr -eq [IntPtr]::Zero) { throw 'DllGetClassObject missing' }

    $getClassObject = [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer(
        $getClassObjectAddr, [BdShellNative+DllGetClassObjectFn])
    $canUnloadNow = [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer(
        $canUnloadNowAddr, [BdShellNative+DllCanUnloadNowFn])

    $pIidUnknown = New-GuidPtr $IID_IUNKNOWN
    $pIidClassFactory = New-GuidPtr $IID_ICLASSFACTORY
    $pIidShellExtInit = New-GuidPtr $IID_ISHELLEXTINIT
    $pIidContextMenu = New-GuidPtr $IID_ICONTEXTMENU
    $pIidExplorerCommand = New-GuidPtr $IID_IEXPLORERCOMMAND
    $pIidEnumExplorerCommand = New-GuidPtr $IID_IENUMEXPLORERCOMMAND

    # ---- positive cases: factory + object for every declared CLSID ----
    # The shell asks the B-path object for IShellExtInit (Initialize) and IContextMenu (menu build),
    # so both are exercised.
    $cases = @(
        @{ Name = 'B path  (classic menu)'; Clsid = $CLSID_B_CLASSIC; Iid = $pIidShellExtInit; IidName = 'IShellExtInit' },
        @{ Name = 'B path  (classic menu)'; Clsid = $CLSID_B_CLASSIC; Iid = $pIidContextMenu; IidName = 'IContextMenu' },
        @{ Name = 'A path  (files)'; Clsid = $CLSID_A_FILES; Iid = $pIidExplorerCommand; IidName = 'IExplorerCommand' },
        @{ Name = 'A path  (directory)'; Clsid = $CLSID_A_DIR; Iid = $pIidExplorerCommand; IidName = 'IExplorerCommand' },
        @{ Name = 'A path  (background)'; Clsid = $CLSID_A_BG; Iid = $pIidExplorerCommand; IidName = 'IExplorerCommand' }
    )

    foreach ($case in $cases) {
        $pClsid = New-GuidPtr $case.Clsid
        $factory = [IntPtr]::Zero
        $hr = $getClassObject.Invoke($pClsid, $pIidClassFactory, [ref]$factory)
        Report ($hr -eq $S_OK -and $factory -ne [IntPtr]::Zero) `
            ("$($case.Name): factory $((FmtHr $hr))")

        if ($factory -ne [IntPtr]::Zero) {
            $createInstance = Get-VtblFn $factory 3 ([BdShellNative+CreateInstanceFn])
            $instance = [IntPtr]::Zero
            $hrCreate = $createInstance.Invoke($factory, [IntPtr]::Zero, $case.Iid, [ref]$instance)
            Report ($hrCreate -eq $S_OK -and $instance -ne [IntPtr]::Zero) `
                ("$($case.Name): CreateInstance($($case.IidName)) $((FmtHr $hrCreate))")
            Release-Unknown $instance
            Release-Unknown $factory
        }
        [System.Runtime.InteropServices.Marshal]::FreeHGlobal($pClsid)
    }

    # ---- negative: unknown CLSID ----
    $pUnknown = New-GuidPtr 'DEADBEEF-0000-0000-0000-000000000000'
    $factoryUnknown = [IntPtr]::Zero
    $hrUnknown = $getClassObject.Invoke($pUnknown, $pIidClassFactory, [ref]$factoryUnknown)
    Report (($hrUnknown -eq $CLASS_E_CLASSNOTAVAILABLE) -and ($factoryUnknown -eq [IntPtr]::Zero)) `
        ("unknown CLSID rejected $((FmtHr $hrUnknown))")
    [System.Runtime.InteropServices.Marshal]::FreeHGlobal($pUnknown)

    # ---- negative: known CLSID but unsupported IID on the factory ----
    $pClsidB = New-GuidPtr $CLSID_B_CLASSIC
    $factoryBadIid = [IntPtr]::Zero
    $hrBadIid = $getClassObject.Invoke($pClsidB, $pIidContextMenu, [ref]$factoryBadIid)
    Report (($hrBadIid -eq $E_NOINTERFACE) -and ($factoryBadIid -eq [IntPtr]::Zero)) `
        ("factory rejects non-IClassFactory IID $((FmtHr $hrBadIid))")
    [System.Runtime.InteropServices.Marshal]::FreeHGlobal($pClsidB)

    # ---- negative: aggregation must be refused (CLASS_E_NOAGGREGATION) ----
    $pClsidB2 = New-GuidPtr $CLSID_B_CLASSIC
    $factoryAgg = [IntPtr]::Zero
    $hrAgg = $getClassObject.Invoke($pClsidB2, $pIidClassFactory, [ref]$factoryAgg)
    if ($factoryAgg -ne [IntPtr]::Zero) {
        $createInstance = Get-VtblFn $factoryAgg 3 ([BdShellNative+CreateInstanceFn])
        $dummyOuter = [IntPtr]::Zero
        $instance = [IntPtr]::Zero
        $hrCreate = $createInstance.Invoke($factoryAgg, [IntPtr]0x1, $pIidContextMenu, [ref]$instance)
        Report (($hrCreate -eq $CLASS_E_NOAGGREGATION) -and ($instance -eq [IntPtr]::Zero)) `
            ("aggregation refused $((FmtHr $hrCreate))")
        Release-Unknown $factoryAgg
    }
    else {
        Report $false 'aggregation check skipped (factory not created)'
    }
    [System.Runtime.InteropServices.Marshal]::FreeHGlobal($pClsidB2)

    # ---- unload gate ----
    $hrUnload = $canUnloadNow.Invoke()
    Report ($hrUnload -eq $S_OK) ("DllCanUnloadNow after release $((FmtHr $hrUnload))")

    foreach ($p in @($pIidUnknown, $pIidClassFactory, $pIidShellExtInit, $pIidContextMenu,
                     $pIidExplorerCommand, $pIidEnumExplorerCommand)) {
        [System.Runtime.InteropServices.Marshal]::FreeHGlobal($p)
    }
}
finally {
    [void][BdShellNative]::FreeLibrary($module)
}

# ---- pure-function smoke (optional) ----
$configPath = Join-Path $env:APPDATA 'BetterDesktop\shellmenu.json'
if (Test-Path $configPath) {
    Write-Host "config present: $configPath"
}
else {
    Write-Host "config absent : $configPath (extension shows nothing until the host writes it)"
}

$binRoot = Join-Path $repoRoot 'packages\shell\shell-context-menu\native\build\bin\Release'
$smoke = Join-Path $binRoot 'ShellMenuSmoke.exe'
if (Test-Path $smoke) {
    Write-Host "--- ShellMenuSmoke ---"
    & $smoke | Select-Object -Last 2
    if ($LASTEXITCODE -ne 0) { $failures++ }
}

# ---- menu pipeline probe (config JSON -> HMENU), needs a config to drive it ----
if (-not [string]::IsNullOrWhiteSpace($MenuProbeConfig)) {
    $probe = Join-Path $binRoot 'ShellMenuHostProbe.exe'
    if (-not (Test-Path $probe)) {
        Write-Host "  [FAIL] ShellMenuHostProbe.exe not built" -ForegroundColor Red
        $failures++
    }
    elseif (-not (Test-Path $MenuProbeConfig)) {
        Write-Host "  [FAIL] probe config not found: $MenuProbeConfig" -ForegroundColor Red
        $failures++
    }
    else {
        Write-Host "--- ShellMenuHostProbe (menu pipeline) ---"
        & $probe $MenuProbeConfig
        if ($LASTEXITCODE -ne 0) { $failures++ }
    }
}

Write-Host ''
if ($failures -eq 0) {
    Write-Host "probe-shellmenu: PASS" -ForegroundColor Green
    exit 0
}
Write-Host "probe-shellmenu: FAIL ($failures)" -ForegroundColor Red
exit 1
