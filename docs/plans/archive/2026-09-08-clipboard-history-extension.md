# Cairo 开发计划 · 剪贴板历史扩展（落地扩展中心）

> Task: 在 better-desktop-cordis 将剪贴板做成**系统级能力**（非扩展中心小插件）：注册 `IClipboardService` 内核服务，为**多个板块**（扩展中心/菜单栏/历史面板/启动器/搜索框/未来板块）提供统一支持。**三方融合**：① **自家探索版** cairoshell-master（功能主框架：监听/UI/热键/隐私/持久化）；② **外部开源实现** 技术力仓库 1301-clipboard-history.md（ZTools 3.2.0，功能想法：图片落盘元数据化/总量限流/最近复制 API/延迟读取兜底）；③ **官方原版** cairoshell原版（机制参考：写回抑制令牌/测试接缝/三格式模型）。在 betterdt 内核服务图（ADR-002 D1：`Provide<T>`/`Get<T>`/依赖感知）上做**完美实现**。**只规划不实现**。
> 证据基于 commit `0ecd841`（2026-09-07 context-menu 收口）验证；目标仓库 `better-desktop-cordis`（工作区含历史未提交改动，实施时禁止覆盖/回滚他人改动）。
> 技术力文档命中：**1301-clipboard-history**（剪贴板历史，TS/L2，ZTools 外部实现：红线 = 延迟读取 180ms、hash 去重、写剪贴板前取消监听防自我触发、图片落盘限流；**功能想法 = 图片落盘 + 总量预算 + getLastCopiedContent 最近复制 API**）、**3101-global-hotkey**（RegisterHotKey 全局热键，C#/L2，红线：0x581 冲突、退出必须 Unregister）；**未命中**：C# 侧剪贴板监听实现文档（1301 关联段标注「C# 侧同类见 04-通知/剪贴板域（仓库暂无）」——检索关键词：剪贴板监听 C# / clipboard C# / AddClipboardFormatListener）。三方源码（master 探索版 / 原版官方 / ZTools）作为**源码级证据**。

## 1. Objective

**产品定位（架构先行）**：剪贴板是**系统级能力**，不是扩展中心的一个小插件——内核注册 `IClipboardService` 服务契约（ADR-002 D1 `Provide<T>`/`Get<T>`），**多个板块共享**：扩展中心（启停管理）、历史面板（热键/任意板块触发）、菜单栏（快捷入口）、启动器/搜索框（历史检索/最近复制，后续接）、未来板块（状态栏指示等）。扩展中心开关只控制"监控活性"，服务契约常驻，历史数据任何板块可查。

用户可感知的结果（场景语言）：

1. **一键开启剪贴板监控**：在菜单栏「+」扩展中心打开「剪贴板历史」开关后，系统开始记录复制的文本、图片、文件；关闭开关即停止记录、释放热键（历史数据保留可查），重启 BetterDesktop 后按上次选择恢复。
2. **随时召回复制内容**：按 `Ctrl+Shift+V` 在光标旁弹出历史面板，搜索、按类型/来源筛选、收藏常用条目，单击或 Enter 粘贴回任意应用（保持原格式，Ctrl+Enter 纯文本），数字键 1-9 直达前九条，Ctrl+Shift+P 直达收藏视图，支持多选合并粘贴、拖拽导出、标签编辑；重复复制同一内容不会产生重复记录。
3. **隐私与容量有底线**：密码管理器/网银/验证码等敏感来源的复制自动跳过；历史上限 10000 条、图片单张 ≤5MB 且**图片落盘 + 总量 200MB 预算**（不撑爆历史 JSON）、非收藏条目 90 天过期自动清理；重启后历史仍在（本地 DPAPI 加密存储，不落明文）。
4. **敏感时刻可暂停**：暂停后 60 秒内不记录（可随时恢复），用于复制临时敏感信息。
5. **供其他板块联动（系统级服务）**：`IClipboardService` 公开查询/变更/粘贴/事件契约——启动器/搜索框可查历史与最近复制（融合 `getLastCopiedContent` 想法），菜单栏可打开面板，未来板块零改造接入。
6. **大段内容粘贴全部成功（内部自动分段带格式，用户无感）**：复制网页/Word 里的大段内容后，用户在面板按一次粘贴（Enter/双击），系统**自动检测**内容为大段 → 内部按段落拆成若干带格式片段依次粘贴，目标文档（Word/微信/钉钉等富文本目标）出现**完整、分段、带格式**的内容——用户感知为一次普通粘贴全部成功，**不感知分段过程、无任何分段 UI**；短条目（单段）照旧单次粘贴；粘贴中途某段失败即停并保持已粘部分完整（日志留痕，不产生乱序半截）。
7. **复杂混合内容自动分类、合适粘贴**：复制文字+图片、代码+文字、带表格网页等**混合内容**时，系统入库即分析并分类（**文字/代码/图片/富文本/混合（含图/表）/文件**，面板显示分类标签并可筛选）；粘贴时**按类别选对格式**——代码条目**强制纯文本**（保留缩进换行，IDE 不出现富文本乱码）、图文混合条目写回完整 HTML（Word 得到图文混排）、纯富文本条目**多格式写回**（HTML+RTF+Text 同时提供，目标应用自动选最佳）；用户无感分类过程，只感觉"粘贴出来的东西是对的"。

验收标准：以上 7 条达成 + 服务契约可被任意板块 `Get<IClipboardService>()` 消费 + 构建门禁 0 警告 0 错误 + 内核单测覆盖（抑制/捕获/去重/驱逐/隐私/清理/过滤/落盘/持久化/分段/分类分析）+ 端到端场景走查通过（§13）。

## 1A. 功能全集（Feature Catalog · 用户已确认）

> 剪贴板作为系统级能力的完整功能清单，按域分组标注版本归属。v1 = 本次实现范围；v1.1 = 契约已预留、后续接入；未来/可选 = 扩展方向（契约保证可接入）。

### A. 监控与捕获（全 v1）
| # | 功能 | 说明 | 版本 |
|---|---|---|---|
| A1 | 系统剪贴板监听 | `AddClipboardFormatListener` + HwndSource 隐藏窗收 WM_CLIPBOARDUPDATE，开关控制 | v1 |
| A2 | 四类内容捕获 | 文本/HTML/图片/文件（优先级 HTML>Text>Image>File） | v1 |
| A3 | 三格式并存 | 富文本条目同时保留 Html+Rtf+Text | v1 |
| A4 | 空内容兜底 | 内容非空判据 + 30ms×3 短重试 | v1 |
| A5 | 写回防自我触发 | 抑制令牌（Interlocked 消费式）+ 内容对比双保险 | v1 |
| A6 | 监听失败降级 | 失败 → 日志，历史仍可查 | v1 |

### B. 历史管理（全 v1）
| # | 功能 | 说明 | 版本 |
|---|---|---|---|
| B1 | 去重置顶 | 同内容复制不新增、置顶刷新 | v1 |
| B2 | 收藏/取消收藏 | 固定条目不参与驱逐/过期 | v1 |
| B3 | 删除 | 单条/多条/清除非收藏 | v1 |
| B4 | 标签编辑 | 可搜索 | v1 |
| B5 | 复制次数 | CopyCount（"常用"排序 v1.1） | v1 |
| B6 | 来源记录 | 进程名+窗口标题+emoji | v1 |
| B7 | 日期分组 | 今天/昨天/N天前/日期 | v1 |

### C. 内容分类与智能分析
| # | 功能 | 说明 | 版本 |
|---|---|---|---|
| C1 | 语义分类 | 文字/代码/图片/富文本/文件，入库只算一次 | v1 |
| C2 | 混合内容检测 | `<img`→图、`<table`→表（「富文本·图/表」） | v1 |
| C3 | 代码检测 | 缩进+关键字密度+注释特征（不猜语言） | v1 |
| C4 | 分类标签与筛选 | 面板 chip 显示 + 按分类筛 | v1 |
| C5 | 语言识别 | 识别 C#/Python/JS 等 | 可选 |

### D. 粘贴与输出（全 v1）
| # | 功能 | 说明 | 版本 |
|---|---|---|---|
| D1 | 原格式粘贴 | 整条原格式写回 + Ctrl+V | v1 |
| D2 | 纯文本粘贴 | Ctrl+Enter 去格式 | v1 |
| D3 | 自动分段粘贴 | 大段自动拆段依次带格式粘贴，用户无感 | v1 |
| D4 | 按类别合适粘贴 | 代码→纯文本；混合→完整 HTML；富文本→多格式自选 | v1 |
| D5 | 多选合并粘贴 | 多条+分隔符合并一次粘贴 | v1 |
| D6 | 拖拽导出 | 条目拖出到目标窗口 | v1 |
| D7 | 打开文件位置 | 文件条目定位 | v1 |
| D8 | 粘贴失败即停 | 分段中途失败终止后续，保持已粘完整 | v1 |

### E. 搜索与筛选
| # | 功能 | 说明 | 版本 |
|---|---|---|---|
| E1 | 全文搜索 | 内容/HTML/标签/文件路径 | v1 |
| E2 | 类型筛选 | 全部/文本/图片/文件/收藏 | v1 |
| E3 | 分类筛选 | 全部/文字/代码/富文本/图片/文件 | v1 |
| E4 | 来源筛选 | Top8 来源应用 | v1 |
| E5 | 搜索历史项 | 最近搜索复用 | 可选 |

