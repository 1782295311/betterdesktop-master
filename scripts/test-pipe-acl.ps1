# test-pipe-acl.ps1 —— 控制管道 ACL 的功能与静态验证
#
# 【为什么必须有这个脚本】ACL 写错了**运行期不会报错**，只会"看起来生效了但实际没拦"。
# 而且有一个极易踩的假验证：
#
#   ❌ 用 CreateRestrictedToken(DISABLE_MAX_PRIVILEGE) 建的进程**用户 SID 不变**，
#      仍然会被"只允许当前用户 SID"的 ACL 放行 —— 测出来的绿色是假的。
#
# 所以真验必须换**另一个用户账户**（方案 A），这也意味着需要管理员权限。
# 没有管理员时**不允许静默通过**，必须显式声明"功能验证被跳过"。
#
# 三档（与计划 §7 S2.5 一致）：
#   方案 C · 静态验证      —— 始终执行，不需要任何权限：断言 ACL 被真的传入、DENY 规则存在、校验在读取前
#   正向功能验证           —— 需要 core 在跑：当前用户应能连上并拿到 status 响应
#   方案 A · 真验低权限用户 —— 需要管理员：建临时用户 → 以该用户连接 → 断言**被拒**
#
# 退出码：0 = 全部执行的项通过；1 = 有失败项；2 = 关键项通过但**方案 A 被跳过**（CI 可据此区分）
#
# 用法：
#   pwsh -NoProfile -ExecutionPolicy Bypass scripts\test-pipe-acl.ps1
#   pwsh -NoProfile -ExecutionPolicy Bypass scripts\test-pipe-acl.ps1 -SkipFunctional

[CmdletBinding()]
param(
    # 跳过"当前用户正向连通"检查（core 没在跑时用）
    [switch]$SkipFunctional,
    # 临时测试用户名（固定前缀便于清理）
    [string]$TestUser = 'BdPipeTest',
    [string]$TestPassword = 'Bd@PipeTest12345!'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\common.ps1')

# 【2026-09-19】本脚本测的是 **core 的控制管道**（`@ctl` 请求/应答 + ACL + 调用者校验）——
# 名字分开后 core 独占 `BetterDesktop.MenuCmd`；Host 的 legacy 服务端已改为 `BetterDesktop.HostCmd`。
# 两者归属见 protocols/bdmc1-test-vectors.json 的 `_routing`。**故此处刻意保持 MenuCmd。**
$script:PipeName = 'BetterDesktop.MenuCmd'
$script:Failures = @()
$script:Skipped = @()

function Write-Section([string]$Title) { Write-Host "== $Title" -ForegroundColor Cyan }
function Write-Ok([string]$Message) { Write-Host "  [OK] $Message" -ForegroundColor Green }
function Write-Skip([string]$Message) {
    Write-Host "  [WARN] $Message" -ForegroundColor Yellow
    $script:Skipped += $Message
}
function Write-Bad([string]$Message) {
    Write-Host "  [FAIL] $Message" -ForegroundColor Red
    $script:Failures += $Message
}

# ─────────────── 单个客户端请求（当前用户身份） ───────────────
# 返回 @{ Connected = bool; Response = string; Error = string }
function Invoke-PipeRequest([string]$Request, [int]$TimeoutMs = 2000) {
    $result = @{ Connected = $false; Response = $null; Error = $null }
    try {
        $client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $script:PipeName, [System.IO.Pipes.PipeDirection]::InOut)
        try {
            $client.Connect($TimeoutMs)
            $result.Connected = $true
            $writer = New-Object System.IO.StreamWriter($client)
            $writer.AutoFlush = $true
            $writer.WriteLine($Request)
            $reader = New-Object System.IO.StreamReader($client)
            $result.Response = $reader.ReadLine()
        } finally {
            $client.Dispose()
        }
    } catch {
        $result.Error = $_.Exception.Message
    }
    return $result
}

