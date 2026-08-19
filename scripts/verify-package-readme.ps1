# 门禁 package-readme：包级 README 强制 + 覆盖率棘轮
# 规则:
#   1. packages/ 下每个 *.csproj（排除 obj/bin）同目录必须有 README.md
#   2. README.md 必须含 "## Known Limitations" 小节，且其下至少一条 "- " 列表项
#   3. 棘轮: scripts/manifests/readme-ratchet.baseline.json 的 allowlist（允许缺失清单）只能减不能增，
#      LOCKED-MAX-COUNT / LOCKED-COUNT 双锁必须与文件一致
. (Join-Path $PSScriptRoot 'lib\common.ps1')
$root = Get-RepoRoot
$manifestPath = Join-Path $root 'scripts\manifests\readme-ratchet.baseline.json'
$baseline = Get-Content $manifestPath -Raw | ConvertFrom-Json

$projects = @(Get-ChildItem (Join-Path $root 'packages') -Recurse -Filter '*.csproj' -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' })
$missing = @($projects | Where-Object { -not (Test-Path (Join-Path $_.DirectoryName 'README.md')) })
$allow = @(@($baseline.allowlist) | ForEach-Object { "$_" })

# 双锁校验
$locksBad = @()
if (@($baseline.allowlist).Count -gt $baseline.'LOCKED-MAX-COUNT') {
    $locksBad += "baseline.json — allowlist 条目数 $(@($baseline.allowlist).Count) 超过 LOCKED-MAX-COUNT $($baseline.'LOCKED-MAX-COUNT')（棘轮禁止增长）"
}
if ($baseline.'LOCKED-COUNT' -ne @($baseline.allowlist).Count) {
    $locksBad += "baseline.json — LOCKED-COUNT $($baseline.'LOCKED-COUNT') 与 allowlist 实际条目数 $(@($baseline.allowlist).Count) 不一致"
}

# 陈旧白名单条目（已补 README 但未收窄基线）只警告不放行
foreach ($s in $allow) {
    if ($s -notin @($missing | ForEach-Object { Get-RelPath $_.FullName })) {
        Write-Output "[WARN] $s 已补 README，白名单条目已陈旧，请收窄基线（只减不增）"
    }
}

$violations = @($missing | Where-Object { (Get-RelPath $_.FullName) -notin $allow })
$fails = @($locksBad)
foreach ($p in $violations) {
    $fails += "$(Get-RelPath $p.FullName) — 缺少 README.md"
}
foreach ($p in $projects) {
    $readme = Join-Path $p.DirectoryName 'README.md'
    if (Test-Path $readme) {
        $content = Get-Content $readme -Raw
        $m = [regex]::Match($content, '(?m)^## Known Limitations\s*$')
        if ($m.Success) {
            $rest = $content.Substring($m.Index + $m.Length)
            if (-not [regex]::IsMatch($rest, '(?m)^\s*-\s+')) {
                $fails += "$(Get-RelPath $p.FullName) — README.md 的 ``## Known Limitations`` 下必须至少一条 ``- `` 列表项"
            }
        } else {
            $fails += "$(Get-RelPath $p.FullName) — README.md 缺少 ``## Known Limitations`` 小节"
        }
    }
}
$fails = @($fails | Select-Object -Unique)
if ($fails.Count -gt 0) { Write-GateFail 'package-readme' $fails }
Write-GatePass 'package-readme' "包 $($projects.Count) 个，缺失 README $($missing.Count) 个，白名单 $($allow.Count) 条，违规 $($violations.Count) 条"
