# 门禁 package-readme：包级 README 强制 + 覆盖棘轮
# 规则:
#   1. packages/ 下每个 *.csproj（排除 obj/bin）同目录必须有 README.md
#   2. README.md 必须含 "## Known Limitations" 小节，且其下至少一条 "- " 列表项
#   3. 棘轮: readme-ratchet.baseline.json 的 allowlist 只能减不能增，LOCKED 双锁必须与文件一致
# 单测: verify-package-readme.Tests.ps1（Pester，覆盖 README 合规判断）
. (Join-Path $PSScriptRoot 'lib\common.ps1')

# 检测逻辑（可单测）：检查单个 README 是否合规，返回违规描述（合规返回 null）
function Get-PackageReadmeViolation([string]$ReadmePath) {
    if (-not (Test-Path $ReadmePath)) { return '缺少 README.md' }
    $content = Get-Content $ReadmePath -Raw
    $m = [regex]::Match($content, '(?m)^## Known Limitations\s*$')
    if (-not $m.Success) { return 'README.md 缺少 ``## Known Limitations`` 小节' }
    $rest = $content.Substring($m.Index + $m.Length)
    if (-not [regex]::IsMatch($rest, '(?m)^\s*-\s+')) { return 'README.md 的 ``## Known Limitations`` 下必须至少一条 ``- `` 列表项' }
    return $null
}

# 门禁主体（dot-source 时跳过）
if ($MyInvocation.InvocationName -ne '.') {
    Write-GateStart 'package-readme'
    $root = Get-RepoRoot
    $manifestPath = Join-Path $root 'scripts\manifests\readme-ratchet.baseline.json'
    $baseline = Get-Content $manifestPath -Raw | ConvertFrom-Json

    # 只扫描 git 跟踪的 csproj（忽略备份/生成目录）
    $relProjects = @(& git -C $root ls-files) | Where-Object { $_ -match '^packages/.*\.csproj$' -or $_ -match '^packages/.*/.*\.csproj$' } | Where-Object { $_ -notmatch '\\(obj|bin)\\' }
    $projects = @()
    foreach ($rel in $relProjects) {
        $full = Join-Path $root $rel
        if (Test-Path $full -PathType Leaf) { $projects += $full }
    }
    $missing = @($projects | Where-Object { -not (Test-Path (Join-Path (Split-Path $_) 'README.md')) })
    $allow = @(@($baseline.allowlist) | ForEach-Object { "$_" })

    $locksBad = @()
    if (@($baseline.allowlist).Count -gt $baseline.'LOCKED-MAX-COUNT') {
        $locksBad += "baseline.json — allowlist 条目数 $(@($baseline.allowlist).Count) 超过 LOCKED-MAX-COUNT $($baseline.'LOCKED-MAX-COUNT')（棘轮禁止增长）"
    }
    if ($baseline.'LOCKED-COUNT' -ne @($baseline.allowlist).Count) {
        $locksBad += "baseline.json — LOCKED-COUNT $($baseline.'LOCKED-COUNT') 与 allowlist 实际条目数 $(@($baseline.allowlist).Count) 不一致"
    }

    foreach ($s in $allow) {
        if ($s -notin @($missing | ForEach-Object { Get-RelPath $_ })) {
            Write-Output "[WARN] $s 已补 README，白名单条目已陈旧，请收窄基线（只减不增）"
        }
    }

    $violations = @($missing | Where-Object { (Get-RelPath $_) -notin $allow })
    $fails = @($locksBad)
    foreach ($p in $violations) {
        $fails += "$(Get-RelPath $p) — 缺少 README.md"
    }
    foreach ($p in $projects) {
        $readme = Join-Path (Split-Path $p) 'README.md'
        $v = Get-PackageReadmeViolation $readme
        if ($null -ne $v) { $fails += "$(Get-RelPath $p) — $v" }
    }
    $fails = @($fails | Select-Object -Unique)
    if ($fails.Count -gt 0) { Write-GateFail 'package-readme' $fails }
    Write-GatePass 'package-readme' "包 $($projects.Count) 个，缺失 README $($missing.Count) 个，白名单 $($allow.Count) 条，违规 $($violations.Count) 条"
}