### F. 隐私与安全
| # | 功能 | 说明 | 版本 |
|---|---|---|---|
| F1 | 隐私黑名单 | 进程名+窗口标题关键词（密码/网银/验证码）双表 | v1 |
| F2 | 临时暂停 | 60s 不记录可恢复，面板指示 | v1 |
| F3 | DPAPI 加密存储 | 当前用户加密 + CBENC1 头，不落明文 | v1 |
| F4 | 敏感内容智能识别 | 卡号/密钥模式跳过 | 可选 |

### G. 容量与生命周期
| # | 功能 | 说明 | 版本 |
|---|---|---|---|
| G1 | 历史容量 | 10000 条，驱逐最旧非收藏 | v1 |
| G2 | 收藏上限 | 200 条 | v1 |
| G3 | 单图上限 | 5MB/张 | v1 |
| G4 | 图片落盘+总量预算 | `images\{id}.png` + 元数据 JSON + 总量 200MB 淘汰 | v1 |
| G5 | 过期清理 | 非收藏 90 天 + 孤儿图片清理，每小时 | v1 |
| G6 | 参数配置化 | 容量/过期/上限进设置分区 | v1.1 |

### H. 存储与持久化
| # | 功能 | 说明 | 版本 |
|---|---|---|---|
| H1 | JSON 持久化 | System.Text.Json（替代手写解析） | v1 |
| H2 | 损坏数据自愈 | 加载失败空历史不崩溃 | v1 |
| H3 | 保存节流 | 800ms 节流写盘 | v1 |
| H4 | 重启恢复 | 历史/收藏/标签全恢复 | v1 |
| H5 | LMDB 存储 | 万条级替代 JSON 全量读写 | 可选 |

### I. 系统级服务与多板块
| # | 功能 | 说明 | 版本 |
|---|---|---|---|
| I1 | IClipboardService 契约 | 查询/变更/粘贴/事件/面板打开，内核服务图注册 | v1 |
| I2 | 最近复制 API | GetLastCopiedContent(timeLimit?) | v1 |
| I3 | 面板打开 API | OpenHistoryWindow() 任意板块可触发 | v1 |
| I4 | 服务事件 | HistoryChanged / PauseStateChanged / MonitoringStateChanged | v1 |
| I5 | 扩展中心开关 | 管理监控活性（服务常驻、历史可查） | v1 |
| I6 | 菜单栏快捷入口 | 菜单栏按钮打开面板 | v1.1 |
| I7 | 启动器/搜索框联动 | 历史检索 + 最近复制 | v1.1+ |
| I8 | 状态栏指示器 | 最近复制/暂停态 | 可选 |
| I9 | 自绘右键菜单接管 | 自绘菜单「复制/粘贴」数据流默认走剪贴板服务（复制→监听自动进历史；粘贴保持系统语义）+ 菜单新增「剪贴板历史…」入口 | v1.1 |
| I10 | 系统右键注册快捷功能 | 注册表 shell verb 注册「BetterDesktop 剪贴板历史」（任意文件/目录/空白/桌面场景），点击 → 宿主 `--menu-cmd clipboard-history` 管道 → 打开面板 | v1.1 |

### J. 历史面板 UI（全 v1）
| # | 功能 | 说明 | 版本 |
|---|---|---|---|
| J1 | 光标处弹出 | ShowAtCursor + 边界 clamp，失焦收起 | v1 |
| J2 | 键盘全导航 | ↑↓/Home/End/Enter/Ctrl+Enter/Del/P/T/O/数字1-9/Esc | v1 |
| J3 | 右键菜单 | 粘贴/纯文本/收藏/标签/删除/打开位置 | v1 |
| J4 | 空状态/暂停指示 | 无历史提示、暂停条 | v1 |
| J5 | 主题适配 | ShellWindow + SetThemeBinding | v1 |
| J6 | 图片缩略图 | 落盘文件异步解码 | v1 |

### K. 热键
| # | 功能 | 说明 | 版本 |
|---|---|---|---|
| K1 | Ctrl+Shift+V | 打开面板 | v1 |
| K2 | Ctrl+Shift+P | 收藏视图 | v1 |
| K3 | Ctrl+Shift+Backspace | 暂停/恢复 | v1 |
| K4 | 热键冲突处理 | 0x581 捕获日志不崩溃 | v1 |
| K5 | 热键可配置 | 换键进设置 | v1.1 |

### L. 按序粘贴（整体 v1.1，用户已确认）
| # | 功能 | 说明 | 版本 |
|---|---|---|---|
| L1 | 多选按序粘贴 | 勾选 N 条 → 每次触发粘贴下一条（写剪贴板+SendPaste，不自动切焦点） | v1.1 |
| L2 | 粘贴队列状态机 | 服务层 Current/Remaining/完成/取消/重置（纯逻辑可测） | v1.1 |
| L3 | 表单填写配合 | 用户 Tab/点击切焦逐项粘贴（不猜表单结构） | v1.1 |

### M. OCR 识别对接（契约 v1，插件未来）
| # | 功能 | 说明 | 版本 |
|---|---|---|---|
| M1 | 批量录入接口 | ImportEntries(items) 外部来源批量入历史，复用去重/分类/落盘 | v1 契约 |
| M2 | 分段录入 | OCR 结果按段落分段入库（复用 Segmenter） | v1 |
| M3 | 自动分类 | 录入走 ContentAnalyzer（文字/表格/代码） | v1 |
| M4 | 录入后粘贴 | 走合适粘贴（分类写回/自动分段） | 复用 |

### N. 便利签 / 钉桌面 / 工作站（未来功能支持）
| # | 功能 | 说明 | 版本 |
|---|---|---|---|
| N1 | 条目访问保证 | 任意板块可拉取历史/条目/最近复制（便签取数据基础） | v1 已有 |
| N2 | 便签载体导出 | 条目 → 可置顶迷你便签（便利签插件做 UI，剪贴板供稳定条目模型） | v1.1+ |
| N3 | 钉桌面任意位置 | 便签拖动/置顶/缩放（便利签插件能力） | 未来 |
| N4 | 工作站联动 | 剪贴板内容作为工作站输入源（**规划中新概念，剪贴板侧只保证数据访问预留**） | 未来 |

**版本统计**：v1 ≈ 55 项（A-M 标 v1 者）｜ v1.1 ≈ 8 项（G6/I6/K5/L1-L3/N2）｜ 未来/可选 ≈ 8 项（C5/E5/F4/H5/I8/N3/N4 + I7 v1.1+）。

## 2. Current Behaviour

### 2.1 扩展中心现状（落地位置）

- 「+」按钮 → `shell-menu-bar/Windows/ExtensionsCenterWindow.cs` [verified]：列出 `ExtensionCatalog.External`，开关写 `ISettingsService`（`extensions.<id>.enabled`，默认 false），`Implemented = {"quick-note","programs-menu"}` 标注「已接入 · 开关立即生效」，其余为「规划中」。
- 启停闭环范式 = **QuickNotePlugin 观察者模式** [verified]：`shell-quick-note/QuickNotePlugin.cs` —— LoadAsync 按键位决定激活，订阅 `ShellEvents.SettingsChanged` 实时 Activate/Deactivate；窗口用 `ShellWindow` 基类（`shell-core/Surface/ShellWindow.cs`）+ `SetThemeBinding` 令牌 + NullVibrancy 降级。
- 宿主注册三件套 [verified]：`host/Bootstrap.cs` Factories 字典（`["quick-note"] = () => new QuickNotePlugin()`）→ `host/cordis.yml`（20 插件保序）→ `BetterDesktop.slnx`（登记项目）。

### 2.2 cairoshell 剪贴板：官方原版 + 用户早期探索版（两版对比，均源码级证据）

**用户早期探索确认**：betterdt 下 `cairoshell原版/`（官方 H5 基线）与 `cairoshell-master/`（用户在此之上大幅扩展的探索成果）剪贴板实现为**两个世代**：

| 维度 | 原版 `cairoshell原版/.../Infrastructure/Clipboard/ClipboardService.cs`（693 行） | 探索版 `cairoshell-master`（用户扩展） |
|---|---|---|
| 监听模型 | 专用 STA 线程 + message-only 窗口（HWND_MESSAGE=-3）+ NativeWindow [verified] | WPF HwndSource 1x1 隐藏窗（依赖 UI 线程 Dispatcher）[verified] |
| 写回防回环 | **抑制令牌** `_suppressCapture`（Interlocked 置位 + `OnClipboardUpdate` 消费式 Exchange 清零）[verified] | `_lastClipboardContent` 字符串对比 [verified] |
| 内容类型 | 四类 Text/File/Image/RichText（**文件优先→富文本→位图→文本兜底** 捕获顺序）[verified] | 四类 Text/File/Image/Html（HTML>Text>Image>FileList 顺序）[verified] |
| 去重 | 同内容移除旧条目保留固定状态再置顶 [verified] | 同内容仅置顶刷新 [verified] |
| 容量 | 200 条、图片 10MB 单张 / 100MB 总量预算淘汰 [verified] | 10000 条 / 收藏 200 / 图片 5MB / 30 天过期 [verified] |
| 持久化 | 无（纯内存）[verified] | DPAPI + JSON（CBENC1 头）[verified] |
| 隐私黑名单/暂停 | 仅 Pause/Resume [verified] | 进程名+窗口标题双表黑名单、60s 定时暂停 [verified] |
| UI/热键 | 无 [verified] | ClipboardQuickAccessWindow 1596 行 + Ctrl+Shift+V/P/Backspace 热键 [verified] |
| **测试接缝** | **protected virtual `ReadClipboardSnapshot` + internal `OnClipboardUpdate` + `InternalsVisibleTo("H5H8Verify")` + 写回抑制回归测试** [verified：ClipboardSuppressionTests.cs] | 无（`GetCurrentClipboardData` 为 static，不可注入）[verified] |

