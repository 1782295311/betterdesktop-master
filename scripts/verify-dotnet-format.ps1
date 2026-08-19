# 门禁 dotnet-format：代码格式一致性（.editorconfig 唯一来源）
# 规则: dotnet format <sln> --verify-no-changes --no-restore; 有差异即红
. (Join-Path $PSScriptRoot 'lib\common.ps1')
$root = Get-RepoRoot
$sln = Join-Path $root 'BetterDesktop.slnx'
if (-not (Test-Path $sln)) {
    Write-GateFail 'dotnet-format' @('BetterDesktop.slnx — 解决方案文件不存在')
}
$output = & dotnet format $sln --verify-no-changes --no-restore 2>&1 | Out-String
$code = $LASTEXITCODE
if ($code -ne 0) {
    $lines = @($output -split "`r?`n" | Where-Object { $_.Trim() -ne '' })
    $tail = @($lines | Select-Object -Last 15)
    Write-GateFail 'dotnet-format' @("格式与 .editorconfig 不一致（dotnet format --verify-no-changes 退出码 $code）" + $tail)
}
Write-GatePass 'dotnet-format' '代码格式与 .editorconfig 一致'
