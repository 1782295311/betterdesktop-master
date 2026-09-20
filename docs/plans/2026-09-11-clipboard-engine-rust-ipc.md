# Cairo 开发计划

> Task: 将 BetterDesktop 剪贴板历史从「宿主内 C# 实现」重构为「独立 Rust 引擎进程 + C# WPF 面板 exe + 命名管道 IPC 层」；引擎接管监听/捕获/存储/热键，面板与宿主作为 IPC 客户端。宿主未运行时右键入口与历史记录仍可用；面板主题与主程序保持同步；扩展中心开关继续控制监听启停；新增两种快捷入口形态（悬浮球 v1 + 侧边栏 v2，设置可切换/并存）。
> 证据基于 commit `b805f6e43c126b858d46988f3a9400737e997959`（2026-09-09，工作区含未提交改动）验证；技术力文档命中：1301-clipboard-history、1502-lmdb-native-image-store、1408-single-instance-mutex、3101-global-hotkey、7413-hotkey-registration、713-mcp-tool-server、3603-菜单栏插入按钮弹窗模式、711-extension-system；未命中：Rust 剪贴板引擎（检索关键词：`rust clipboard win32 engine` / `clipboard rust`）。

## 1. Objective

场景语言（用户可感知的结果）：

- **O1** 宿主未运行时：右键桌面背景 →「剪贴板历史…」→ 面板照常打开、历史照常记录与检索。
- **O2** 复制大段文本/4K 截图：宿主与面板均不卡顿；万条历史下入库即时（去重 O(1)），面板打开 <300ms。
- **O3** 面板视觉与主程序主题同步（亮/暗/皮肤切换一致）。
- **O4** 扩展中心开关（extensions.clipboard-history.enabled）仍控制引擎监听启停，改动即时生效。
- **O5** 快捷入口：悬浮球（v1）hover 就近展开**专用紧凑适配面板 OrbPanel**（可一键切完整面板）；侧边栏（v2）屏幕边缘**「>」收纳手柄**（收纳态 UI），点击/悬停滑出**复用原生完整面板**；两种形态由设置切换（orb/sidebar/both/off）。

## 2. Current Behaviour

- `ClipboardManager`（shell-clipboard）常驻宿主：`HwndSource` + `AddClipboardFormatListener` 广播 + 500ms `GetClipboardSequenceNumber` 轮询兜底（ClipboardManager.cs:998-1055, 1626-1643，Win11 26200 广播不可用教训）。
- 捕获 = WPF `System.Windows.Clipboard` 快照式（HTML>Text>Image>Files，ClipboardManager.cs:1198-1274）；图片 `PngBitmapEncoder` 同步编码落盘（:1706）。
- 去重 = `ContentFingerprint`（SHA256 全文，ClipboardEntry.cs:262）+ `_history.FirstOrDefault(IsSameContent)` **O(n) 全表扫描**（ClipboardManager.cs:1437）。
- 持久化 = 800ms 防抖 `SaveToFile`（UI 线程 DispatcherTimer）全量 JSON + DPAPI（CBENC1 头）+ 写盘（:1798-1850）。
- 面板 = `ClipboardHistoryWindow : PopupWindowBase`，`ScrollViewer + StackPanel` 无虚拟化全量重建（ClipboardHistoryWindow.cs:196,505-537）；构造依赖 `ClipboardManager` 具体类 + `IAppearanceService`/`IVibrancyService`（:92-107）。
- 入口：右键 4 场景命令指向宿主 `--menu-cmd clipboard-history`（ClipboardShellMenuRegistrar.cs:46-47）；宿主经 `MenuCommandPipe` 单向命令桥派发（Bootstrap.cs:256-268）；插件 `ClipboardPlugin` Provide `IClipboardService`（Bootstrap.cs:143）。
- 设置：`%APPDATA%\BetterDesktop\settings.json` 扁平键值，**跨进程共享既定模式**（SettingsService.cs:20-44，SaveMutex 落盘互斥）；`extensions.clipboard-history.enabled` 默认 **true**（ClipboardPlugin.cs:95，README/1301 文档「默认 false」过时）。
- 热键：当前仅 ClipboardManager 注册 Ctrl+Shift+V/P/Backspace（ClipboardManager.cs:1069-1099），宿主其余模块无 RegisterHotKey → 迁移无冲突面。

## 3. Relevant Architecture

- 宿主 = WPF host + kernel（CordisContext 服务图 + cordis.yml 插件树 + HMR），消费方全部经 `IClipboardService` 契约（api 包，零项目依赖）：I6 菜单栏📋、I7 搜索最近复制、桌面控制菜单、右键 I10、设置分区、灵动岛（只读订阅，API 预留，对齐 IMediaPlaybackService「订阅+快照、无订阅者零开销」范式）。
- 面板基类 `PopupWindowBase`（shell-core/Windows）负责无边框/透明/置顶/失焦收起/chrome；外观与毛玻璃来自 shell-core.Surface/Vibrancy（宿主内存态服务 + 主题令牌体系）。
- 多语言择优（AGENTS.md）：界面层 C#，功能层择优——引擎用 Rust 是既定架构的落点（对标 1502 的 C# 外壳 + C 原生、66 域 Rust 组件）。

## 4. Technical-Knowledge Findings

- **1301-clipboard-history**（L2，TS 原版 + C# v1.3 变体，[verified]）：红线 = 监听回调延迟读、hash 去重、写前取消监听（C# 落地为消费式抑制令牌 `Interlocked.Exchange(ref _suppressCapture,0)!=0`，ClipboardManager.cs:1164）、图片落盘、大小限制（5MB/200MB/10000 条/200 固定/90 天）。C# 变体记录 Win11 26200 广播不可用 → 轮询兜底双通道。**引擎必须保留双通道 + 消费式抑制**。
- **1502-lmdb-native-image-store**（L2，[verified]）：**vacuous alpha 红线**——Windows CF_DIB 常见 Bgra32 全 alpha=0 但 RGB 有内容，直接 PNG 编码整图不可见，必须先 `NormalizeVacuousAlpha`；图片 blob 不进 JSON；GC fail-closed。**引擎图片捕获必须做 alpha 归一化**。
- **1408-single-instance-mutex**（L2，[verified]）：PaperTodo 变体 E = Mutex + NamedPipe 参数转发（第二实例发命令→退出）；固定名 GUID 后缀、AbandonedMutex 接管。→ 面板/引擎单实例。
- **3101-global-hotkey / 7413-hotkey-registration**（L2，[verified]）：RegisterHotKey 组合键系统全局唯一（0x581 冲突捕获）；退出配对释放；需消息循环。→ 引擎热键纪律。
- **713-mcp-tool-server**（L2，[verified]）：JSON-RPC 协议纪律（协商/并发上限/进度/取消/结构化错误）→ IPC 协议设计参考。
- **3603-菜单栏插入按钮弹窗模式 / 711-extension-system**（L2，[verified]）：消费入口模式与扩展生命周期，代理化后不变。
- 未命中：Rust 侧剪贴板引擎文档 → 本计划核心为新建资产。

## 5. Constraint Findings（机制文档验收标准 + 源码等价断言）

| 关键点 | 约束（来源） |
|---|---|
| 数据兼容 | 引擎必须读写现有 `%LOCALAPPDATA%\BetterDesktop\clipboard_history.json`（CBENC1 头 + DPAPI + JSON 字段与 `ClipboardEntry` 对齐）与 `clipboard\images\{id}.png`；旧历史不丢（ClipboardManager.cs:53-55, 1828-1898） |
| 抑制回环 | 写回剪贴板前置位抑制令牌，仅写失败才复位；广播+轮询双路径消费同一序列号防二次触发（ClipboardManager.cs:1163-1176, 1626-1643） |
| 图片红线 | CF_DIB → PNG 前 NormalizeVacuousAlpha；单图像素 ≤100MP、PNG 存储 ≤64MB、总量 ≤500MB（设置可调），超限拒绝捕获；**不做有损降级——容纳 P 图照片，保持无损**；捕获/编码全部后台线程池（ClipboardManager.cs:44-45, 1243-1246；1502 红线 1） |
| 文本上限（新增） | 文本/HTML/RTF 单条 ≤10MB 原始（超限拒绝，容纳百万字）；>100KB → Deflate 压缩独立存 `clipboard\content\{id}.bin`；文本总量 ≤100MB（压缩后，设置可调），超预算驱逐最旧未收藏 |
| 存储模式（新增） | `storage-mode = full\|paths-only`（默认 full）：full=图片原图 PNG+缩略图、文件记路径+副本；paths-only=图片只存缩略图+元数据（同帧带单图片源路径则记路径，粘贴优先读源文件）、文件只记路径。统一总量预算 `max-total-mb`（默认 1024MB，覆盖图片原图+文件副本+文本压缩）；文件副本单文件上限 `file-copy-max-mb`（默认 64MB，超限仅记路径）；超预算按时间驱逐最旧未收藏 |
| 格式保真（新增） | HTML/RichText 条目**三格式全存**：Content（纯文本）+ HtmlContent（HTML 全文）+ RtfContent（RTF 全文，若剪贴板有，含 CF_RTF/CF_RTFNOOBJS 双格式按实际提供存储）；HTML 元信息 `HasImages`（含 &lt;img&gt;）/`HasTable`（含 &lt;table&gt;）随条目存储（ClipboardEntry 字段已存在）；**data URI 图片提取为独立文件** `clipboard\html-images\{entryId}\{n}.{ext}`（字节级提取+还原，不做图像解码），HtmlContent 中替换为占位符 `{{BD_IMG:n}}`，写回剪贴板时由引擎还原为 base64 data URI（外部应用可见）并构造正确 CF_HTML header（StartHTML/StartFragment）；单张 >2MB 或单条目提取总量 >16MB 跳过提取（data URI 原样保留，受 10MB 文本上限兜底）；提取图片计入统一总量预算并随条目驱逐级联清理；粘贴按格式层级写回（CF_HTML+CF_RTF+CF_UNICODETEXT 全写，目标应用自选）；**代码条目保留「强制纯文本粘贴」裁决**（1301）；大 HTML 分段粘贴语义（6 段/250ms）保留在 C# 面板侧 |
| 剪贴板竞争（新增） | `OpenClipboard` 被其他进程占用 → 重试 3×50ms，仍失败跳过本次捕获（不崩、不丢已有）；所有捕获/写回路径统一此纪律 |
| 内容加密（新增） | `content\{id}.bin`（>100KB 压缩文本）**随 JSON 一起 DPAPI 加密**；图片原图/缩略图/文件副本维持明文（与现有 C# 一致） |
| 隐私黑名单 | 进程 22 项 + 标题关键词 15 项，命中跳过记录（ClipboardManager.cs:66-81, 1764-1784） |
| 热键 | Ctrl+Shift+V/P/Backspace；0x581 失败记 warn 不崩；退出配对释放（3101/7413；ClipboardManager.cs:57-63, 1082-1099） |
| 设置共享 | 引擎直读 `%APPDATA%\BetterDesktop\settings.json`（跨进程各持一份 + SaveMutex 落盘互斥为既定模式，SettingsService.cs:20-44）；enabled 键默认 true |
| 安全 | 管道 magic 握手 + 实例数限制（MenuCommandPipe.cs:29,42-55 C1 模型） |
| 契约 | `IClipboardService` 可加性：v1.1+ 以新增方法扩展，不改既有成员（IClipboardService.cs:9）——IPC 客户端必须实现全契约 |

## 6. Proposed Changes

