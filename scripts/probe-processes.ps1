<#
  同名进程的「先分父子、再下结论」探针

  【为什么需要它】2026-09-19 的 S4-3 真机验收里，我在**同一个会话**中两次把「两个同名进程」
  读成"多主拉起 / 未清理的残留" ✗，而两次 `ParentProcessId` 都显示那是**父子链**
  （core → 服务 → 服务自己的子进程）。第二次踩坑时我已经把规则写进计划了 —— 说明
  **"下次记得别忘"不是防线**。故把规则做成**脚本的一步**：同名进程出现多个时，
  本脚本**先打印父链**，再让人判断。

  【判读口径】
    · 链式（A 的父是 B、B 的父是 C）= 正常的父子进程，**不是多主**；
    · 同父并列（两个同名的父进程是同一个）= 才可能是多主拉起，需要查是谁拉的第二份。

  用法：
    pwsh -File scripts/probe-processes.ps1
    pwsh -File scripts/probe-processes.ps1 -Name 'BetterDesktop.DesktopControl.exe'
#>
param([string]$Name = 'BetterDesktop*')

# 【踩过的坑，别再犯】不要写成 WQL 的 `-Filter "Name LIKE 'BetterDesktop*'"`
# —— **WQL 的 LIKE 通配符是 `%` 不是 `*`**，写成 `*` 会**静默返回零条**，
# 而"零条"在探针里读起来像"一切正常"（假阴性比报错更糟：第一次运行本脚本，
# 它对着**正在跑的 core** 报了"没有匹配的进程"）。
# 故不借用 WQL 的通配语义，取全量后用 PowerShell 的 `-like`（`*` 在这里才是通配符）。
$procs = @(Get-CimInstance Win32_Process |
        Where-Object { $_.Name -like $Name } |
        Select-Object ProcessId, ParentProcessId, Name, CreationDate, ExecutablePath)
if ($procs.Count -eq 0) {
    "没有匹配 '$Name' 的进程。"
    # 【自检】"零条"有两种含义：真的没有，或**过滤条件本身写错了**（本文件顶部记着两条这样的教训）。
    # 而"零条"读起来像"一切正常"，所以必须自证。core 几乎总在跑：它在跑而这里说"没有进程" = 脚本错了。
    $core = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like 'betterdesktop-core*' })
    if ($core.Count -gt 0) {
        "⚠️ 自检失败：core 正在跑（PID $($core[0].ProcessId)），本脚本却一个都没匹配到 ⇒ **是过滤条件错了，不是没有进程**。"
        exit 1
    }
    exit 0
}

$byId = @{}
foreach ($p in $procs) { $byId[[int]$p.ProcessId] = $p }

"=== 匹配 '$Name' 的进程 $($procs.Count) 个 ==="
$procs | Sort-Object CreationDate |
    Format-Table ProcessId, ParentProcessId, CreationDate, Name -AutoSize |
    Out-String -Width 200

function Get-Ancestry($p) {
    $chain = @()
    $cur = $p
    $depth = 0
    while ($cur -and $depth -lt 6) {
        $chain += "$($cur.Name)($($cur.ProcessId))"
        # 【踩过的坑，别再犯】不要把这个变量命名为 `$pid` —— 它是 PowerShell 的**只读自动变量**
        #（= 当前进程 ID）。赋值会失败，而失败后 `$cur` 不变 ⇒ 循环原地打转，
        # 最后输出一条**看起来合理但完全错误**的祖先链（实测把 `pwsh.exe` 报成了桌面服务的祖先）。
        # 错的且像真的，比报错危险得多：读的人不会去怀疑一条"链"。
        $parentId = [int]$cur.ParentProcessId
        if ($byId.ContainsKey($parentId)) {
            $cur = $byId[$parentId]
        }
        else {
            $cur = Get-CimInstance Win32_Process -Filter "ProcessId=$parentId" -ErrorAction SilentlyContinue
        }
        $depth++
    }
    return ($chain -join ' <- ')
}

foreach ($g in ($procs | Group-Object Name | Where-Object { $_.Count -gt 1 })) {
    "--- 同名 '$($g.Name)' 有 $($g.Count) 个 → **先看父链再判断** ---"
    foreach ($p in ($g.Group | Sort-Object CreationDate)) {
        "    $($p.ProcessId) 的祖先链: $(Get-Ancestry $p)"
    }
    $parents = @($g.Group | ForEach-Object { [int]$_.ParentProcessId } | Sort-Object -Unique)
    if ($parents.Count -eq 1) {
        "    ⇒ 同名进程**同一个父**（$($parents[0])）：这才可能是多主拉起，查是谁拉的第二份。"
    }
    else {
        "    ⇒ 父各不相同（$($parents -join ', ')）：按上面祖先链看是否为父子/不同来源，**默认不是多主**。"
    }
}
