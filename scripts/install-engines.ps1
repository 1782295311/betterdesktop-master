# ============================================================
#  BetterDesktop 格式转换引擎安装脚本（随安装包 zip 分发）
#
#  把本目录的 engines\ 复制到 BetterDesktop 安装根目录下的 engines\。
#  前置：已运行 Setup.exe 完成主程序安装（会生成 deployment.json）。
#
#  用法：
#    powershell -ExecutionPolicy Bypass -File install-engines.ps1
#  （右键 -> 使用 PowerShell 运行 亦可）
#
#  NOTE: keep this file pure UTF-8 with BOM (Windows PowerShell 5.1 parses .ps1
#  as ANSI/GBK unless the file has a BOM, so non-ASCII literals break otherwise).
# ============================================================
$ErrorActionPreference = 'Stop'

$product = 'BetterDesktop'
$pointer = Join-Path $env:LOCALAPPDATA "$product\deployment.json"

Write-Host '[engines] BetterDesktop 格式转换引擎安装' -ForegroundColor Cyan

if (-not (Test-Path $pointer)) {
    Write-Host "[engines] 未找到 $pointer" -ForegroundColor Red
    Write-Host '[engines] 请先运行 Setup.exe 完成主程序安装，再执行本脚本。' -ForegroundColor Yellow
    exit 1
}

$installRoot = $null
try {
    $installRoot = [string](Get-Content $pointer -Raw -Encoding UTF8 | ConvertFrom-Json).installRoot
}
catch {
    Write-Host "[engines] deployment.json 读取失败: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
if ([string]::IsNullOrWhiteSpace($installRoot) -or -not (Test-Path $installRoot)) {
    Write-Host "[engines] deployment.json 中的安装根目录无效: $installRoot" -ForegroundColor Red
    exit 1
}

$src = Join-Path $PSScriptRoot 'engines'
if (-not (Test-Path $src)) {
    Write-Host "[engines] 未找到引擎目录: $src" -ForegroundColor Red
    Write-Host '[engines] 请确认本脚本与 engines 文件夹在同一目录（完整解压安装包）。' -ForegroundColor Yellow
    exit 1
}

$dst = Join-Path $installRoot 'engines'
Write-Host "[engines] 源目录: $src"
Write-Host "[engines] 目标:   $dst"

New-Item -ItemType Directory -Path $dst -Force | Out-Null
Copy-Item -Path (Join-Path $src '*') -Destination $dst -Recurse -Force

Write-Host ''
Write-Host '[engines] 安装完成。格式转换 / OCR 功能现已可用。' -ForegroundColor Green
