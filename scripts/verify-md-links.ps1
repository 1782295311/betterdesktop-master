# 门禁 md-links：Markdown 相对链接与 #锚点 完整性
# 规则:
#   - 扫描范围: 根 *.md、docs/**、packages/**、scripts/**、.agents/notes/**（活跃区）
#   - 豁免: .agents/notes/archived/（只校验密封，不校验链接）
#   - 跳过: http(s)/mailto 外链、代码块（``` 与 ~~~ 围栏内）、纯 # 自锚
#   - 失败: 目标文件不存在 / 链接越出仓库 / 锚点标题不存在
# 单测: verify-md-links.Tests.ps1（Pester，覆盖 slug 归一化）
. (Join-Path $PSScriptRoot 'lib\common.ps1')

# 检测逻辑（可单测）：标题 → slug（去特殊字符、空格转 -、转小写）
function Get-Slug([string]$heading) {
    $h = $heading.Trim() -replace '[`*_]', ''
    $h = $h -replace '\s+', '-' -replace '[^\p{L}\p{N}\-]', ''
    return $h.ToLowerInvariant()
}

# 门禁主体（dot-source 时跳过）
if ($MyInvocation.InvocationName -ne '.') {
    $root = Get-RepoRoot
    $fails = @()
    $mdFiles = @()
    $mdFiles += @(Get-ChildItem $root -Filter '*.md' -File -ErrorAction SilentlyContinue)
    foreach ($dir in @('docs', 'packages', 'scripts', '.agents\notes')) {
        $mdFiles += @(Get-ChildItem (Join-Path $root $dir) -Recurse -Filter '*.md' -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\archived\\' })
    }
    $mdFiles = @($mdFiles | Sort-Object FullName -Unique)

    foreach ($f in $mdFiles) {
        $relF = Get-RelPath $f.FullName
        $lines = Get-Content $f.FullName
        $inFence = $false
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]
            if ($line -match '^\s*(```|~~~)') { $inFence = -not $inFence; continue }
            if ($inFence) { continue }
            foreach ($m in [regex]::Matches($line, '\[[^\]]*\]\(([^) ]+)\)')) {
                $target = $m.Groups[1].Value
                if ($target -match '^(https?:|mailto:|<|#)') { continue }
                if ($target -match '^(/|[A-Za-z]:)') {
                    $fails += "$relF`:$($i + 1) $target — 禁止绝对/仓库外链接"
                    continue
                }
                $parts = $target -split '#', 2
                $pathPart = $parts[0]
                $frag = if ($parts.Count -gt 1) { $parts[1] } else { $null }
                if ($pathPart -eq '') {
                    if ($frag) {
                        $heads = @($lines | Where-Object { $_ -match '^#{1,6}\s+' })
                        $slugs = @($heads | ForEach-Object { Get-Slug (($_ -replace '^#{1,6}\s*', '').Trim()) })
                        if ($frag -notin $slugs) { $fails += "$relF`:$($i + 1) #$frag — 本文件内锚点不存在" }
                    }
                    continue
                }
                $resolved = [System.IO.Path]::GetFullPath((Join-Path $f.DirectoryName $pathPart))
                if (-not $resolved.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $fails += "$relF`:$($i + 1) $target — 链接越出仓库"
                    continue
                }
                if (-not (Test-Path $resolved -PathType Leaf)) {
                    $fails += "$relF`:$($i + 1) $target — 目标文件不存在"
                    continue
                }
                if ($frag) {
                    $targetHeads = @(Get-Content $resolved | Where-Object { $_ -match '^#{1,6}\s+' })
                    $slugs = @($targetHeads | ForEach-Object { Get-Slug (($_ -replace '^#{1,6}\s*', '').Trim()) })
                    if ($frag -notin $slugs) { $fails += "$relF`:$($i + 1) $target — 锚点 #$frag 在目标中不存在" }
                }
            }
        }
    }

    if ($fails.Count -gt 0) { Write-GateFail 'md-links' $fails }
    Write-GatePass 'md-links' "检查 $($mdFiles.Count) 个 Markdown 文件，链接与锚点全部有效"
}