# 托盘程序 · 系统右键菜单：功能落地审计与修复

- 日期：2026-09-17
- 触发：用户反馈"托盘程序和系统右键菜单的快捷功能还没有落实好"
- 方式：两份只读审计（托盘 27 个菜单项逐项追到落地实现；系统右键注册链逐条对账）× 逐项修复
- 验证：全仓构建 0 警告 0 错误；`Shell.ContextMenu.Tests` 113/113、`Cli.Tests` 52/52、`Shell.Core.Tests` 138/138

---

## 一、审计结论（先说清楚"是不是空壳"）

**托盘不是空壳**：27 个菜单项全部有真实落地代码，`tray/` 目录内 `TODO`/`NotImplemented`/`throw` 命中数为 0；
托盘调用的 CLI action 与 CLI 实际支持的 action **零遗漏**。

**系统右键也不是没注册**：B 路 COM 扩展、A 路 IExplorerCommand、静态 verb、剪贴板项四条链都在，
菜单里会调用的 action 也全部被 CLI 认识。

用户感知的"没落实"，来自下面这些**具体缺口**——菜单画出来了，但点了没反应 / 看不到 / 显示与实际不符。

---

## 二、托盘：已修复

| # | 缺口 | 修复 |
|---|---|---|
| 1 | **缺「打开剪贴板历史」入口**（CLI/宿主自 09-17 起支持 `clipboard-history`，托盘只能"开/关这个功能"，用户得靠侧边手柄或全局热键） | 新增顶级菜单项「打开剪贴板历史」：优先直连面板进程 `--open`（唤醒已运行实例），未部署时回退 CLI `clipboard-history` |
| 2 | **缺两个开关**：`双击隐藏桌面图标`、`热键侧板`（`DesktopToggleCatalog` 里早已定义、CLI 与宿主都支持，托盘「功能开关」没有） | 功能开关从 9 项补到 11 项 |
| 3 | **文案谎报**：「打开设置中心（将启动主程序）」——实际行为是直接拉起独立设置进程，**不启动主程序** | 统一为「打开设置中心」，不再承诺启动任何东西 |
| 4 | **开关项从不置灰**：CLI 缺失时点下去只会得到退出码 -1、勾选不变（"可点但无效果"） | 与系统集成一致，按 `File.Exists(CliExe)` 统一置灰 |
| 5 | **更新项 Enabled 不完整**：只看 updater 是否存在，缺 `update.config.json` 时点了必然报"更新源不可达" | 同时检查更新源配置，缺失即置灰 |
| 6 | **「重启主程序」从不置灰** | 与「启动主程序」同款判定 |
| 7 | **开关说明与真实机制不符**：注释称索引/截图"实际启停由看门狗执行"，而看门狗**刻意不守护**这两个（按需拉起 + 空闲即退，守护会互相打架） | 菜单标签如实标注：「索引引擎（停止后续按需拉起）」「截图工具（需常驻服务在运行）」 |
| 8 | **「恢复系统外观」误报**：CLI 缺失时 `RunCli` 返回 -1 → 什么都没恢复却报"系统外观已是原生状态" | 区分"命令没执行"与"本就是原生状态"，前者明确报错并写日志 |
| 9 | **桌面服务运行判定按进程名**：短命「桌面控制菜单」进程与常驻服务**同名** → 弹菜单时被误判"服务在运行"（「启动」被置灰、「停止」误杀菜单进程） | 判据改为**服务独有的命名管道** `BetterDesktop.DesktopCmd` 是否可达 |
| 10 | **应急恢复清理名单陈旧**：只有 Host/Watchdog，漏了 Tray / DesktopControl / Agent（恢复后它们仍会把组件拉起来）；自启值名也漏了 `BetterDesktop.Tray` | 进程名单与自启值名补全 |

---

## 三、系统右键菜单：已修复

