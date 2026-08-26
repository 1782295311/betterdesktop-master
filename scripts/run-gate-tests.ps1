# 运行所有门禁单测（Pester）
# 用法: pwsh -NoProfile -ExecutionPolicy Bypass -File scripts\run-gate-tests.ps1
# 对应 DeepSeek Harness 的 pnpm test（跑所有 *.spec.ts，含门禁 spec）；此脚本跑全部 verify-*.Tests.ps1
Import-Module Pester
$tests = @(Get-ChildItem $PSScriptRoot -Filter 'verify-*.Tests.ps1' -File)
if ($tests.Count -eq 0) {
    Write-Output '[FAIL] 未找到任何门禁单测（verify-*.Tests.ps1）'
    exit 1
}
$result = Invoke-Pester -Script @($tests | ForEach-Object { $_.FullName }) -PassThru
if ($result.FailedCount -gt 0) { exit 1 }
exit 0