**移植策略（三方取其精华）**：功能主框架取探索版（监听 HwndSource / UI 全量 / 热键 / 隐私 / 持久化）；**写回防回环取原版抑制令牌**（比字符串对比语义更严谨，消除理论竞态）；**测试接缝取原版设计**（protected virtual 快照探针 + internal 变更入口 + InternalsVisibleTo 测试程序集），使去重/抑制/过滤管线可 headless 单测——这是本计划单测可行性的前提（§6.1 改动 5、§8）；**图片落盘/总量预算/最近复制 API/90 天取外部实现（1301）**——这是"完美实现"的核心融合点（§2.4 矩阵）。

### 2.3 探索版源文件清单（移植主体）

| 文件 | 行数 | 内容 | 状态 |
|---|---|---|---|
| `cairoshell-master/CairoDesktop.Infrastructure/Services/ClipboardManager.cs` | 1584 | 监听（HwndSource + `AddClipboardFormatListener` + WM_CLIPBOARDUPDATE）、四类内容、去重、隐私黑名单、临时暂停、收藏/驱逐、30 天过期、DPAPI 加密 JSON、粘贴（keybd_event Ctrl+V） | 主体复用 |
| `cairoshell-master/CairoDesktop.Infrastructure/Services/ClipboardEntry.cs` | 461 | 条目模型：Preview/PlainText/StatsText/来源显示名/emoji/缩略图/纯文本剥离 | 主体复用 |
| `cairoshell-master/CairoDesktop.Interop/ClipboardNative.cs` | 142 | 原生写入（CF_UNICODETEXT/CF_HTML 格式） | 参考 |
| `cairoshell-master/CairoDesktop.MenuBar/Clipboard/ClipboardQuickAccessWindow.xaml.cs` | 1596 | 历史面板 UI：搜索/类型筛选/来源筛选/日期分组/收藏/标签/多选合并/键盘导航/右键菜单 | 移植核心 |
| `cairoshell-master/CairoDesktop.MenuBar/Services/ClipboardQuickAccessService.cs` | 211 | Ctrl+Shift+V / Ctrl+Shift+P / Ctrl+Shift+Backspace 热键入口 | 热键模式参考 |
| `cairoshell-master/CairoDesktop.Application/Interfaces/Services/IClipboardService.cs` | 213 | 服务契约 | 接口参考 |
| `cairoshell-master/CairoDesktop.Infrastructure/Features/BuiltIn/ClipboardHistoryFeature.cs` | 54 | 特性装配（引用计数启停） | 模式参考 |

### 2.4 三方功能融合矩阵（核心决策表）

| 功能点 | 探索版（master，主框架） | 外部实现（ZTools/1301，想法） | 原版（官方，机制） | **融合决策** |
|---|---|---|---|---|
| 监听宿主 | HwndSource 1x1 隐藏窗 [verified] | 原生模块 | STA 线程 + message-only 窗 | **探索版**（与 betterdt WPF 融合，生命周期简单） |
| 写回防自我触发 | `_lastClipboardContent` 内容对比 [verified] | setTimeout 临时取消监听（有竞态窗口） | **`_suppressCapture` 抑制令牌**（Interlocked 消费式）[verified] | **原版令牌 + 内容对比双保险**（语义最强、无竞态） |
| 延迟读取兜底 | 内容非空判据（空则不记录）[verified] | **sleep 180ms + 30ms×5 重试** [verified：1301 红线 1] | HasContent 判据 [verified] | **内容判据 + 30ms×3 短重试兜底**（C# 监听时序已准，重试仅兜底延迟写入应用） |
| 去重 | 插入前内容比较置顶（O(n) 查重）[verified] | lastSavedHash 单次比较 [verified] | 同内容移除旧条目保留固定置顶 [verified] | **探索版**（语义最全：A→B→A 也能去重；hash 作可选加速缓存） |
| **图片存储** | base64 存 JSON（单张 5MB，无总量预算）[verified] | **落盘 IMAGE_DIR + DB 存路径/分辨率 + 总量 500MB** [verified：1301 红线 5] | 内存 + 10MB/100MB 预算 [verified] | **落盘 `clipboard\images\{id}.png` + 元数据存 JSON + 总量 200MB 淘汰**（融合外部核心优势） |
| 容量 | 10000 / 收藏 200 [verified] | 1000 [verified] | 200 [verified] | **探索版 10000/200** |
| 过期 | 30 天 [verified] | 180 天 [verified] | 无 | **90 天折中**（§12 登记可配置化） |
| 隐私黑名单 | 进程名 + 窗口标题双表 [verified] | — | — | **探索版** |
| 暂停 | 60s 定时恢复 [verified] | — | Pause/Resume [verified] | **探索版** |
| 最近复制 API / 系统级服务 | — | **`getLastCopiedContent(timeLimit?)`** [verified：1301 §二] | — | **新增 `IClipboardService` 内核服务**（ADR-002 D1 `Provide<T>`/`Get<T>`，服务常驻、多板块消费：查询/变更/粘贴/事件/面板打开，含 `GetLastCopiedContent`） |
| UI 功能面 | 搜索/类型/来源筛选/日期分组/收藏/标签/多选合并/**拖拽**/键盘导航/3 热键 [verified] | 检索联动 | — | **探索版全量纳入 v1**（含拖拽与 Ctrl+Shift+P，代码已现成） |
| 测试接缝 | 无（static 不可注入）[verified] | — | **protected virtual 快照探针 + internal 入口 + 抑制回归测试** [verified] | **原版**（本计划 §6.1 改动 5） |
| 多格式保留 | Html+Text 并存、RtfContent 字段存在（RichText 类型无实际入口）[verified] | — | **RichText = Html+Rtf+Text 三格式并存** [verified：原版 CaptureOnce] | **原版三格式模型**（Html+Rtf+Text 全保留，支撑分段带格式粘贴） |
| 分段粘贴（内部机制） | MergePaste（多选合并）交互可借鉴 [verified] | — | — | **新增**（用户点名，产品定位 = 用户无感）：粘贴操作内部自动检测大段 → 拆段依次带格式粘贴；**零分段 UI、无选段交互**；RTF 整段粘贴不拆分（生死线 7） |
| **内容分类/合适粘贴** | HTML 条目只 SetData(Html)、Text 条目 SetText（单一格式写回）[verified：966-971, 997-998]；HtmlContent 完整保留（含 img/table 标签）[verified] | — | RichText 三格式并存（Html+Rtf+Text 多格式写回基础）[verified] | **新增**（用户点名）：入库 `ContentAnalyzer` 语义分类（文字/代码/图片/富文本/混合/文件）；粘贴按类别写回——**代码强制纯文本**、混合写回完整 HTML、纯富文本**多格式**（HTML+RTF+Text）让目标应用自选 |
| 平台降级 | 监听失败 try/catch [verified] | 不可用平台安全降级 [verified] | 同 | **探索版**（Windows 专属场景） |

### 2.5 技术库命中资产

- **1301-clipboard-history.md**（TS/L2，**外部开源 ZTools 3.2.0 实现**，功能想法来源）：红线 = 监听回调不能立即读（180ms + 重试）、hash 去重、写剪贴板前临时取消监听防自我触发、图片落盘限流；**功能想法 = 图片落盘元数据化（红线 5）+ 总量 500MB 预算 + `getLastCopiedContent` 最近复制 API + 180 天保留**。三方融合已将其纳入（§2.4 矩阵：落盘/总量/最近复制 API/90 天折中/短重试兜底）。
- **3101-global-hotkey.md**（C#/L2）：`RegisterHotKey` 全局热键；红线 = 同组合键系统全局唯一（0x581 冲突须捕获）、退出前必须 Unregister、需要消息泵。betterdt 目前**无热键服务** [verified：shell-core 全库 grep hotkey/RegisterHotKey/WM_HOTKEY 无命中]，故热键由本插件自包含实现（与剪贴板监听共用 HwndSource 收 WM_HOTKEY）。

## 3. Relevant Architecture

**系统级服务定位（本计划核心架构决策）**：剪贴板注册为内核服务（ADR-002 D1），不是扩展中心小插件。

```
                        ┌────────────────────────────────────────────────┐
                        │  内核服务图 CordisContext（ADR-002 D1）          │
                        │  Provide<T> / Get<T> / 依赖感知自动重载          │
                        └────────────────────────────────────────────────┘
        ▲  Provide<IClipboardService>（ClipboardPlugin.LoadAsync）        ▲ Get<IClipboardService>
        │                                                                │
┌───────┴───────────────────────────┐         ┌──────────────────────────┴───────────────┐
│  shell-clipboard（能力提供者）      │         │  消费板块（多）                            │
│  IClipboardService（契约）          │         │  · 扩展中心（shell-menu-bar）：开关管理监控  │
│  ClipboardManager（监听/历史/分类/   │         │  · 全局热键（服务内部）：Ctrl+Shift+V/P/BS  │
│    粘贴/持久化）                    │         │  · 历史面板 ClipboardHistoryWindow（热键触发）│
│  ContentAnalyzer / ClipboardSegmenter│         │  · 菜单栏快捷入口（v1.1，Get+OpenHistoryWindow）│
│  ClipboardEntry（公开模型）         │         │  · 启动器/搜索框（v1.1+，历史检索/最近复制）  │
│  ClipboardNative（P/Invoke 收口）   │         │  · 未来板块（状态栏指示等，零改造接入）       │
└─────────────────────────────────────┘         └──────────────────────────────────────────┘
        │ 观察者模式（quick-note 同构）
        ▼
  shell-menu-bar/ExtensionsCenterWindow（开关写 extensions.clipboard-history.enabled）
  host/cordis.yml + Bootstrap.cs + BetterDesktop.slnx（宿主注册）
```