### 6.1 新增 Rust 引擎 `engine/`（`betterdesktop-clipboard-engine`，Windows 无窗口进程）
| 文件 | 符号 | 职责 |
|---|---|---|
| `src/main.rs` | `main()` | 单实例 Mutex（`Local\BetterDesktop.Clipboard.Engine-{GUID}`，1408 变体 A，会话级多会话隔离）；隐藏消息窗口收 `WM_CLIPBOARDUPDATE`/`WM_HOTKEY`；**`WM_ENDSESSION`/退出信号 → 立即 flush 防抖数据再退出**；IPC `exit` 命令支持更新模式（退出→替换 exe→重启）；启动读配置 |
| `src/listener.rs` | `ClipboardListener::start/stop` | AddClipboardFormatListener + 500ms 序列号轮询兜底；消费式抑制令牌（AtomicU32）；广播+轮询序列号消费防双触发 |
| `src/capture.rs` | `read_snapshot()` | 原生 Win32：EnumClipboardFormats → CF_UNICODETEXT/CF_HTML/CF_RTF/CF_RTFNOOBJS/CF_DIB→PNG（NormalizeVacuousAlpha，1502 红线）/CF_HDROP；**OpenClipboard 竞争重试 3×50ms，失败跳过本次**；**HTML/RichText 条目三格式全存（CF_HTML+CF_RTF+CF_UNICODETEXT）**；**data URI 图片提取：解析 &lt;img src=&quot;data:…&quot;&gt; → base64 解码存 `html-images\{entryId}\`，HtmlContent 替换占位符 `{{BD_IMG:n}}`（写回时引擎还原 + 构造 CF_HTML header）**；捕获顺序 HTML>Text>Image>Files（与现 C# 一致）；隐私黑名单（进程 22 + 标题关键词 15）；**大图/大文本处理在后台线程池，不阻塞事件循环**；paths-only 模式下同帧 CF_HDROP 单图片文件 → 记录源路径（粘贴优先读源文件） |
| `src/analyzer.rs` | `analyze(kind, html, text)` | 移植 ContentAnalyzer 语义：File>Image>Code>RichText>Text；代码 = 缩进占比 + 关键字 27 + 注释 + 括号配对（行 <3 不判）；**HTML 元信息检测：`HasImages`（&lt;img&gt; 存在）/`HasTable`（&lt;table&gt; 存在）**；含图含表不影响类型裁决（仍按 HTML 优先） |
| `src/store.rs` | `Store::load/save/upsert/evict` | 兼容 CBENC1+DPAPI（windows crate CryptProtectData）+ serde_json（字段与 ClipboardEntry 对齐）；≤100KB 文本内联 JSON、>100KB Deflate 压缩 **+DPAPI 加密**为 `clipboard\content\{id}.bin`（**懒加载：启动只载元数据，查询命中才读 bin**）；**storage-mode 分支**：full=图片原图 `clipboard\images\{id}.png` + 缩略图 `clipboard\thumbs\{id}.{png\|jpg}`（**有 alpha 用 PNG、无 alpha 用 JPEG**）、文件副本 `clipboard\files\{id}\`（≤64MB 单文件）+ HTML 提取图 `clipboard\html-images\{entryId}\`；paths-only=图片仅缩略图+元数据、文件仅路径；**哈希索引 `HashMap<fingerprint, id>` 去重 O(1)**（修复现有 O(n)）；800ms 防抖后台线程保存 + 退出/`WM_ENDSESSION` 立即 flush；驱逐 10000 条/200 收藏/90 天 + 统一总量 1024MB（图片原图+文件副本+HTML 提取图+文本压缩，设置可调）预算；**条目驱逐/删除时级联清理 html-images/files 目录** |
| `src/hotkey.rs` | `Hotkeys::register/unregister` | RegisterHotKey ×3（Ctrl+Shift+V/P/Backspace），0x581 捕获不崩、退出配对释放 |
| `src/ipc.rs` | `IpcServer` | NamedPipe server（管道名 `BetterDesktop.Clipboard.Engine`，magic `BDCB1|`，**多客户端：4 个连接槽循环，每连接独立后台任务处理请求+事件推送——宿主与面板并发连接**）；JSON-RPC 行协议；请求：`query`（**分页 offset/limit**）/`get_last`/`pin`/`unpin`/`delete`/`delete_many`/`clear_unpinned`/`set_tags`/`copy_to_clipboard`（写回+抑制+data URI 占位符还原，文件条目按 storage-mode 写原路径或副本路径）/`import`/`pause`/`resume`/`apply_settings`/`ping`/`open_panel`（拉起面板 exe）/`exit`（更新模式退出）；事件推送按连接独立维护：`history_changed`/`clipboard_changed`（**轻量摘要，供灵动岛等只读订阅方**：id/contentType/textPreview≤200 字符/thumbPath/sourceApp/copiedAt，引擎侧截断，不推大内容，无订阅者不推送）/`pause_changed`/`monitoring_changed` |
| `src/config.rs` | `Config::load` | 读 `%APPDATA%\BetterDesktop\settings.json` 的 `extensions.clipboard-history.*`（enabled 默认 true） |
| `Cargo.toml` | — | windows / serde / serde_json / 图片解码（image 或 wic 桥） |

### 6.2 新增 C# 面板 exe `packages/shell/shell-clipboard-panel/`（`BetterDesktop.Clipboard.Panel`，WPF）
- 迁移 `ClipboardHistoryWindow` 视觉全量（卡片化条目/搜索/筛选/日期分组/键盘导航/多选合并/按序/拖拽导出/暂停/收藏/缩略图异步解码），依赖从 `ClipboardManager` 具体类改为 **IPC 客户端**（`IClipboardService` 远端实现）；**条目列表改 `VirtualizingStackPanel` 虚拟化 + 分页拉取（每页 100）**——万条下面板打开 <300ms 的前提。
- 单实例（1408 变体 B：Mutex 失败即转发 `--open` 给先行实例）；启动时 IPC ping 引擎，无响应则拉起引擎 exe。
- 主题同步：引用 shell-core，复用 `PopupWindowBase`/`IAppearanceService`/`IVibrancyService`；主题引导读 settings.json 主题键 + 注册表强调色（1603/7405 机制），随共享设置变化刷新；**app.manifest 声明 PerMonitorV2 DPI**（悬浮球/侧边栏跨屏缩放正确）。
- 粘贴路径：`copy_to_clipboard` 请求引擎（引擎侧抑制回环），响应后 C# 侧 `keybd_event` SendPaste；分段/合并/按序纯逻辑留 C#；**粘贴格式层级**：混合条目（HTML+RTF+Text）三格式全写回，目标应用自选；代码条目强制纯文本（1301 裁决）。
- 预览规则（格式保真呈现）：HTML/RichText 条目预览显示剥 HTML 纯文本（3 行），**meta 行追加「🖼 含图」「▦ 表格」徽标**（HasImages/HasTable）；代码条目等宽字体 3 行预览。
- 承载快捷入口层（§6.5）：悬浮球 + 侧边栏 + 完整面板同进程，入口与面板共享 IPC/主题/引擎探活。

### 6.3 新增 C# IPC 客户端库 `packages/shell/shell-clipboard-ipc/`（`BetterDesktop.Shell.Clipboard.Ipc`）
- `ClipboardIpcClient : IClipboardService`：NamedPipeClientStream + JSON-RPC + 事件订阅线程（断线自动重连 + 全量刷新事件）；供宿主插件与面板 exe 共用（契约单点维护）。
- **新增分页契约成员**（可加性允许，不改既有成员）：`GetFilteredEntriesPage(kind/category/keyword/sourceApp, offset, limit)` 返回分页结果——面板虚拟化按页拉取（每页 100），避免万条全量传输。
- 单元可测：注入 fake transport（内存管道）。

### 6.4 宿主改造
- `ClipboardPlugin`：`Provide<IClipboardService>` 改为 IPC 代理（`ClipboardIpcClient`）；加载时 `EnsureEngine`（ping → 无响应拉起）；设置键变更 → `apply_settings` IPC；enabled 开关 → 引擎 `start/stop` 监听。
- **设置分区 UI 更新**：剪贴板设置页新增 storage-mode（full/paths-only）、总量预算、文件副本上限、入口形态（orb/sidebar/both/off）表单项 + 引擎运行状态显示（经 IPC ping）。
- `ClipboardShellMenuRegistrar`：命令改为 `"<panelExe>" --open`（宿主不再参与右键链路；旧宿主命令残留按现有更新逻辑 ClipboardShellMenuRegistrar.cs:54-67 迁移）。
- `Bootstrap.cs` MenuCommandPipe `clipboard-history`/`desktop-controls` 分支：改经 IPC 代理 `OpenHistoryWindow()`（代理拉起面板 exe）。
- 旧 `ClipboardManager` 保留为回退实现（配置 `extensions.clipboard-history.backend = engine|legacy` 切换；**切换 legacy 时引擎经 `exit` 退出**；含占位符条目仅引擎模式可完整粘贴，设置页提示）。
- **自启拓扑定稿（Q2）**：引擎 + 面板 exe 都注册 HKCU Run 自启（引擎=数据面保证任意时刻记录；面板 exe=入口面承载悬浮球/侧边栏，entry-style=off 时不启面板自启）；右键/宿主/球体探活拉起互为兜底。

### 6.5 快捷入口层（面板 exe 内，入口策略化，不堆 if-else）
| 组件 | 职责 |
|---|---|
| `EntryHost` | 入口生命周期管理：按设置键 `extensions.clipboard-history.entry-style = orb\|sidebar\|both\|off` 装配形态策略；引擎探活/面板拉起统一入口；订阅引擎事件分发预览 |
| `FloatingOrbWindow`（v1） | 悬浮球：半透明可拖动小窗（默认右下，可吸附边缘）；常态常驻 / 复制后浮现 3-5s 淡出（设置可选）；hover 就近展开 **`OrbPanel` 专用紧凑适配面板**；失焦收起 |
| `OrbPanel`（v1，悬浮球专用面板适配） | 紧凑历史面板（宽 ~340，从球体位置就近展开）：搜索框 + 最近列表（条目行复用 `RecentStrip`）+ 底部「打开完整面板」动作；视觉走主题令牌；**独立适配布局，非完整 `ClipboardHistoryWindow` 的简单缩放** |
| `EdgeSidebarWindow`（v2） | 侧边栏：屏幕左/右边缘**「>」收纳手柄**（半透明窄条 ~28px，点击/悬停触发）→ 滑出**复用原生完整 `ClipboardHistoryWindow`**（不重做适配）→ 移开自动滑回手柄；防误触 = 手柄热区延迟 + 开关 |
| `RecentStrip` | 共享条目行组件（类型图标 + 文本片段/缩略图），供 `OrbPanel` 列表与面板行复用；数据来自引擎 `history_changed` 事件 + `query` 拉取 |
| 形态策略 | `IEntryMode`（orb/sidebar）两实现 + 工厂按设置装配；v2 侧边栏复用 v1 全部共享件，仅新增热区/滑出模块 |

## 7. Implementation Sequence

独立可执行步骤（任一步后树一致）：
1. **S1 引擎骨架**：`cargo new engine`；隐藏窗口 + 消息循环 + 单实例 + IPC server 骨架（ping 往返）；`cargo build` 通过。
2. **S2 存储层**：CBENC1+DPAPI 读写 + ClipboardEntry JSON 对齐 + 图片目录 + 旧文件加载兼容测试。
3. **S3 监听+捕获**：双通道 + 快照读取 + 图片 alpha 归一化 + 隐私黑名单。
4. **S4 去重+分类+入库**：HashMap 索引 + analyzer + 驱逐/预算 + 事件推送。
5. **S5 热键+配置**：RegisterHotKey ×3 + settings.json 读取。
6. **S6 IPC 客户端库**：`ClipboardIpcClient` 全契约实现 + fake transport 单测。
7. **S7a 面板 exe + 悬浮球（v1）**：迁移窗口代码 + 依赖换 IPC 客户端 + 主题引导 + 单实例 + 引擎探活 + `EntryHost`/`FloatingOrbWindow`/`RecentStrip`（v1 全链路跑通：复制→预览→hover 展开→粘贴）。
8. **S7b 侧边栏（v2）**：`EdgeSidebarWindow` 热区 + 滑出 + 防误触 + 形态策略装配（复用 S7a 共享件，仅新增热区/滑出模块）。
9. **S8 宿主集成**：ClipboardPlugin 代理化 + 右键命令改向 + MenuCommandPipe 调整 + EnsureEngine + backend 开关。
10. **S9 打包**：`cargo build --release` + `dotnet publish` 产物进 dist；构建脚本固化。
11. **S10 文档回写**：1301 更新（C# 默认值修正 + Rust 变体章节）。

### 实现进度（截至 2026-09-12）

| 步骤 | 状态 | 实测验证 |
|---|---|---|
| S1 骨架 | ✅ | 单实例/隐藏窗口/消息循环/IPC 骨架 ping 往返真机通过；0.58 API 差异修复清单见引擎源码注释 |
| S2 存储层 | ✅ | CBENC1+DPAPI 往返、明文旧格式回退、损坏自愈；单测 10/10 |
| S3 监听+捕获 | ✅ | 广播+轮询双通道真机（复制→日志 captured）；CF_DIB→RGBA→真空 alpha 归一化→PNG；OpenClipboard 重试 3×50ms 真机命中（0x80070005 重试成功）；隐私黑名单 23 进程+15 标题词 |
| S4 去重+分类+入库 | ✅ | **40 单测 0 failed；0 warning**。真机全链路：文本/中文/图片捕获入库 ✓；指纹去重（同内容二次复制 history_changed=updated + copyCount=2）✓；事件推送（无订阅者零推送实测 800ms 无数据；订阅后 history_changed+clipboard_changed 摘要含 textPreview≤200/sourceApp/thumbPath）✓；query/get_last 完整字段 ✓；图片条目 PNG 原图（1307B）+ 480px JPEG 缩略图落盘 ✓；UTF-16LE→UTF-8 修复（CF_UNICODETEXT 无 BOM）✓ |

**S4 关键修复记录**：
- IPC 读写并发挂起：读线程阻塞 ReadFile 时独立写线程 WriteFile 实测挂起 → 改为**单线程统一读写 + PeekNamedPipe 非阻塞轮询（50ms）+ 事件队列 drain**；PIPE_WAIT 读超时不生效已排除。
- CF_UNICODETEXT 无 BOM 被误判 UTF-8 → 专用 `read_unicode_text()`（UTF-16LE 解码）。
- 占位符统一 `{{BD_IMG:n}}` 双括号（含 restore 匹配）。
- windows 0.58 签名：PWSTR 在 `windows::core`、`HANDLE` 需显式导入、`OpenProcess` 返回 Result、`GetWindowTextW(hwnd,&mut[u16])` slice 形式、`QueryFullProcessImageNameW` 需 `PROCESS_NAME_FORMAT(0)`、`GlobalFree` 在 Foundation、`SetClipboardData(format,HANDLE(hmem.0))`。
- Subscriber 改 id 注销（Sender 无 PartialEq）；LISTENER 统一放 engine.rs（main.rs 只留 HIDDEN_WINDOW）。

| S5 热键+配置 | ✅ | **43 单测 0 failed；0 warning**。真机：3 热键注册成功（原子 ID 49368-49370，日志逐条确认）；模拟按键 Ctrl+Shift+V→`hotkey triggered: OpenPanel`+面板未建诚实告警（S7a 前）/Ctrl+Shift+P→TogglePause+`pause_changed` 广播/Ctrl+Shift+Backspace→删除最近一条+`history_changed deleted`；配置读取 `%APPDATA%\BetterDesktop\settings.json` 的 `extensions.clipboard-history.*`（capacity=3/storage-mode=PathsOnly 实测生效，BOM 容错）；暂停语义（paused 复制不入库 total=0→resume 后入库）；扩展中心开关 apply_settings enabled=false→`monitoring_changed`+停止监听（复制不入库）→true 恢复 |

**S5 关键修复记录**：
- settings.rs 原直接解析根级 JSON（宿主结构是 `extensions.clipboard-history.*` 节）→ 改为按节提取；缺节→默认值（首次运行正常路径）。
- UTF-8 BOM 容错（PowerShell/编辑器写文件带 EF BB BF，serde_json 拒绝）。
- windows 0.58：`RegisterHotKey(hwnd, id: i32, HOT_KEY_MODIFIERS, vk)` 在 `UI::Input::KeyboardAndMouse`（需开 feature `Win32_UI_Input_KeyboardAndMouse`）；`GlobalAddAtomW` 在 `System::DataExchange` 返回 u16（0=失败退回常量 ID 0xB001）；`HOT_KEY_MODIFIERS` 元组构造（MOD_CONTROL.0|MOD_SHIFT.0|MOD_NOREPEAT.0）。
- 热键语义：Ctrl+Shift+V=open_panel（拉起 `BetterDesktop.Clipboard.Panel.exe --open`，S7a 生效）/Ctrl+Shift+P=暂停恢复/Ctrl+Shift+Backspace=删除最近一条。
- pause/resume 补 `pause_changed` 广播；apply_settings 的 enabled 变化补 `monitoring_changed` + listener.set_enabled（扩展中心开关接线完成）。

| S6 C# IPC 客户端库 | ✅ | **15 单测全绿（fake transport 内存仿真）**；真机验证全链路通过：分页查询（total=3 直通引擎）、CopyEntryAsPlainText（plain 参数写回，剪贴板验证纯文本）、PauseTemporarily(2)→自动恢复、clipboard_changed 事件（id/type/preview）、错误响应→ClipboardIpcException(-32602)。修复 3 个真机 bug：管道名重复前缀（`\\.\pipe\` 被 NamedPipeClientStream 拼两次）；.NET 消息模式 Read 读完一条消息后阻塞等下一条（不返回 0）→改字节模式+行协议（引擎响应带 \n，engine/src/ipc.rs:331）；断线盲区——Peek 无法区分「空闲」与「对端断开」→加 2s 心跳 ping 探测触发重连。引擎配套增量：copy_to_clipboard 增 `plain` 参数（capture.rs write_back(entry,store,plain)，引擎侧抑制回环不变） |

| S7a 面板 exe + 悬浮球 v1 | ✅（悬浮球弃用） | 新工程 `packages/shell/shell-clipboard-panel/`（**0 warning 0 error**，已加 slnx）。**真机全链路**：`--open` 启动 → 主题引导读宿主 settings.json（mode=0/entryStyle 日志确认）→ 引擎探活自动拉起（无引擎进程时面板拉起，双进程存活）→ IPC 连接 → 完整面板窗口可见（'剪贴板历史' 420×640，EnumWindows 实测）→ 毛玻璃 BlurBehind 在 Win11 24H2+ 失效自动降级系统亚克力（日志确认 hr=0x1 降级路径）→ 单实例 Mutex + `--open` 命名事件转发（二次启动不产生新进程）→ 复制触发捕获（OpenClipboard 重试成功→新条目入库）→ clipboard_changed 事件链无异常。**关键修复**：BuildContent 前事件先到导致 _listHost/_entryList null NRE（Reload 判空跳过）。**2026-09-12 用户实测否决悬浮球**（'悬浮球并没有用，还是用侧边栏好一点'）：FloatingOrbWindow/OrbPanel 代码保留未装配，入口整体迁移到 S7b 侧边栏 |

| S7b 侧边栏（默认入口） | ✅ | **用户决策**：悬浮球弃用、侧边栏成为默认入口（entry-style 默认 sidebar；orb/both 均降级装配侧边栏，off 不装配）。新增 `EdgeHandleWindow`（右缘「›」收纳手柄 14×180 常驻置顶，hover/单击滑出）+ `PanelMainWindow.ShowRightAligned`（贴右缘垂直居中 + 180ms CubicEase 滑入动画；--open 与手柄共用 ShowMainWindow 右缘入口）。**真机全链路**：干净启动 → 主题引导 entryStyle=sidebar → 引擎探活经 **LOCALAPPDATA 约定路径** `%LOCALAPPDATA%\BetterDesktop\BetterDesktop.Clipboard.Engine.exe` 拉起（无需环境变量；已把引擎 debug exe 部署到该路径）→ 手柄窗口可见（'剪贴板侧边栏' 右缘 2035-2049）+ 完整面板可见（'剪贴板历史' 右缘 1628-2048）→ 复制捕获入库无异常。**修复 3 个真机 bug（用户实测反馈轮）**：①引擎未连接根因=LocateEngineExe 找不到 exe（发布路径未部署）+ EnsureEngineRunning 进程名写死 `betterdesktop-clipboard-engine`（部署名是 `BetterDesktop.Clipboard.Engine`，匹配不到会重复拉起抢管道）→ 双进程名探测 + 引擎部署约定路径；②悬浮球点击打不开完整面板=WPF DragMove 吞掉 MouseLeftButtonUp（悬浮球已弃用，侧边栏无此问题）；③**UI 重叠（用户截图确认）**=BuildContent 里 BuildList()/BuildFooter() 双调用：第一份 SetRow 后未 Add、第二份 Add 时未 SetRow → 列表与底部栏（含"共3条"）默认落 Row 0 叠在标题上 → 改单次调用 + SetRow 后 Add；④**头部不渲染**=ShowRightAligned 首次把窗口 Show 在屏幕外（wa.Right+2）再动画 Left——窗口屏幕外首显时 DWM 合成异常内容不渲染（像素级验证 0-140px 仅 6 亮像素）→ 改为窗口直接 ShowAt 目标位置 + 内容 RenderTransform 滑入动画；⑤**面板空白半透明板（用户二次截图确认）**=滑入动画写成 `chrome.BeginAnimation(TranslateTransform.XProperty, …)`——XProperty 不属于 Border，运行时抛 InvalidOperationException 被兜底捕获，动画不跑、内容被 RenderTransform 永久平移出窗口 420px → 改 `transform.BeginAnimation`（动画目标必须是 transform 对象本身）+ From=Width 显式起止；⑥**列表条目不渲染**=CreateListContainerStyle 的 ListBoxItem 模板只有空 Border、无 ContentPresenter → 条目内容完全不呈现（分页 total=3 但列表区空白）→ 模板改 Border+ContentPresenter；⑦**引擎 console 窗口弹出**（部署后全屏抓屏确认黑色命令行窗）=Rust exe console subsystem → StartEngine 改 UseShellExecute=false + CreateNoWindow=true，MainWindowHandle=0 验证。**验证方法学教训**：CopyFromScreen 抓半透明窗口会透出底下内容造成假阳性（13:50"列表正常"实为底下 CodeBuddy 代码）→ 改用 PrintWindow(PW_RENDERFULLCONTENT) 抓窗口自身渲染；PowerShell 5.1 命令行中文字面量编码损坏导致 -eq 匹配失效 → 脚本文件方式执行。**最终真机验证（PrintWindow）**：标题/搜索/分类/3 条历史（文本+2 图片 547×845）全显示、无重叠、无空状态误显。**遗留**：多显示器 v1 仅主屏 workArea；侧边栏收起由面板 MouseHook 外点/Esc 承担（无收起动画） |

| S8 宿主集成 | ✅ | **用户口径「别改回原方案」下完成 engine 化接线**（2026-09-12）。`ClipboardPlugin` 重写为双后端：默认 `backend=engine`（`ClipboardIpcClient` + `NamedPipeTransport` Provide `<IClipboardService>`；宿主不再监听/存储/建面板），`backend=legacy` 仅为紧急回退。新增 `ClipboardEngineLauncher`（IPC 包，宿主与面板共用引擎/面板定位与无窗口拉起）；`ClipboardIpcClient` 补 `ApplySettings`（引擎 `apply_settings` 为 kebab-case 部分字段合并）/`RequestExit`。设置变更经 `apply_settings` 推送（enabled 触发引擎 `monitoring_changed` + 监听启停）；`Reconnected` 后自动重推全量配置。`ClipboardShellMenuRegistrar.EnsureRegistered(panelExePath)` 参数化：4 场景命令改向 `"<panelExe>" --open`（宿主未运行仍可用，O1），面板缺失回退宿主命令，含旧宿主命令迁移判定。设置分区新增存储模式（full/paths-only）、总存储预算、文件副本上限、快捷入口形态（sidebar/off/both/orb）、引擎状态与后端快照。**真机验证**：宿主启动后注册表 4 场景全部改向 `%LOCALAPPDATA%\BetterDesktop\BetterDesktop.Clipboard.Panel.exe --open`；`EnsureEngine` 复用已有引擎进程未重复拉起；面板 `--open` 独立启动成功（total=3 分页加载正常）；写入剪贴板探针 4s 后引擎日志 `new entry 84bcf8d0-…` 入库、宿主日志**无 `[Clipboard]` 特征行**（旧 `ClipboardManager` 未运行）→ 双写同一历史库的隐患消除。构建 0 警告 0 错误；`shell-clipboard-ipc-tests` 15/15 + `shell-clipboard-tests` 85/85 全绿 |
| S9 打包部署 | ✅（脚本固化） | 新增 `scripts/deploy-clipboard.ps1`：停面板 → 拷面板输出目录 → `cargo build --release`（失败回退已有 debug 产物）→ 引擎 exe 落 `%LOCALAPPDATA%\BetterDesktop\`。`scripts/publish.ps1` 暂未纳入（dist 全量发布会另批收口，本批以约定路径部署为准；S7b 已把引擎 debug exe 部署到该路径，本次补面板） |
| S10 文档回写 | ✅ | 本文进度表补 S8/S9/S10；`packages/shell/shell-clipboard/README.md` 更新 engine 默认后端 + 右键改向 + 设置分区；`docs/2026-09-11-resident-architecture.md` B4 行「剪贴板按用户决定跳过（半成品）」改为随 S8 收口（含宿主不再双写的说明）；1301 技术库补 Rust 变体接线段 |
| S12 图片去重修复 | ✅ | **用户报告**：系统截图会在历史里留下两条相同记录，违背"砍重复"底层逻辑。**根因**：`ClipboardEntry::fingerprint()` 的 Image 分支用 `image_path` 作指纹，而它是 `clipboard\images\{id}.png`（**含条目自己的 id**）→ 每次捕获都不同 → 图片**永不命中去重**；截图工具一次复制连续写多个剪贴板格式（CF_PNG/CF_DIB），于是每次写入都建新条目。**修复**：新增 `engine/src/fingerprint.rs`（**像素级**内容指纹：解码 RGBA → SHA256 前 16 hex；截图两次字节不同而像素相同，字节哈希会漏判）；`ClipboardEntry` 增 `content_hash` 字段（serde default，C# 忽略未知字段），`build_entry` 落盘时写入；`fingerprint()` Image 分支改用 `content_hash`（为空时退回路径指纹，绝不误合并）；新增 `Store::dedupe_images()` 启动迁移（补历史指纹 + 合并重复 + 级联清理 + 转移 pinned/copy_count，原图缺失则保留），`engine::init` 调用并在有变更时落盘。**真机验证**：`Clipboard.SetImage` 连写两次 → 引擎收到 4 次更新、日志 `new entry 19321630-…` 后连续 `updated entry … (copy_count=2..4)`，`total` 仅 +1（修复前每次写入都新增）；测试条目已删除、`total` 复原；库中 3 张图片无重复指纹。Rust 测试 54/54（新增 7 项：像素指纹 4 + 去重迁移 3）|
| S11 搜索/分类下沉 + 窗口基类统一 | ✅ | **用户口径**：规模由 Rust 引擎扛、"更应该做好的是搜索与分类"（"没人在万条里大海捞针"）。引擎 `cmd_query` 新增 `kind`/`pinned`/`sourceApp`/`keyword` 过滤 + **`total` 改为过滤后命中数**（旧实现返回全库条数，带过滤时客户端分页判断失真）+ 列表摘要载荷（剥离 htmlContent/rtfContent、content 按**字符**截断 1024，中文按字节切会 panic）；新增 `list_sources`（来源统计下沉，替代客户端拉全量再分组）。`ClipboardIpcClient` 删除 `FetchAll`（循环拉满 500/页直到全库）与 `ApplyLocalFilter` —— 此前只要有搜索词或点筛选 chip，整库（含 MB 级 HTML 正文）就会序列化到 UI 线程，分页形同虚设；现全部条件直通引擎，UI 永远只取一页。面板修复「收藏」chip 漏传 `pinned` 导致的筛选失效；列表恢复**虚拟化**（`VirtualizingPanel.ScrollUnit=Pixel` —— 像素滚动与容器回收兼得；曾误判"像素滚动必须放弃虚拟化"而设 600 行上限，经用户否决"会影响体验"后撤除）+ 图片懒解码/滚出释放（虚拟化只回收容器，不回收内容对象持有的位图）。窗口基类：`UseNoActivateWindowStyle` 上提 `ShellWindow`（默认 false，弹层/常驻浮窗 override true，`MakeFloatingNoActivate` 收敛为单一消费点），`PopupWindowBase` 只改默认值，常驻手柄 `EdgeHandleWindow` 由裸 `Window` 改为继承 `ShellWindow`（补 `ChromeBorder`，否则基类 DEBUG 断言直接 FailFast）。**真机验证**：引擎侧 `total` 19 →(kw=PROBE) 15 →(kw=不存在) 0 →(pinned) 0 →(kind=图片) 4，`htmlContent` 长度 0（摘要生效），`list_sources` = `BetterDesktop.Host, CodeBuddy CN, explorer`；面板 `--open` 进程存活、分页日志 `total=19`。**虚拟化实测**：用 UIA 数 ListBox 顶层子元素，25 条与 527 条（导入 500 条压测、验证后已按 id 精确清理回 25）均为 **9 个**；面板内存 162.4MB → 158.8MB（数据涨 20 倍而内存不升）。构建 0 警告 0 错误；Rust 47/47（新增 4 项：keyword 命中面 / 多条件取与 / 摘要按字符截断 / 来源排序）、IPC 15/15、契约 85/85 |
| S13 分类真空地带修复 | ✅ | **用户报告**：复制的一段代码显示为 HTML，但"代码"分类里找不到记录 —— 明确的真空地带。**双重根因**：① `analyzer::is_code_text` 的**缩进信号完全失效**（先 `trim()` 再 `trim_start()` 比长度恒相等 → `indented` 恒 0；C# 原版同款 bug 被"忠实移植"），且关键词表偏 .NET/C（无 `fn`/`use`/`impl`/`pub`）→ 从 IDE 复制的 Rust 片段一个词都命不中；② 面板筛选**维度混用**："文本/图片/文件"按剪贴板**格式**(kind)、"代码"按**语义**(category)，且 RichText 没有 chip → 带 HTML 格式的代码（contentType=Html、category=RichText）在任何 chip 下都查不到。**修复**：修缩进判定（用未 trim 的原始行判前导空白）+ 关键词表扩充 Rust/JS-TS/Python/Go（`fn`/`use`/`impl`/`pub`/`mut`/`match`/`crate::`/`std::`/`async`/`await`/`export`/`from`/`func`/`package`/`val`/`elif`/`lambda`/`def`/`self.`/`#include`/`console.`/`print(`）；新增 `Store::reclassify_all()` 启动迁移重算历史 `category`；面板 chips 统一为 `category` 维度并补 **「富文本」chip**（Text/Code/RichText/Image/File 完备覆盖，与行内 CategoryLabel 一一对应）；`CreateChip(key, label)` 消除"标签反推 key"的双真相源（本次正是踩到它）。**真机验证**：迁移日志 `reclassified=2`；`category=1(代码)` 由 **0 → 2**（正是用户复制的两段 Rust，`isCode=True`），富文本 6 → 4，各分类计数之和 = 26 = 全库 total（无条目丢失）。Rust 58/58（新增 4 项：Rust 片段识别 / 缩进信号回归 / HTML 富文本不误判 / 分类重算幂等），构建 0 警告 0 错误 |
| S14 UI 状态同步 + 预览容量 | ✅ | **用户报告三问题**：① 收藏"点了没反应/取消不了"；② 暂停图标在"点横幅恢复"后一直保持选中态；③ 长文本显示文字太少、开头相同的两条分不出来。**根因**：① `OnRowPin` 只调引擎、**不更新本地 `entry.IsPinned` 也不刷新行内视觉**，而引擎 `pin`/`unpin` **有意不广播** `history_changed`（广播会触发面板 `HistoryChanged→Reload` 整页重建、丢滚动位置）→ 再点仍是 Pin（取消不了收藏）、★ 标记不动；② `pauseBtn` 是**局部变量**（未存字段），`OnPauseState` 只隐藏横幅、**从不复位 `IsChecked`** → 任何非"再点图标"的恢复路径都留下永久选中态；③ 瓶颈不是 `Preview` 的 200 字截断，而是 `RecentStrip` 的 `MaxHeight=44`（**3 行 ≈ 80 字**）。**修复**：① 更新本地 `IsPinned` + `RefreshRow()` 重建单行（保持顺序与滚动位置），「收藏」筛选下取消收藏则整页重载，失败写 `_countLabel` 让用户可见；行内 📌 按钮改为状态化（已收藏 → ★ + 强调色）；② `_pauseBtn` 存字段，`OnPauseState` 作为**唯一真源**按引擎事件同步 `IsChecked`，`catch` 分支回正；③ `PreviewMaxHeight 44→90`（6 行）+ `Preview` 截断 200→500 字（`PreviewMaxChars`），短文本按内容自适应。**真机验证**：UIA 读 `TogglePattern`：初始 Off → 点图标 On → IPC `resume`（等价"点横幅恢复"/外部恢复）后 **Off**（修复前恒为 On）；UIA 读行高：长文本 136–148px（6 行生效）、短文本 85px（自适应）；收藏往返 pin 2→unpin 1（库中原本已有 1 条收藏，逻辑正确且已复原）。构建 0 警告 0 错误 |
| S15 分类语义收紧（格式与语义解耦） | ✅ | **用户质疑**："这个代码的判断还是有些模糊 —— 我从 md 文档复制的全文被判定为 HTML，那从 docx 复制的论文（含图片、格式、字体、大小、表格）会被判定为什么？"**根因**：`analyzer` 的 RichText 判据是 `html 非空` —— 于是 md 全文、网页段落、docx 论文**全是"富文本"**，`has_images`/`has_table` 只当了徽标、没参与分类，分类毫无区分度。**修复**：新增 `has_rich_features()`，RichText 判据改为"**含视觉排版信息**"（`<img`/`<table`/`<tr`/`<td`/`<svg`/`<video` 或 `font-family`/`font-size`/`font-weight`/`font-style`/`color:`/`background-color`/`text-decoration`），刻意不含 `p`/`h1-6`/`ul`/`li`/`strong` 等结构标签（否则退回旧行为）；列表条目标签由 `ContentTypeLabel`（格式名 HTML/RTF）改为 **`CategoryLabel`（语义）**，与筛选 chips 同维度，格式信息移入 ToolTip。**真机实证（同一 contentType=Html，分类被分开）**：`<h1>+<p>+<ul>+<strong>` 纯文字全文 → `category=0`（**文字**，收紧前会是富文本）；`<h1 style="font-family…font-size…">+<table>+<img>` docx 论文式 → `category=2`（**富文本**，hasImages/hasTable=True）。历史条目由既有 `reclassify_all()` 启动迁移自动归位（本轮 `reclassified=2`）。Rust 61/61（新增：纯文字 HTML→Text / docx 式→RichText / 带图网页→RichText）、构建 0 警告 0 错误 |
| S16 代码判定加密度门槛 | ✅ | **用户报告**：12.6KB 中文技术文档（功能说明 + 代码示例 + 术语）被判为代码。**精确诊断**（用 `get_entry` 取全文，而非 `query` 的 1024 字摘要——首轮诊断就踩了这个坑）：122 行/115 非空行、缩进仅 10%（**不是缩进问题**）、括号平衡、**关键词命中 11 个**（`import`/`interface`/`string`/`=>`/`end`/`crate::`/`std::`/…）→ `keyword_hits >= 2` 命中，判为代码。**根因**：关键词只数绝对个数、不看**密度** —— 文档被上百行正文摊薄（11/115 = 0.10），真代码则几乎每行都含关键词。**修复**：`keyword_hits >= 2` 收紧为 `keyword_hits >= 2 && kw_density >= 0.15`；注释分支同样加门槛（`>= 0.10`）；移除 `end`（子串匹配下 `append`/`depend`/`send`/`backend` 全命中，纯噪声）。**真机验证**：该文档条目 `category=1(代码) → 0(文字)`、`isCode=False`；分类计数 文字16/代码2/富文本5/图片4/文件1 = 28 = 全库（守恒）；迁移 `reclassified=1`。Rust 62/62（新增「技术文档 + 少量代码示例 → 不判代码，对照组纯代码仍判代码」）、构建 0 警告 0 错误 |
| S17 跨格式文本去重（按内容而非格式） | ✅ | **用户报告**："他也被文本与富文本同时认证"。**精确比对**（`get_entry` 取全文）：两条记录 `content` 长度均为 12596 且**逐字相同**，仅 `contentType`（0=Text / 3=Html）与 `htmlContent`（0 / 140358）不同。**根因**：`fingerprint()` 带类型前缀（`text:` / `html:` / `rtf:`）→ 同一段文字从纯文本源（记事本、md 源码）与富文本源（网页、聊天）各复制一次 = 两个指纹 = 两条并存，且因 html 有无被判成「文字」「富文本」两个分类。**修复**：Text/Html/RichText **统一按剥离后纯文本取指纹**（纯文本为空时退回 html/rtf，如只含图表的 HTML）；`upsert` 命中分支新增**格式升级**（已有缺 HTML 而新条目带 → 补齐 `html_content`/`rtf_content` 并升级 `content_type`/`category`，只升级不降级，避免"先纯文本后富文本"导致后续粘贴丢格式）；新增 `Store::dedupe_text_duplicates()` 启动迁移（保留最早一条以维持列表位置 + 内容取最丰富一侧 + 合并 pinned/copy_count/timestamp + 级联清理）。**真机验证**：迁移 `text-merged=3`，全库 28 → 25，含「功能ID」条目 2 → 1（保留 Html 版本、`htmlContent` 140358 字节完整、`copyCount=3`）；分类计数 文字13/代码2/富文本5/图片4/文件1 = 25（守恒）。Rust 64/64（新增：同文本跨格式指纹一致 + 迁移合并、upsert 命中格式升级）、构建 0 警告 0 错误 |
| S18 空数据不入库 | ✅ | **用户要求**："复制不要录入空数据，这个没有意义，只会占用历史记录的位置"。**现状核查**：`on_clipboard_update` 仅有 `let Some(snapshot) = read_snapshot() else return`，**无任何空内容检查** → 空快照照样走 `build_entry` 入库。**触发场景**：剪贴板被清空（`EmptyClipboard`）、复制了引擎不支持的类型、复制纯空白文本、空壳 HTML。**修复**：新增 `effective_content_len()` —— **按"有效内容长度"而非字节数**（用户提"用存储大小判定"，但字节数双向误判：纯空白 3 字节 / 空壳 HTML 30 字节会漏判；而 paths-only 的文件条目 `size_bytes==0`、图片的文本字段全空会误杀）→ 图片/文件按有无计 1、文本取 `trim` 后字数、HTML 取剥标签后字数（空壳=0，含图/表=1），返回 0 则跳过并记 `empty snapshot skipped`；新增 `Store::purge_empty()` 启动迁移清除历史真空条目（**保留**图片 / 文件 / `content_in_bin` 条目）。**真机验证（对比实验）**：写入纯空白（3 字节）+ 空壳 HTML（30 字节）→ `total` **保持 24 不变**（若按字节数判定会变成 26）；图片 4 条 / 文件 1 条**未被误杀**；日志 `empty snapshot skipped`。随后写入真实内容 → `total` +1（对照组）。Rust 66/66（新增：有效内容长度 8 场景判定，含"字节数>0 但有效长度=0"与"字节数=0 但必须保留"双向用例 / 真空条目清理与豁免）、构建 0 警告 0 错误 |
| S19 阶段收尾体检（三路审查 + 日志体检） | ✅ | **用户要求**："第一阶段功能开发要到头了，检查还有没有 bug，不能带着问题去开发新功能"。**方式**：三路只读审查（契约一致性 / 遗留与死代码 / 异常边界）+ 运行日志体检。**共修 15 项**（3 崩溃级、3 数据级、4 卡死/泄漏级、5 健壮性）：① `capture.rs` DIB 解码**先校验数据长度再分配**（原按 header 声明尺寸预分配 → 恶意 DIB 致数十 GB 分配 → abort/capacity overflow → 引擎崩溃）+ stride 改 u64；② `engine.rs on_clipboard_update` 的 `lock().unwrap()` 改 `unwrap_or_else(into_inner)`（锁中毒后每次捕获都 panic）；③ **`store.rs evict` 级联清理是死代码**（按 id 回查已删条目 → 永远找不到 → 孤儿文件堆积 + 预算驱逐"越驱越占"；改收集条目本体直接清理）；④ `save()` 改 **temp + rename 原子写**（原直接覆盖，写盘中断 → 自愈为空历史 = 整库丢失）；⑤ `set_html_entry` 图片扫描 64 → 512（超出则占位符不还原 = 内容不可逆损坏）；⑥ `ipc.rs` 首帧等待加 5s 超时（原无限循环占满连接槽 → 所有客户端连不上）；⑦ `read_message` 加 32MB 帧上限（原无限累积可 OOM）；⑧ `SUBSCRIBERS` 锁中毒改 into_inner（原 `Ok` 判断 → 事件永久静默）；⑨ 客户端 `_wake.Wait` 纳入 try（原在 try 外，Dispose 竞态 → 进程终止）；⑩ 断线清 `_sendQueue`（原重连重放 → 非幂等命令重复执行）；⑪ 面板 banner 字段判空（BuildContent 前事件先到 → NRE 静默失败）；⑫ **重连后 Reload()**（原只隐藏横幅 → 面板永远空着）；⑬ `Truncate` 不切代理对（emoji 乱码）；⑭ 设置 UI 移除已废弃「悬浮球」选项（选到不生效形态）；⑮ `WriteFile failed` 正常竞态降为 info（实测 1835 条 ERROR 淹没真错误）。**日志体检**：无功能性故障（1903 条"失败"中 1835 条为上述竞态、34 条同源、14 条 OpenClipboard 重试；`ipc-client`/`tray` 零错误；`popup-trace` 的"异常堆栈"实为基类主动 DiagTrace 误报）。**验证**：Rust 68/68（新增 evict 级联清理、save 原子可读回）、契约 87/87（新增 Preview 代理对/截断）、IPC 15/15、构建 0 警告 0 错误。**已知限制已登记**：`ShowFavoritesOnly`/`CloseHistoryWindow` no-op、`IsMonitoringEnabled` 初值、非分页 `GetFilteredEntries` 上限 500、面板 UI 线程同步 RPC、死代码 `FloatingOrbWindow`/`OrbPanel` 待拍板、引擎 5 个 Settings 未实装 |
| S20 死代码清理 + 侧边栏手柄重定 | ✅ | **用户拍板**：① 删除死代码；② 侧边栏"太大也太敏感（鼠标放在区域就触发）"→ 改点击触发 + 可拖动移动 + 箭头更显眼。**① 删死代码**：`FloatingOrbWindow.cs`/`OrbPanel.cs`（无任何调用点 + 继承裸 `Window` 违反统一基类纪律）已删除；`EntryHost` 注释与 `orb/both` 分支改为"历史值静默降级"；`App.xaml.cs` 删悬浮球占位订阅（`Host.ClipboardChanged += _ => {}`）与 2 处过时注释；`PanelTheme` 的 entry-style 兜底 `"orb"` → `"sidebar"`；README 删悬浮球行 + 更新 Known Limitations。**② 手柄重定**（`EdgeHandleWindow`）：hover 触发展开 → **点击触发**（hover 仅提亮箭头）；**拖动自实现**（`CaptureMouse` + 手动位移 —— 不用 WPF `DragMove`：它会吞 `MouseLeftButtonUp` 且无法锁水平，这正是悬浮球时代遗留未解的问题），水平锁右缘、垂直夹工作区，松手按位移 >3px 区分拖动/单击，位置存 `%LOCALAPPDATA%\BetterDesktop\clipboard-panel-state.json`（**不写宿主 settings.json**，避免双写覆盖）；坐标换算避开 `PointToScreen`（物理像素 ≠ DIP，缩放屏会漂）；高度 180→120；箭头 muted/14px → **`AccentBrush`/22px/Bold**。**真机验证**（UIA + 鼠标模拟）：手柄尺寸 14×120 DIP（UIA 读 18×150 物理像素 ÷ 1.25 DPI）、悬停 2s 不开面板 ✓、单击开面板 ✓、点外部收起 ✓、拖动 `Top 591→741`（+150，且不误触发展开）、状态文件 `{"handleTop":592.8}`（= 741÷1.25 ✓ DIP 存储）。构建 0 警告 0 错误 |
| S21 设置控制补齐 + 双收起机制 | ✅ | **用户口径**：①"设置中对于剪贴板功能的控制太糙"；②"加入两种收起机制，一个是现在的，另一个是手动收起"。**① 设置补齐**：引擎 `Settings` 有 14 项而设置界面只暴露 7 项 + `apply_settings` patch 只推 8 键 → 新增 9 个控件（监听开关 `enabled`、后端模式 `backend`、图像素上限、缩略图宽度、HTML 内嵌图上限、单条文本上限、文本总量上限、事件推送、收起方式），并把分区重组为「捕获与容量 / 存储与空间 / 面板与入口 / 引擎与诊断」四卡片（热键与隐私保持只读说明）；新增 `ChoiceRow` 统一枚举型设置的 SelectedIndex 映射。**补 `ClipboardPlugin` 的 patch 6 键 + `WatchKeys` 6 键**（此前缺失 → 界面改了传不到引擎）。**② 双收起机制**：`extensions.clipboard-history.dismiss-mode` = `auto`/`manual`，落 `PopupWindowBase.AutoHideOnOutsideClick` 虚属性，**同时受控外点钩子与 `Deactivated` 两条路径**（只关一处会漏）；手动模式面板底部提示改「手动收起（✕ / Esc）」。**真机验证**（UIA + 鼠标模拟，仅面板无宿主干扰）：manual 模式下点手柄展开 ✓、点面板外**不收起** ✓、点「✕」收起 ✓；还原 auto 后点面板外**收起** ✓（两模式行为相反且都正确）；引擎接受 6 个新键 `{"ok":true}` 且旧键回归正常。**过程中修掉两个自引入缺陷**：① 改完只 build 未 deploy → 跑的是旧 exe（验证假阴性）；② `LostMouseCapture → EndDrag` 一刀过重 → 捕获若在按下时丢失会清掉 `_dragging`，`MouseUp` 直接 return，**点击语义被整条吃掉**（症状：点手柄不展开且 `popup-trace.log` 无新记录），改为仅在 `MouseMove` 内"左键已松开则自愈"，顺带修掉"点击后移开鼠标被误当拖动、手柄位置被写坏成 `handleTop=281.6`"。构建 0 警告 0 错误 |
| S22 设置滑条布局修复 + 双击粘贴顺序修复 | ✅ | **用户实测反馈**：①"控制也太率了…也要提供给用户修改的机会"（截图可见 9 个滑条全渲染成白色圆点）；②"双击粘贴是什么鬼"。**① 滑条**：根因是 `SliderRow` 的 `DockPanel` Add 顺序 —— `labelText` 被放在最后成了**填充元素**、占满剩余宽度，滑条被压成 **0 宽只剩 Thumb 圆点**（不是"不能改"，是渲染坏了）。重排为 `labelText`(Left) → `valueText`(Right) → `slider`(最后=填充) + `MinWidth=160`；同文件 `Labeled`（Combo 行）本就是该顺序且渲染正常，构成同文件对照实验。**② 双击粘贴**：`SendPaste` 用 `keybd_event` 把 Ctrl+V 发给**当前前台窗口**，而面板 `UseNoActivateWindowStyle=false`（可激活，为搜索框）→ 原代码顺序是"先粘贴后 `HidePopup`"，Ctrl+V 全打在面板自己身上 → 用户感知"毫无反应"。改为**先 `HidePopup()` + 120ms 延时再粘贴**（对齐 legacy `ClipboardHistoryWindow` 的既有正确顺序），提示文案由"双击粘贴"改为"**双击粘贴到原窗口**"。真机验证：双击后面板正确收起、`panel.log` 无"粘贴失败"。构建 0 警告 0 错误 |
| S23 双击粘贴移除 + 滑条范围与可编辑化 | ✅ | **用户实测反馈**：①"没用，而且我们也不用这个功能"（指 S22 刚修的双击粘贴）；②"存储总上限才 4 个 G，面对上万的条目上限也太不合理了"。**① 移除双击粘贴**：S22 按 legacy 顺序修好后用户实测**仍无感** —— 该功能先天脆弱（面板自身可激活 → 焦点归还时机不可靠），且产品明确不需要 → 删除 `OnMouseDoubleClick` 与提示文案（改为"单击复制 · Esc 关闭"），并清理唯一调用方已消失的 `FindVisualParent`。**② 滑条范围与可编辑化**：范围与"条目上限"脱节是硬伤（10000 条里混进截图/大文件时 4GB 上限远远不够）→ 总存储预算 4GB→**200GB**、容量 1 万→**10 万条**、保留 365→**3650 天**、单图 256→**2048MB**、像素 200→**500MP**、文本总量 1024→**20480MB**、缩略图宽度 1024→**2048px** 等；范围放宽后纯拖动必然调不准 → 数值改为**可直接输入的编辑框**（回车/失焦提交、非法输入回退、输入中不被滑条回写覆盖）+ 关掉刻度吸附（`IsSnapToTickEnabled=false`）用于粗调。**真机验证**（UIA 选中 `ClipboardSection` 后测量，此前几轮因"先找 Text 元素而非 ListItem"选不中分区，本次改用 `SelectionItemPattern`）：11 个 Slider 宽度 **449–456px**（修复前 ≈0、只剩 Thumb 圆点）、11 个可编辑数值框；读到的 424/210/4096 经核对是**用户历史设置值**（正好落在旧范围 10–500 / 7–365 / 200–4096 内），非初始化污染。构建 0 警告 0 错误；IPC 15/15、契约 87/87、Rust 68/68 |
| S24 存储软限制 + 设置实时生效 | ✅ | **用户口径**：①"还得注意修改实时生效"；②"总上限不是占位的上限，是历史数据达到存储上面也可以存储，但我们要发消息提醒用户及时清理"。**① 总存储预算改软限制**：`Store::evict` 第 3 条（超预算即删最旧未收藏）**移除** —— 此前会静默丢弃用户历史（且曾因孤儿文件计入 `total_used_bytes` 而"越驱越占"）；改为引擎上报 `storage_status`（usedBytes/budgetBytes/usedMb/budgetMb/overBudget/entries/unpinned/capacity），面板顶部横幅提醒 + 一键「清理未收藏」（复用 `clear_unpinned`，收藏条目受保护）；新增单测 `evict_never_drops_entries_for_total_budget` 锁死语义（有人把预算驱逐加回来会立刻红）。**② 设置实时生效**：引擎侧 `cmd_apply_settings` 合并配置后**立即跑一次 evict 并落盘** + 广播 `history_changed`（此前只在捕获时驱逐 → 调小 capacity 要再复制一次才生效，观感像"没生效"；锁序：块内 clone 快照出块释放锁再取 store）；面板侧在**每次打开面板**时 `PanelTheme.Load()` 重读 settings.json，覆盖 dismiss-mode 与 entry-style（`EntryHost.RefreshEntryStyle` 即时显隐手柄），无需重启面板。**真机验证**：预算调 1MB → `apply_settings` 返回 `evicted:0`、`overBudget=true`、条目 28 守恒；恢复 1024MB → `overBudget=false`、仍 28 条（**全程零删除**）；面板横幅实测显示「存储已用 6 MB / 预算 1 MB（共 28 条）· 建议清理」+「清理未收藏」按钮；**实时生效实测**：面板进程不重启（以 manual 启动的实例）、仅改配置文件 → 点面板外立刻从"不收起"变为"收起"。**过程中踩两坑并修掉**：① 部署脚本在 `cargo` 不在 PATH 时**静默回退旧引擎产物** → 新 IPC 报 Method not found，差点误判为代码 bug（现要求部署后核对产物时间戳）；② 存储横幅在 `ShowAt()` 之前查询 → `BuildContent` 尚未执行、`_storageBanner` 为 null 被静默跳过（表现为"超限也不提醒"）。构建 0 警告 0 错误；IPC 15/15、契约 87/87、Rust 69/69 |
| S25 过时淘汰触发点补齐 + 设置键读取修复 | ✅ | **用户追问**："我们过时淘汰机制可以正常运行吗？"。**核查结论：逻辑正确、触发点缺失**。`Store::evict` 真机验证有效（导入 2020-01-01 条目 → `evicted: 1`，被正确清掉），但此前只有两个调用点：`on_clipboard_update` 的 **`New` 分支**（新条目入库）与 `apply_settings` —— **长时间不复制新内容（或只重复复制同一内容走 `Updated` 分支）时，过期条目永远不会被清理**，`retention-days` 形同虚设；引擎启动时也不淘汰。**补齐**：① `init` 末尾 `sweep_evict("startup")`；② 新增 `start_eviction_sweeper()` 定时线程（每 **30 分钟**，对齐 legacy 面板"每小时 DispatcherTimer"思路，`main` 在 init 后启动）；两者共用 `sweep_evict(reason)`（仅在真有驱逐时落盘 + 广播 `history_changed`）。**顺带修一个真 bug**：`ISettingsService`（设置界面）把 `extensions.clipboard-history.<key>` 存成**扁平键**，而 `PanelTheme` 只读**嵌套节** → 用户在设置里改 `entry-style`/`dismiss-mode` 时宿主照常生效、**面板读不到**（"改了设置只有面板没反应"）；现按「嵌套节 → 扁平键覆盖」两段读取。**真机验证**：隔离捕获路径（先 `enabled=false` 停监听）后导入 2 条过期条目 → 等 autosave 落盘 → 重启引擎，日志 `loaded 30 entries` → `startup eviction removed 2 entries` → `eviction sweeper started (every 1800s)` → 重启后 28 条、残留 0；扁平键 `dismiss-mode=manual`（嵌套节无此键）→ 重启面板 → 点面板外**不收起** = 确认读到扁平键。构建 0 警告 0 错误；Rust 69/69、IPC 15/15、契约 87/87 |
| S26 CF_HTML 双重包装修复 + HTML 纯文本兜底 | ✅ | **用户实测报告**（贴出粘贴结果）：内容里出现 `Version:0.9 / StartHTML:0000000105 / EndHTML:… / StartFragment:…` 元数据，"从第二个开始就被一股脑输出"。**根因（两个叠加）**：① 剪贴板 `HTML Format` 是**带偏移量头**的 CF_HTML 串，**捕获时未剥头**就存进 `html_content`，写回时 `wrap_html_for_clipboard` 又包一层 → **双重 CF_HTML 头**，应用解析失败后回退显示整串元数据；② 部分应用富文本复制**无 `CF_UNICODETEXT`** → 条目 `content` 为空 → 无兜底格式。**为什么"从第二个开始"**：第 1 条是图片（正常），第 2 条起才是 HTML 条目。**修复**：① 新增 `html::strip_cf_html_header`（捕获时剥头）+ `Store::strip_cf_html_headers`（存量迁移，真机 `cf-html-stripped=11`）；② 新增 `html::html_to_text`（去标签/解实体/块级转行/压空白）→ 捕获时对空文本回填 + `Store::backfill_html_text` 存量回填（只要 html 非空就重算、结果不同才写，使算法修正能覆盖旧结果）；③ `MergePasteToActiveWindow` 不再用本地 `e.PlainText`（摘要载荷可能为空）→ 逐条 `get_entry` 回引擎取全文。**过程中自查出并修掉一个自引入缺陷**：首版 `html_to_text` 用 `s.replace("</p", "\n")` 前缀替换，把 SVG 的 `</path>` 也啃成 `ath`（真机回填出现 `ath / athimage.png` 乱码）→ 改为解析标签名后按白名单精确匹配。**验证**：写回剪贴板后 `Version:0.9` **只出现 1 次**、偏移为真实值；6 条 HTML 条目回填后 `ath` 残渣全为 False、纯文本可读（如「但 dock 必须是顶层窗口」）；迁移幂等（二次启动 `cf-html-stripped=0`）。构建 0 警告 0 错误；Rust **75/75**（新增 strip 幂等/去元数据、html_to_text 实体与 SVG 回归等 6 项） |
| S27 按序粘贴自吞修复 + CF_HTML 尾部字段剥净 | ✅ | **用户实测**："会把第一和第二丢掉，直接从第三开始，似乎点击有序粘贴后有一个自动泄露的问题"。**定位**：不是玄学 —— 按序粘贴时**我们自己也会发一个 Ctrl+V**（`SendPaste`），而"会话激活时吞掉 Ctrl+V"的键盘钩子**分不清这个 Ctrl+V 是用户按的还是自产的** → **自吞自**：序号已前进、内容没粘出去 → 表现为"某几条凭空消失"。此前抑制窗口只在"用户按键"那条路径设置，**首次粘贴那条路径漏了**。**修复**：① `PasteNextSequential()` **内部**统一置 `_suppressPasteHook`（覆盖首次粘贴 / 用户 Ctrl+V / Ctrl+Shift+V 全部路径），钩子见此放行；**不能立刻复位**（钩子回调异步到达）→ 250ms 后 `DispatcherTimer` 复位；② 首次粘贴延时 120 → **250ms**（面板刚 Hide 时前台切换未完成，太早发会打在自己身上）。**顺带修掉 CF_HTML 尾部残留**：`SourceURL` 位于 `EndFragment:` **之后**，首版只跳到 `EndFragment:` 就停 → 正文首行残留 `SourceURL:file:///…`；第二版加条件"含 `Version:`/`EndFragment:` 才剥"又导致**已被剥过一半的存量条目不再匹配、永远修不掉**（真机第 2 条即为此）→ 改为**逐行跳过开头连续的白名单头字段**（`HEADER_KEYS` 8 项）直到正文，干净 HTML 首行即返回（代价一次 find）；迁移侧改为**无条件调用**（幂等）。**真机端到端验证**（勾选历史前 3 条 → 按序粘贴 → 模拟 Ctrl+V×2，逐步读剪贴板）：①`SCROLL-PROBE-14` = 第1条 ✓ ②`SCROLL-PROBE-13` = 第2条 ✓ ③`SCROLL-PROBE-15` = 第3条 ✓（**一条不丢、顺序完全正确**）。**踩坑记录**：验证模拟的 Ctrl+V 粘出内容会被引擎**当新复制再次捕获入库**（`SCROLL-PROBE-*` 回流）→ 收尾清理 16 + 1 条，总数回到 17（用户真实内容）。构建 0 警告 0 错误；Rust **78/78** |
| S28 按序粘贴误判纠正 + 防御性改动回退 | ✅ | **用户后续澄清**："不，你第一次成功了，没有丢失历史。只不过一个勾选的顺序问题" —— 即"漏了第一个"**实指勾选先后顺序与列表顺序不同**（`_selected` 本就是 `List` FIFO、勾选先后即粘贴顺序，实现正确）。**S27 的自注入抑制保留**（正确且无害）；但 S27 后追加的"前台是本进程则拒绝粘贴 + 轮询等前台让出"双保险**基于错误前提**，真机直接把正常粘贴拦死（记事本始终为空、日志却照打"已粘一条，剩余 N"——该日志是**无条件打印**的，不反映真实结果）→ **全部回退**为固定 250ms 延时。**真机验收**（记事本为目标、`Ctrl+A/C` 回读）：①点『按序粘贴』→ 记事本 `[SEQTEST-C]` ✓ ②Ctrl+V → `[SEQTEST-B]` ✓ ③Ctrl+V → `[SEQTEST-A]` ✓，三条各自正确、顺序 = 勾选先后。**方法论教训（已入红线）**：① 读剪贴板 ≠ 验证粘贴出去（`PasteNextSequential` 先设剪贴板再注入，剪贴板对不代表落到目标窗口）；② 记事本 UIA 无 ValuePattern，且 `Ctrl+A→Ctrl+C` 会留全选状态、随后的 Ctrl+V 会**替换**全选内容（连续验证须每轮清空）；③ **不要在只有推测、没有现场证据时改正常路径**。测试数据清理干净，用户历史 17 条一条未少；契约 87/87 |
| S29 按序粘贴丢第一条 · 真因定位与修复 | ✅ | **用户反馈**："不对，怎么又回到了第一个被丢了的问题中了？"（**关键信息**：S28 回退后问题重现 → 说明被我回退掉的东西里有一个是真管用的）。**真因**（此前的诊断输出已留下证据）：`当前前台 = BetterDesktop.Clipboard.Panel / 剪贴板侧边栏` —— **WPF `Hide()` 会把焦点移交给同进程的另一个可见窗口**（侧边栏手柄），`WS_EX_NOACTIVATE` 拦不住；于是面板收起后前台落在自家窗口上，`SendPaste` 发出的 Ctrl+V 打在自己身上 → **第一条凭空消失**。因是时序竞态，表现为**偶发**（固定 250ms 有时够、有时不够），故上一轮验收碰巧通过、造成误判。**修复**：① `PopupWindowBase.HidePopup()` 新增 `OnBeforeHide()` 虚钩子，**在 `Hide()` 之前**调用（`SetForegroundWindow` 仅当前台进程调用才被系统接受，Hide 后再调静默失败）；② `EdgeHandleWindow` 在 `MouseLeftButtonDown` 记录 `GetForegroundWindow()`（手柄 `WS_EX_NOACTIVATE`、不改变前台，此刻拿到的就是用户窗口）；③ `PanelMainWindow.OnBeforeHide` 据此归还前台；④ `NativeMethods` 补 `SetForegroundWindow`。**真机验收**（记事本为目标）：点手柄前前台 `Notepad / 无标题 - Notepad` ✓ → **点『按序粘贴』后前台 `Notepad / *SEQTEST-C - Notepad`** ✓✓（修复前是 `剪贴板侧边栏`；标题带星号证明内容真的落进记事本）→ 记事本依次 `[SEQTEST-C]` → `[SEQTEST-CSEQTEST-B]` → `[SEQTEST-CSEQTEST-BSEQTEST-A]`，**三条一条不丢、顺序 = 勾选先后**。契约 87/87、IPC 15/15 |
| S30 按序粘贴定稿（去哨兵/去自动首发）+ 自注入抑制修正 | ✅ | **用户两点反馈**：① 建议"在第一个前面插一个空的作为唤醒词（哨兵）"；② "刚刚的修复会导致目标窗口莫名其妙出现一个粘贴结果，还导致后面又被一股脑卸出来"。**② 定位**（加临时诊断日志抓到决定性证据）：`[hook] V down inj=0` —— 自注入的 Ctrl+V **没带上我打的 `dwExtraInfo` 标记**（注入实际在**宿主进程**，经 IPC 过去标记丢失，`KBDLLHOOKSTRUCT.dwExtraInfo` 恒为 0）→ 自注入被钩子当用户按键**再消费一条** → **自激**（日志实证：一次按键三次消费挤在 250ms 内）。**修正**：`PasteNextSequential` 期间置 `_hookPaused`（500ms），钩子窗口内**只放行不消费**（最坏漏过一次按键，绝不多粘）。**① 采纳后又放弃哨兵**：哨兵能消耗首发风险，但零宽字符会**入库成空条目**（`trim()` 不认 `\u{200B}`）；最终定稿为**两者都不做** —— 模式启动只收起面板、等用户按 Ctrl+V，首发时机交还用户。**真机验收**：点『按序粘贴』后剪贴板**未被改动**（无哨兵无自动粘贴）✓ → Ctrl+V×3 → `ZZTEST-3`→`ZZTEST-2`→`ZZTEST-1` **每次一条、间隔=按键间隔、顺序=勾选先后** ✓。**附带修复**：`Store::is_blank_text`（零宽字符算空，`purge_empty` 与 HTML 文本回填均改用），Rust 80/80。**重大事故（已入红线）**：清理测试数据时误用"内容关键词"批量删除（`\u200B`、`NORMAL`），**误伤用户约 6 条真实历史**（`clipboard_history.json` 直接落盘、无备份不可恢复）→ 今后一律**按 id 精确删除**，拿不准先备份。 |

