# ============================================================
#  BetterDesktop publish script
#
#  Produces a SINGLE deployment folder containing every component
#  (Host / Cli / Tray / Updater / Watchdog / Recovery) plus:
#    version.json   - single source of truth for "what is this build"
#    manifest.json  - release manifest (path + size + sha256 of every file)
#                     -> consumed by BetterDesktop.Updater --check/--download
#
#  Deployment rule (docs/build-release.md): Cli must live in the SAME folder as Host,
#  so all components are merged into one output folder here.
#
#  NOTE: this file is intentionally ASCII-only. Windows PowerShell 5.1 parses .ps1 as
#  ANSI/GBK unless the file has a BOM, which breaks non-ASCII literals at parse time.
#
#  Usage:
#    powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 [-Configuration Release]
# ============================================================
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [string]$OutRoot = 'dist',
    [string]$Channel = 'stable',
    [string]$Notes = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Component -> project path. Order matters: Host first (brings the bulk of dependencies),
# the rest overwrite with their own outputs. All must land in the SAME folder.
$components = [ordered]@{
    # 2026-09-18 启动器：出货名 BetterDesktop.exe（用户双击的那个入口）。
    # 放最前：它只额外带来 BetterDesktop.exe/.dll/.deps.json/.runtimeconfig.json 与图标，
    # 共享程序集随后被 Host 的那份覆盖（同一版本，覆盖顺序只影响"哪一份更权威"）。
    'Launcher' = 'launcher\BetterDesktop.Launcher.csproj'
    'Host'     = 'host\BetterDesktop.Host.csproj'
    'Cli'      = 'BetterDesktop.Cli\BetterDesktop.Cli.csproj'
    # 2026-09-17 桌面控制独立化：主程序没跑时「桌面控制」菜单靠它（原生扩展 → Cli --menu-batch → 本 exe）。
    # 必须与 Host/Cli 同目录：CLI 按 AppContext.BaseDirectory 定位它。
    'DesktopControl' = 'packages\shell\shell-desktop-control\BetterDesktop.DesktopControl.csproj'
    # 2026-09-17 托盘=中转站：设置中心独立进程（不依赖 Host 打开，关窗即退省内存）。
    'Settings' = 'packages\shell\shell-settings-host\BetterDesktop.Settings.csproj'
    'Tray'     = 'tray\BetterDesktop.Tray.csproj'
    'Updater'  = 'updater\BetterDesktop.Updater.csproj'
    'Recovery' = 'recovery\BetterDesktop.Recovery.csproj'
}

# Files that MUST exist in the final folder, otherwise the release is broken.
$required = @(
    # 2026-09-18：启动器 = 用户唯一入口（拉起全部组件 + 自检修复 + 首次偏好）。
    # 与 install-betterdesktop.ps1 的 $required 逐字同步（门禁 verify-system-integration 会校验）。
    'BetterDesktop.exe',
    'BetterDesktop.Host.exe',
    'BetterDesktop.Cli.exe',
    'BetterDesktop.DesktopControl.exe',
    'BetterDesktop.Settings.exe',
    'BetterDesktop.Tray.exe',
    'BetterDesktop.Updater.exe',
    'BetterDesktop.Recovery.exe',
    # 2026-09-17：Rust 转换引擎（native/convert-engine 的 cargo 产物，经 shell-convert 的 Content 随构建落位）。
    # 放进必检清单 = 缺了就**发布失败**（本文件纪律："a half release must never ship"）——
    # 此前它不在任何清单里，dev/发布目录一律缺 → 转换功能点开就弹"Rust 转换引擎未部署"。
    'convert-engine.exe',
    # 原生右键扩展（B 路 in-proc COM + A 路 IExplorerCommand 同一枚 DLL）。
    # 缺了它就是"右键菜单注册成功但没有菜单"——半成品，必须在发布门禁拦住。
    'native\BetterDesktopShellMenu.dll',
    'cordis.yml',
    'agent.yml',
    # 2026-09-17 安装器级：安装/卸载脚本随包分发。
    # 卸载必须能在"托盘自己也在被删目录里"的情况下完成，所以卸载器必须是本进程之外的脚本；
    # 托盘「卸载 BetterDesktop…」按组件目录找它（tray/AppPaths.UninstallScript）。
    # 放进必检清单 = 缺了就发布失败（否则用户拿到的是"卸载不了"的包）。
    'install-betterdesktop.ps1',
    'uninstall-betterdesktop.ps1'
)