- **服务生命周期**：`ClipboardPlugin.LoadAsync` 即 `context.Provide<IClipboardService>(service)`——**服务常驻**，任何板块任何时候 `Get<IClipboardService>()` 可消费（历史数据查询/粘贴/面板打开均可用）；扩展中心开关只控制**监控活性**（监听+热键+面板启停，历史保留）。[verified：IContext.cs:16,22 `Get<T>`/`Provide<T>`；CordisContext.cs:44-82]
- **契约与实现同程序集**（shell-settings 模式：`packages/shell/shell-settings/Contracts/ISettingsService.cs` 同程序集 Contracts 命名空间 [verified]）——消费板块引用 shell-clipboard 程序集。
- **依赖方向（M7 单向）**：shell-clipboard → kernel（IPlugin/IContext）+ shell-core（ShellWindow/IAppearanceService/IVibrancyService/ShellEvents）+ shell-settings（ISettingsService）；**禁止**引用 shell-menu-bar（避免反向依赖；面板定位自实现光标 clamp）。
- 窗口基类：`ShellWindow`（quick-note 同构），`SetThemeBinding` 令牌 `CardBorderBrush/ThemePanelBackground/ThemeForeground` [verified：QuickNoteLauncher.cs:46-52]。
- 事件通道（违规1 纪律）：跨程序集通信走 `IEventBus` + `ShellEvents` 常量；剪贴板内部 HistoryChanged 为同程序集裸 event（无跨程序集暴露）[verified：ShellEvents.cs 存在]。
- 单测框架：xunit 2.9.3 + Microsoft.NET.Test.Sdk 17.11.1，TreatWarningsAsErrors，x64 [verified：shell-context-menu-tests csproj]。

## 4. Technical-Knowledge Findings

| 文档 | 定位 | 对本计划的作用 |
|---|---|---|
| 1301-clipboard-history（L2） | **外部开源 ZTools 3.2.0**：原生监听 + 延迟读取 + hash 去重 + 图片落盘 + 总量限流 | **功能想法来源**：图片落盘元数据化（红线 5）、总量预算、getLastCopiedContent 最近复制 API、90 天保留折中、短重试兜底（§2.4 矩阵） |
| 3101-global-hotkey（L2） | RegisterHotKey 全局热键 + 0x581 冲突 + 退出释放 | 热键实现的**生死线**（Ctrl+Shift+V 被占 → try/catch 日志降级不崩溃；Deactivate 必须 Unregister） |
| 拆析/（cairoshell 项目拆析，若有） | 双轨扩展系统 | 观察者模式佐证（本项目 quick-note 已落地同范式） |

**未命中（须新建技术力文档）**：C# 侧剪贴板监听（AddClipboardFormatListener + WM_CLIPBOARDUPDATE + HwndSource 收 WM_HOTKEY 共用）——实施完成后按积累 skill 更新 1301 补「C# 变体」小节（见 §12 D6）。

## 5. Constraints & Invariants（约束切片）

**生死线（绝对不能做错）**：

1. **防自我触发（写回抑制）**：本插件自己写剪贴板（粘贴/复制条目）引发的 WM_CLIPBOARDUPDATE 必须被抑制——采用**原版抑制令牌**（`CopyItemToClipboard` 写前 `Volatile.Write(_suppressCapture, 1)`，`OnClipboardUpdate` 入口 `Interlocked.Exchange(ref _suppressCapture, 0)` 消费式清零并跳过；写失败立即复位，防止令牌卡死监控）[verified：原版 ClipboardService.cs:286-339, 385-398]，并保留探索版 `_lastClipboardContent` 内容对比作双保险。**必须配写回抑制回归单测**（原版 ClipboardSuppressionTests 同款：令牌置位 → OnClipboardUpdate 不触发 HistoryChanged、令牌归零、历史不写入）——这是 1301 红线 4 与两版源码的三重确认。
2. **监听捕获不靠立即读**：WM_CLIPBOARDUPDATE 到达时剪贴板可能尚未写入完成。探索版以「内容非空 + 四类格式存在」为捕获判据（空内容不记录）[verified]；原版同以 HasContent 判据 [verified]。移植保留该判据，**不引入固定 180ms 硬延迟**（1301 的 TS 方案为跨平台补偿，C# 侧两版源码均以内容判据为准；冲突以源码为准）。
3. **热键配对释放**：Activate 注册 / Deactivate 注销，缺一则重启后 `0x581 already registered` 永久失效（3101 红线 2）。
4. **隐私黑名单命中即跳过**：进程名表 + 窗口标题关键词表（密码/网银/验证码）双通道，误记一次敏感内容即隐私事故。
5. **存储不落明文 + 图片元数据化**：DPAPI `ProtectedData.Protect(CurrentUser)` + 头标识 `CBENC1\0`（探索版格式 [verified]），文件路径 `%LOCALAPPDATA%\BetterDesktop\clipboard_history.json`；**图片字节落盘** `%LOCALAPPDATA%\BetterDesktop\clipboard\images\{id}.png`，JSON 只存路径/宽高/尺寸（融合 1301 红线 5：DB 存元数据不存大对象），总量 200MB 预算淘汰最旧未固定图片（1301 maxTotalImageSize 想法）；**禁止**把图片 base64 塞进历史 JSON。
6. **不破坏扩展中心现有契约**：ExtensionCatalog.External 追加条目只增不改；ExtensionsCenterWindow.Implemented 只增集合成员；quick-note/programs-menu 行为零变化。
7. **大段粘贴的原子性与无感（新功能生死线）**：① **用户无感**：不新增任何分段 UI/选段交互/分段入口——分段是粘贴操作的内部机制，用户感知 = 一次普通粘贴（Objective 6）；② **格式完整性**：片段写回必须经 `ClipboardNative.SetHtmlText` 的 **CF_HTML 头包装**（Version:0.9 + Start/EndFragment 偏移 [verified：探索版 ClipboardNative.cs:58-89]），禁止裸 HTML SetData（Word/微信乱码）；③ 片段必须**自包含**（内联标签配对闭合，块级标签剥离时缺口补齐）；深层嵌套无法安全切分的片段**降级纯文本**（目标应用不支持富文本时同样自动降级），**禁止产出损坏 HTML**；④ **粘贴中途失败即停**（某段写剪贴板失败 → 终止后续段，保持已粘部分完整、不产生乱序半截，日志留痕）；⑤ RTF 条目**禁止分段**（线性码流切分风险高），整段带格式粘贴保留。
8. **分类正确性（新功能生死线）**：① **代码条目禁止以 HTML/RTF 写回**——代码粘贴到 IDE 时富文本会乱码/污染缩进，检测为代码 → 粘贴强制纯文本（保留换行缩进），这是"合适粘贴"最关键的分支（Objective 7）；② **混合内容禁止丢图**：HTML 含 `<img`/`<table>` 时分类标记混合，写回必须保留完整 HTML（含嵌入图片/表格结构），不得因"降级"丢弃；③ 分类只在入库时计算一次（存条目），**不因内容变化重复分析**；④ 分类误判可接受（代码/文字误判不产生数据损坏，仅影响粘贴格式），但**代码→富文本误判**（把代码当富文本粘）必须靠 ① 兜底。

**验收标准（做对了的定义）**：Objective 5 条 + 复制"abc"去重置顶、Ctrl+Shift+V 弹出、Enter 粘贴到前台、关开关即停、重启历史在、黑名单不记、10000 条驱逐、90 天清理、图片落盘元数据化。

## 6. Proposed Changes

### 6.1 新建系统级服务插件包 `packages/shell/shell-clipboard/`

**csproj**（`BetterDesktop.Shell.Clipboard.csproj`，照抄 quick-note 模板 [verified]）：
TFM `net8.0-windows10.0.19041.0`、Nullable/ImplicitUsings、TreatWarningsAsErrors、`Platforms=x64`、`UseWPF=true`；ProjectReference：`kernel/kernel/BetterDesktop.Kernel.csproj` + `shell-core/BetterDesktop.Shell.Core.csproj` + `shell-settings/BetterDesktop.Shell.Settings.csproj`。

