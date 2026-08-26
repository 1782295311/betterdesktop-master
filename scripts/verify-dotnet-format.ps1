# 门禁 dotnet-format：代码格式一致性（.editorconfig 唯一来源）
# 规则: dotnet format <sln> --verify-no-changes --no-restore; 有差异即红
# 单测: verify-dotnet-format.Tests.ps1（Pester，覆盖退出码判断）
. (Join-Path $PSScriptRoot 'lib\common.ps1')

# 判断逻辑（可单测）：退出码 0 = 格式一致，非 0 = 有差异
function Test-FormatResult([int]$ExitCode) {
    return $ExitCode -eq 0
}

# 门禁主体（dot-source 时跳过）
if ($MyInvocation.InvocationName -ne '.') {
    $sln = Join-Path (Get-RepoRoot) 'BetterDesktop.slnx'
    if (-not (Test-Path $sln)) {
        Write-GateFail 'dotnet-format' @('BetterDesktop.slnx — 解决方案文件不存在')
    }
    $output = & dotnet format $sln --verify-no-changes --no-restore 2>&1 | Out-String
    $code = $LASTEXITCODE
    if (-not (Test-FormatResult $code)) {
        $lines = @($output -split "`r?`n" | Where-Object { $_.Trim() -ne '' })
        $tail = @($lines | Select-Object -Last 15)
        Write-GateFail 'dotnet-format' @("格式与 .editorconfig 不一致（dotnet format --verify-no-changes 退出码 $code）" + $tail)
    }
    Write-GatePass 'dotnet-format' '代码格式与 .editorconfig 一致'
}