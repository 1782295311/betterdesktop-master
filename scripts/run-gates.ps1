# Better Desktop Cordis — 统一门禁入口
# 用法:
#   .\scripts\run-gates.ps1              全部门禁
#   .\scripts\run-gates.ps1 -Filter md   只跑 id 含 "md" 的门禁
#   .\scripts\run-gates.ps1 -List        列出注册表
# 退出码: 0 = 全绿; 1 = 任一门禁失败或依赖失败; 2 = 参数无匹配

param(
    [string]$Filter = '',
    [switch]$List
)

$gates = @(
    [pscustomobject]@{ Id = 'package-readme'; Script = 'verify-package-readme.ps1'; Needs = @() },
    [pscustomobject]@{ Id = 'agent-note';     Script = 'verify-agent-note.ps1';     Needs = @() },
    [pscustomobject]@{ Id = 'archived-notes'; Script = 'verify-archived-notes.ps1'; Needs = @() },
    [pscustomobject]@{ Id = 'md-links';       Script = 'verify-md-links.ps1';       Needs = @() },
    [pscustomobject]@{ Id = 'md-wrap';        Script = 'verify-md-wrap.ps1';        Needs = @() },
    [pscustomobject]@{ Id = 'doc-budgets';    Script = 'verify-doc-budgets.ps1';    Needs = @() },
    [pscustomobject]@{ Id = 'gate-registry';  Script = 'verify-gate-registry.ps1';  Needs = @() },
    [pscustomobject]@{ Id = 'dotnet-format';  Script = 'verify-dotnet-format.ps1';  Needs = @() }
)

if ($List) {
    Write-Output "门禁注册表（$($gates.Count) 道）:"
    $gates | ForEach-Object { Write-Output ("  {0,-16} {1}" -f $_.Id, $_.Script) }
    exit 0
}

$selected = @($gates | Where-Object { $_.Id -like "*$Filter*" })
if ($selected.Count -eq 0) {
    Write-Output "没有匹配 '$Filter' 的门禁。"
    exit 2
}

$results = @{}
$failed = @()
foreach ($g in $selected) {
    $needFail = @($g.Needs | Where-Object { $results[$_] -ne $true })
    if ($needFail.Count -gt 0) {
        Write-Output "[SKIP] $($g.Id) — 依赖门禁未通过: $($needFail -join ', ')"
        $results[$g.Id] = $false
        continue
    }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $gatePath = Join-Path $PSScriptRoot $g.Script
    & pwsh -NoProfile -ExecutionPolicy Bypass -File $gatePath
    $code = $LASTEXITCODE
    $sw.Stop()
    $ok = ($code -eq 0)
    $results[$g.Id] = $ok
    if ($ok) {
        Write-Output "[PASS] $($g.Id) — exit 0（$($sw.ElapsedMilliseconds) ms）"
    } else {
        Write-Output "[FAIL] $($g.Id) — exit $code（$($sw.ElapsedMilliseconds) ms）"
        $failed += $g.Id
    }
}

Write-Output ''
if ($failed.Count -gt 0) {
    Write-Output "门禁失败: $($failed -join ', ')"
    exit 1
}
Write-Output "全部门禁通过（$($selected.Count) 道）。"
exit 0
