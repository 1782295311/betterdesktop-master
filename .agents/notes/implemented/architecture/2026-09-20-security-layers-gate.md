# Agent Note: 五层防线机检与 C# 侧安全判据落地

Status: implemented

## Problem

安全域当时**只有 core（Rust）一侧**有成体系的机器判据：`core/src/security.rs` 有「DENY 先于 GRANT」的管道 DACL 与按 PID 取令牌的调用方校验，`process.rs` 有裸名与前缀校验，`protocol.rs` 有消息大小上限，`ownership.rs` 有写权三态。C# 侧则是**人工纪律**：同一批风险（管道、进程启动、反序列化、原生加载、密钥、异常处理）没有任何门禁，写代码时想不到、review 时靠人记得，而人的注意力恰好是最先被消耗的资源。

审计实测出 4 个具体缺口，全部**功能测试无法发现**（功能测试只发善意输入）：

1. **两个 C# 服务端管道无长度上限**（`host/MenuCommandPipe.cs`、`packages/shell/shell-desktop-control/DesktopControlEntry.cs`）。`DesktopCmd` 连总超时都没有。管道实例数限 1 ⇒ 对端「只写不换行」即可吃满内存并钉死整条命令通道；「连上不说话」即可占死唯一实例槽。core 侧早已用 `read_line_bounded` 堵住，C# 侧只做了一半。
2. **全仓 0 处 `MaxDepth`**。而 `UpdateSource`（HTTP 清单）、`MenuBrokerClient`（跨进程 stdio）、`IndexIpcClient` / `ClipboardIpcClient`（命名管道）、`KugouMusicApi`（外部 API）都是外部输入 —— 深层嵌套 JSON 可把解析打成栈深炸弹。
3. **命令注入形态 3 处**：`UninstallString` / 本进程命令行被字符串插值拼进 `cmd.exe /c` 或 CLI 参数（`AppEntryActions.RunUninstaller`、`UninstallResolver.RunUninstaller`、`StartMenuService`，以及 `host/App.xaml.cs` 的 `--menu-cmd` 转发、`DesktopIconsControl.OpenFilesWith` 的拖放路径、`ExternalPluginAdapter` 的子进程参数 —— 后三处的注释甚至已经写着「禁 shell 拼接，数组直传」，实现却是拼接，注释与实现相反）。
4. **两个 CMake 原生工程未显式声明安全编译项**：`/guard:cf` 是 MSVC 默认**关**的，不写就没有；`/DYNAMICBASE` / `/NXCOMPAT` 只靠链接器默认，换工具链版本即失守。`BetterDesktopShellMenu.dll` 还是**进程内加载进 explorer** 的组件。

## Decision

**一、把 C# 侧也变成机器可判**。新增 `scripts/verify-security.ps1`（七条规则）+ `scripts/verify-security.Tests.ps1`（33 用例，逐条覆盖反例与正例）+ `scripts/manifests/security-baseline.json`（棘轮基线），登记进 `run-gates.ps1` 并进 Fast 通道（实测 7 秒，只扫源码目录而非全仓 51 万文件）。

七条规则与五层防线的映射：规则 1 管道有界读 → 第一层 IPC；规则 2 参数不拼接、规则 4 原生加载路径 → 第二层进程与路径；规则 3 反序列化限深 → 第三层；规则 5 明文密钥 → 第四层；规则 7 编译加固 → 第五层；规则 6 空 catch → 横向。

**二、先修硬红线，存量入棘轮**。修复：管道接 `BoundedPipeLine`（新 kernel 共享类，上限复用 `MenuCommandPipeCodec.MaxMessageBytes`，与 core 的 `MAX_MESSAGE_BYTES` 同源）；6 处命令注入改 `ArgumentList`；5 个外部输入点补 `MaxDepth`；两个 CMake 工程显式开 `/guard:cf` `/DYNAMICBASE` `/NXCOMPAT` `/GS` `/HIGHENTROPYVA`。规则 1/2/4/5/7 因此**零违规零基线**。规则 3 的本地配置（25 文件）与规则 6 的空 catch（34 处 / 14 文件）登记棘轮，理由见下。