| # | 缺口 | 修复 |
|---|---|---|
| 1 | **注销后被 Agent 60s 自动回补**（用户点「注销系统右键扩展」→ Agent 自愈周期发现 `shellmenu.comExtension` 为真 → 重新注册，1 分钟内悄悄装回来） | 新增"用户显式注销"标记 `shellmenu-unregistered.flag`（与 host-stopped / agent-stopped 同款约定）：注销时写下、注册时清除，Agent 自愈据此停手 |
| 2 | **选中文件夹时永远没有我们的项**：快照里的项只有「格式转换」（Files 场景）与「桌面控制」（Background 场景），而 B 路/A 路都注册了 Directory 场景 → 注册了却恒空 | 新增「压缩为 ZIP」（Files + Directory 场景），并把它纳入批协议白名单（`ClassifyBatch` + `RunBatch` + 多路径 `ArchiveCore`）。验证链：C# 写 `"directory"` → 原生 `ParseScene` 认识 → 批协议执行 |
| 3 | **注册后不提示要重启 explorer**（B 路是进程内 shellex，explorer 启动时枚举并缓存，不重启就看不到） | 托盘注册/修复/注销完成后气泡明确提示"右键菜单需重启资源管理器后生效" |
| 4 | **卸载残留两处注册键无人清理**：`BetterDesktop.ToggleDesktop`（桌面空白「切换到自绘桌面」）、`BetterDesktopClipboardHistory`（四场景剪贴板项）—— 卸载时桌面服务/剪贴板插件已停止，留下指向已删 CLI 的死链 | `SystemIntegrationRegistrar.Unregister` 新增静态 verb 清理（含历史残留 `BetterDesktop.Ui` / `Compress` / `Dock`） |
| 5 | **设置页漏了总开关** `shellmenu.comExtension`：Agent 自愈、快照写入、托盘注销都取决于它，用户却无法从 UI 关掉整路接管 | 设置 → 菜单管理 → 「BetterDesktop 快捷功能」卡片置顶补上总开关；同时补上新加的「压缩为 ZIP」开关 |
| 6 | **注册状态查询只看一个场景**：`IsRegistered` 只查 `*\shellex\...`，只注册了桌面空白场景时误报"未注册"（设置页开关显示关闭、用户切不动） | 改为"至少一个场景已注册"，并新增 `RegisteredSceneCount()` / `IsFullyRegistered()` 供诊断与自愈使用 |
| 7 | **快照缺失完全静默**：原生扩展只是空壳，菜单里有什么全由 `shellmenu.json` 决定；快照唯一写入者是桌面服务 → 桌面服务没跑过时"注册成功但一项都不显示"，且无人告知 | Agent 注册后检查快照存在性，缺失时写明确警告（指向"桌面服务是否在运行"） |

---

## 四、验证

| 项 | 结果 |
|---|---|
| 全仓构建 | 0 警告 0 错误 |
| `BetterDesktop.Shell.ContextMenu.Tests` | 113/113 通过 |
| `BetterDesktop.Cli.Tests` | 52/52 通过（含按新行为更新的批协议分类用例） |
| `BetterDesktop.Shell.Core.Tests` | 138/138 通过 |
| 场景字符串契约 | C# `SceneText` 输出 `"directory"`，原生 `MenuModel.cpp:ParseScene` 识别 `L"directory"` ✅ |

---

## 五、仍未处理（建议下一批）

| 优先级 | 缺口 | 说明 |
|---|---|---|
| 高 | **快照写入者只有桌面服务** | 本轮只加了"缺失告警"。根治方案二选一：① 把快照装配下沉到 Agent/CLI 可用的共享服务；② 桌面服务改为"即使自绘桌面关闭也保持运行"。涉及架构取舍，需先定方向 |
| 中 | **磁盘（Drive）场景完全缺位** | B 路 4 场景不含 `Drive`，A 路 ItemType 也没有。磁盘右键想挂"格式化保护/压缩整盘"等才有意义，需产品决策 |
| 中 | **统一注册体系（`ContextMenuRegistry` / `ContextMenuContribution`）是死代码** | 只有单测调用；plugin 里声明的 contextMenus 不生效。要么接上、要么删掉，避免后来者误以为它在工作 |
| 中 | **`open-settings` / `dock-pin` 默认不注册** | 只有用户在设置页点「恢复默认快捷功能」才注入，且命令指向宿主 exe 而非 CLI（与"命令入口统一走 CLI"的红线不一致） |
| 低 | 托盘缺"单组件重启"、tooltip 不反映状态、更新回滚无入口 | 体验增强项 |
| 低 | `shellmenu.archive` 之外，7z/RAR 未挂菜单 | 依赖外部引擎，原生菜单无法置灰提示，暂时只挂内置 ZIP |

---

# 六、2026-09-18 形态统一（用户第二轮反馈）