# ─────────────── 方案 C：静态验证（无需权限） ───────────────
function Test-PipeAclStatic([string]$RepoRoot) {
    Write-Section '方案 C · 静态验证（ACL 是否被真的用上）'

    $pipePath = Join-Path $RepoRoot 'core\src\pipe.rs'
    $secPath = Join-Path $RepoRoot 'core\src\security.rs'
    foreach ($p in @($pipePath, $secPath)) {
        if (-not (Test-Path $p -PathType Leaf)) {
            Write-Bad "$p 不存在（无法做静态验证）"
            return
        }
    }

    $pipe = Get-Content $pipePath -Raw
    $sec = Get-Content $secPath -Raw
    # 去除注释后再做"禁用手段"扫描：文档里**解释**为什么不能用某手段，不应该被判成"用了它"
    $secCode = (($sec -split "`n") | Where-Object { $_.TrimStart() -notmatch '^//' }) -join "`n"

    # 1. 创建管道时必须把安全属性传进去
    if ($pipe -match 'attrs\.as_ref\(\)\.map\(\|a\| a as \*const _\)') {
        Write-Ok '创建管道时传入了 SECURITY_ATTRIBUTES（CreateNamedPipeW 的 lpSecurityAttributes）'
    } else {
        Write-Bad 'core/src/pipe.rs 未把 SECURITY_ATTRIBUTES 传给 CreateNamedPipeW —— ACL 不会被应用'
    }

    # 2. 必须有 DENY 条目（拒绝 Anonymous / Network）
    if ($sec -match 'DENY_ACCESS' -and $sec -match 'WinAnonymousSid' -and $sec -match 'WinNetworkSid') {
        Write-Ok 'DACL 含 DENY（anonymous S-1-5-7 / network S-1-5-2）'
    } else {
        Write-Bad 'DACL 缺少 DENY(anonymous/network) —— 仅靠不 GRANT 不足以防冒用'
    }

    # 3. DENY 必须排在 GRANT 之前（顺序写反 DENY 不生效，且**运行期不报错**）
    $denyIdx = $sec.IndexOf('DENY_ACCESS')
    $grantIdx = $sec.IndexOf('GRANT_ACCESS')
    if ($denyIdx -ge 0 -and $grantIdx -ge 0 -and $denyIdx -lt $grantIdx) {
        Write-Ok 'DENY 条目排在 GRANT 之前（顺序红线）'
    } else {
        Write-Bad 'DENY 未排在 GRANT 之前 —— SetEntriesInAclW 按数组顺序写入，顺序反了 DENY 不生效'
    }

    # 4. 调用者校验必须存在，且必须是**能在读数据前完成**的机制
    #    （ImpersonateNamedPipeClient 实测要求管道上已有数据 → 无法满足"读取前校验"，故不作为判据）
    if ($sec -match 'GetNamedPipeClientProcessId' -and $sec -match 'OpenProcessToken' -and $sec -match 'TokenSessionId') {
        Write-Ok '存在调用者校验（按 PID 取进程令牌 + 用户 SID + 会话 ID 比对，可在读取前完成）'
    } else {
        Write-Bad '缺少"读取前可完成"的调用者校验（需要 GetNamedPipeClientProcessId + 令牌 SID/会话比对）'
    }

    # 5. 校验必须在**读任何消息之前**（物理位置：serve_client 的前几行）
    $serveIdx = $pipe.IndexOf('fn serve_client')
    $validateIdx = $pipe.IndexOf('validate_client', $serveIdx)
    $readIdx = $pipe.IndexOf('read_line_bounded', $serveIdx)
    if ($serveIdx -ge 0 -and $validateIdx -gt $serveIdx -and $readIdx -gt $validateIdx) {
        Write-Ok 'serve_client 中 validate_client 出现在 read_line_bounded 之前（读取前校验）'
    } else {
        Write-Bad '校验未前置到读取之前 —— 攻击者可用伪造消息先进入解析器'
    }

    # 6. 禁止把"不改用户 SID"的手段当成 ACL 验证（只在**代码**里查，注释里的说明不算）
    if ($secCode -match 'CreateRestrictedToken|DISABLE_MAX_PRIVILEGE') {
        Write-Bad 'security.rs 代码里出现 CreateRestrictedToken —— 它不改变用户 SID，不能用来验证本 ACL'
    } else {
        Write-Ok '未把 CreateRestrictedToken 这类会给出假绿色的手段当作 ACL 验证'
    }
}

