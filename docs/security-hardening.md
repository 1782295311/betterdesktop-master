# security-hardening.md — 五层防线与机器门禁

> 地位：本文件是安全域的**执行侧**。三份文档分工不重叠：
> `docs/security.md` 写「我们**要求**什么」；`docs/threat-model.md` 写「当前**防住 / 不防**什么、为什么」；本文件写「这些要求**怎么被机器挡住**、挡住到什么程度、还剩哪些欠账」。
> 机器实现：`scripts/verify-security.ps1`（七条规则）+ `scripts/verify-security.Tests.ps1`（逐条正反例）+ `scripts/manifests/security-baseline.json`（棘轮基线）。

## 一、五层防线 → 机检映射

| 层 | 防线 | 规则 | 现状 |
|---|---|---|---|
| 1 | IPC 命名管道 | 规则 1 服务端必须有界读 | ✅ 两个服务端已接 `BoundedPipeLine` |
| 2 | 进程与路径 | 规则 2 参数不拼接；规则 4 原生加载不用相对名 | ✅ 三处注入点已改 `ArgumentList` |
| 3 | 反序列化 | 规则 3 限深（外部输入硬红线 / 本地配置棘轮） | ✅ 外部 5 处全覆盖 |
| 4 | 模型与数据 | 规则 5 禁止明文密钥 | ✅ 当前 0 命中；模型校验待 `bd-infer` 立项 |
| 5 | DLL 与原生 | 规则 7 编译加固 | ✅ 两个 CMake 工程已开 |
| — | 横向 | 规则 6 空 catch 吞异常 | 🔧 存量 34 处入棘轮 |

## 二、七条规则

每条都对应一处**真实可利用面**，不是风格偏好。规则全在 `scripts/verify-security.ps1`，阈值与豁免写在脚本注释里。

1. **管道有界读**：含 `new NamedPipeServerStream(` 的文件必须引用 `BoundedPipeLine`。依据：管道实例数普遍为 1，裸 `ReadLine` 下对端只要「只写不换行」就能吃满内存并钉死整条命令通道 —— 这是可用性攻击，功能测试永远发现不了（它只发善意输入）。core（Rust）侧同判据在 `core/src/pipe.rs::read_line_bounded`，两侧共用协议常量 `MAX_MESSAGE_BYTES`。
2. **参数不拼接**：非壳语义（`UseShellExecute = false` 或未设）的 `ProcessStartInfo.Arguments` 不得用插值/拼接，必须 `ArgumentList` 逐项传参；两参字符串重载 `Process.Start(exe, args+...)` 同样禁止。壳语义（`UseShellExecute = true`，如 `explorer.exe "路径"`）不判 —— 那是调用方**有意**把参数交给 shell。
3. **反序列化限深**：来自管道 / HTTP / 跨进程 stdio 的 JSON 必须显式 `MaxDepth`（**硬红线**，清单见脚本的 `$script:ExternalJsonInputs`；新增外部输入点必须同步登记，否则棘轮会放过它）。本地配置文件按 `docs/threat-model.md` 的信任边界判定为可信，限深属纵深防御，登记棘轮逐步清零。
4. **原生加载路径**：`LoadLibrary` / `NativeLibrary.TryLoad` / `Assembly.LoadFrom` 不得接**不含路径分隔符的字面量**（如 `LoadLibrary("helper.dll")` 会被当前目录劫持）。逐行扫描，注释里提到不算。
5. **明文密钥**：`api key` / `token` / `secret` 不得被赋值成长字面量（阈值 12 字符，避免把占位空串判红）。当前 0 命中。
6. **空 catch**：`catch { }` 无语句即违规。**「块内只有注释」判为通过** —— 那说明作者有意忽略而非遗漏，下一个人据此可判断它还成不成立。
7. **原生编译加固**：CMake 工程必须显式声明 `/guard:cf`（MSVC 默认**关**，必须写）、`/DYNAMICBASE`、`/NXCOMPAT`。依赖链接器默认值等于「换个工具链版本就把防线丢了」。刻意不加 `/Qspectre`：它要求额外安装 Spectre 缓解库组件，未装即链接失败 —— 让构建取决于「这台机器装了哪个 VS 组件」是更坏的结果。

