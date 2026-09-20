# BetterDesktop.Shell.Clipboard.Ipc

剪贴板引擎的 **C# IPC 客户端库**（宿主插件与面板 exe 共用）：契约与协议单点维护，避免两端各写一份。

## 内容

| 文件 | 职责 |
|---|---|
| `ClipboardIpcClient.cs` | `IClipboardService` 的远端实现：JSON-RPC over NamedPipe、单线程统一读写、断线重连、事件订阅、按序粘贴状态机、临时暂停、诊断痕迹 |
| `ClipboardTransport.cs` | 传输抽象 + `NamedPipeTransport`（字节模式写 + **自有行缓冲读**） |
| `ClipboardEngineLauncher.cs` | 引擎/面板 exe 的定位、探活、无窗口拉起；`OpenPanel()` = IPC 降级入口 |
| `IpcProtocol.cs` | 管道名 / magic / 方法名 / 分页上限等协议常量 |
| `ClipboardChangedInfo.cs`、`ClipboardIpcException.cs` | 事件摘要 DTO、RPC 异常 |

## 红线（2026-09-12 真机血案 · 逐条都是踩过的坑）

1. **心跳必须节流，且不得唤醒发送循环**。`SendHeartbeat` 若调 `_wake.Set()`，会形成
   "发心跳 → 唤醒循环 → 条件仍真 → 再发"的**正反馈风暴**（实测 7ms 内 12 个心跳），响应处理被拖垮 →
   3s 等不到响应 → 误判超时 → 断开重连 → 无限循环。症状是"IPC 时好时坏、engine not connected /
   点击入口没反应"。健康信号只认 `_lastInbound`（任何入站帧都会刷新）。
2. **读路径禁止 `StreamReader` + `PeekNamedPipe` 组合判断**。`StreamReader` 会预读整段管道数据，
   管道随之变空 → `IsDataAvailable()` 误判"无数据" → 后续响应**永久闷在客户端缓冲里取不出来**，
   对应请求 5s 超时。必须用自有行缓冲（`_inbox`）——判断与读取共用同一事实来源。
3. **`Connect()` 必须先 `Disconnect()`**。重试路径若泄漏旧流，引擎侧连接永不释放 → 4 个连接槽耗尽 →
   所有客户端（面板/宿主）都连不上。
4. **工作线程永生**：`WorkerLoop` 必须整轮 try/catch。任何未预期异常逃出循环 = 永不重连，
   而 UI 仍显示"正在重试"（假重试、真死亡）。
5. **`Reconnected` 必须异步派发**：订阅者的典型用法是"重连后全量刷新"（会发同步 RPC），
   在读线程同步回调会自死锁（该 RPC 的响应需要同一线程读取）。
6. **入口不依赖长连接**：`OpenHistoryWindow()` 在 IPC 失败时降级为直接拉起面板 exe `--open`
   （面板自身会探活引擎）。用户点右键/菜单栏必须出界面。
7. **诊断痕迹**：连接/断开/失败写 `%LOCALAPPDATA%\BetterDesktop\logs\ipc-client.log`
   （多进程追加、失败静默）。本轮全部根因都是靠它数出来的——没有它只能靠猜。
8. **按序粘贴队列必须"自包含"**（2026-09-12 用户实测"选了按序粘贴它会自己释放"）：
   队列**不得**只存条目 id —— 条目在引擎侧一旦消失（被删 / 清理未收藏 / 迁移去重合并 / 引擎重启快照回退），
   `copy_to_clipboard(id)` 必然 `entry not found`。`BeginSequentialPaste` 必须逐条抓内容全文
   （`get_entry`，`contentInBin` 的大文本走 `get_content`），粘贴时**引擎失败即降级为快照本地写回**，
   两条路都不通则**抛错 + 经 `SequentialIssue` 报提示**，**禁止静默跳过**（静默正是"内容自己消失"的观感来源）。
   单测不碰真实剪贴板/桌面按键：注入 `SnapshotWriterHook` / `PasteInjectorHook`。
9. **写帧禁止用 `StreamWriter`**（2026-09-12 真机复现，二次踩坑）：消息模式管道「一次写入 = 一帧」，
   `StreamWriter` 的 8192 缓冲会把 >8KB 的请求拆成多次 `WriteFile` → 引擎只读到半截 JSON（`-32700 Parse error`），
   残片还会被当成新请求（`engine.log` 实证：批量请求在 column 1034 被切断）。
   必须 `pipe.Write(UTF8.GetBytes(json + "\n"), 0, len)` + `Flush()`。
10. **请求体必须 camelCase**：引擎 serde 是 `rename_all = "camelCase"` 且必填字段无 default，
    默认 `JsonSerializer.Serialize`（PascalCase）序列化模型对象会让批量请求恒失败（`invalid entries`）。
    统一走 `RequestOptions`（`PropertyNamingPolicy = CamelCase`）；字典键不受该策略影响，`apply_settings` 的 kebab-case 仍然安全。
11. **表情包导入（`AddStickers` → `add_sticker`）的返回必须整体消费**：`{added, skipped, errors[]}` 三态缺一不可 ——
    `skipped` 是"同一张图重复导入"（内容哈希去重），`errors` 是逐文件失败原因（不支持格式/超限/不可读）。
    **只报 added 而丢掉 errors** 就等于静默失败（用户以为存进去了）。paths 需先去重 + trim 再发。
    引擎侧语义与红线见 `TECH-KNOWLEDGE/13-剪贴板/1302-sticker-mode.md`。

## Known Limitations

- 事件订阅按连接维护（无订阅者零开销）；重连后由 `Reconnected` 触发全量刷新。
- 协议版本未协商（`ping` 返回值含 `version`/`pid`，可用于诊断）。
- 心跳间隔 2s / 超时 3s 为常量；极端慢机器可能需要放宽。
- 列表查询只提供 `GetFilteredEntriesPage`（引擎侧过滤 + 分页，单页上限 500）；契约 `IClipboardService` 不含列表查询，需要读历史的消费方直接用客户端的具体类型。
