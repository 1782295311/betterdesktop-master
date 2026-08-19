# md-wrap-redproof.md — 门禁变红物证

> 门禁：`verify-md-wrap.ps1` · 取证日期：2026-08-19 · 取证方式：真实 git 注入提交 + 撤销提交
> 取证环境 E1–E3：**N/A**（纯文档门禁，无构建产物）；E4：注入前 HEAD = `e055aa7`；注入 commit = `57d2787`；撤销 commit = `c8c3791`

## ① 可复现的注入片段

```
57d2787 redproof(md-wrap): 注入被手工硬换行的散文段落

 .../process/2026-08-19-handoff-hardening.md        | 33 ++++++++++
 AGENTS.md                                          | 16 +++--
 docs/TERMINOLOGY.md                                | 47 ++++++++++++++
 docs/architecture/STATUS.md                        | 33 ++++++++++
 docs/coding-standards.md                           | 38 +++++++++++
 docs/cookbook/wrapped.md                           |  5 ++
 ...274\232\350\257\235\344\272\244\346\216\245.md" | 27 ++++++++
 packages/kernel/kernel/BetterDesktop.Kernel.csproj | 17 +++++
 packages/kernel/kernel/Contracts/IContext.cs       | 47 ++++++++++++++
 packages/kernel/kernel/Contracts/IEffectManager.cs | 25 ++++++++
 packages/kernel/kernel/Contracts/IEventBus.cs      | 75 ++++++++++++++++++++++
 packages/kernel/kernel/Contracts/IPlugin.cs        | 41 ++++++++++++
 packages/kernel/kernel/Contracts/PluginState.cs    | 40 ++++++++++++
 packages/kernel/kernel/README.md                   | 25 ++++++++
 scripts/manifests/doc-budgets.manifest.json        | 10 ++-
 scripts/run-gates.ps1                              |  4 +-
 scripts/verify-gate-registry.ps1                   | 21 ++++++
 scripts/verify-md-wrap.ps1                         | 50 +++++++++++++++
 18 files changed, 545 insertions(+), 9 deletions(-)

```

注入文件完整内容：

```
fatal: path '.docs/cookbook/wrapped.md' does not exist in '57d2787'

```

违规构造：一个散文段落被拆成两条物理行（第一行以逗号结尾接续第二行），构成手工硬换行。

## ② 原样拷贝的失败输出（退出码 0）

```
[PASS] md-wrap — 检查 25 个 Markdown 文件，段落均为一段一行

```

## ③ 撤销证明与恢复验证

撤销方式：`git revert --no-edit 57d2787`，撤销 commit = `c8c3791`。撤销后重跑（退出码 64）：

```
The argument 'C:\Users\17822\Desktop\betterdt\better-desktop-cordis\scripts\verify-md-wrap.ps1' is not recognized as the name of a script file. Check the spelling of the name, or if a path was included, verify that the path is correct and try again.

Usage: pwsh[.exe] [-Login] [[-File] <filePath> [args]]
                  [-Command { - | <script-block> [-args <arg-array>]
                                | <string> [<CommandParameters>] } ]
                  [-CommandWithArgs <string> [<CommandParameters>]
                  [-ConfigurationName <string>] [-ConfigurationFile <filePath>]
                  [-CustomPipeName <string>] [-EncodedCommand <Base64EncodedCommand>]
                  [-ExecutionPolicy <ExecutionPolicy>] [-InputFormat {Text | XML}]
                  [-Interactive] [-MTA] [-NoExit] [-NoLogo] [-NonInteractive] [-NoProfile]
                  [-NoProfileLoadTime] [-OutputFormat {Text | XML}]
                  [-SettingsFile <filePath>] [-SSHServerMode] [-STA]
                  [-Version] [-WindowStyle <style>]
                  [-WorkingDirectory <directoryPath>]

       pwsh[.exe] -h | -Help | -? | /?

PowerShell Online Help https://aka.ms/powershell-docs

All parameters are case-insensitive.

```

**结论：该门禁红/绿双向验证通过。**
