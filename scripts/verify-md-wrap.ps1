# 门禁 md-wrap：散文段落必须一段一行物理行（禁止手工硬换行）
# 块分类（简化 GFM 近似，非完整 AST）：
#   跳过: 围栏代码块（以 ``` 或 ~~~ 开头的块）、表格（整块行以 | 开头）、
#         列表（整块行以 -/*/+/数字. 开头）、标题块（以 # 开头）、引用块（以 > 开头）
#   其余块: 非空物理行数 >= 2 即判为硬换行违规
#   已知简化: 段落后紧跟列表/表格行且无空行分隔的块会误报——写法要求列表与段落之间空行（与 GFM 渲染一致）
# 豁免: docs/guard-redproof/**（物证含历史现场）、.agents/notes/archived/（只校验密封，不校验排版）
. (Join-Path $PSScriptRoot 'lib\common.ps1')
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
    if ($relF.StartsWith('docs\guard-redproof\')) { continue }
    $lines = Get-Content $f.FullName
    $block = @()
    $blockLineNo = 0
    $flush = {
        if ($block.Count -gt 0) {
            $nonEmpty = @($block | Where-Object { $_.Trim() -ne '' })
            $skip = $false
            if ($block[0] -match '^\s*(```|~~~)') { $skip = $true }
            elseif (@($block | Where-Object { $_ -notmatch '^\|' }).Count -eq 0) { $skip = $true }
            elseif (@($block | Where-Object { $_ -notmatch '^\s*([-*+]|\d+\.)\s' }).Count -eq 0) { $skip = $true }
            elseif (@($block | Where-Object { $_ -notmatch '^#{1,6}\s' }).Count -eq 0) { $skip = $true }
            elseif (@($block | Where-Object { $_ -notmatch '^>' }).Count -eq 0) { $skip = $true }
            if (-not $skip -and $nonEmpty.Count -gt 1) {
                $script:fails += "$relF`:$blockLineNo — 散文段落被硬换行（应一段一行物理行）"
            }
        }
        $script:block = @()
    }
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line.Trim() -eq '') {
            & $flush
            continue
        }
        if ($block.Count -eq 0) { $blockLineNo = $i + 1 }
        $block += $line
    }
    & $flush
}
if ($fails.Count -gt 0) { Write-GateFail 'md-wrap' $fails }
Write-GatePass 'md-wrap' "检查 $($mdFiles.Count) 个 Markdown 文件，段落均为一段一行"
