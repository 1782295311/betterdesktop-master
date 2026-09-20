# 核验：剪贴板历史是否已由 Rust 引擎单跑、C# 是否只做 UI

日期：2026-09-14
核验人：AI（只读核验：源码调用链 + 真实配置 + 运行日志 + 进程状态；未改动任何配置、未触碰剪贴板）
前置文档：`docs/audits/2026-09-14-clipboard-duplication-deepdive.md`

---

## 0. 结论

| 核验项 | 结论 | 依据 |
|---|---|---|
| legacy 未参与 | ✅ **成立** | 配置层无 `backend` 键（默认 engine）、代码层主路径零引用、运行期日志与数据库全部由引擎写 |
| 数据面业务在 Rust | ✅ **成立** | 引擎 19 个模块承担监听/捕获/去重/分类/加密存储/驱逐/热键/写回；面板 25 处业务调用全走 IPC |
| C# 主要承担 UI | ⚠️ **基本成立，但有 4 处例外** | 面板 exe = 纯 UI；IPC 客户端除 RPC 外还本地做键盘注入、合并粘贴写剪贴板、按序粘贴状态机、explorer 定位 |
| 无 legacy 时功能可用 | ✅ **成立** | 宿主当前**根本没在运行**，引擎 + 面板仍在采集/分页/落盘 |

---

## 1. legacy 未参与：三层证据

### 1.1 配置层（真实磁盘状态）

```
%APPDATA%\BetterDesktop\settings.json  → 顶层键数 = 1
  extensions.clipboard-history.enabled = True
  （没有任何 *backend* 键）
```

`ClipboardPlugin.cs:98-99` 的判定是 `Get(BackendKey, "engine") == "legacy"` → 无键 ⇒ 取默认 `"engine"` ⇒ **走 else 分支（引擎 + IPC 代理），legacy 分支不可达**。

### 1.2 代码层（主路径包内零引用）

在 `shell-clipboard-ipc` / `shell-clipboard-panel` / `host` / `shell-desktop` / `shell-menu-bar` 全量搜索
`ClipboardManager|ClipboardSegmenter|ContentAnalyzer|ClipboardNative|ClipboardHistoryWindow`：

> 唯一命中：`shell-clipboard-panel/RecentStrip.cs:19` —— 一句**注释**（记录视觉血缘）。

`new ClipboardManager(...)` 全仓唯一构造点 = `ClipboardPlugin.cs:106`（legacy 分支内部）。
legacy 用的业务工具类（`ContentAnalyzer` / `ClipboardSegmenter` / `ClipboardNative` / `BuildMergeText` / `IsPrivacySensitive`）只出现在 `shell-clipboard` 自身与 `shell-clipboard-tests` 里。

### 1.3 运行期（实证）

| 证据 | 内容 |
|---|---|
| 进程列表 | `BetterDesktop.Clipboard.Engine`(41096, 18:45:18 启动)、`BetterDesktop.Clipboard.Panel`(52392, 18:46:11)、`BetterDesktop.Index.Engine`(30872)。**`BetterDesktop.Host` 未运行** |
| `engine.log` | 初始化 → `eviction sweeper started (every 1800s)` → `hotkey registered: Ctrl+Shift+V / Ctrl+Shift+P / Ctrl+Shift+Backspace` → `ipc server started` → 采集循环：`named-format: 采集 2 项`、`duplicate fingerprint; skipping external file writes`、`updated entry ... (copy_count=2)`、`new entry f3f14064-...`（18:47:39） |
| `panel.log` | `分页加载成功: total=47 count=47 offset=47`（多次）→ 面板经 IPC 读引擎分页；`主题引导完成（entryStyle=sidebar）` |
| `clipboard_history.json` | 919,821 B，header = `43 42 45 4E 43 31 00 01`（`CBENC1` + 版本），**最后写入时间持续刷新** |

→ 三者同时成立：**捕获、存储、查询三条链都在引擎侧闭环**，legacy 一行代码都没跑。

补充：宿主不在运行时，剪贴板功能仍可用（引擎注册全局热键 + 面板常驻侧边栏）；不可用的只有宿主侧入口（菜单栏按钮、桌面右键、设置中心），那属正常解耦，不是缺陷。

---

## 2. 业务在 Rust：引擎职责与 IPC 面

引擎（`engine/src`，19 个模块，**108 个内联单测**）：`listener`/`capture`（监听与捕获）、`fingerprint`（去重指纹）、`analyzer`（分类/代码识别）、`html`+`formats`（HTML/RTF/文本格式层级）、`privacy`（敏感遮罩）、`rules`（按应用清洗规则）、`store`（CBENC1+DPAPI+原子 rename+驱逐）、`thumb`（480px 缩略图）、`hotkey`（3 个全局热键）、`settings`（配置热更新）、`ipc`（命名管道服务端）、`engine`（消息循环 + 800ms autosave + 1800s 驱逐扫描）。

