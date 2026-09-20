# BetterDesktop.Shell.IndexIpc

> 角色：跨语言客户端库（非插件）——索引引擎（`engine-index`，Rust）的宿主侧接入面。
> 状态：**M1 + M2 已落**（协议 + ping/status/apply_settings/shutdown + `list_apps`）；
> 文件/图标查询属 M3/M4。**已接入消费者**：`shell-app-source`（engine 后端，见其 README）。

## 职责

- **协议常量**（`IndexIpcProtocol`）：管道名 `BetterDesktop.Index.Engine`、magic `BDIX1|`、方法名、JSON-RPC 错误码——**与 `engine-index/src/ipc.rs` 对齐，勿单侧改动**。
- **传输层**（`IIndexTransport` / `NamedPipeTransport`）：字节模式写 + 自有行缓冲读（不用 `StreamReader`，见「红线」）。
- **客户端**（`IndexIpcClient`）：单线程统一读写工作线程 + 心跳节流 + 断线退避重连 + `Reconnected` 事件；公开 `PingAsync` / `GetStatusAsync` / `TryGetStatusAsync` / **`ListAppsAsync`** / `ApplySettingsAsync` / `ShutdownAsync`。
- **进程管理**（`IndexEngineLauncher`）：引擎 exe 定位（环境变量 → 同目录 → `%LOCALAPPDATA%\BetterDesktop\`）、探活、无窗口拉起；**永不抛异常**。
- **模型**（`IndexModels`）：`IndexStatus` / `AppCandidate`（**raw 磁盘事实**：路径 + 名称候选 + 来源根标记）/ `ListAppsResult`（含 `Building`/`Degraded`，供消费者决定回退）/ `IndexSettingsPatch`。

## 依赖

无工程引用（客户端库自带协议与模型；M2 的消费方按需引用本包与 `BetterDesktop.Api`）。

## 对外扩展点

- `IIndexTransport`：传输可替换（单测用内存 fake；未来若改共享内存/其它通道，替换实现即可，客户端零改动）。
- `IndexEngineLauncher.EnsureEngine(log)`：拉起策略集中一处，宿主/托盘/面板共用同一套路径约定（避免「一边找得到、一边找不到」重复拉起抢管道）。
- M2/M3/M4 新增查询方法时，只在本包加「方法常量 + 强类型包装 + fake transport 用例」，不改传输与工作线程。

## 关键红线（真机踩坑沉淀，勿回退）

1. **禁止用 `StreamReader` 读帧**：其 8KB 预读会与 `PeekNamedPipe` 打架 → 响应闷在客户端缓冲里取不出 → 请求超时（症状：心跳超时反复重连，外部脚本探测却正常）。
2. **写帧必须一次性 `Write` 完整字节**：`StreamWriter` 会把 >8KB 的帧拆成多次 `WriteFile` → 引擎收到半截 JSON 报 `-32700`。
3. **重复 `Connect` 前必须 `Disconnect`**：否则旧管道在引擎侧永不释放，4 槽耗尽后所有客户端连不上。
4. **心跳必须节流**（≥2s 且不唤醒发送循环）：否则「发心跳 → 唤醒 → 再发」正反馈风暴拖垮响应处理。
5. **工作线程必须永生**：异常一旦逃出 `WorkerLoop` 即永不重连，而 UI 仍显示「正在重试…」（假重试真死亡）。
6. **`Reconnected` 必须异步派发**：订阅者在其回调里发同步 RPC，若在读线程回调会自死锁。
7. **断线时清空发送队列**：重放已入队帧会让非幂等命令被执行两次。
8. **设置下发必须显式 kebab-case 键**（`app-source-backend` 等）：两侧命名策略漂移会让请求被**静默忽略**，是最难排查的故障类（契约测试 `ApplySettingsWireKeysAreKebabCase` 守护）。

## Known Limitations

- M1 无事件订阅：引擎侧 `subscribe_events` 与 `AppSourceChanged` 增量通知属 M2。
- `search_files` / `get_icons` 引擎尚未实现（属 M3/M4），调用会得到 `-32601`（**故意不返回空集**：空集会让消费者误以为系统里没有文件/图标）。`list_apps` 已在 **M2** 落地，并被 `shell-app-source` 消费。
- 本包未接入 `IResourceGovernor`：索引引擎是独立进程，内存由引擎自管并经 `status.rssBytes` 上报（计划 §5.4）。