**IClipboardService.cs**（新，**系统级服务契约**，`BetterDesktop.Shell.Clipboard.Contracts` 命名空间，同程序集模式 [verified：shell-settings]）——多板块消费面：
- 查询：`GetFilteredEntries(ClipboardItemKind? kind, ContentCategory? category, string? keyword, string? sourceApp)`、`GetSourceApps()`、`GetLastCopiedContent(TimeSpan? timeLimit)`（融合 1301 想法）
- 变更：`PinEntry/UnpinEntry/TogglePin/DeleteEntry/DeleteEntries/ClearAllUnpinned/SetEntryTags`
- 粘贴：`CopyEntryToClipboard(entry)`（内部按类别多格式写回 + 自动分段）、`CopyEntryAsPlainText(entry)`、`PasteEntryToActiveWindow(entry)`、`MergePasteToActiveWindow`、`OpenFileLocation`
- **录入（M1-M3，OCR 对接契约预留）**：`ImportEntries(IEnumerable<ClipboardImportItem> items)`（批量入历史，复用去重/分类/分段/落盘）、`ClipboardImportItem` 公开模型
- 状态：`IsMonitoringEnabled`、`PauseTemporarily/Resume`、`OpenHistoryWindow()`
- 事件：`event Action? HistoryChanged / PauseStateChanged / MonitoringStateChanged`
- 模型：`ClipboardEntry`（公开，字段稳定可序列化——N1 便签/工作站取数基础）、`ClipboardItemKind`、`ContentCategory`、`LastCopiedContent`、`ClipboardImportItem`
- **可扩展性**：v1.1 按序粘贴（L1-L2）以**新增方法**方式扩展（如 `BeginSequentialPaste`/`PasteNext`），不修改既有成员——契约保持可加性，不破坏 v1 消费方（§12-7）。

**ClipboardNative.cs**（新，P/Invoke 收口，全部 `internal static`）：
- `User32.AddClipboardFormatListener / RemoveClipboardFormatListener`（user32.dll，bool + IntPtr）
- `User32.GetForegroundWindow / GetWindowThreadProcessId / GetWindowText / GetWindowTextLength`
- `User32.keybd_event`（VK_CONTROL=0x11 / VK_V=0x56，KEYEVENTF_KEYUP=0x0002）
- `User32.RegisterHotKey / UnregisterHotKey`（WM_HOTKEY=0x0312）+ `HotKeyModifiers` 常量
- `ClipboardNative.SetText / SetHtmlText`（CF_UNICODETEXT=13 / "HTML Format" 注册格式 + GlobalAlloc 系列，直接照搬 cairoshell ClipboardNative.cs [verified]）

**ClipboardEntry.cs**（新，照搬探索版模型 [verified]，删 CairoDesktop 命名空间引用）：常量 `DefaultContentType="text"/ImageContentType="image"/FileListContentType="files"/HtmlContentType="html"/RichTextContentType="richtext"`；字段 Id/Content/HtmlContent/RtfContent/Timestamp/IsPinned/ContentType/**ImagePath（落盘路径，替代探索版 ImageData base64，融合 1301 红线 5）**/FilePaths/ImageWidth/ImageHeight/SizeBytes/CopyCount/SourceProcessName/SourceWindowTitle/Tags/**ContentCategory（Text/Code/RichText/Image/File）+ HasImages/HasTable/IsCode（ContentAnalyzer 入库时计算，生死线 8 ③）**；派生 Preview/PlainText/StatsText/ContentTypeLabel/**CategoryLabel（分类显示名：文字/代码/富文本/图片/文件，混合加「·图」「·表」）**/SourceAppDisplayName/SourceAppIcon/ImageDimensionText；`GetThumbnail/GetFullImage`（按 ImagePath 从磁盘解码，DecodePixelWidth 按尺寸解码）+ `FinalizeMetadata` + `StripHtmlTags`。

**ClipboardSegmenter.cs**（新，**内部机制**，纯逻辑拆分器，可 headless 单测，用户不可见）：
- `HtmlSegmenter.Split(html)`：按块级标签（`</p>`、`</div>`、`</li>`、`<br>`、`</tr>`、`</h1-6>` 等）切分，**保留内联标签白名单**（`b/i/u/strong/em/span style/font/a/code/sub/sup`），剥离块级标签时补齐内联标签配对缺口（自包含）；解析失败或嵌套无法安全切分 → 该片段降级纯文本并标记 `IsFallback=true`；返回 `List<HtmlSegment{Content, IsFallback}>`。
- `TextSegmenter.Split(text)`：按空行（`\n\s*\n`）拆段（**仅段落模式**——无感场景不需要按行模式），去首尾空行；无空行的单段大文本不拆（保持单次粘贴，避免无意义拆分）。
- 片段不再二次包装（CF_HTML 头由 `ClipboardNative.SetHtmlText` 统一处理 [verified：58-89]）。
- RTF 条目 `GetSegments` 返回空（不分段，整段带格式粘贴）。

**ContentAnalyzer.cs**（新，**内容语义分类器**，纯逻辑可 headless 单测）：
- `Analyze(html, text) → ContentProfile { Category, HasImages, HasTable, IsCode }`，Category ∈ { **Text, Code, RichText, Image, File** }（混合内容不设独立类别，用 Flags 表达：RichText+HasImages+HasTable = 图文/表格混合；分类标签在 UI 层组合显示为「富文本·图」「富文本·表」）。
- **代码检测**（`IsCode`，最高优先级）：行首缩进（空格/制表符）占比 + 关键字密度（if/for/while/return/function/def/class/import/using/var/const/let 等） + 注释特征（`//`、`/*`、`#`、`--`） + 括号配对密度；命中阈值 → Code。**语言猜测不做**（v1 只判"是代码"不判语言，克制）。
- **混合检测**：html 含 `<img`（含 data URI/外链）→ HasImages；含 `<table`/`<tr` → HasTable。
- **分类顺序**：File（文件列表）> Image（纯图片）> Code（代码特征）> RichText（Html/Rtf 存在）> Text。分类只在入库时计算一次（生死线 8 ③）。

**ClipboardManager.cs**（新，探索版移植 + 原版机制 + 外部想法，共 5 项机制改动 + 改动 6 自动分段 + 改动 7 分类写回）：
- 保留（探索版 [verified：ClipboardManager.cs]）：HwndSource 1x1 隐藏窗 + AddClipboardFormatListener 监听；`OnClipboardUpdated` 四类优先级（HTML>Text>Image>FileList）+ `GetForegroundAppInfo` 来源 + `IsPrivacySensitive` 双表黑名单；Add*Entry 重复置顶不新增；`EvictExcessUnpinnedEntries`（10000/200）；`CleanupExpiredEntries`（**90 天**，每小时 DispatcherTimer）；`PauseTemporarily(60)/ResumeMonitoring` + `PauseStateChanged`；`CopyEntryToClipboard/CopyEntryAsPlainText/PasteEntryToActiveWindow/PasteEntryAsPlainTextToActiveWindow/MergePasteToActiveWindow/OpenFileLocation/SendPaste`；`PinEntry/UnpinEntry/TogglePin/DeleteEntry/DeleteEntries/ClearAllUnpinned/SetEntryTags/GetFilteredEntries/GetSourceApps/FindEntryIndex`；`GetLastCopiedContent`（融合 1301 `getLastCopiedContent` 想法：返回最近一条 {类型/内容或路径/时间戳}，供内核服务查询）。
- **改动 1（持久化）**：探索版手写 JSON 序列化/解析（~200 行）替换为 `System.Text.Json`（.NET 8 内置；`JsonSerializerOptions` + JsonInclude/JsonPropertyName 标注；**兼容保留** DPAPI 加密头 `CBENC1\0` 读写与明文旧格式回退）。序列化字段与探索版序列化器逐字段对齐（id/content/timestamp(ISO-8601)/isPinned/contentType/copyCount/imageWidth/imageHeight/**imagePath**/htmlContent/rtfContent/sourceProcessName/sourceWindowTitle/tags/sizeBytes/filePaths）——**imageData base64 字段改为 imagePath 落盘路径**（融合 1301 红线 5）。
- **改动 2（图片落盘 + 总量预算，融合外部实现核心优势）**：图片捕获时 PNG 字节写入 `clipboard\images\{id}.png`（不完整保留在内存），条目存 `ImagePath` + `ImageWidth/Height` + `SizeBytes`；面板缩略图按需从磁盘解码（DecodePixelWidth）；删除条目/清理过期时同步删除孤儿图片文件；**总量 200MB 预算**（超限淘汰最旧未固定图片条目并删文件，1301 maxTotalImageSize 想法）；历史 JSON 永不携带图片字节。
- **改动 3（生命周期）**：去掉静态单例 + 引用计数（探索版 Instance/AddClient/RemoveClient 为多客户端设计），改为插件持有单实例 + `Start()/Stop()`（幂等）。
- **改动 4（热键并入）**：`RegisterHotkeys()/UnregisterHotkeys()` 与监听共用同一 HwndSource 消息钩子（同时处理 WM_CLIPBOARDUPDATE 与 WM_HOTKEY）；Ctrl+Shift+V=打开面板、Ctrl+Shift+Backspace=暂停切换、**Ctrl+Shift+P=仅收藏视图**（探索版现成，v1 纳入）；注册失败（0x581）→ `context.Logger.Warn` + 面板仍可由扩展中心开关侧提示（不崩溃，3101 红线）。
- **改动 5（测试接缝 + 捕获兜底，吸收原版与外部实现）**：`GetCurrentClipboardData` 由 static 改为 **protected virtual `ReadClipboardSnapshot()`**（返回可序列化快照，内容判据 HasContent 化）；捕获判据 = 内容非空 + **30ms×3 短重试兜底**（融合 1301 红线 1 的延迟写入场景，仅对空结果重试，不引入固定 180ms 延迟）；`OnClipboardUpdate` 改 **internal 变更入口**（WndProc 与测试共用）；`CopyItemToClipboard` 写前置**抑制令牌 `_suppressCapture`**（Interlocked 消费式，写失败立即复位）+ `_lastClipboardContent` 双保险；程序集加 `InternalsVisibleTo("BetterDesktop.Shell.Clipboard.Tests")`——单测经虚方法注入固定快照 + 令牌反射，headless 覆盖去重/抑制/过滤/捕获管线，不触碰真实剪贴板 [verified：原版 ClipboardService.cs + ClipboardSuppressionTests.cs 同款设计]。
- **改动 6（自动分段粘贴，内部机制、用户无感，用户点名产品定位）**：粘贴入口（`PasteEntryToActiveWindow`（公开契约）/面板 Enter/双击）内部升级——先检测条目是否"大段"（HTML 含 ≥2 个块级段，或纯文本含 ≥2 个空行段），非大段 → 原单次粘贴路径不变；大段 → `GetSegments`（委托 ClipboardSegmenter）→ 逐段 `CopySegmentToClipboard`（HTML 段 → `ClipboardNative.SetHtmlText` / 文本段 → `SetText`，每段写前置抑制令牌）+ `SendPaste`，段间 250ms；**某段写剪贴板失败即停**（终止后续段、保持已粘部分完整、日志留痕，生死线 7 ④）；用户零感知分段过程、无任何分段 UI（生死线 7 ①）。`GetSegments`/`CopySegmentToClipboard` 为 internal（**公开面在 IClipboardService**，细分实现不外露，测试经 InternalsVisibleTo 覆盖）。多格式捕获同步升级：HTML 条目按**原版三格式模型**同时保留 HtmlContent+RtfContent（存在时）+TextContent [verified：原版 RichText 分支]。
- **改动 7（内容分类入库 + 按类别多格式写回，用户点名"学会分类/合适粘贴"）**：① 入库（AddHtmlEntry/AddTextEntry/AddImageEntry）时调用 `ContentAnalyzer.Analyze` 计算 ContentCategory+HasImages/HasTable/IsCode 存条目（只算一次，生死线 8 ③）；② **`CopyEntryToClipboard` 按类别写回**（替代探索版单一格式写回 [verified：966-971, 997-998]）——**Code → 仅 SetText（强制纯文本，保留缩进换行，IDE 不出现富文本乱码，生死线 8 ①）**；RichText（含混合）→ DataObject 同时 SetData(Html)+SetData(RTF)+SetText（**HTML 含 img/table 时完整保留**，目标应用自动选最佳格式，生死线 8 ②）；Text → SetText；Image → Image 位图；File → FileDropList；③ 面板按 ContentCategory 过滤（`GetFilteredEntries` 增加 category 参数）与分类标签显示（CategoryLabel）。