function Fail([string]$message) {
    Write-Host "PUBLISH FAILED: $message" -ForegroundColor Red
    exit 1
}

Write-Host "=== BetterDesktop publish ($Configuration/$Platform) ===" -ForegroundColor Cyan
Write-Host "repo root: $root"

$stamp = (Get-Date).ToUniversalTime().ToString('yyyy.MM.dd.HHmm')
$staging = Join-Path $root "Temp\publish-staging"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null

$finalDir = $null
foreach ($name in $components.Keys) {
    $proj = Join-Path $root $components[$name]
    if (-not (Test-Path $proj)) { Fail "component project missing: $proj" }

    $out = Join-Path $staging $name
    Write-Host "--- publishing $name -> $out"
    # 2026-09-18 方案 A：**自包含发布**（-r win-x64 -p:SelfContained=true）。
    # 此前是框架依赖（runtimeconfig 声明 Microsoft.WindowsDesktop.App 8.0.0，包里没有 coreclr.dll /
    # hostfxr.dll / PresentationFramework.dll）→ 目标机器必须先装 .NET 8 桌面运行时，否则连界面都出不来。
    # 用户目标："拷到别的电脑就能百分百用" → 把运行时打进包，彻底去掉这一项外部前置。
    # 注意：.NET 6+ 起"只给 RID"**不再**隐含自包含，必须显式 -p:SelfContained=true。
    & dotnet publish $proj -c $Configuration -p:Platform=$Platform -r win-x64 -p:SelfContained=true -o $out --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed for $name (exit $LASTEXITCODE)" }
    if (-not (Test-Path $out)) { Fail "publish output missing for $name" }
}

# Merge: Host first (bulk), then everything else overwrites.
$finalDir = Join-Path $root "$OutRoot\BetterDesktop-$stamp"
if (Test-Path $finalDir) { Remove-Item $finalDir -Recurse -Force }
New-Item -ItemType Directory -Path $finalDir -Force | Out-Null

foreach ($name in $components.Keys) {
    $out = Join-Path $staging $name
    Copy-Item -Path (Join-Path $out '*') -Destination $finalDir -Recurse -Force
}

# Installer scripts ship with the release (see $required below): the uninstaller MUST be a separate
# script, because it deletes the very folder the tray runs from.
foreach ($scriptName in @('install-betterdesktop.ps1', 'uninstall-betterdesktop.ps1')) {
    $scriptPath = Join-Path $root "scripts\$scriptName"
    if (-not (Test-Path $scriptPath)) { Fail "installer script missing: $scriptPath" }
    Copy-Item $scriptPath -Destination $finalDir -Force
}

# Cleanup: no PDB / dev leftovers in the release folder.
Get-ChildItem -Path $finalDir -Recurse -Include *.pdb,*.xml -File -ErrorAction SilentlyContinue | Remove-Item -Force

# ---- component completeness gate (fail loudly: a half release must never ship) ----
$missing = @()
foreach ($file in $required) {
    if (-not (Test-Path (Join-Path $finalDir $file))) { $missing += $file }
}
if ($missing.Count -gt 0) {
    Fail ("release folder is incomplete, missing: " + ($missing -join ', '))
}

