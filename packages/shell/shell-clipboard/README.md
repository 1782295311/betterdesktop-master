# BetterDesktop.Shell.Clipboard

剪贴板历史的**宿主侧接线**。引擎化后宿主只做 IPC 客户端——数据面在独立 Rust 引擎进程，入口面在面板 exe，三者经命名管道协作。终态版本 **v1.3**；蓝图 = `docs/plans/archive/2026-09-08-clipboard-history-extension.md`，引擎化方案 = `docs/plans/2026-09-11-clipboard-engine-rust-ipc.md`，文档链时间序 = `docs/plans/clipboard-docs-index.md`（早的在前）。

## 架构（唯一后端 = engine）

```
Rust 引擎（数据面）              面板 exe（入口面）                 宿主（本包）
BetterDesktop.Clipboard.Engine   BetterDesktop.Clipboard.Panel      ClipboardPlugin
 监听 / 捕获 / 存储 / 热键 / IPC  侧边栏入口 / 完整面板 / 粘贴 / 分段  Provide<IClipboardService> = IPC 代理
        ▲                                ▲                                ▲
        └────────── 命名管道 BetterDesktop.Clipboard.Engine（BDCB1| magic）┘
```

- 宿主 `ClipboardPlugin` 唯一走引擎：`Provide<IClipboardService>` 即 `ClipboardIpcClient`（`shell-clipboard-ipc`），**不监听、不存储、不建面板**。
- 宿主内旧实现（`backend=legacy` 那套 `ClipboardManager` / `ClipboardHistoryWindow`）已于 2026-09-14 **整层删除**：它与引擎写同一份 `%LOCALAPPDATA%\BetterDesktop\clipboard_history.json`，而引擎每 800ms 无条件整体重写，二者并存必然互相覆盖。
- 引擎/面板的定位与拉起 = `ClipboardEngineLauncher`（IPC 包，宿主与面板共用同一套候选路径）；约定部署路径 `%LOCALAPPDATA%\BetterDesktop\`，用 `scripts/deploy-clipboard.ps1` 部署。

## 本包内容

- **插件接线**
  - `ClipboardPlugin.cs`：引擎装配（连 IPC + 拉起引擎与面板入口）+ 设置推送（`apply_settings`，引擎侧 kebab-case 部分字段合并）+ 重连后自动重推全量配置。
  - `ClipboardShellMenuRegistrar.cs`：系统右键 4 场景（`*\shell` / `Directory\shell` / `Directory\Background\shell` / `DesktopBackground\shell`）。engine 模式下命令改向 `"<panelExe>" --open`（**宿主未运行仍可用**）；面板 exe 缺失时回退宿主 `--menu-cmd clipboard-history`；含旧宿主命令的迁移判定（幂等，不覆盖用户同名项）。
  - `Sections/ClipboardSection.cs`：设置分区（容量 / 收藏 / 保留天数 / 图片与总量预算 / 存储模式 / 入口形态 / 引擎状态），热键为只读展示。
- **消费入口**：菜单栏「📋」按钮（I6）——经契约调 `OpenHistoryWindow()`，即引擎 `open_panel`（拉起面板 exe）。

## 配置键（`extensions.clipboard-history.*`）

| 键 | 默认 | 说明 |
|---|---|---|
| `enabled` | **true** | 剪贴板监听开关（引擎捕获启停；键缺失按开启） |
| `capacity` | 10000 | 历史条数上限 |
| `pinned-limit` | 200 | 收藏上限 |
| `retention-days` | 90 | 未收藏条目保留天数 |
| `storage-mode` | `full` | `full`=原图+副本 / `paths-only`=仅路径（图片只留缩略图、文件只记路径） |
| `max-total-mb` | 1024 | 统一总量预算（原图 + 文件副本 + HTML 提取图 + 文本压缩） |
| `max-image-mb` | 64 | 单张图片上限 |
| `max-image-pixels` | 100000000 | 单图像素上限（宽×高） |
| `thumb-width` | 480 | 缩略图宽度（px） |
| `max-html-image-mb` | 2 | HTML data URI 提取图单张上限 |
| `file-copy-max-mb` | 64 | 文件副本单文件上限（超限仅记路径） |
| `max-text-bytes` | 10485760 | 单条文本上限（字节，超限拒绝捕获） |
| `max-content-total-mb` | 100 | 文本总量上限（压缩后） |
| `push-events` | `true` | 剪贴板更新事件推送开关（灵动岛等只读订阅方） |
| `entry-style` | `sidebar` | 面板入口形态：`sidebar` / `off`（历史值 `orb`/`both` 一律按 `sidebar` 处理） |
| `dismiss-mode` | `auto` | 面板收起方式：`auto`=点面板外/失焦即收起；`manual`=只能点「✕」或 Esc 收起（需重启面板生效） |
| `app-rules` | `[]` | **P1-5** 按应用清洗规则（JSON 数组字符串）：`{app,action,pattern,replacement}`，`action ∈ ignore/drop/replace`；设置界面「按应用清洗规则」可视化编辑 |
| `named-format-passthrough` | `true` | **P1-4** 命名格式透传（Excel/WPS 表格"可编辑表格"数据） |
| `named-format-max-count` | 8 | **P1-4** 单条目最多保留的自定义格式数（超限跳过） |
| `named-format-max-kb` | 1024 | **P1-4** 单个自定义格式字节上限（KB，超限跳过——截断的二进制是坏数据） |
| `named-format-total-kb` | 4096 | **P1-4** 单条目自定义格式总字节上限（KB） |
| `sensitive-detection` | `true` | **P2-2** 敏感信息识别（手机号/身份证/邮箱/银行卡/密钥）→ 打标记 + 面板预览遮罩 |
| `sensitive-mask-leading` | 3 | **P2-2** 遮罩保留的前缀可见字符数 |
| `sensitive-mask-trailing` | 2 | **P2-2** 遮罩保留的后缀可见字符数 |
| `cell-paste-auto-key` | `tab` | **按格粘**默认自动按键：`tab` / `enter` / `none`。⚠️ **只由面板消费**（面板直读扁平键），不进引擎 Settings —— 与 `paste-back-hotkey` 同模式 |
| `hotkey-*` | — | **只读**：组合键由引擎统一注册（全局唯一），不支持改键 |

> **新增设置项必须四处同步**：引擎 `Settings` 字段 / 设置分区控件 / `ClipboardPlugin` 的 `apply_settings` patch /
> `WatchKeys`。否则"界面能改、引擎收不到"——引擎只在自己启动时读一次 `settings.json`，运行中靠 `apply_settings` 热更新
>（2026-09-12 实测：14 项里有 6 项从未推给引擎、且不在监听集内）。

引擎自行读取同一份 `settings.json`；宿主侧变更经 `apply_settings` 推送增量。

## Known Limitations

- 引擎是常驻进程：宿主退出/卸载都不关停它（面板同样常驻）。停引擎须由引擎自身处理，宿主侧无停用入口。
- 引擎/面板必须位于约定路径（`%LOCALAPPDATA%\BetterDesktop\`）才能被三方互相定位；发布链路见 `scripts/deploy-clipboard.ps1`（`scripts/publish.ps1` 的 dist 全量发布尚未纳入剪贴板引擎/面板，另批收口）。
- WM_CLIPBOARDUPDATE 广播在部分 Win11 版本（如 26200）不可用，回退 500ms 序列号轮询，捕获延迟 ≤1s（引擎侧；宿主内实现已于 2026-09-14 删除）。
- 引擎每 800ms 无条件整体重写历史库（`engine.rs` autosave，dirty 标志待优化）：919KB 历史 ≈ 常态 1 MB/s 磁盘写入。
- 存储文件以 DPAPI 加密（CBENC1 头），无法人工直接阅读；验证需走服务接口/IPC/日志。
- 面板为纯代码 UI，无 XAML 设计时支持；视觉/交互走查依赖真机人工验证。
- 多显示器 v1 仅主屏 workArea；侧边栏收起由面板 MouseHook 外点/Esc 承担（无收起动画）。

## 未启动项（立项标注"未来/可选"）

C5 语言识别、E5 搜索历史、F4 敏感模式识别、H5 LMDB、I8 状态栏指示器、N2-N4 便签/工作站（N1 数据访问已预留）。
