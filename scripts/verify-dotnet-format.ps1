# 门禁 dotnet-format：代码格式一致性（.editorconfig 唯一来源），基线棘轮模式
# 规则: dotnet format <sln> --verify-no-changes --no-restore
#   - 违规集合相对基线（scripts/manifests/dotnet-format-baseline.txt，格式"相对路径:行号"）比对：
#     仅**新增**违规红（既有长尾不阻塞，真违规不淹没在噪声里——P2 评审项）。
#   - 新文件/新代码必须零违规（基线不覆盖它们，天然强制）。
# 单测: verify-dotnet-format.Tests.ps1（Pester，覆盖退出码判断与新增违规判定）
. (Join-Path $PSScriptRoot 'lib\common.ps1')

# 判断逻辑（可单测）：退出码 0 = 格式一致，非 0 = 有差异
function Test-FormatResult([int]$ExitCode) {
    return $ExitCode -eq 0
}

# 从 dotnet format 输出提取违规（相对路径:行号），路径统一正斜杠（可单测）
function Get-FormatViolations([string]$Output, [string]$RepoRoot) {
    $result = @()
    foreach ($m in [regex]::Matches($Output, '([A-Za-z]:[^\(\)\r\n]+\.cs)\((\d+),\d+\):\s*error')) {
        $full = $m.Groups[1].Value
        $line = $m.Groups[2].Value
        if ($full.StartsWith($RepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            $rel = $full.Substring($RepoRoot.Length).TrimStart('/', '\').Replace('\', '/')
        } else {
            $rel = $full
        }
        $result += "$rel`:$line"
    }
    return ($result | Sort-Object -Unique)
}

# 新增违规判定（可单测）：当前违规中不在基线内的子集
function Get-NewViolations([string[]]$Current, [string[]]$Baseline) {
    $baseline = @($Baseline)
    return @($Current | Where-Object { $_ -notin $baseline })
}

# 门禁主体（dot-source 时跳过）
if ($MyInvocation.InvocationName -ne '.') {
    Write-GateStart 'dotnet-format'
    $sln = Join-Path (Get-RepoRoot) 'BetterDesktop.slnx'
    $baselinePath = Join-Path $PSScriptRoot 'manifests\dotnet-format-baseline.txt'
    if (-not (Test-Path $sln)) {
        Write-GateFail 'dotnet-format' @('BetterDesktop.slnx — 解决方案文件不存在')
    }
    if (-not (Test-Path $baselinePath)) {
        Write-GateFail 'dotnet-format' @('基线清单缺失：scripts/manifests/dotnet-format-baseline.txt')
    }

    $repoRoot = Get-RepoRoot
    $output = & dotnet format $sln --verify-no-changes --no-restore 2>&1 | Out-String
    $code = $LASTEXITCODE

    if (Test-FormatResult $code) {
        Write-GatePass 'dotnet-format' '代码格式与 .editorconfig 一致'
        exit 0
    }

    # 有差异 → 棘轮比对：只对新增违规红
    $baseline = @(Get-Content $baselinePath | Where-Object { $_.Trim() -ne '' -and -not $_.StartsWith('#') })
    $current = @(Get-FormatViolations $output $repoRoot)
    $new = @(Get-NewViolations $current $baseline)

    if ($new.Count -gt 0) {
        $detail = @($new | Select-Object -First 20)
        if ($new.Count -gt 20) { $detail += "…（共 $($new.Count) 处新增违规）" }
        Write-GateFail 'dotnet-format' @("新增格式违规 $($new.Count) 处（基线内 $($current.Count - $new.Count) 处既有不计）" + $detail)
        exit 1
    }

    Write-GatePass 'dotnet-format' "既有格式违规 $($current.Count) 处均在基线内（无新增；棘轮基线：$baselinePath）"
    exit 0
}