# ─────────────── 正向功能验证（当前用户） ───────────────
function Test-PipeFunctionalPositive() {
    Write-Section '正向功能验证 · 当前用户应能连上并拿到 status'

    $pong = Invoke-PipeRequest "BDMC1|@ctl|status|"
    if (-not $pong.Connected) {
        Write-Skip "core 未在运行（连不上 $($script:PipeName)）：$($pong.Error)"
        return
    }
    Write-Ok 'TCP-less 连接建立成功（当前用户被 ACL 放行）'

    if ([string]::IsNullOrWhiteSpace($pong.Response)) {
        Write-Bad 'status 无响应（服务端未回包）'
        return
    }
    try {
        $json = $pong.Response | ConvertFrom-Json
    } catch {
        Write-Bad "status 响应不是合法 JSON：$($pong.Response)"
        return
    }
    if ($json.ok -ne $true) { Write-Bad "status 返回 ok=false：$($pong.Response)"; return }
    foreach ($field in @('desired', 'actual', 'health', 'restarts', 'uptime')) {
        if ($null -eq $json.data.PSObject.Properties[$field]) {
            Write-Bad "status 响应缺少字段 '$field'"
            return
        }
    }
    Write-Ok "status 返回完整字段（desired/actual/health/restarts/uptime），uptime=$($json.data.uptime)s"

    # 未知 verb 必须回结构化错误码（而不是静默或裸文本）
    $bad = Invoke-PipeRequest "BDMC1|@ctl|frobnicate|"
    if ($bad.Connected -and $bad.Response -match '"error":"unknown-verb"') {
        Write-Ok '未知 verb 返回结构化错误码 unknown-verb'
    } else {
        Write-Bad "未知 verb 未返回结构化错误码：$($bad.Response)"
    }
}

