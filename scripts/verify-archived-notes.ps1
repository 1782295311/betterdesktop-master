# 门禁 archived-notes：归档决策记录 SHA-256 密封与追加保护
# 规则（详见 .agents/notes/archived/AGENTS.md）:
#   - archived/AGENTS.md 与六个类别目录必须存在
#   - 每个归档 .md 必须登记进 manifest.json（file -> sha256）
#   - 已密封条目: 内容与哈希不符即红；与 HEAD 版本比对，条目不得消失、哈希不得改写（追加保护）
#   - -Write 模式: 为未登记条目追加密封后退出 0（归档的唯一合法写入口）
# 单测: verify-archived-notes.Tests.ps1（Pester，覆盖哈希比对）
param([switch]$Write)
. (Join-Path $PSScriptRoot 'lib\common.ps1')

# 判断逻辑（可单测）：实际哈希与记录哈希是否一致
function Test-SealMatches([string]$ActualHash, [string]$RecordedHash) {
    return $ActualHash -eq $RecordedHash
}

# 门禁主体（dot-source 时跳过）
if ($MyInvocation.InvocationName -ne '.') {
    $root = Get-RepoRoot
    $arch = Join-Path $root '.agents\notes\archived'
    $manifestPath = Join-Path $arch 'manifest.json'
    $classes = @('architecture', 'feature', 'bug-fix', 'process', 'testing', 'simplification')

    $fails = @()
    if (-not (Test-Path (Join-Path $arch 'AGENTS.md'))) { $fails += '.agents/notes/archived/AGENTS.md — 缺少归档说明文件' }
    foreach ($cls in $classes) {
        if (-not (Test-Path (Join-Path $arch $cls))) { $fails += ".agents/notes/archived/$cls — 缺少类别目录" }
    }
    if (-not (Test-Path $manifestPath)) { Write-GateFail 'archived-notes' @('.agents/notes/archived/manifest.json — 缺少密封清单') }

    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schema -ne 1) { $fails += "manifest.json — schema 必须为 1" }

    $files = @(Get-ChildItem $arch -Recurse -Filter '*.md' -File | Where-Object { $_.Name -ne 'AGENTS.md' })
    $recorded = @{}
    foreach ($e in @($manifest.entries)) { $recorded[[string]$e.file] = [string]$e.sha256 }

    foreach ($f in $files) {
        $rel = $f.FullName.Substring($arch.Length + 1).Replace('\', '/')
        $hash = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not $recorded.ContainsKey($rel)) { $fails += "$rel — 未登记进 manifest.json（先运行 -Write 追加密封）" }
        elseif (-not (Test-SealMatches $hash $recorded[$rel])) { $fails += "$rel — 内容与密封哈希不符（归档后禁止修改）" }
    }
    foreach ($rel in $recorded.Keys) {
        if (-not (Test-Path (Join-Path $arch ($rel -replace '/', '\')))) { $fails += "$rel — 已密封条目对应的文件被删除" }
    }

    # 追加保护：与 HEAD 版本比对
    $headManifest = $null
    try {
        $headText = git -C $root show "HEAD:.agents/notes/archived/manifest.json" 2>$null
        if ($headText) { $headManifest = $headText | ConvertFrom-Json }
    } catch { }
    if ($headManifest) {
        $headEntries = @{}
        foreach ($e in @($headManifest.entries)) { $headEntries[[string]$e.file] = [string]$e.sha256 }
        foreach ($rel in $headEntries.Keys) {
            if (-not $recorded.ContainsKey($rel)) { $fails += "$rel — HEAD 中已密封的条目在 manifest 中被移除（追加保护）" }
            elseif (-not (Test-SealMatches $recorded[$rel] $headEntries[$rel])) { $fails += "$rel — 已密封条目的哈希被改写（追加保护）" }
        }
    }

    if ($Write) {
        $newEntries = @(@($manifest.entries) | ForEach-Object { $_ })
        foreach ($f in $files) {
            $rel = $f.FullName.Substring($arch.Length + 1).Replace('\', '/')
            if (-not $recorded.ContainsKey($rel)) {
                $newEntries += [pscustomobject]@{ file = $rel; sha256 = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
            }
        }
        $out = [ordered]@{ schema = 1; entries = @($newEntries) }
        $out | ConvertTo-Json -Depth 4 | Set-Content -Path $manifestPath -Encoding UTF8
        Write-Output "[PASS] archived-notes -Write — 已追加密封，清单共 $(@($newEntries).Count) 条"
        exit 0
    }

    if ($fails.Count -gt 0) { Write-GateFail 'archived-notes' $fails }
    Write-GatePass 'archived-notes' "归档密封清单 $(@($manifest.entries).Count) 条，全部哈希一致"
}