**ClipboardHistoryWindow.cs**（新，ShellWindow 子类，探索版 ClipboardQuickAccessWindow **全量移植**；打开方式 = `IClipboardService.OpenHistoryWindow()`，热键/未来菜单栏按钮均可触发）：
- 保留（探索版 1596 行现成代码）：搜索框 + 类型筛选 chips（全部/文本/图片/文件/收藏）+ 来源筛选栏（Top8 应用）+ 日期分组头 + 条目行（类型指示器：图片缩略图 56px/文件图标/文本图标；预览 + 类型 chip + 来源 emoji 名 + 复制次数 + 相对时间；收藏⭐/删除按钮）+ 右键菜单（粘贴/纯文本/收藏/编辑标签/删除/打开文件位置）+ 键盘导航（↑↓/Home/End/Enter/Ctrl+Enter/Del/P/T/O/数字 1-9/Esc）+ 多选合并粘贴（复选 + 分隔符 chips）+ **拖拽导出**（PreviewMouseMove DragDrop，探索版现成）+ 空状态 + 暂停指示 + **Ctrl+Shift+P 收藏视图**。**粘贴行为内部增强**（Enter/双击/单击）：大段条目自动分段依次粘贴（改动 6）+ 按类别多格式写回（改动 7）——用户无感，**不新增任何分段/分类选择 UI**。
- **分类展示（用户可见，Objective 7）**：条目行类型 chip 升级为**分类 chip**（文字/代码/富文本/图片/文件，混合显示「富文本·图」），代码条目加代码图标标识；筛选 chips 增加分类维度（全部/文字/代码/富文本/图片/文件/收藏）——**分类可见可筛，但粘贴策略零 UI 自动生效**。
- 适配：基类改 `ShellWindow`（构造 `(IAppearanceService?, IVibrancyService?)`，同 quick-note）；`ShowAtCursor()`（光标处 + WorkArea clamp，探索版 ShowAtCursor 逻辑 [verified]）；主题用 `SetThemeBinding` 令牌替代硬编码画刷；`OnDeactivated → Hide`（失焦收起）；图片缩略图改从落盘 ImagePath 解码（改动 2 适配，含异步解码防卡 UI）。
- 依赖注入：构造 `(ClipboardManager manager, IAppearanceService? appearance, IVibrancyService? vibrancy)`，订阅 `manager.HistoryChanged`（Dispatcher.BeginInvoke 刷新，探索版同款 [verified]）。

**ClipboardPlugin.cs**（新，quick-note 同构 + **系统服务注册** [verified：QuickNotePlugin.cs / IContext.cs:22]）：
- `Name = "shell.clipboard"`（cordis id/name `clipboard-history`，实现时核对 quick-note 的 Name↔cordis name 对应惯例）。
- `EnabledKey = "extensions.clipboard-history.enabled"`；LoadAsync：`Get<ISettingsService>`/`IAppearanceService`/`IVibrancyService`（null 降级 NullVibrancy）→ **`context.Provide<IClipboardService>(manager)`（服务常驻，任何板块可 Get）** → 订阅 `SettingsChangedEventArgs`（key == EnabledKey）→ 按 `settings.Get(EnabledKey, false)` **Activate/Deactivate 监控活性**（服务本体不注销）。
- Activate：`manager.Start()`（监听 + 热键）；Deactivate：`manager.Stop()`（Unregister 热键 + 停监听 + FlushSave，**历史保留可查**）→ 面板关闭销毁。
- `RunOnUi` 封送（quick-note 同款）。

### 6.2 扩展中心注册（2 处小改，语义 = 监控活性开关）

- `shell-menu-bar/Contracts/ExtensionCatalog.cs` External 追加：
  `new ExtensionDescriptor("clipboard-history", "剪贴板历史", "记录剪贴板历史，搜索/收藏/一键粘贴（Ctrl+Shift+V）", "剪")`
- `shell-menu-bar/Windows/ExtensionsCenterWindow.cs` Implemented 追加 `"clipboard-history"`。

### 6.3 宿主注册（3 处小改）

- `host/Bootstrap.cs`：`using BetterDesktop.Shell.Clipboard;` + Factories `["clipboard-history"] = () => new ClipboardPlugin()`（照 quick-note 位置）。
- `host/cordis.yml`：quick-note 之后追加 `- id: clipboard-history\n    name: clipboard-history`。
- `BetterDesktop.slnx`：登记 `packages/shell/shell-clipboard/BetterDesktop.Shell.Clipboard.csproj`（照 quick-note 登记块）。

### 6.4 新建单测项目 `packages/shell/shell-clipboard-tests/`

csproj 照 shell-context-menu-tests 模板 [verified]（xunit + Test.Sdk，引用 shell-clipboard + kernel）。测试文件见 §8。

## 7. Impact Analysis

- **增量式、零既有行为破坏**：ExtensionCatalog/ExtensionsCenterWindow/Bootstrap/cordis.yml/slnx 均为追加；quick-note、programs-menu 及 15 项系统功能不动 [verified：ExtensionCatalog 现有条目]。
- **系统级服务注册（新增能力，无破坏）**：`Provide<IClipboardService>` 是新增服务类型，不覆盖任何既有服务 [verified：CordisContext.cs:63-82 Provide 语义]；依赖感知 NotifyDependents 仅通知 Inject IClipboardService 的插件（当前无，未来消费板块注册即自动生效）。
- **依赖方向**：shell-menu-bar 不加任何新引用（扩展中心只读 ExtensionCatalog 静态表 + ISettingsService，不感知 ClipboardPlugin 存在——由观察者模式解耦）[inferred：ExtensionsCenterWindow 仅读写设置键]。
- **系统级副作用**：① 全局热键 Ctrl+Shift+V 占用（与其他程序冲突风险，见 §11）；② 剪贴板监听仅当开关开启（默认 false，启动零副作用）[inferred：QuickNotePlugin 默认 false 同款]；③ 存储文件仅开关开启后创建。
- **性能**：监听回调仅做内存操作 + 800ms 保存节流 [verified：探索版 ScheduleSave]；每小时清理定时器仅监控开启时运行；面板仅热键触发时创建（懒创建）；服务常驻开销 = 单实例对象 + 事件订阅（可忽略）。

## 8. Testing Plan（内核逻辑单测，测非 UI）

`shell-clipboard-tests`（xunit）：

