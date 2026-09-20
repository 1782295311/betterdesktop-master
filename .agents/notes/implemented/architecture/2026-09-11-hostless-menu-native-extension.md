# Agent Note: 免宿主右键——稀疏包 IExplorerCommand 与注册表 in-proc 双路原生扩展

Status: implemented

## Problem

「桌面控制」「格式转换」两个快捷功能的右键入口依赖宿主在场：命令指向宿主自绘二级菜单或"需宿主"分支，宿主未运行时用户只能看到报错。同时注册表静态 verb 存在两个天花板：只支持 `%1`，多选时 explorer 逐文件调用 N 次、拿不到选中全集；无法按上下文动态决定显示内容。Win11 新版右键菜单另有硬约束——只有带包标识的 `IExplorerCommand` 才会进第一层，传统 handler 一律落到"显示更多选项"。

## Decision

同一份功能声明输出双路原生扩展，两路都由同一份配置驱动：

| 路 | 注册方式 | 承载 | 覆盖 |
|---|---|---|---|
| A | 稀疏 MSIX 包 + `windows.fileExplorerContextMenus` + `windows.comServer`（`com:SurrogateServer`） | dllhost（进程外） | Win11 新菜单第一层 |
| B | `HKCU\Software\Classes\...\shellex\ContextMenuHandlers` + in-proc COM DLL | explorer | Win10 / 经典菜单 |

关键约定：

- **命令派发固定 CLI**：两路都只把结果交给 `BetterDesktop.Cli.exe`（headless 执行转换/压缩/解压/桌面切换），绝不启动 WPF Application、不偷偷拉起静默宿主。
- **菜单内容由主程序配置**：原生 DLL 只读配置决定显示项与可用性（按 mtime 失效），新增功能不改原生代码、不需重编。
- **构建方法必须极快**：`GetTitle` / `GetIcon` / `GetState` / `EnumSubCommands` 只读内存缓存，昂贵工作只能在 `Invoke` 之后；这是 MS 集成文档的明确红线。
- **B 路纪律最严**：in-proc handler 崩溃 = explorer 崩溃 = 桌面全掉，因此所有方法 try/catch 不抛、零阻塞 IO，参数与结构体布局逐项核对。
- **签名与版本**：`Identity/@Publisher` 必须等于签名证书 Subject；`Identity/@Version` 每构建递增；自签证书公钥须导入 `Cert:\CurrentUser\TrustedPeople`，否则安装报 `0x800B0109`。

## Alternatives considered

- **只用注册表静态 verb**：实现最简但多选与动态内容无解，无法支撑"主程序控制显示 + 未来扩展"，否决。
- **只用 in-proc DLL**：Win11 新菜单不显示（无包标识），且把崩溃面直接放进 explorer，否决。
- **复用宿主自绘菜单**：宿主未运行时不可用，正是本问题本身。
- **A 路也走 in-proc**：Win11 要求包标识，且 in-proc 承载会把崩溃风险移回 explorer；改用 `com:SurrogateServer` 由 dllhost 进程外承载。

## Consequences

- 仓库首次引入 MSIX 打包与签名链路（此前无任何 AppxManifest/打包基础设施），安装脚本必须包含证书信任步骤与 OS 版本检查（`AllowExternalContent` 需 10.0.19041+）。
- 原生产物被 explorer/dllhost 锁定，运行中不可覆盖：升级需先停止扩展加载（重启 explorer 或结束 dllhost），发布脚本不得假设可覆盖。
- 注册/注销扩展需重启 explorer 才生效，设置侧因此必须提供"立刻重启桌面"出口。
- A/B 两路需保持行为一致，配置成为两路共同的唯一真相源；任何单侧改动都要在另一侧验证。
- B 路的失败模式是系统级（桌面整体不可用），故其代码纪律（不抛、不阻塞、布局核对）高于常规插件代码。