用户反馈：**「桌面控制」「剪贴板历史」「切换到自绘桌面」没有做到和「格式转换」一样的标准——缺程序图标、缺功能子菜单**；
并要求「剪贴板历史」改成**功能开关**、补上**热键侧板开关**；另外**系统托盘区看不到托盘程序**。

## 6.1 调研结论：原生侧能力早就完备，断点只在 C# 侧

| 能力 | 原生侧状态 | C# 侧状态（改动前） |
|---|---|---|
| 图标 | **已就绪**：`MenuModel.cpp` 早已解析 `icon` 字段；B 路 `ShellMenuHandler.cpp` 的 `ApplyItemIcon`（`SetMenuItemBitmaps`）、A 路 `ExplorerCommand::GetIcon` 都会消费 | **断点**：`ShellMenuItem` 没有 `Icon` 字段、序列化从不写 `icon` |
| 子菜单 | 已就绪：`kind="submenu"` + `children` 递归，多级支持（层级上限 8） | 已支持 |
| 勾选框 | 已就绪：B 路 `MF_CHECKED`、A 路 `ECS_CHECKBOX\|ECS_CHECKED` | 已支持（`Kind=Toggle` + `IsChecked`） |

⇒ **无需重编原生 DLL**，改 C# 侧即可。

> 补充：改动前四项其实都会回退到宿主 exe 图标（`ResolveEffectiveIcon`），用户感觉"只有格式转换标准"，
> 主因是**渲染位置差异**——「剪贴板历史」「切换到自绘桌面」当时是注册表静态项，只出现在经典菜单
> （「显示更多选项」），而「格式转换」是快照项、出现在 Win11 新版菜单第一层。

## 6.2 改动清单

| # | 改动 | 文件 |
|---|---|---|
| 1 | `ShellMenuItem` 新增 `Icon` 字段并在序列化时写 `icon` | `shell-context-menu/Services/ShellMenuConfigWriter.cs` |
| 2 | **「桌面控制」从叶子项改为带图标的开关子菜单**，子项含：桌面图标 / 隐藏任务栏 / 双击隐藏图标 / 菜单栏 / 底部 Dock / **剪贴板历史** / **热键侧板** / 自绘桌面 / 更多控制（实时状态）… | `shell-desktop/Services/ShellMenuContentBuilder.cs` |
| 3 | 全部项（含格式转换与其子项、压缩为 ZIP）统一写入 `Icon` | 同上 |
| 4 | 批协议新增 `toggle-desktop`（自绘桌面开关走批协议） | `BetterDesktop.Cli/HeadlessExecutor.cs` |
| 5 | 快照重写订阅补齐开关自身的键（`components.wintaskbar` / `desktop.iconsHidden` / `desktop.doubleClickHideIcons` / `extensions.clipboard-history.enabled` / `hotkeys-panel.enabled`）——**否则点了开关，下次右键看到的勾选态还是旧值** | `shell-desktop/DesktopPlugin.cs` |
| 6 | 注销两个重复的静态注册项（「剪贴板历史…」「切换到自绘桌面」），避免同名但语义不同的两个入口 | `shell-clipboard/ClipboardShellMenuRegistrar.cs`（新增 `Unregister`）、`ClipboardPlugin.cs`、`DesktopPlugin.cs` |
| 7 | 托盘首次运行提示图标位置（Win11 默认把新图标折叠进「^」溢出区，用户会以为程序没装上） | `tray/TrayApplicationContext.cs` |

## 6.3 取舍说明（为什么保留「更多控制（实时状态）…」）

快照里的勾选态是**静态值**——原生菜单点击后不会回写，刷新依赖设置变更触发快照重写。
这意味着快照形态**做不到**「宿主不在线时把开关置灰并注明原因」（那正是当初把「桌面控制」做成独立进程菜单的理由）。

因此本次保留子菜单最后一项「更多控制（实时状态）…」，转交独立进程菜单：
它每次打开都现读设置、现判宿主在线、能置灰并提示"需启动 BetterDesktop"。
**静态开关负责形态统一，实时菜单负责边界情况**，两者互补。

## 6.4 托盘"看不到"的结论

- 排查发现托盘进程当时**未在运行**（此前排查过程中被停止），自启注册项与启动批准态均正常；
- 另一个系统性原因是 **Windows 11 默认把新出现的托盘图标折叠进「^」弹出区**，系统没有 API 能替用户固定到任务栏；
- 故新增首次运行气泡引导（用 `tray-icon-hint.flag` 标记只提示一次）。
