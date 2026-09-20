# Agent Note: 剪贴板历史引擎化——Rust 数据面 + 面板入口面 + 宿主 IPC 接线

Status: implemented

## Problem

剪贴板历史原先整体活在 WPF 宿主进程内：监听、捕获、去重、持久化、面板、热键同进程。这带来四个结构性约束：

- 生命周期绑定宿主——宿主退出或崩溃后记录停止，右键入口同时失效；用户期望的是「关掉主程序仍能复制留痕」。
- 不可信与大对象同进程——图片解码、PNG 编码、大文本字符串、万条历史都落在同一个 GC 堆上，峰值内存与停顿不可控。
- 高频路径与 UI 线程耦合——去重按内容全表比较、持久化在 UI 线程全量序列化加密落盘，规模上升后直接影响交互。
- 面板是入口与数据的混合体——面板打开必须先起宿主，无法作为独立入口形态（侧边栏/悬浮）自由演进。

## Decision

剪贴板拆为三个进程，各自只有单一职责，彼此经命名管道协作：

| 角色 | 进程 | 职责 |
|---|---|---|
| 数据面 | `BetterDesktop.Clipboard.Engine`（Rust，`engine/`） | 监听（广播 + 序列号轮询双通道）、捕获与格式归一化、去重、存储与驱逐、全局热键、IPC 服务端 |
| 入口面 | `BetterDesktop.Clipboard.Panel`（WPF，`packages/shell/shell-clipboard-panel/`） | 侧边栏手柄、完整面板、粘贴编排（分段/合并/按序）、主题同步 |
| 接线 | 宿主 `ClipboardPlugin` | `Provide<IClipboardService>` 即 IPC 代理，不监听、不存储、不建面板 |

关键约定：

- **契约不变**：`packages/api/Clipboard/IClipboardService` 只做可加性扩展（如分页查询），消费方（菜单栏按钮、搜索弹窗、系统右键、设置分区、灵动岛订阅）零改动。
- **通道**：命名管道 `BetterDesktop.Clipboard.Engine`，首帧 magic `BDCB1|`，随后 JSON-RPC 行协议；多连接槽，每连接独立维护事件订阅。
- **数据向后兼容**：引擎直接读写既有 `clipboard_history.json`（CBENC1 头 + DPAPI）、`clipboard\images\` 与 `clipboard\content\`；`CF_DIB → PNG` 前必须做 vacuous alpha 归一化；HTML 内嵌 data URI 图片提取为独立文件并以占位符还原。
- **性能红线落在引擎侧**：指纹 → id 用 `HashMap` 索引把去重降为 O(1)；持久化改后台线程 + temp/rename 原子写 + 退出/`WM_ENDSESSION` 立即 flush；大文本 >100KB 压缩为独立 bin 文件并懒加载。
- **回退开关**：`extensions.clipboard-history.backend = engine | legacy`，`legacy` 保留宿主内旧实现供排障；两后端同跑会双写同一历史库，回退前须让引擎 `exit`。
- **设置项同步纪律**：新增配置键必须四处同步——引擎 `Settings` 字段、设置分区控件、`ClipboardPlugin` 的 `apply_settings` patch、`WatchKeys`；引擎只在启动时读一次 `settings.json`，运行中靠 `apply_settings` 热更新。

## Alternatives considered

- **仅在 C# 内做设计层重构**（哈希去重、后台保存、列表虚拟化、原生剪贴板 API）：覆盖了大部分卡顿，但解决不了「宿主退出即停止」「不可信/大对象与 UI 同进程」这两条结构性目标；最终这些设计层修复随引擎化一并落在 Rust 侧。
- **进程内原生库（cdylib + FFI）**：跨语言边界最窄，但拿不到崩溃隔离与独立生命周期——宿主崩溃会带走数据面，与"退出主程序仍可用"直接冲突，否决。
- **全量重写（连插件体系与 UI 一起跨语言）**：收益与混合方案相同而成本翻倍，且会丢掉现有契约测试与插件装配，否决。
- **引擎兼做入口 UI**：入口形态需要 WPF 主题令牌、毛玻璃与统一弹窗基类，原生窗口无法复用；入口留在 C# 面板 exe。

## Consequences

- 部署面从单 exe 变为三件套，落在 `%LOCALAPPDATA%\BetterDesktop\`，由 `scripts/deploy-clipboard.ps1` 构建部署；`scripts/publish.ps1` 的 dist 全量发布尚未纳入引擎与面板，属未收口项。
- 三方互相定位依赖约定路径与单实例互斥；引擎缺失时面板与宿主按候选路径拉起，缺失不抛异常而是降级提示。
- 跨进程带来两类新故障面：管道僵死/半开（靠 `ping` 探活 + 客户端重连 + 重连后重推全量配置）与事件保序（按连接独立订阅 + 单调序号）。
- 面板不再是"宿主里的一个窗口"：它需要自带主题引导（读 `settings.json` 主题键）与 `PerMonitorV2` DPI 声明。
- 宿主侧不再持有剪贴板数据，`IClipboardService` 的任何实现细节（如占位符还原）都成为跨进程契约的一部分，改动需同时评估引擎与面板两侧。