| 二轮修复（2026-09-12 用户实测三问题） | ✅ | ① **面板"引擎未连接"**：根因 = `ClipboardIpcClient` 心跳风暴——`SendHeartbeat` 自调 `_wake.Set()` 唤醒循环、触发条件（`_lastInbound` 未刷新）仍成立 → 正反馈（实测 7ms 内 12 个心跳）→ 响应处理被拖垮 → 3s 误判超时 → 断开重连死循环。修复 = 心跳**节流**（`_lastHeartbeatAt`，每 ≥2s 一次）+ 不唤醒循环 + 健康判定只看 `_lastInbound`；另修 `WorkerLoop` 无外层 try/catch（异常即线程死亡=假重试）、`Connect()` 未先 `Disconnect()`（重试泄漏引擎连接槽，4 槽耗尽后全客户端连不上）、`NamedPipeTransport` 用 `StreamReader` 预读与 `PeekNamedPipe` 打架（后续响应永久闷在客户端缓冲）→ 改**自有行缓冲**；加 `ipc-client.log` 诊断痕迹（本轮全部根因都靠它数出来）。② **侧边栏被 dock 当运行中应用**：`EdgeHandleWindow` 只设 WPF `ShowInTaskbar=false` 不保证 `WS_EX_TOOLWINDOW`（`RunningAppDetector` 正是靠它过滤）→ 显式 `WindowStyleHelper.MakeFloatingNoActivate`（探针实证 `TOOLWINDOW=True NOACTIVATE=True`）。③ **自绘右键剪贴板入口唤不起**：与①同源（宿主客户端连接震荡）+ `OpenHistoryWindow()` 是同步 IPC 阻塞 UI 线程 → 新增 **IPC 降级路径**（`ClipboardIpcClient.OpenHistoryWindow` 失败即 `ClipboardEngineLauncher.OpenPanel()` 直启面板 `--open`）+ 桌面菜单项改 `Task.Run`；另修宿主入口面 `EnsurePanelEntry`（宿主启动即拉起面板，侧边栏才存在）。**验证**：`ipc-client.log` 心跳严格 2s 一条且每次均有响应、当前会话零断开；`panel.log` `分页加载成功 total=3`；入口连续触发 2/2 成功唤出面板；构建 0 警告 0 错误 |