| 测试文件 | 用例（正常/边界/异常） |
|---|---|
| ClipboardEntryTests.cs | Preview 各类型前缀（[图片]/[文件]/[HTML]/文本截断 200 字）；PlainText 剥离 HTML 标签；StatsText 字数/行数；SourceAppDisplayName 进程名映射（chrome→Chrome/未知→去.exe）；GetThumbnail 尺寸解码 |
| ClipboardSuppressionTests.cs | **写回抑制回归（原版同款）**：令牌置位 → OnClipboardUpdate 不触发 HistoryChanged、令牌消费归零、历史不写入；未抑制对照 → 正常捕获并触发（经虚方法接缝注入固定快照） |
| ClipboardCaptureTests.cs | 经 ReadClipboardSnapshot 接缝注入：文本/HTML/图片（PNG 字节）/文件四类 → 正确建条目；空快照 HasContent=false → 不记录；捕获顺序（HTML 优先于 Text） |
| ClipboardDedupeTests.cs | 同内容复制 → 置顶不新增（Index 0 且 Count 不变）；不同内容 → 新增；HTML 与文本同源优先 HTML 单条 |
| ClipboardEvictionTests.cs | 超 10000 条驱逐非收藏尾部；收藏超 200 驱逐最旧收藏；驱逐后收藏仍在 |
| ClipboardPrivacyTests.cs | 黑名单进程名（1password）跳过；窗口标题含「密码/验证码」跳过；正常来源记录 |
| ClipboardCleanupTests.cs | 90 天前非收藏被清（含对应落盘图片文件删除）、收藏保留；未过期保留 |
| ImageStorageTests.cs | 图片捕获 → 字节落盘 `images\{id}.png` + 条目存 ImagePath/宽高（JSON 无 base64）；总量超 200MB → 淘汰最旧未固定图片条目并删文件；收藏图片条目不参与淘汰；删除条目/清理过期 → 孤儿图片文件同步删除 |
| ClipboardFilterTests.cs | filter=image/files/pinned 过滤；searchKeyword 命中 Content/Preview/Tags/FilePaths；sourceApp 过滤 |
| ClipboardSegmenterTests.cs | **分段拆分（内部机制核心）**：HTML 按 `</p>/</div>/</li>/<br>` 切分正确且内联标签（b/i/span style）保留在片段内；块级标签剥离后内联配对补齐；深层嵌套/无法安全切分 → IsFallback 降级纯文本；纯文本按空行拆分、去首尾空行、无空行单段不拆；RTF → 空（不分段）；片段不含 CF_HTML 头（由 Native 统一包装） |
| ClipboardAutoPasteTests.cs | **自动分段触发（用户无感）**：HTML 含 ≥2 块级段 → 走分段粘贴路径（逐段写+发送）；纯文本 ≥2 空行段 → 分段；单段/短条目 → 原单次粘贴路径；某段写剪贴板失败 → 终止后续段（不产生乱序半截） |
| ContentAnalyzerTests.cs | **内容分类（新功能核心）**：代码文本（缩进+关键字+注释）→ Code；普通正文 → Text；HTML 含 `<img`/data URI → RichText+HasImages；含 `<table` → HasTable；纯 HTML 富文本 → RichText；File/Image 优先级正确；分类顺序（File>Image>Code>RichText>Text）；同一内容重复分析结果稳定 |
| JsonPersistenceTests.cs | 序列化→反序列化往返（含 imagePath 元数据/文件路径/标签）；加密头 CBENC1 存在；损坏数据 → 空历史不抛；**图片字节不在 JSON 中** |

## 9. Build & Verification

- 门禁（STEP8B 同款 [verified]）：`dotnet build BetterDesktop.slnx -warnaserror -nologo -m:1 --disable-build-servers` → 0 警告 / 0 错误。
- 单测：`dotnet test`（新测试项目，全部绿）。
- 若首次构建遇 MSB3021（Host 残留实例锁 DLL）：`Stop-Process` 释放后重跑（STEP8B 记录 [verified]）。

## 10. Rollout

1. 6.1 插件包（csproj → Native/Entry/Manager → Window → Plugin）
2. 6.4 单测项目（随 Manager 同步写，测内核非 UI）
3. 6.2 扩展中心注册 → 6.3 宿主注册 → 构建门禁 + 单测
4. 端到端走查（§13 D1-D4）
5. 技术库回写（§12 D6）

## 11. Risks

| 风险 | 等级 | 缓解 |
|---|---|---|
| Ctrl+Shift+V 与其他程序热键冲突（0x581） | 中 | try/catch 日志 Warn，不崩溃；面板关闭时仍记录历史，仅入口失效；§12 登记设置页换键 |
| 自粘贴触发历史污染 | 高 | **原版抑制令牌**（写前置位 + OnClipboardUpdate 消费式清零 + 写失败复位）+ `_lastClipboardContent` 双保险；ClipboardSuppressionTests 回归单测锁定 |
| 剪贴板被占用读/写失败 | 低 | 全 try/catch 静默降级（cairoshell 同款），空内容不记录 |
| 监听线程/Dispatcher 时序 | 中 | 回调内只做数据变更 + `HistoryChanged` 由 UI 侧 Dispatcher.BeginInvoke 刷新（探索版同款）；`RunOnUi` 封送窗口生命周期；缩略图异步解码 |
| 历史 JSON 膨胀（图片基数大） | 低 | **图片落盘 + 元数据化 + 200MB 总量预算**（融合 1301 红线 5），JSON 永不携带图片字节 |
| 存储文件损坏/权限 | 低 | 加载失败 → 空历史 + 日志，不抛；孤儿图片文件下次清理回收 |
| 与 245 项未提交改动共存 | 中 | 只追加不覆盖；ExtensionCatalog/ExtensionsCenterWindow/Bootstrap/cordis.yml 改动前先核对工作区现状 |
| 扩展中心「已接入」语义变化 | 低 | Implemented 集合加成员即可，quick-note 行为零变化 |

## 12. Open Questions & Deferred