# ---- version.json: single source of truth (generated from the built Host, never hand-edited) ----
$hostExe = Join-Path $finalDir 'BetterDesktop.Host.exe'
$fileVersion = (Get-Item $hostExe).VersionInfo.FileVersion
$informational = (Get-Item $hostExe).VersionInfo.ProductVersion
if (-not $fileVersion) { $fileVersion = $stamp }
$prodVersion = '1.3.0'
if ($informational -match '^([0-9]+\.[0-9]+\.[0-9]+)') { $prodVersion = $Matches[1] }

$publishedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$versionJson = [ordered]@{
    product       = 'BetterDesktop'
    version       = $prodVersion
    build         = $fileVersion
    informational = $informational
    channel       = $Channel
    publishedAt   = $publishedAt
}
# 不要用 Set-Content -Encoding UTF8：PS5.1 会写入 BOM（EF BB BF），
# 而 HTTP 源取回字符串时 BOM 会成为 JSON 的首字符 → JsonSerializer 报
# "'0xEF' is an invalid start of a value"，HTTP(S) 更新通道必失败（本地路径因 ReadAllText 剥 BOM 才正常）。
# 这里显式写无 BOM UTF-8。
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText(
    (Join-Path $finalDir 'version.json'),
    ($versionJson | ConvertTo-Json -Depth 4),
    $utf8NoBom)

# ---- manifest.json: every file + size + sha256 (self excluded) ----
# 【2026-09-18 实测修复（两处）】
#   ① 用 List 而不是 `$files += ...`：后者在 2 万+ 文件上是 O(n²)（每次追加都整数组复制），
#      实测这一步要跑数分钟，长到"看起来像卡死"。
#   ② 单个文件哈希失败**不再中止整个发布**：杀软/索引器短暂持有文件会让 Get-FileHash 抛异常，
#      而 $ErrorActionPreference='Stop' 会把这唯一一次异常放大成"整个发布在最后一步静默中止"
#      —— 本机实测踩过两次：文件合并好了、version.json 也写了，却没有 manifest.json。
#      改为重试 3 次，仍失败才终止（保持"半成品不得出厂"的纪律，同时容忍瞬时占用）。
$files = [System.Collections.Generic.List[object]]::new()
Get-ChildItem -Path $finalDir -Recurse -File | Sort-Object FullName | ForEach-Object {
    if ($_.Name -eq 'manifest.json') { return }
    $rel = $_.FullName.Substring($finalDir.Length).TrimStart('\')
    $sha = $null
    for ($attempt = 1; $attempt -le 3 -and $null -eq $sha; $attempt++) {
        try {
            $sha = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        catch {
            if ($attempt -eq 3) {
                Fail "manifest: unable to hash after 3 attempts: $rel - $($_.Exception.Message)"
            }
            Start-Sleep -Milliseconds 250
        }
    }

    $files.Add([ordered]@{
        path   = $rel
        size   = $_.Length
        sha256 = $sha
    })
}

$manifest = [ordered]@{
    product     = 'BetterDesktop'
    version     = $prodVersion
    build       = $fileVersion
    channel     = $Channel
    publishedAt = $publishedAt
    minHost     = $prodVersion
    notes       = $Notes
    fileCount   = $files.Count
    files       = $files
}
[IO.File]::WriteAllText(
    (Join-Path $finalDir 'manifest.json'),
    ($manifest | ConvertTo-Json -Depth 6),
    $utf8NoBom)

Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

$sizeMb = [int](((Get-ChildItem $finalDir -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB)
Write-Host ''
Write-Host "=== PUBLISH OK ===" -ForegroundColor Green
Write-Host "folder  : $finalDir"
Write-Host "version : $prodVersion  build=$fileVersion  channel=$Channel"
Write-Host "files   : $($files.Count)  total=${sizeMb}MB"
Write-Host "manifest: $(Join-Path $finalDir 'manifest.json')"
Write-Host ''
Write-Host "Next: host this folder (or copy it to a share/web root) and run:"
Write-Host "  BetterDesktop.Updater.exe --check --source <that folder or URL>"