| S31 按序粘贴「自己释放」根治：队列内容快照 + 失效可见 | ✅ | **用户报告**："按序粘贴功能的问题 —— 从选择了按序粘贴后，他会自己释放"。**现场证据**（panel/engine log）：勾 4 条 → 粘贴时第 1、3 条 `entry not found: fd54eb94-…` / `4a2136b8-…`，**失败也照样把序号往前顶**、面板零提示；同期引擎侧 `total` 由 14 掉到 11（条目在会话开始后被释放）。**双重根因**：① 队列（`ClipboardIpcClient._seqQueue`）只存**条目 id**，每条都回引擎 `copy_to_clipboard(id)` 取全文 —— 条目一旦在引擎侧消失（用户删掉该条 /「清理未收藏」/ 启动迁移去重合并 / 引擎重启快照回退）就必然 `entry not found`；② `PasteNextSequential` 失败**静默吞掉并前进**，观感就是"勾好的列表自己少了、内容凭空没了"。**修复（不依赖根因是哪一个，两条一起收口）**：① **队列升级为内容快照**——`BeginSequentialPaste` 逐条 `get_entry` 抓全文（`contentInBin` 的大文本走 `get_content`），抓不到的退回勾选时携带的摘要载荷；② `PasteNextSequential` 三级路径：**引擎写回**（条目仍在 → 完整格式 + 抑制回环 + HTML 内嵌图还原）→ **快照本地写回**（CF_HDROP / CF_UNICODETEXT / 标准 CF_HTML / RTF，与引擎 `write_back` 同格式层级、同包装模板）→ **显式抛错 + 报提示**（绝不静默）；③ 新增 `SequentialIssue` 事件 + `LastSequentialInvalidCount`（**不在 IClipboardService 契约内**，契约只加不改），面板状态条常驻提示"第 N 条已被引擎释放，已用本机内容快照粘贴"；④ 面板勾选一致性：**行内删除后剔除勾选并 Reload**（新增 `DropSelection`）、「清理未收藏」后 `ClearSelection`——此前删除后 `_selected` 残留死条目，正是"一点按序粘贴就失效"的直接来源；⑤ 注入缝 `PasteInjectorHook` / `SnapshotWriterHook`（headless 单测不碰真实剪贴板与桌面按键）。**验证**：IPC 单测 **18/18**（新增 3：失效降级到快照且内容取自引擎全文 / 双路不通时显式抛错并报提示 / 条目仍在时不走降级且只注入一次）、契约 **87/87**、宿主 `-t:Build` 0 警告 0 错误、面板 0 警告 0 错误；新面板已部署并重启（pid 39412，`主题引导完成 entryStyle=sidebar`）|