1. **容量/过期/图片上限配置化（G6）**：v1 用常量（10000/200/5MB/200MB 总量/90 天，90 天为三方折中）；v1.1 进 shell-settings 分区。
2. **热键冲突交互（K5）**：v1 仅日志；v1.1 扩展中心条目提示或设置分区换键。
3. **LMDB 替代 JSON 存储（H5）**：技术库 1501-lmdb-kv-store（L2）可作候选（万条级 JSON 全量读写仍够用，需原生依赖）。
4. **消费板块落地（服务已就绪，逐步接入）**：① v1.1 菜单栏快捷入口（I6：StatusBarMenuBarExtension 加按钮 → `Get<IClipboardService>().OpenHistoryWindow()`）；② v1.1 自绘右键菜单接管（I9：见下）；③ v1.1 系统右键注册（I10：见下）；④ v1.1+ 启动器/搜索框历史检索与最近复制（I7）；⑤ 可选：状态栏指示器（I8）。
5. **I9 自绘右键菜单接管（v1.1）**：自绘菜单「复制/粘贴」数据流默认由剪贴板服务接管——复制动作本就写系统剪贴板，服务监听（WM_CLIPBOARDUPDATE）天然记录，**零改动即接管**；粘贴保持系统语义（Ctrl+V 等价）；落地改动 = ① 文件域 `FileClipboard` 保持 CF_HDROP 互通（不破坏资源管理器双向），**剪切语义条目**进历史时标注 cut 标记（探索版文件捕获为纯复制，需注意）；② 自绘菜单新增「剪贴板历史…」菜单项（文本/文件场景合适位置）→ `Get<IClipboardService>().OpenHistoryWindow()`；③ 动词执行处（MenuManagerService Invoke）接线确认，不绕开服务。
6. **I10 系统右键注册（v1.1）**：注册表 shell verb 注册「BetterDesktop 剪贴板历史」快捷功能——场景 `HKCU\Software\Classes\` 下 `*\shell`（任意文件）/`Directory\shell`（目录）/`Directory\Background\shell`（空白）/`DesktopBackground\shell`（桌面）；命令 = `BetterDesktop.exe --menu-cmd clipboard-history`（**复用宿主参数路由 [verified：App.xaml.cs] + MenuCommandPipe 管道**，宿主侧新增 clipboard-history 命令 → 运行实例打开面板）；注册/注销 HKCU 可撤销，用 RegistryVerbs 领域模型（72-右键菜单 L2 文档）；新键名不与 SuppressedKeys 冲突。
7. **hash 去重加速缓存**：万条级内容比较已够快，量级增大再引入 hash 缓存（1301 lastSavedHash 想法）。
8. **D6 技术库回写**：实施完成后按「技术力积累」skill 更新 1301-clipboard-history.md（补 C# 变体：AddClipboardFormatListener/HwndSource/System.Text.Json/图片落盘元数据化/热键 0x581/观察者插件模式/抑制令牌/内核服务注册模式），并登记本次拆析。
9. **按序粘贴（L1-L3，整体 v1.1，用户已确认）**：队列状态机放服务层（`IClipboardService` 后续版本扩展，v1 契约保持可加性——新增方法不破坏既有消费方）；UI 提供勾选、进度、继续/取消/重置。
10. **OCR 对接（M，插件未来）**：`ImportEntries` 录入接口 v1 即进契约（M1-M3 复用去重/分类/分段/落盘）；OCR 插件本体未来实现，录入后走合适粘贴（M4 复用）。
11. **便利签/钉桌面/工作站（N，未来）**：剪贴板侧仅保证数据访问（N1 已具备）+ 条目模型稳定可序列化；便利签插件（N2/N3）与工作站（N4，**规划中新概念**）未来实现，消费方经 `Get<IClipboardService>()` 零改造接入。

## 13. Definition of Done

- **D1 端到端主场景（真机走查）**：启动 → 菜单栏「+」→ 打开「剪贴板历史」开关 → 复制"abc" → 按 Ctrl+Shift+V → 面板弹出且首条为"abc" → 记事本中 Enter 粘贴成功（焦点回传）→ 再复制"abc" → 面板条目数不变（去重）→ 关闭开关 → Ctrl+Shift+V 无响应、复制新内容面板无新增。
- **D2 类型场景（真机走查）**：截图复制 → 面板图片条目带缩略图 → 粘贴回 mspaint；**图片落盘**：`%LOCALAPPDATA%\BetterDesktop\clipboard\images\` 下出现对应 PNG 文件且历史 JSON 中无 base64 字节；资源管理器复制文件 → 文件条目 → 右键「打开文件位置」选中。
- **D3 隐私/暂停场景（真机走查）**：复制密码管理器窗口内容 → 面板无该条目；面板「暂停」→ 复制任意内容 60s 内不记录 → 恢复后正常记录。
- **D4 持久化（真机走查）**：重启 BetterDesktop 后开关状态保持、历史条目仍在、存储文件非明文（CBENC1 头）、图片文件仍可缩略图显示。
- **D5 构建门禁**：`dotnet build BetterDesktop.slnx -warnaserror -nologo -m:1 --disable-build-servers` 0 警告 0 错误；`dotnet test` 全绿（§8 用例）。
- **D6 技术库**：1301-clipboard-history.md 更新 C# 变体（或按 §12 D6 登记）。
- **D7 大段粘贴全部成功（真机走查，用户视角）**：从网页复制一段含加粗的多段落富文本 → 面板按 Enter 粘贴一次 → Word 中**一次操作后出现完整分段且加粗保留的内容**（用户无感分段过程）→ 粘贴短条目（单段）仍是单次粘贴 → 记事本（不支持富文本）粘贴同一条目 → 内容全部进入（自动降级纯文本，仍"粘贴全部成功"）→ 纯文本大段（多空行段）粘贴 → 全部段落依次进入目标。
- **D8 复杂混合内容分类与合适粘贴（真机走查，用户视角）**：① 从 IDE 复制一段代码 → 面板显示「代码」分类 chip → 粘贴到 IDE → **纯文本且缩进完整**（无富文本乱码）；② 从网页复制"文字+图片"混合段 → 面板显示「富文本·图」→ 粘贴到 Word → **图文混排完整**；③ 复制带表格网页 → 显示「富文本·表」→ 粘贴到 Word → 表格结构保留；④ 纯文字 → 粘贴到 Word 与记事本都正常（多格式让目标应用自选）；⑤ 分类筛选：面板按「代码」筛选 → 只显示代码条目。
- **D9 系统级服务契约（真机走查）**：内核服务图存在 `IClipboardService`（`context.Get<IClipboardService>()` 非 null）；另一插件（如 shell-start-menu 临时探针或后续消费板块）调用 `GetLastCopiedContent()` 返回最近复制、调用 `OpenHistoryWindow()` 打开面板；关闭扩展中心开关后服务仍可 Get、历史仍可查询（监控停止）。

## 14. Handoff（交接给「技术力应用」skill）

### 注入清单（实现前必读）
1. 技术库：`TECH-KNOWLEDGE/13-剪贴板/1301-clipboard-history.md`（红线）、`TECH-KNOWLEDGE/31-热键/3101-global-hotkey.md`（0x581/释放纪律）
2. 移植源（用户早期探索版 = cairoshell-master，源码权威）：
   - `Cairo Desktop/CairoDesktop.Infrastructure/Services/ClipboardManager.cs`（探索版核心：监听/隐私/收藏/驱逐/过期/持久化/粘贴）
   - `Cairo Desktop/CairoDesktop.Infrastructure/Services/ClipboardEntry.cs`（条目模型）
   - `Cairo Desktop/CairoDesktop.Interop/ClipboardNative.cs`（原生写入）
   - `Cairo Desktop/CairoDesktop.MenuBar/Clipboard/ClipboardQuickAccessWindow.xaml.cs`（UI 核心）
   - `Cairo Desktop/CairoDesktop.MenuBar/Services/ClipboardQuickAccessService.cs`（热键模式）
   - `Cairo Desktop/CairoDesktop.Application/Interfaces/Services/IClipboardService.cs`（契约参考）
3. 机制吸收源（官方原版 = cairoshell原版，用户未使用但机制优势纳入，**必须读**）：
   - `Cairo Desktop/CairoDesktop.Infrastructure/Clipboard/ClipboardService.cs`（抑制令牌 `_suppressCapture` + `ReadClipboardSnapshot` 测试接缝 + STA 监听模型——本计划改动 5 的出处）
   - `Cairo Desktop/H5H8Verify/ClipboardSuppressionTests.cs`（写回抑制回归单测范式——本计划 ClipboardSuppressionTests.cs 直接对应）
4. 本项目范式（观察者插件）：`packages/shell/shell-quick-note/QuickNotePlugin.cs` + `QuickNoteLauncher.cs`（ShellWindow/SetThemeBinding/NullVibrancy/RunOnUi 全套）
5. 本项目接线点：`shell-menu-bar/Contracts/ExtensionCatalog.cs`、`shell-menu-bar/Windows/ExtensionsCenterWindow.cs`（Implemented）、`host/Bootstrap.cs`（Factories）、`host/cordis.yml`、`BetterDesktop.slnx`

### 模式判定与适配参数
- **模式**：**系统级服务插件** = `Provide<IClipboardService>`（ADR-002 D1，服务常驻）+ QuickNotePlugin 观察者模式控制监控活性（扩展中心开关 → ISettingsService 键 → Activate/Deactivate 监听与热键，历史保留）；监听/存储/粘贴 = 探索版 ClipboardManager 移植 + 原版抑制令牌与测试接缝 + 外部实现图片落盘/总量预算/最近复制 API/90 天。
- **适配参数**：命名空间 `BetterDesktop.Shell.Clipboard`（契约子命名空间 `.Contracts`）；TFM `net8.0-windows10.0.19041.0`/x64/UseWPF/TreatWarningsAsErrors；设置键 `extensions.clipboard-history.enabled`（监控活性）；存储 `%LOCALAPPDATA%\BetterDesktop\clipboard_history.json`（DPAPI + CBENC1 头 + System.Text.Json）+ 图片 `%LOCALAPPDATA%\BetterDesktop\clipboard\images\{id}.png`；热键 Ctrl+Shift+V / Ctrl+Shift+P / Ctrl+Shift+Backspace；容量 10000/200 收藏/单图 5MB/总量 200MB/90 天；**自动分段粘贴（内部机制）**：触发条件 = HTML ≥2 块级段 / 纯文本 ≥2 空行段、段间 250ms、某段失败即停、RTF 不分段、HTML 片段经 SetHtmlText 包装、**零分段 UI**；**内容分类**：Category ∈ {Text/Code/RichText/Image/File} + HasImages/HasTable/IsCode、入库只算一次、**代码强制纯文本写回**、混合保留完整 HTML、RichText 多格式（HTML+RTF+Text）；Name `shell.clipboard`（cordis id/name `clipboard-history`）；测试程序集名 `BetterDesktop.Shell.Clipboard.Tests`（InternalsVisibleTo 目标）。

### DoD 核销表（实现方逐项自检后由验收方确认）
| # | 核销项 | 结果 |
|---|---|---|
| 1 | §6.1 插件包 9 文件（含 IClipboardService 契约）+ csproj 落地，引用三件套正确 | ☐ |
| 2 | §6.2 扩展中心 2 处注册（External + Implemented） | ☐ |
| 3 | §6.3 宿主 3 处注册（Bootstrap/cordis.yml/slnx） | ☐ |
| 4 | §8 单测全绿（13 文件覆盖抑制/捕获/去重/驱逐/隐私/清理/落盘/过滤/**分段**/**自动触发**/**分类**/持久化/模型） | ☐ |
| 5 | 门禁 0 警告 0 错误 | ☐ |
| 6 | §13 D1-D4 + D7 + D8 + D9 端到端走查通过 | ☐ |

---

## § 可选增强 / 超越需求建议（beyond，不混入强制 scope）

- **beyond-1 · System.Text.Json 替换手写 JSON**：cairoshell 手写序列化/解析 ~200 行（`SerializeHistoryToJson/DeserializeHistoryFromJson/ParseClipboardEntry/SkipJsonValue/ParseJsonString` 等）整体替换为 `System.Text.Json`——更少代码、更稳、少 200 行移植面。收益：降低实现与维护成本。依赖：无（.NET 8 内置）。**本计划已将其纳入强制 scope（§6.1 改动 1）**，因移植手写解析器属于纯浪费。
- **beyond-2 · 热键设置化**：v1.1 将热键字符串写入 `ISettingsService`（`extensions.clipboard-history.hotkey`），复用 3101 的 `"Ctrl+Shift+K"` 风格解析。收益：冲突可自解。依赖：3101 资产。
- **beyond-3 · 来源应用统计驱动常用置顶**：`GetSourceApps` 已按计数排序，可据此在面板顶部加「常用来源」快捷筛选。收益：高频来源（如 IDE）一键过滤。依赖：现成方法。
- **beyond-4 · 条目「复制次数」排序视图**：CopyCount 字段已存在，筛选 chips 可加「常用」档。收益：零成本增强检索效率。依赖：现成字段。
- **beyond-5 · LMDB 存储**（1501 资产）：历史量级达万条后 JSON 全量读写仍够用（10000 条文本 ~MB 级），LMDB 为 v1.1 可选，需评估原生依赖引入成本。