# ─────────────── 方案 A：真验低权限用户被拒（需管理员） ───────────────
#
# 用 CreateProcessWithLogonW 直接以另一凭据启动子进程 —— 这是**唯一能在非交互会话里**
# 以另一个用户身份跑程序的方式（Start-Process -Credential / runas 都依赖交互或会挂起）。
# 子进程把结论写进公共目录的结果文件，父进程读回来断言。
function Test-PipeAclLowPrivilege([string]$RepoRoot) {
    Write-Section '方案 A · 以另一个本地用户连接，断言被拒'

    $isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        # **先判能力再动手**：不能执行就一个用户都不建（建了再跳过是既危险又虚伪）
        Write-Skip 'ACL 功能验证被跳过，原因：无管理员权限（无法创建测试用户）。这不是通过 —— 发布前必须在管理员环境跑一次本脚本'
        return
    }

    if (-not ('BdPipeAclNative' -as [type])) {
        Add-Type -Namespace '' -Name 'BdPipeAclNative' -MemberDefinition @'
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct STARTUPINFO { public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
  public int dwX; public int dwY; public int dwXSize; public int dwYSize; public int dwXCountChars; public int dwYCountChars;
  public int dwFillAttribute; public int dwFlags; public short wShowWindow; public short cbReserved2; public IntPtr lpReserved2;
  public IntPtr hStdInput; public IntPtr hStdOutput; public IntPtr hStdError; }
[StructLayout(LayoutKind.Sequential)]
public struct PROCESS_INFORMATION { public IntPtr hProcess; public IntPtr hThread; public int dwProcessId; public int dwThreadId; }
[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern bool CreateProcessWithLogonW(string user, string domain, string password, int logonFlags,
  string appName, System.Text.StringBuilder cmdLine, int creationFlags, IntPtr env, string cwd,
  ref STARTUPINFO si, out PROCESS_INFORMATION pi);
[DllImport("kernel32.dll", SetLastError = true)] public static extern int WaitForSingleObject(IntPtr h, int ms);
[DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
'@
    }

    $secure = ConvertTo-SecureString $TestPassword -AsPlainText -Force
    # 结果文件放公共目录：测试用户与当前用户都能读写（各自私有临时目录对方访问不到）
    $resultFile = Join-Path $env:PUBLIC ("bd-pipe-acl-result-{0}.txt" -f ([guid]::NewGuid().ToString('N')))
    $created = $false
    try {
        # 幂等：已存在先删（残留用户密码不一致会让登录失败，那会被误读成"ACL 生效"）
        if (Get-LocalUser -Name $TestUser -ErrorAction SilentlyContinue) {
            Remove-LocalUser -Name $TestUser -ErrorAction Stop
        }
        New-LocalUser -Name $TestUser -Password $secure -PasswordNeverExpires -ErrorAction Stop | Out-Null
        $created = $true
        Write-Ok "已创建临时测试用户 $TestUser（仅用于本次验证，结束即删）"

        $snippet = "try { `$c = New-Object System.IO.Pipes.NamedPipeClientStream('.', '$($script:PipeName)', [System.IO.Pipes.PipeDirection]::InOut); `$c.Connect(2000); Set-Content -Path '$resultFile' -Value 'CONNECTED' } catch { Set-Content -Path '$resultFile' -Value ('REJECTED: ' + `$_.Exception.GetType().Name) }"
        $cmdLine = New-Object System.Text.StringBuilder
        [void]$cmdLine.Append('"' + (Join-Path $PSHOME 'pwsh.exe') + '" -NoProfile -NonInteractive -Command "' + $snippet + '"')

        $si = New-Object BdPipeAclNative+STARTUPINFO
        $si.cb = [System.Runtime.InteropServices.Marshal]::SizeOf($si)
        $pi = New-Object BdPipeAclNative+PROCESS_INFORMATION
        $LOGON_WITH_PROFILE = 1
        $CREATE_NO_WINDOW = 0x08000000

        $started = [BdPipeAclNative]::CreateProcessWithLogonW($TestUser, $null, $TestPassword, $LOGON_WITH_PROFILE,
            $null, $cmdLine, $CREATE_NO_WINDOW, [IntPtr]::Zero, $null, [ref]$si, [ref]$pi)
        if (-not $started) {
            Write-Bad "CreateProcessWithLogonW 失败（Win32 $([System.Runtime.InteropServices.Marshal]::GetLastWin32Error())）"
            return
        }
        [void][BdPipeAclNative]::WaitForSingleObject($pi.hProcess, 15000)
        [void][BdPipeAclNative]::CloseHandle($pi.hThread)
        [void][BdPipeAclNative]::CloseHandle($pi.hProcess)

        if (-not (Test-Path $resultFile)) {
            Write-Bad '测试用户未能写出结果文件（子进程可能没跑起来）—— 无法判定，按失败处理'
            return
        }
        $verdict = (Get-Content $resultFile -Raw).Trim()
        if ($verdict -like 'REJECTED*') {
            Write-Ok "低权限用户连接被拒（$verdict）—— ACL 真实验证通过"
        } elseif ($verdict -eq 'CONNECTED') {
            Write-Bad '低权限用户**成功连接** —— ACL 未生效！这是必须修的安全缺陷'
        } else {
            Write-Bad "无法识别的测试结论：$verdict"
        }
    } catch {
        Write-Bad "方案 A 执行失败：$($_.Exception.Message)"
    } finally {
        if ($created) {
            Remove-LocalUser -Name $TestUser -ErrorAction SilentlyContinue
            Write-Ok "已清理临时测试用户 $TestUser"
        }
        Remove-Item $resultFile -ErrorAction SilentlyContinue
    }
}

# ─────────────── 主流程 ───────────────
$root = Get-RepoRoot
Write-Host "控制管道 ACL 验证（$root）" -ForegroundColor White

Test-PipeAclStatic $root
if (-not $SkipFunctional) { Test-PipeFunctionalPositive }
else { Write-Skip '正向功能验证被显式跳过（-SkipFunctional）' }
Test-PipeAclLowPrivilege $root

Write-Host ''
if ($script:Failures.Count -gt 0) {
    Write-Host "结果：失败 $($script:Failures.Count) 项，跳过 $($script:Skipped.Count) 项" -ForegroundColor Red
    $script:Failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
if ($script:Skipped.Count -gt 0) {
    Write-Host "结果：已执行项全部通过；但有 $($script:Skipped.Count) 项被跳过（见上方 [WARN]）" -ForegroundColor Yellow
    exit 2
}
Write-Host '结果：全部通过（含低权限拒绝的真机验证）' -ForegroundColor Green
exit 0