| S32 全链路体检批（两路只读审计 → 修真 bug） | ✅ | **用户口径**："按序粘贴最大的都解决了，下面的应该很简单了吧"→ 做一次全链路体检。**方式**：两路并行只读审计（C# 面板/IPC 客户端；契约/legacy/Rust 引擎），逐条甄别"真 bug vs 既有设计限制"后只修真问题。**修复 9 项**：① **IPC 写帧分片（高）**：`NamedPipeTransport` 用 `StreamWriter`（8192 缓冲）写帧，消息模式管道下 >8KB 的帧被拆成多次 `WriteFile` → 引擎只读到半截 JSON，`engine.log` 实证批量 `import` 在 **column 1034（≈8192 字节边界）** 被切断、残片被报 `expected value at line 1 column 1`（1301 曾记过这个教训，重写时又退回 StreamWriter）→ 改**一次性字节写入** `stream.Write(utf8 + '\n')`。② **import 恒失败（高）**：`BuildRequest` 用默认 PascalCase 序列化，而引擎 `model.rs` 是 `rename_all = "camelCase"` 且必填字段无 default → `invalid entries`；匿名 payload 属性恰好都是小写开头所以一直没暴露（测试只数个数）→ 加 `RequestOptions`（camelCase），并在单测里断言字段名。③ **合并粘贴被摘要截断（高）**：列表页返回的是**摘要载荷**（content 截断 1024、htmlContent 剥离），旧实现"PlainText 非空就不取全文" → 长文本合并粘贴静默截断 → 改**一律回引擎取全文**（大文本走 `get_content`）；取不到可粘文本时改为**显式抛错**（旧实现静默 return）。④ **引擎"损坏即清库"（高·数据丢失级）**：`Store::load` 遇 IO 错误 / DPAPI 失败 / JSON 损坏只记日志留空 entries，而 800ms autosave 随即用空库覆盖磁盘 = 用户历史静默消失且无备份 → 新增 `storage_trusted` 守卫：载入失败即**拒绝写盘**（磁盘原文件原样保留），autosave/flush 先判再写（不刷屏），补 2 条单测（失败拒写 / 正常可写不误伤）。⑤ **面板行重建漏订阅（中）**：`RefreshRow` 重建行时漏 `SelectionToggled` → pin 一次后该行多选勾选永久失效（点了没反应）→ 补齐订阅 + `RefreshSelectionUi`。⑥ **失败静默（中·成组）**：复制/单条删除/批量删除/清理未收藏/收藏/合并粘贴失败此前只写日志 → 新增底部**反馈条**（3s 自动隐去，成功绿/失败红），且批量删除与清理**失败时不刷新界面**（不再呈现"已删"假象）。⑦ **按序快照抓取加总预算（中）**：抓快照是 UI 线程同步 IPC，引擎假死时勾 N 条会冻结 N×5s → 2.5s 预算，超时剩余条目用摘要兜底并明确报出。⑧ **窗口关闭资源清理（中）**：`OnClosed` 卸载低级键盘钩子、停 3 个 DispatcherTimer、退订静态 `CompositionTarget.Rendering`。⑨ **Rust 锁中毒统一 + 消灭裸 unwrap（中）**：autosave/flush/WM_ENDSESSION/热键注册注销/日志锁全部改 `unwrap_or_else(into_inner)`（旧写法在中毒后**静默跳过落盘**，与其余锁点不一致）；`cmd_apply_settings` 全库唯一生产 `unwrap()` 改为 `if let`（坏输入会在消息循环线程 panic）。**未修（记录理由）**：`ShowFavoritesOnly`/`CloseHistoryWindow` no-op（需新增 IPC 通道，v1 限制）、`PauseTemporarily` 不向引擎传 seconds（面板存活时本地 Timer 会 resume，仅面板崩溃才滞留）、引擎 5 个 Settings 声明未被消费（计划未落地项，非回归）、`GetFilteredEntries` 只回一页 500（兼容壳）、`TogglePin` 非原子、`uuid_v4` 非真 UUID。**验证**：Rust **82/82**（含新增 2 条数据保护回归）、IPC **20/20**（新增合并粘贴取全文 / 无可粘内容显式失败 / import camelCase 断言）、契约 **87/87**、面板 0 警告 0 错误；release 引擎 + 面板均已部署重启（引擎日志 `loaded 12 entries` + 3 热键注册 + 零 ERROR）。**遗留**：宿主进程需重启才会用上新的 IPC 写入/序列化修复（当前宿主仍跑旧 DLL） |