**三、文档按「要求 / 现状 / 执行」三分，不合并**。新建 `docs/security-hardening.md` 承接执行侧（七条规则、禁止事项、存量欠账与触发条件、显式 DACL 的取舍）；`docs/security.md` 更新为真实机检状态 + 三份文档的分工指针；`docs/threat-model.md` 新增 C1–C5 五条 C# 侧威胁面（此前该文件只描述 core）。

**四、`AGENTS.md` 增加「安全禁止事项」一节**（八条 + 豁免写法），保证红线在唯一行为规范里可见，而不是只藏在 docs 深处。

**五、两处对通行建议的有意偏离，并写明触发条件**：

- **不做显式管道 DACL**（通行建议是「必须 `NamedPipeServerStreamAcl.Create` + `PipeSecurity`」）：.NET 默认 DACL 已仅放行创建者 / Administrators / SYSTEM，跨用户、匿名、网络已被拒；同用户威胁模型下显式 DACL **不增加任何防护**（同令牌）；落地它要引入 `System.IO.Pipes.AccessControl`，与内核零依赖冻结冲突。触发条件：需要与 SYSTEM 进程互连、开放第三方插件生态、或零依赖原则被 ADR 修订。
- **不禁止 `UseShellExecute = true`**（通行建议是「一律禁止」）：壳动词调用（`explorer.exe "路径"`、`Verb = "runas"/"properties"/"openas"`）**必须**用它，那不是漏洞而是语义要求；规则只管「非壳语义下自己构造命令行却用拼接」。

## Alternatives considered

1. **全盘照搬外部安全规范**（`NamedPipeServerStreamAcl` + 全局禁 `UseShellExecute=true` + 所有 `Deserialize` 强制 `MaxDepth`）。否决理由：前两条见 Decision 五；第三条会让 25 个「本地配置文件（仅当前用户可写）」解析点被同一标准要求，而按 `threat-model.md` 的信任边界它们本就可信 —— 把可信输入与不可信输入按同一强度要求，结果是**红线失去区分度**：真正该拦的外部输入混在大量噪音里，人就开始忽略报错。
2. **把本地配置也改成硬红线**（顺手清 25 处）。代价是 25 处机械改动 + 一次无法充分验证的行为面变化，收益是纵深防御。选择登记棘轮：**拦住新增**（新增即红）比**一次清完存量**更稳，且棘轮"条目失效也红"的规则保证它会收缩而非腐烂。
3. **空 catch 一律报红**。实测 34 处、集中在 UI 与 native interop 容错路径，是项目既定风格而非疏漏。若一律报红，门禁上线即 34 条红，按 `run-gates.ps1` 自己的注释「被绕开的门禁等于不存在」—— 那不是安全，是训练人忽略门禁。改为「块内只有注释即通过」，把判据对准**真正静默失效**的那部分。
4. **把安全规则塞进 `verify-architecture-guard`**。否决：那是边界/分层门禁，判据是"唯一位置"；安全判据是"是否留有可利用面"，两类知识的腐化方式不同，混在一起会让两边的豁免清单互相污染。

## Consequences

**正面**：C# 侧首次有了与 core 对齐的机器判据；三处可用性攻击面（无界读、实例槽独占）与六处参数注入被消除；外部输入 JSON 全部显式限深；原生组件对内存破坏的可利用性下降。门禁 7 秒、进 Fast 通道，编辑后即可跑。

**代价与已知边界**：

- 文档预算上调四处（`threat-model` 1005→1290、`security.md` 650→740、`AGENTS.md` 1100→1330、新增 `security-hardening.md`=1560），均按实测值棘轮设定，理由记在 `doc-budgets.manifest.json` 的 note 里。
- 棘轮基线含 25 + 14 条欠账，需靠后续改动顺手清；棘轮的"条目失效也报红"是它不腐烂的唯一保证。
- 规则 2 的 `cmd /c` 三处改为 `ArgumentList` 后，参数引用方式从"我们拼接"变为"CreateProcess 引用"——语义等价（cmd 仍解析 command 内容），但**卸载软件是破坏性操作**，首次真机验证时应确认卸载器正常启动。
- 门禁只覆盖**静态可判**的形态。动态面（路径穿越的实际输入、内存破坏）不在此列，仍靠 `docs/threat-model.md` 的"不防谁 + 触发条件"表管理。