IPC 方法表（`IpcProtocol.cs`）= 引擎侧业务面：

```
query / get_entry / get_content / get_last / list_sources / storage_status
pin / unpin / set_sticker / delete / delete_many / clear_unpinned / set_tags
copy_to_clipboard / copy_temp_to_clipboard / restore_temp_clipboard
import / add_sticker / pause / resume / apply_settings / open_panel / subscribe_events
事件：history_changed / pause_changed / monitoring_changed / clipboard_changed
```

面板侧 `PanelMainWindow` 的业务动作 **25 处全部经 `_client.` 走 IPC**（pin/unpin/delete/delete_many/clear_unpinned/set_tags/set_sticker/add_stickers/copy/copy_temp/restore_temp/分页查询/storage_status/pause/resume/按序粘贴/按格粘/合并粘贴）。

---

## 3. C# 侧实际承担什么（含 4 处"非 UI"例外）

**真正属于 UI/平台的部分**（占 C# 侧绝大部分）：

| 承担 | 位置 | 规模 |
|---|---|---|
| 面板全部界面（列表/条目行/筛选/横幅/编辑器/toast/侧边栏手柄） | `shell-clipboard-panel` | 4,945 行 |
| 设置分区 UI + 右键菜单注册 + 插件装配 + 设置推送 | `shell-clipboard` 共用部分 | 1,393 行 |
| RPC 编解码 / 心跳 / 重连 / 事件分发 | `ClipboardIpcClient` 主体 | 约 1,500 行 |
| 进程探活拉起 | `ClipboardEngineLauncher` | 188 行 |

**例外：以下 4 处不是 UI，但也留在 C#**（需要知道，其余都不影响"数据面在 Rust"的结论）：

1. **键盘注入**（`ClipboardIpcClient.cs:1666,1766-`，`keybd_event`/`SendInput`）：写回剪贴板由引擎做，但"按 Ctrl+V"这一步是 C# 发的——这是设计使然（引擎无窗口、注入要落在用户前台窗口）。
2. **合并粘贴的本地写剪贴板**（`ClipboardIpcClient.cs:792-829`）：多条文本拼接后由 **C# 本地** `SetClipboardText`（OpenClipboard/GlobalAlloc/SetClipboardData）写入再注入。代码自己标注了副作用：*"本地写剪贴板不经引擎抑制令牌，引擎轮询可能将合并文本捕获为新条目（去重置顶，语义可接受）"*。
3. **按序粘贴状态机 + 降级快照**（`ClipboardIpcClient.cs:856-1139`）：序号推进、内容快照、失败提示在 C#；正常路径每条仍走 `copy_to_clipboard`（引擎），引擎写回失败时**降级为本进程快照直接写剪贴板**（`WriteSnapshotToClipboard`），按格粘的单元格同样自包含写回。
4. **外壳动作**：`OpenFileLocation` → `explorer.exe /select`（`ClipboardIpcClient.cs:846-854`）。

---

## 4. 顺带核出的两个运行期事实

1. **引擎当前是"800ms 无条件整体重写"历史库**：`engine.rs:1595-1622` 的 autosave 线程每 800ms 就 `store.save()` 一次（整份序列化 → DPAPI 加密 → 临时文件 → 原子 rename），注释自述 *"dirty 标志后续优化；万条序列化 ~几十 ms，可接受"*。实测 919 KB 文件的时间戳/哈希每秒都在变（密文带随机熵，**内容未变也会变哈希**）。
   - 含义：磁盘写 ≈ 1.1 MB/s 常态churn；且这把 §5 的双写风险从"偶尔覆盖"升级为**双方每秒互相覆盖**。
   - 不紧急（引擎侧已标注为已知简化），但上 dirty 标志的收益比看上去大。
2. **`engine.log` 有 1,896 条 ERROR，全部是 `response write failed; terminating connection`**（客户端心跳超时/宿主重启时断连的收尾噪声，最后一条 18:38），**没有一条数据错误**。属日志噪声，不是故障；但噪声量足以淹没真实问题，值得降级为 WARN 或去重。

---

## 5. 遗留风险（本次核验确认仍然成立）

`ClipboardIpcClient.RequestExit()`（:1635-1646，注释写明"切回 legacy 后端用"）**全仓调用点仍为 0**。

- 当下**不是活跃风险**：legacy 未被启用，引擎是唯一写入方。
- 一旦有人在设置页把"后端模式"切到 legacy 并重启宿主（同时引擎/面板按设计常驻不退出），就会立刻变成 **legacy 与引擎每秒互相覆盖同一个历史库**。

最小收口（与深挖文档 §5 一致）：legacy 分支先调 `RequestExit()` 停引擎（停不掉就不激活 legacy）；面板读 `backend` 后不再回拉引擎；设置页补一句真话。
