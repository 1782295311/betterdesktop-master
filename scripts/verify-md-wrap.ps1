# 门禁 md-wrap：散文段落必须一段一行物理行（禁止手工硬换行）
# 块分类（简化 GFM 近似，非完整 AST）：
#   跳过: 围栏代码块（``` 或 ~~~）、表格（| 开头）、列表（-/*/+/数字.）、标题（#）、引用（>）
#   其余块: 非空物理行数 >= 2 即判为硬换行违规
# 豁免: .agents/notes/archived/（只校验密封，不校验排版）
# 单测: verify-md-wrap.Tests.ps1（Pester，覆盖块分类与硬换行检测）
. (Join-Path $PSScriptRoot 'lib\common.ps1')

# 检测逻辑（可单测）：一个块是否为需判违规的「散文硬换行块」
function Test-HardWrapBlock([string[]]$Block) {
    if ($Block.Count -eq 0) { return $false }
    $nonEmpty = @($Block | Where-Object { $_.Trim() -ne '' })
    if ($nonEmpty.Count -le 1) { return $false }
    if ($Block[0] -match '^\s*(```|~~~)') { return $false }
    if (@($Block | Where-Object { $_ -notmatch '^\|' }).Count -eq 0) { return $false }
    if (@($Block | Where-Object { $_ -notmatch '^\s*([-*+]|\d+\.)\s' }).Count -eq 0) { return $false }
    if (@($Block | Where-Object { $_ -notmatch '^#{1,6}\s' }).Count -eq 0) { return $false }
    if (@($Block | Where-Object { $_ -notmatch '^>' }).Count -eq 0) { return $false }
    return $true
}

# 检测逻辑（可单测）：内容中硬换行违规的段落数
function Get-HardWrappedParagraphCount([string]$Content) {
    $lines = @($Content -split "`r?`n")
    $count = 0
    $block = @()
    $inFence = $false
    foreach ($line in $lines) {
        if ($line -match '^\s*(```|~~~)') {
            if (Test-HardWrapBlock $block) { $count++ }
            $block = @()
            $inFence = -not $inFence
            continue
        }
        if ($inFence) { continue }
        if ($line.Trim() -eq '') {
            if (Test-HardWrapBlock $block) { $count++ }
            $block = @()
            continue
        }
        $block += $line
    }
    if (Test-HardWrapBlock $block) { $count++ }
    return $count
}

# 门禁主体（dot-source 时跳过）
if ($MyInvocation.InvocationName -ne '.') {
    $root = Get-RepoRoot
    $fails = @()
    $mdFiles = @()
    # 只扫描 git 跟踪的 Markdown（忽略备份/生成目录），并豁免归档 notes
    $tracked = @(& git -C $root ls-files) | Where-Object { $_ -like '*.md' }
    foreach ($rel in $tracked) {
        if ($rel -match '(^|/|\\)archived(/|\\|$)') { continue }
        # 历史规划/性能记录文档豁免（一阶段收口前的产物，不追责硬换行）
        if ($rel -match '^docs/(plans|performance)/') { continue }
        $full = Join-Path $root $rel
        if (-not (Test-Path $full -PathType Leaf)) { continue }
        $mdFiles += $full
    }
    $mdFiles = @($mdFiles | Sort-Object -Unique)

    foreach ($f in $mdFiles) {
        $relF = Get-RelPath $f
        $content = Get-Content $f -Raw
        $n = Get-HardWrappedParagraphCount $content
        if ($n -gt 0) { $fails += "$relF — 散文段落被硬换行（应一段一行物理行）" }
    }

    if ($fails.Count -gt 0) { Write-GateFail 'md-wrap' $fails }
    Write-GatePass 'md-wrap' "检查 $($mdFiles.Count) 个 Markdown 文件，段落均为一段一行"
}