| S33 遗留项收口批（设置项落地 + 状态正确性 + 交互打磨） | ✅ | **用户口径**："程序已经不再运行了，你继续修复吧，最好没有 bug 和问题"。前置：宿主已退出 → 可全仓构建（无 DLL 锁定）。**收口 6 项**：① **设置项真正生效（引擎）**：`max-text-bytes`（文本/HTML/RTF 合计字节）、`max-image-mb`、`max-image-pixels` 三项此前只在 Settings 里声明、设置界面可调，**引擎从不消费**（用户改了毫无效果）→ 新增 `capture_limit_violation()` 在捕获入库前判定并记 `snapshot rejected by settings limits: …`；`push-events` 以前只声明未接线 → 现门控全部 `clipboard_changed` 摘要广播（含写回路径）。② **定时暂停不再可能"永久"（引擎）**：旧实现 `PauseTemporarily(60s)` 只靠**客户端本地 Timer** 补发 `resume` —— 面板崩溃/被杀后引擎**永久暂停**、剪贴板从此静默不记录且无提示 → 新增 `PAUSE_UNTIL`（`pause` 支持 `seconds` 参数），autosave 周期线程（800ms）检查到期自动恢复 + 广播；热键手动切换一律清定时。客户端 `PauseTemporarily` 改为把秒数随 IPC 发出（本地 Timer 保留为幂等双保险）。③ **订阅即推状态快照（引擎）**：`broadcast` 只在状态**变化**时发事件，客户端连上时若引擎已处于"暂停 / 关闭监听"就永远收不到那次事件 → 显示与实际相反 → 新增 `push_state_snapshot()`：首帧 `subscribe_events` 生效后立即补推 `monitoring_changed` + `pause_changed`（校正客户端初值，修掉 `_isMonitoringEnabled` 默认 true 的误报）。④ **单条删除不再整页重载（面板）**：新增 `RemoveRow()` 原地移除行 + `_offset`/`_total` 同步 -1（不减 offset 会让触底加载漏一条），避免删除后滚动位置被弹回顶部。⑤ **打开面板即最新（面板）**：`EnsureLoaded` 旧实现仅在"列表为空"时 Reload → 上次遗留的过期列表会一直显示到下一次 `history_changed`（删过的条目还在、新复制的看不到）→ 改为每次显示都 Reload。⑥ 复核结论：✕ 按钮是 `HidePopup()`（非 `Close()`），`PanelApp._mainWindow` 不会失效，此前担心的"关窗后无法重开"不成立。**未做（记录）**：`_selected` 与结果集对账（筛选/分页下无法安全判定失效，已有"快照兜底 + 失效提示"覆盖）、`store_content` 落 bin 后清空 `content`（会让 `fingerprint()` 误合并大文本，需连带改造，风险 > 收益）、`max-content-total-mb`（需全量文本统计）。**验证**：Rust **82/82**、全仓 `dotnet build BetterDesktop.slnx` **0 警告 0 错误**（含宿主，宿主已停无锁）；release 引擎 + 面板重新部署重启（引擎 `loaded 12 entries` + 3 热键 + 零 ERROR，面板 pid 41084 单次连接无重连风暴）。**宿主**：exe 已随全仓构建更新，用户下次启动即为新版（含本轮 IPC 写入/序列化修复） |