## 三、安全禁止事项

- 禁止服务端命名管道用裸 `ReadLine`（必须有界读 + 总超时）。
- 禁止字符串拼命令行，一律 `ProcessStartInfo.ArgumentList`。
- 禁止非壳语义下 `UseShellExecute = true`。
- 禁止 JSON 外部输入不设 `MaxDepth`；禁止多态反序列化（`TypeNameHandling.All` 等）。
- 禁止 `LoadLibrary` / `Assembly.LoadFrom` 用相对名字面量。
- 禁止密钥 / 令牌入源码或日志；敏感值一律 DPAPI 或配置注入。
- 禁止原生工程不开 `/guard:cf` `/DYNAMICBASE` `/NXCOMPAT`。
- 禁止空 `catch { }` 吞安全异常（至少记日志或注明为何可忽略）。
- 豁免必须在**同一行或上方 3 行内**写 `SECURITY-EXEMPT: <理由>` —— 没有理由的豁免等于把红线变成装饰。

## 四、显式 DACL：为什么**没有**照搬

「管道必须用 Acl 版本 + `PipeSecurity`」是通行建议，本仓库**有意未采用**，理由三条：

1. .NET 的 `NamedPipeServerStream` 默认 DACL 已仅放行创建者 / Administrators / SYSTEM —— **跨用户、匿名、网络已被拒**，与 core 侧显式 ACL 的效果等同。
2. 在同用户威胁模型下，显式 DACL **不增加任何防护**：同用户恶意进程持有同一令牌，写得再细也照样连得上（`host/MenuCommandPipe.cs` 模块头 2026 年已记录这条边界）。
3. 落地它需要引入 `System.IO.Pipes.AccessControl` 包，与内核「零第三方依赖」的冻结原则冲突，代价与收益不成比例。

**触发条件**（满足任一即回来做）：① 需要与以 SYSTEM 身份运行的进程互连；② 开放第三方插件 / 机器人生态，攻击者不再是「用户本人」；③ 内核零依赖原则被 ADR 修订。届时实现落在 kernel 侧，并由 `scripts/test-pipe-acl.ps1` 用**另一个用户账户**验证（该脚本明确记着：`CreateRestrictedToken` 会给出**假绿色**，不能用来测「只允许当前用户」）。

## 五、存量欠账与触发条件

| 欠账 | 现状 | 触发条件 |
|---|---|---|
| 本地配置 JSON 未限深 | 25 个文件在棘轮基线内 | 随改动顺手清；棘轮只许收缩 |
| 空 catch | 34 处 / 14 个文件 | 补一句「为何可忽略」即通过 |
| 模型文件哈希校验 | **未实现**（无 ML 模型加载面） | `bd-infer` 立项时按规则 5 同期落地 |
| 原生二进制完整性校验 | 未实现（`ExplorerTAP.dll` / `natives\*.dll` 无签名校验） | 引入 Authenticode 签名时 |
| 显式管道 DACL | 有意未做 | 见 §四 触发条件 |

## 六、验证手法

1. 单点：`pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/verify-security.ps1`（约 7s）。
2. 全量：`pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/run-gates.ps1`（本门禁已进 Fast 通道）。
3. 规则自证：`pwsh -File scripts/run-gate-tests.ps1` 跑 Pester 单测 —— 每条规则都有**反例**（应报违规）与**正例**（应放过），因为安全门禁最大的失败模式不是漏检，而是假红太多被绕过。
4. 管道 ACL 真机：`scripts/test-pipe-acl.ps1`（**必须换另一个用户账户**，见 §四）。