| S34 表情包模式（用户自填动图 + 跨应用粘贴） | ✅ | **用户需求**："再加一个类别，需要用户自己往里面填入，即表情包模式，可以把保存的动图存在里面，留到任何平台粘贴使用"。**计划**：`docs/plans/2026-09-12-clipboard-sticker-mode.md`（含 §3 六条决策、§5 生死线、§14 交接节、DoD 核销表）。**技术关键**：① `image` crate 原本**只开了 png/jpeg**、不能解 GIF → 补 `gif`/`webp` 解码特性（否则连首帧缩略图都做不了）；② **Windows 剪贴板里"保动画"只有一条路** —— 写 `CF_HDROP`（原文件），位图格式（DIB/PNG）永远只有首帧 → 定案"**原文件珍藏 + 粘贴多格式齐发**"，而非"转码存储"。**落地**：引擎新增 `Category::Sticker=5` + `clipboard\stickers\` 原文件珍藏 + IPC `add_sticker`（白名单/大小校验 → sha256 → **内容哈希去重** → 复制 → 首帧尺寸 + 缩略图 → 入库）+ `set_sticker_entry` 多格式写回（`CF_HDROP` + PNG 透传 + `CF_DIB` 首帧）+ **驱逐与「清理未收藏」双重豁免**（表情包是珍藏）；契约新增 `ContentCategory.Sticker=5`、`AddStickers`、`StickerImportResult`，并**补齐 `ClipboardEntry.ContentHash`**（引擎早已写该字段而 C# 模型缺失 → 一直静默丢弃）；legacy 后端同语义实现（双后端等价）；面板新增「表情包」chip + 底部「＋ 表情包」多选导入（三态 toast：新增/重复跳过/失败原因）+ 条目行首帧缩略图与大预览。**自测抓到的真 bug**：`has_sticker_by_hash` 的索引键与 `fingerprint()` 各写一遍 → **判重永远查不到**（重复导入会持续堆副本）→ 抽 `ClipboardEntry::sticker_fingerprint()` 作**单一真相源**。**真机验证**（隔离探针，用完即删）：80×80 GIF → 首次 `added=1`、重跑 `skipped=1`（内容哈希去重生效）、`.txt` 被拒并回中文原因 → `query category=5` 返回 `contentType=2(Files)`/`category=5`/`contentHash`/`filePaths=[stickers\….gif]`/`imageWidth=80`（**GIF 解码生效**）→ 缩略图 `thumbs\.jpg` 3016B 生成 → **按 id 精确删除**（S30 红线）→ 条目归零 + 副本与缩略图**双双级联清理**。**验证**：Rust **86/86**（新增 4：分类/指纹同源、哈希判重、驱逐豁免、**只删副本不删用户原文件**）、IPC **22/22**（新增 2）、契约 **87/87**、全仓 0 警告 0 错误；引擎 + 面板已部署重启。**待用户真机**：把表情包粘进微信/QQ/浏览器确认"发出的是动图"（取决于目标应用对 CF_HDROP 的取舍） |

**S6 关键修复记录**：
- IClipboardService 33 成员全实现 + 分页扩展 `GetFilteredEntriesPage(kind/category/keyword/sourceApp, offset, limit, pinned)`（**v1.4/2026-09-12 起全部条件直通引擎**：引擎侧过滤 + 过滤后 `total` + 列表摘要载荷；客户端本地过滤与全量拉取路径已删除）；GetSourceApps 走引擎 `list_sources`；OpenHistoryWindow→引擎 open_panel；CloseHistoryWindow 空实现（独立 exe 面板自管，v1 限制）；MergePaste 本地写剪贴板+SendPaste（不经引擎抑制，合并文本可能被捕获为新条目，语义可接受）。
- 单线程统一读写 + PeekNamedPipe 轮询（与引擎同构）；同步 RPC = 发送队列 + pending TCS + 工作线程分派；WaitConnected 2s 门（首次连接异步建立）。
- 事件：history_changed→HistoryChanged / pause_changed→PauseStateChanged / monitoring_changed→MonitoringStateChanged / clipboard_changed→ClipboardChanged（扩展，灵动岛）+ Reconnected（扩展）。
- 按序粘贴状态机/临时暂停定时器纯客户端实现；plain 写回复用引擎抑制令牌防回环。


## 8. Test Strategy

- **Rust 单测**（`cargo test`）：store（加密往返/旧文件加载/驱逐边界）、analyzer（代码/富文本/文本判定矩阵）、capture（模拟 CF_* 数据、vacuous alpha 归一化）、ipc（请求响应/事件推送含 clipboard_changed 摘要截断/坏 magic 丢弃/超长 payload）。
- **C# 单测**：现有 85 例契约测试改挂 IPC 客户端（fake transport）；新增重连/事件保序测试；入口形态策略装配测试（orb/sidebar/both/off）、侧边栏防误触延迟逻辑测试。
- **集成**：引擎进程级——拉起引擎 → 复制文本 → IPC 查询命中 → copy_to_clipboard 抑制回环不重复入库；悬浮球订阅 `history_changed` 预览更新；侧边栏热区滑出/收起。
- **端到端场景走查**：见 §13 D3-D6。
- 验证命令（已确认工具链存在）：`cargo test`、`cargo build --release`、`dotnet build BetterDesktop.slnx -c Debug`、`dotnet test`。

## 9. Risk and Impact Analysis

| 风险 | 级别 | 缓解 |
|---|---|---|
| 面板主题同步（Appearance/Vibrancy 原为宿主内存态服务） | 高 | 面板 exe 引用 shell-core 复用服务 + settings.json 主题键/注册表强调色持久化引导；先验证 ThemeSection 持久化键（§12） |
| IPC 断线/事件保序 | 中 | 客户端自动重连 + 重连后全量刷新；事件序号单调 |
| 引擎与宿主双写 settings.json | 中 | 沿用 SaveMutex 既定模式（SettingsService.cs:44）；引擎只读为主，写仅经宿主 |
| 迁移期回归 | 中 | backend 开关双实现并行；右键命令迁移逻辑已有（ClipboardShellMenuRegistrar.cs:54-67） |
| 引擎自启方式未决 | 中 | §12 开放问题；临时由宿主 EnsureEngine + 面板拉起双保险 |
| d=1 消费者 | — | I6/I7/桌面控制/右键/设置分区全部经 IClipboardService，代理化后契约不变，无消费方改动 |

## 10. Files Expected to Change

| File | Symbols | Reason |
|---|---|---|
| `engine/`（新增） | 见 §6.1 | Rust 引擎 |
| `packages/shell/shell-clipboard-panel/`（新增） | `PanelApp`、`PanelMainWindow`、迁移 `ClipboardHistoryWindow` 视觉、`EntryHost`、`FloatingOrbWindow`、`EdgeSidebarWindow`、`RecentStrip` | 面板 exe + 快捷入口层 |
| `packages/shell/shell-clipboard-ipc/`（新增） | `ClipboardIpcClient`、`IpcProtocol` | IPC 客户端库 |
| `packages/shell/shell-clipboard/ClipboardPlugin.cs` | `ClipboardPlugin` | Provide 代理化 + EnsureEngine |
| `packages/shell/shell-clipboard/ClipboardShellMenuRegistrar.cs` | `EnsureRegistered` | 命令改向面板 exe |
| `host/Bootstrap.cs` | MenuCommandPipe dispatch `clipboard-history`/`desktop-controls` | 经代理拉起面板 |
| `TECH-KNOWLEDGE/13-剪贴板/1301-clipboard-history.md` | — | 修正默认值 + 新增 Rust 变体 |
| `dist/`、构建脚本 | — | 引擎/面板产物打包 |

## 11. Reusable Implementation Context

- 固定 commit：`b805f6e43c126b858d46988f3a9400737e997959`（工作区 dirty，未提交改动以当前文件为准）。
- 注入文档（repo 相对路径）：`TECH-KNOWLEDGE/13-剪贴板/1301-clipboard-history.md`（红线 + C# v1.3 变体）、`TECH-KNOWLEDGE/15-数据库存储/1502-lmdb-native-image-store.md`（vacuous alpha 红线）、`TECH-KNOWLEDGE/14-窗口与快捷键/1408-single-instance-mutex.md`（单实例 + NamedPipe 转发）、`TECH-KNOWLEDGE/31-热键/3101-global-hotkey.md` + `TECH-KNOWLEDGE/74-Windows内部接口逆向/7413-hotkey-registration.md`（热键纪律）、`TECH-KNOWLEDGE/07-扩展自动化/mcp-tool-server.md`（JSON-RPC 纪律）、`TECH-KNOWLEDGE/36-设计系统/3603-菜单栏插入按钮弹窗模式.md`。
- 源码锚点：`packages/api/Clipboard/IClipboardService.cs`（契约全集）、`packages/shell/shell-clipboard/ClipboardManager.cs`（语义权威）、`packages/shell/shell-clipboard/Native/ClipboardNative.cs`（Win32 声明）、`packages/shell/shell-core/Windows/PopupWindowBase.cs`（面板基类）、`host/MenuCommandPipe.cs`（管道安全模型）、`packages/shell/shell-settings/Services/SettingsService.cs`（settings.json 共享模式）。

## 12. Assumptions and Open Questions

**假设**：
- 引擎独立自启，宿主与面板均为 IPC 客户端（O1 的前提）。
- IPC 协议 = JSON-RPC over NamedPipe，管道名 `BetterDesktop.Clipboard.Engine`、magic `BDCB1|`（仿 MenuCommandPipe C1 模型）。
- 面板主题可经 settings.json 主题键 + 注册表强调色与宿主一致（1603/7405 已有持久化机制）。

**开放问题（实现前或实现中验证）**：
- Q1 面板 exe 引用 shell-core 是否连带 kernel 依赖（构建面膨胀）→ S7 验证，必要时抽取轻量子集。
- Q2 引擎自启方式：**已定稿**——引擎 + 面板 exe 都注册 HKCU Run 自启（entry-style=off 时面板不自启），右键/宿主/球体探活拉起兜底（§6.4）。
- Q3 主题持久化键名（ThemeSection 实际写入 settings.json 的键）→ S7 前核对。
- Q4 Rust 图片解码：`image` crate vs WIC 桥（PNG 保真与速度）→ S3 用 `image` crate 起步。
- Q5 引擎日志独立文件（`%LOCALAPPDATA%\BetterDesktop\logs\engine-yyyyMMdd.log`）避免与宿主 FileLogSink 锁竞争。
- Q6 侧边栏热区与多显示器/全屏/贴边操作冲突面（防误触阈值、边缘选择）→ S7b 前定稿。
- Q7 引擎崩溃自愈策略：面板/宿主探活失败即拉起 + 崩溃退出码记录日志（下次可见）；是否叠加引擎自身 watchdog → S5 后评估。
- Q8 灵动岛消费通道（**已定，API 预留**）：引擎事件 `clipboard_changed`（轻量摘要，仅新捕获时推送）+ 请求 `get_last`（只读快照），对齐 IMediaPlaybackService「订阅+快照、无订阅者零开销」范式；灵动岛 UI 模块本身不在本任务范围（与 shell-music 同范式，未来接入即用）。

**deferred follow-ups**：
- 图片存储升级 LMDB（H5 未启动项，1502 变体 A 可套用）→ beyond。
- 面板虚拟化（VirtualizingStackPanel）→ beyond（引擎化已解决主要卡顿，UI 侧仍建议）。

## 13. Definition of Done

- **D1** `cargo test` + `cargo build --release` 全绿；引擎单测覆盖 store/analyzer/capture/ipc（正常+边界+异常）。
- **D2** `dotnet build BetterDesktop.slnx -c Debug` 全绿 0 警告；现有剪贴板契约测试经 IPC 客户端全绿。
- **D3 端到端场景**：宿主完全未运行 → 右键桌面背景 →「剪贴板历史…」→ 面板打开 → 复制一段文本 → 面板列表出现该条 → 点条目粘贴到记事本成功（引擎独立完成全链路）。
- **D4 端到端场景**：宿主运行 → 菜单栏📋打开面板，亮/暗主题切换面板同步 → 扩展中心关闭剪贴板 → 再复制不入库 → 重新开启恢复。
- **D5 端到端场景**：复制 4K 截图 → 引擎后台编码落盘，面板/宿主不卡 → 图片条目缩略图正常显示（alpha 归一化生效，图非全黑）。
- **D6 兼容场景**：旧 `clipboard_history.json` 与 images 目录被引擎加载，历史完整不丢；引擎写回后文件仍可被旧 C# 实现读取（backend 开关切换无损）。
- **D7 性能验收**：万条历史下连续复制大文本，引擎入库 <5ms（去重 O(1)）；面板打开 <300ms；保存不阻塞引擎事件循环。
- **D8** 1301 文档回写（enabled 默认值修正 + Rust 变体章节）。
- **D9 端到端场景（v2）**：侧边栏形态开启 → 屏幕右边缘出现「>」收纳手柄 → 点击/悬停滑出**完整面板（复用原生窗口）** → 移开自动滑回手柄；切换 entry-style=off 后手柄不再响应；悬浮球与侧边栏并存（both）互不冲突。
- **D10 端到端场景（v1）**：悬浮球 hover → 就近展开 OrbPanel 紧凑面板（搜索/列表可用）→ 点「打开完整面板」切到完整窗口；复制新内容球体浮现预览提示。
- **D11 边界场景（容量）**：复制 20MB 文本 → 超 10MB 上限拒绝捕获、引擎不崩、已有历史不受影响；复制 6000×4000 大图 → 原图 PNG 无损落盘 + 480px 缩略图生成，面板显示整图缩略图；万条 + 大条目并存时面板打开 <300ms。
- **D12 端到端场景（存储模式）**：storage-mode=paths-only → 复制文件仅记路径（历史库无副本）、复制图片仅存缩略图（粘贴降级可用）；切回 full → 文件复制副本（≤64MB）可离线粘贴；总量超 1024MB → 最旧未收藏被驱逐，收藏不受影响。
- **D13 端到端场景（格式保真）**：从网页复制「图文+表格」内容（HTML 含 data URI 图）→ data URI 图提取为独立文件、HtmlContent 为占位符 → 条目标记「🖼 含图」「▦ 表格」→ 粘贴到 Word：格式/表格完整保留、图还原显示不丢；复制代码块 → 等宽 3 行预览 + 粘贴为纯文本（缩进不破坏）；含图条目删除后 html-images 目录级联清理。
- **D14 端到端场景（生命周期）**：引擎进程被杀（任务管理器结束）→ 面板/宿主探活自动拉起、历史数据不丢；注销/关机 → 引擎 flush 防抖数据后退出，重启后数据完整；引擎 exe 被占用时走 `exit` 更新模式（退出→替换→重启）成功。

## 14. Handoff to 技术力应用（交接节）

> 实现由 `skills/ability-reuse-alignment/SKILL.md` 按本节执行；本节为唯一输入，可独立执行、零重新调研。

| 项 | 内容 |
|---|---|
| 模式判定 | ① Rust 引擎核心（监听/捕获/存储/热键/IPC）：**无匹配→工程代码权威**（检索 `rust clipboard win32` 未命中，语义以 1301/1502 红线 + ClipboardManager 源码为准）；② 单实例/IPC 模式：**标准文档注入**（1408/713）；③ 热键：**标准文档注入**（3101/7413）；④ 面板/主题/消费入口：**标准文档注入**（3603/711 + shell-core 源码） |
| 注入清单 | `TECH-KNOWLEDGE/13-剪贴板/1301-clipboard-history.md`；`TECH-KNOWLEDGE/15-数据库存储/1502-lmdb-native-image-store.md`（仅红线：vacuous alpha/图片不进 JSON/大小上限）；`TECH-KNOWLEDGE/14-窗口与快捷键/1408-single-instance-mutex.md`；`TECH-KNOWLEDGE/31-热键/3101-global-hotkey.md`；`TECH-KNOWLEDGE/74-Windows内部接口逆向/7413-hotkey-registration.md`；`TECH-KNOWLEDGE/07-扩展自动化/mcp-tool-server.md`（JSON-RPC 纪律节）；`TECH-KNOWLEDGE/36-设计系统/3603-菜单栏插入按钮弹窗模式.md` |
| 适配参数 | Rust 引擎：crate 名 `betterdesktop-clipboard-engine`，输出 `engine\betterdesktop-clipboard-engine.exe`（release，x64）；管道名 `BetterDesktop.Clipboard.Engine`、magic `BDCB1|`；JSON 字段与 `BetterDesktop.Shell.Clipboard.Contracts.ClipboardEntry` 逐字段对齐（含 ContentType/Category/Content/HtmlContent/RtfContent/ImagePath/ImageWidth/ImageHeight/SizeBytes/CopyCount/SourceProcessName/SourceWindowTitle/FilePaths/Tags/HasImages/HasTable/IsCode/Id/Timestamp/IsPinned）；存储文件 `%LOCALAPPDATA%\BetterDesktop\clipboard_history.json`（CBENC1+DPAPI）+ `clipboard\images\`；设置键 `extensions.clipboard-history.{enabled,capacity,pinned-limit,max-text-bytes,max-content-total-mb,max-image-mb,max-image-pixels,retention-days,thumb-width,storage-mode,max-total-mb,file-copy-max-mb,max-html-image-mb}`（enabled 默认 true；默认值：文本单条 10MB / 文本总量 100MB 压缩后 / 单图 64MB 与 100MP / 缩略图 480px / storage-mode=full / 统一总量 1024MB / 文件副本单文件 64MB / HTML 提取图单张 2MB）；存储文件 `%LOCALAPPDATA%\BetterDesktop\clipboard_history.json`（CBENC1+DPAPI）+ `clipboard\images\` + `clipboard\thumbs\` + `clipboard\content\`（>100KB 文本 Deflate 压缩）+ `clipboard\files\`（full 模式文件副本）+ `clipboard\html-images\`（HTML data URI 提取图）。C# 侧：IPC 客户端命名空间 `BetterDesktop.Shell.Clipboard.Ipc`，TFM `net8.0-windows10.0.19041.0`，x64；面板 exe `BetterDesktop.Clipboard.Panel.exe`，右键命令 `"<panelExe>" --open` |
| 禁区 | 不改 `IClipboardService` 既有成员（可加性契约，IClipboardService.cs:9）；不动 1301 已记录的 C# 语义裁决（同 ContentType 去重、消费式抑制令牌、捕获顺序）；禁止把图片 base64 回塞 JSON（1502 红线）；禁止跳过 vacuous alpha 归一化；`extensions.clipboard-history.enabled` 按源码默认 **true**（README/1301 旧文「false」过时，不得照抄） |
| DoD 核销表 | D1 `cargo test`/`cargo build --release` 全绿；D2 `dotnet build` 0 警告 + 契约测试全绿；D3 宿主未运行右键全链路走查；D4 主题同步 + 开关启停走查；D5 4K 截图不卡 + 缩略图非黑；D6 旧数据加载 + backend 切换无损；D7 万条性能验收（入库 <5ms / 面板虚拟化+分页打开 <300ms）；D8 1301 回写；D9 侧边栏「>」手柄场景走查（v2）；D10 OrbPanel 悬浮球适配面板场景走查（v1）；D11 大容量边界场景（20MB 文本拒绝不崩 / 6000×4000 大图无损+缩略图）；D12 存储模式切换场景（paths-only 仅路径/full 文件副本离线粘贴/总量驱逐保护收藏）；D13 格式保真场景（图文表格粘贴完整/代码纯文本）；D14 生命周期场景（崩溃拉起/注销 flush/更新模式）；形态切换（orb/sidebar/both/off） |

---

## § 可选增强 / 超越需求建议（beyond scope，不混入强制 scope）

- **beyond-1** 存储升级 LMDB（`1502-lmdb-native-image-store` 变体 A 设计可直接套用）：增量写 + fail-closed GC + 损坏隔离，替换 JSON 全量重写；收益=保存路径 O(1) 化与损坏自愈。依赖资产：1502。风险：数据迁移脚本 + 失去人类可读。
- **beyond-2** 面板虚拟化（`VirtualizingStackPanel` / 分页渲染）：引擎化已消除主要卡顿，UI 侧全量重建仍是万条规模隐患；收益=面板打开 <100ms。依赖资产：shell-core。
- **beyond-3** 引擎图片编码用 `image` crate 的并行/尺寸上限策略：捕获路径编码吞吐再提 2-3x。依赖资产：Rust 生态。
- **beyond-4** IPC 双管道（命令管道 + 事件管道分离）或共享内存事件通道：事件推送与大数据量查询解耦；收益=面板刷新延迟进一步下降。依赖资产：1408/713。
