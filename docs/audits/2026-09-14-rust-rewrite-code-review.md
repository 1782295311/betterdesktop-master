# 代码审查报告 · Rust 重写批次（剪贴板引擎 / 索引引擎 / 转换引擎）

> 日期：2026-09-14
> 范围：本仓三个 Rust 工程 —— `engine/`（剪贴板）、`engine-index/`（索引）、`native/convert-engine/`（格式转换）
> 起因：用户反馈"最近在用新编程语言重写项目部分的功能，很容易出问题，你好好审查审查"
> 方法：`cargo clippy --all-targets`（Rust 官方静态分析）+ 逐文件人工审查 + 与 C# 原实现逐项对照 + 关键缺陷**可复现实证**
> 状态：**4 项严重缺陷 + 7 项一般缺陷已修复**，见 §7 修复记录（含验证证据）；其余一般/优化项按 §7.3 留档待排期。

## 0. 审查方法说明（含工具适配）

| 项 | 说明 |
|---|---|
| 代码审查 skill 自带脚本 | `code_reviewer.py` 的 `LANGUAGE_MAP` **不含 `.rs`**（只有 py/js/ts/java/c/cpp/cs），直接跑会「扫描 0 个文件」；其「华为 Java 规范评分」亦仅对 Java 生效。故本次以 **Rust 官方等价静态分析（clippy）** 替代，规则映射见附录 A。 |
| clippy 结果 | engine 51 条 / engine-index 11 条 / convert-engine 19 条告警（**全部为 style/complexity 级，无 `correctness` 级**）；可 `--fix` 的分别是 45 / 11 / 10 条。 |
| 人工审查覆盖面 | 全量读取：clipboard 的 capture/ipc/model/formats/listener/engine/store/dpapi/hotkey/log/fingerprint/html；convert 的 exec/pdf_security/service/run；index 的 9 个文件（并行子代理通读 + 关键结论逐条回源码复核）。 |
| 证据级别 | 采用本仓 `diagnose-*.md` 口径：`SOURCE`（源码可核）/ `RUNTIME_ARTIFACT`（有运行产物）/ `EVIDENCE_REQUIRED`（仅推断）。**严重项均给出 SOURCE 或已实测复现**。 |

## 1. 总体结论

**三个引擎的工程质量整体好于预期**：Win32 FFI 的结构体大小、宽字符串生命周期、HRESULT 低位掩码、句柄释放（`HICON`/`DC`/`HBITMAP`/管道/Mutex）、锁中毒统一 `into_inner` 取回、C# 契约字段命名（camelCase / 枚举数值 / 错误码 -327xx）**逐项核对全部一致**。用户「很容易出问题」的观感，主要来自下面这类**跨语言移植特有的语义陷阱**，而不是内存越界或句柄泄漏。

但也确实存在 **4 个严重缺陷**，其中第 1 项已**运行复现**，且都是"改对 C# 却写错 Rust"的典型：

| # | 严重度 | 一句话 | 证据 |
|---|---|---|---|
| C1 | 严重 | 生成的 `CF_HDROP` 头长度自相矛盾 → 复制文件/文件夹/表情包粘出来路径被吃掉前 2 个字符 | 已复现（RUNTIME_ARTIFACT） |
| C2 | 严重 | 去重命中时，本次捕获已落盘的原图/缩略图/副本/大文本 bin **永不回收**（磁盘无限增长 + 存储横幅虚高） | SOURCE |
| C3 | 严重 | 剪贴板序列号去重是 load-then-store 非原子 → 广播线程与轮询线程可同时通过 → 同一次复制被处理两次 | SOURCE |
| C4 | 严重 | 索引引擎「应用索引为空/构建失败」不置降级位 → 消费者把"没扫到"读成"系统里没有应用" | SOURCE |

### 评分（Rust 适配五维，非华为 Java 规范）

| 维度 | 得分 | 说明 |
|---|---|---|
| 正确性 / 语义等价 | 12 / 20 | 严重项 1、2 与多项一般项都属语义移植错位；单元测试覆盖了"自证式"往返，未覆盖跨进程/跨实现契约 |
| 安全性与 FFI | 15 / 20 | FFI 基本盘扎实；扣 IPC 无安全描述符、magic 为公开常量、超长帧当"正常关闭" |
| 并发与资源 | 12 / 20 | 锁中毒处理是全仓亮点；扣序列号 TOCTOU、孤儿文件、事件通道无上限 |
| 错误处理与可观测 | 13 / 20 | 「失败不得正常化」纪律大体落实（degraded/errors 数组/日志分级），扣 `shutdown` 恒 ok、`GetMessageW(-1)`、日志无轮转 |
| 可维护性 / 一致性 | 14 / 20 | 注释密度与"为什么"记录极佳（本项目特色）；扣 clippy 81 条、多处死代码与文档/实现不符 |

**合计 66 / 100 — 评级：需改进（60–69）。**判据：存在 1 项已复现的用户可见功能损坏 + 3 项会造成数据/磁盘/状态错误的缺陷，必须先修再谈优化。

## 2. 严重问题

### C1【已复现】`build_hdrop` 的 DROPFILES 头长度自相矛盾，`parse_hdrop` 读错 `fWide` 偏移

**位置**：`engine/src/capture.rs:1030-1045`（构造）、`engine/src/capture.rs:541-547`（解析）

```1030:1045:better-desktop-cordis/engine/src/capture.rs
fn build_hdrop(paths: &[String]) -> Vec<u8> {
    let header_len = 24usize; // DROPFILES 20 + 对齐 4
    let mut out = Vec::new();
    out.extend_from_slice(&(header_len as u32).to_le_bytes()); // pFiles  ← 写 24
    out.extend_from_slice(&[0u8; 8]); // pt (POINT)
    out.extend_from_slice(&0u32.to_le_bytes()); // fNC
    out.extend_from_slice(&1u32.to_le_bytes()); // fWide     ← 实际写在偏移 16
```

`DROPFILES` 的真实布局是 `pFiles(4) + POINT(8) + fNC(4) + fWide(4) = 20` 字节（`sizeof(DROPFILES)==20`，`fWide` 在**偏移 16**）。但本函数只写了 20 字节，却把 `pFiles` 声明为 **24** —— 路径表因此比声明位置早 4 字节。解析侧 `let wide = bytes.get(20)...`（`:547`）同样错把"路径首字节"当 `fWide`（在宽字符路径下恰好非零而"蒙对"）。

**与 C# 原实现对照（这是重写引入的回归，不是继承缺陷）**：

```1428:1442:better-desktop-cordis/packages/shell/shell-clipboard-ipc/ClipboardIpcClient.cs
    private static byte[] BuildDropFiles(string[] paths)
    {
        const int HeaderSize = 20;
        ...
        BitConverter.GetBytes(HeaderSize).CopyTo(buffer, 0); // pFiles
        BitConverter.GetBytes(1).CopyTo(buffer, 16); // fWide = TRUE
```

**复现实证**（临时在 `capture.rs` 测试模块加契约断言后 `cargo test`，跑完已回滚）：

```
pFiles=24 len=40 byte16=1 byte20=67
roundtrip=["\\a.txt"]          <-- 输入 "C:\a.txt"，前两个字符被吃掉
assertion `left == right` failed: DROPFILES.pFiles 必须 = 20
  left: 24
 right: 20
test result: FAILED
```

**影响**：`set_files_entry` / `set_sticker_entry` / `write_back_snapshot` 三条写回路径共用它 —— 用户"复制文件/文件夹/表情包 → 粘到资源管理器"会拿到**不存在的路径**（`C:\a.txt` → `\a.txt`），表现为"粘不出来 / 粘出来是错的"。现有单测 `hdrop_wide_parse` 手工构造了"正确的 24 字节头"，因此**恰好掩盖了构造侧的错**。

**修法**：`build_hdrop` 补 4 字节对齐填充（或令 `header_len = 20` 与之一致）；`parse_hdrop` 的 `fWide` 改读偏移 **16**；并把「构造 → 解析」往返写成正式单测（`build_hdrop` 输出必须能被 `parse_hdrop` 原样还原）。

### C2 去重命中时，本次捕获已落盘的文件全部变成孤儿（永不回收）

**位置**：`engine/src/engine.rs:314-375`（`build_entry` 先落盘）+ `engine/src/store.rs:280-320`（`upsert` 命中分支只改条目）

`build_entry` 用**新 id** 写盘：`clipboard\images\{id}.png`、`clipboard\thumbs\{id}.{png,jpg}`、`clipboard\files\{id}\*`、`clipboard\html-images\{id}\*`、`clipboard\content\{id}.bin`。之后才 `store.upsert(entry)`：

```280:320:better-desktop-cordis/engine/src/store.rs
    pub fn upsert(&mut self, mut entry: ClipboardEntry) -> UpsertOutcome {
        let fp = entry.fingerprint();
        if let Some(id) = self.index.get(&fp).cloned() {
            ... existing.copy_count += 1; ... return UpsertOutcome::Updated(id, copy_count);
        }
        ...
```

命中即 `Updated`，**新 id 的产物没有任何人引用、也没有任何清理路径**：`on_clipboard_update` 只在 `New` 分支调 `evict()`（`engine.rs:232`），`remove_entry_files` 只处理"被保留条目自己的路径"。而指纹去重如今是**激进**的（图片按像素、文本按剥离后纯文本）——重复复制同一张截图 / 同一段文字（>100KB 走 `content.bin`）每一次都在磁盘留下一份垃圾。

**后果**：① 磁盘占用随"重复复制次数"线性增长且永不自愈；② `total_used_bytes()` 扫描整个 `clipboard\` 目录 → 存储横幅长期虚高、用户被误导去"清理"，而清理也删不掉这些孤儿（不在任何条目名下）。`evict()` 的注释（`store.rs:796-799`）已经记录过同类问题（"孤儿文件计入 total_used_bytes"）但只修了驱逐路径，**未修这条**。

**修法**：`upsert` 命中时返回新 id（或由调用方判定），命中即调用一次等价的 `remove_entry_files(&新 entry)`；或把 `build_entry` 的落盘延后到"确认是 New"之后。

### C3 剪贴板序列号去重非原子：广播线程与轮询线程可同时放行

**位置**：`engine/src/listener.rs:104-114`

```104:114:better-desktop-cordis/engine/src/listener.rs
    pub fn should_capture(&self) -> bool {
        if !self.is_enabled() { return false; }
        let seq = unsafe { GetClipboardSequenceNumber() };
        if seq == self.last_seq.load(Ordering::Acquire) {
            return false; // 已处理（另一通道先到）
        }
        self.last_seq.store(seq, Ordering::Release);   // ← load 与 store 之间有窗口
        true
    }
```

调用方是**两个线程**：窗口过程（`WM_CLIPBOARDUPDATE`，`main.rs:206-213`）与 500ms 轮询线程（`listener.rs:117-125`，`main.rs:79` 启动）。注释声称"另一通道先到则去重"，但 `load` 与 `store` 不是一个原子操作 —— 两线程可同时读到旧值、同时返回 `true`。随后 `on_clipboard_update` 全程无串行化（隐私判定、快照、`build_entry` 都在锁外并行），会对**同一次复制捕获两遍**。

**后果**：`copy_count` 虚增；两次并发 `upsert` 里后者可能仍按 `Updated` 处理（文本/图片可去重，但 Files 类在 paths-only 下指纹依赖路径尚可）；更实际的是**双份磁盘写**（与 C2 叠加，垃圾翻倍）与双份事件广播（面板刷新抖动）。

**修法**：`last_seq.compare_exchange(prev, seq, ...)` 只有成功者放行；或给 `on_clipboard_update` 加一次性互斥（`Mutex`/`try_lock` 直接丢弃重复触发）。

### C4 索引引擎：应用索引「空/失败」不置降级位，静默降级

**位置**：`engine-index/src/main.rs:46-57` + `engine-index/src/appindex.rs`（`build` 只返回 `(count, ms)`）

```46:57:better-desktop-cordis/engine-index/src/main.rs
    let (app_count, app_ms) = appindex::build(&settings);
    let (file_count, file_ms, capped) = fileindex::build(&settings);
    if capped {
        engine::set_degraded(Some(format!("文件索引达到 maxEntries=... 上限已截断 ...")));
    } else {
        engine::set_degraded(None);
    }
```

`degraded` 完全由 `fileindex` 的 `capped` 决定。appindex 侧即便 `%APPDATA%`/`%ProgramData%`/`ProgramFiles` 取不到、开始菜单全部枚举失败，也只返回 `count = 0`，`degraded` 仍为 `false`。于是 `list_apps` 返回 `{"apps":[], "count":0, "degraded":false}`，消费者据 `Degraded` 决定是否回退本地实现 —— 现在会直接展示**空列表**。

这与该模块头部自述的「M10 纪律：禁止静默降级」和本仓 `runtime-health.md` 的 fail-closed 纪律直接冲突。

**修法**：`appindex::build` 返回失败/触顶位（或"启用索引但枚举到 0 个根"即置失败），与 `capped` 一起决定 `set_degraded` 的原因串；同时补一条"appindex 返回 0 时 status.degraded 必须为真"的回归测试。

## 3. 一般问题

### G1 `GetMessageW` 返回 `-1`（错误）被当作"有消息"——两个引擎同病

`engine/src/main.rs:109` 与 `engine-index/src/main.rs:123` 均为 `while GetMessageW(&mut msg, None, 0, 0).as_bool()`。Win32 契约里 `BOOL(-1)` 表示**调用失败**，而 `BOO::as_bool()` 是 `!= 0` → `-1` 为真 → 在未初始化的 `MSG` 上自旋（高 CPU、消息泵已坏但进程"看着活着"）。MSDN 要求显式三态判断。
**修法**：`let r = GetMessageW(...); if r.0 == 0 || r.0 == -1 { break; }`。

### G2 `read_message` 在 `ERROR_MORE_DATA` 分支用整块缓冲而非 `chunk[..read]`——两个引擎同病

`engine/src/ipc.rs:279-283`、`engine-index/src/ipc.rs:243-253`：成功分支用 `&chunk[..read]`，`ERROR_MORE_DATA` 分支用整个 64KB `chunk`。当前消息模式管道下缓冲恒被填满，**恰好**正确，但这是隐性依赖而非 API 契约；一旦 `read < READ_BUF`（例如后续改成字节模式或调大读超时），未写入的尾随旧数据会被拼进帧 → JSON 解析错乱。
**修法**：统一 `&chunk[..read as usize]`。

### G3 后续帧不强制 magic，与"首帧强制校验"的安全意图不对称

`engine/src/ipc.rs:248-254`、`engine-index/src/ipc.rs:223-229`：`strip_magic` 把 magic 当**可选**前缀。首帧必须带（握手），之后不带也照收 —— 扫描器只要通过首帧即可无障碍通信。
**修法**：后续帧同样强制（缺失即断连），或明确文档化为"仅握手期"，别让注释暗示这是安全边界。

### G4 命名管道无安全描述符，magic 是公开常量

`CreateNamedPipeW(..., None)`（两个引擎）。同会话任意进程都可连入并调用 `apply_settings`（含任意 `scan-roots`）、`open_panel`、`shutdown`。magic（`BDCB1|` / `BDIX1|`）是编译期常量，仅相当于"擦除误连"。
**修法**：传入仅当前用户 SID 的 `SECURITY_ATTRIBUTES`，或 `ConnectNamedPipe` 后校验对端进程镜像；至少把 magic 改为每实例随机值。

### G5 索引引擎：慢同步 RPC 撞客户端 3s 心跳阈值 → 假断线重连

`engine-index/src/engine.rs:317-336` 的 `get_icons` 在**连接线程内**串行处理最多 64 个图标（提取 + PNG 编码 + base64），冷缓存批次可达秒级；期间不读也不回任何帧。客户端只以"最近是否收到入站帧"判活，阈值 3s：

```33:36:better-desktop-cordis/packages/shell/shell-index-ipc/IndexIpcClient.cs
    private const int HeartbeatIntervalMs = 2000;

    /// <summary>心跳响应等待上限：超时即判连接死亡并触发重连。</summary>
    private const int HeartbeatTimeoutMs = 3000;
```

→ 满批图标请求会周期性触发（假）断线重连，在途请求被打断。建议两处一起明确"服务端最大同步处理时长 < 客户端心跳超时"，或引擎分批让出/改工作线程池。

### G6 索引引擎：`shutdown` 恒返回 `{"ok": true}`（把失败当成功）

`engine-index/src/engine.rs:212-216` 丢弃 `request_shutdown()` 的返回值；而 `request_shutdown`（`:168-179`）在窗口未创建或 `PostMessageW` 失败时**只记日志**。调用方收到成功却不会退出。
**修法**：`request_shutdown` 返回 `bool/Result`，据此回 `ok` 或 `-32603`。

### G7 索引引擎：路径经 `to_string_lossy()` 跨进程 → 非 UTF-8 路径被损坏

`fileindex.rs:126-127`、`appindex.rs:73` 均 `to_string_lossy().into_owned()`。Windows 文件名可含非法 UTF-16 序列，`to_string_lossy` 会替换为 `U+FFFD`，C# 侧拿到的路径**再也打不开/启动不了**（`AppCandidateMapper` 直接用它建 `AppItem` 与稳定 id）。中文等合法 UTF-16 不受影响，属低频但真实的静默数据损坏。
**修法**：跨进程用可逆编码（UTF-16 `Vec<u16>` 或 `\\?\` + 原始字节 + 标记）；至少在发生替换时记日志，别静默产出坏路径。

### G8 索引引擎：`appindex` 完全忽略 `max_entries`，与设置文档的"合计硬上限"矛盾

`settings.rs:22-23` 注释为「应用 + 文件合计的硬上限」，实际只有 `fileindex.rs:85` 消费它。要么让 appindex 也受约束并入降级原因，要么把注释/契约改成"文件索引条目上限"。

### G9 HTML data URI：`svg+xml` / `x-icon` 被归一成 `png` → 还原出错误 MIME

`engine/src/html.rs:378-384`：

```378:384:better-desktop-cordis/engine/src/html.rs
fn normalize_ext(ext: &str) -> String {
    match ext {
        "jpeg" => "jpg".to_string(),
        "jpg" | "png" | "gif" | "webp" | "bmp" | "svg" | "ico" | "avif" => ext.to_string(),
        _ => "png".to_string(),
    }
}
```

输入是 MIME 子类型（`mime_and_data[..b64_start]`，见 `:307`），而真实的 `image/svg+xml` 会得到 `svg+xml`、`image/x-icon` 会得到 `x-icon` → 双双落到 `_ => "png"`：文件按 `0.png` 落盘、还原时拼出 `data:image/png;base64,<svg 或 ico 字节>` → **粘贴出去是坏图**。（`svg` / `ico` 分支因此形同虚设。）
**修法**：先按 `;` / `+` 归一（`svg+xml → svg`、`x-icon → ico`），或直接用白名单外"原样保留 MIME 子类型"。

### G10 转换引擎：`publish_all` 的"输出不得等于输入"守卫恒不成立（死检查）

`native/convert-engine/src/service.rs:117-132`：`input_set` 由 `std::fs::canonicalize` 得到（Windows 上返回 `\\?\C:\...` 扩展前缀路径），却与**未规范化**的 `target` 直接 `==` 比较 → 永远为假。真正兜住"不覆盖输入"的是 `unique_target` 的存在性检查，这层防线实际是死的。同一函数里 `unique_target` 的 `exists()` → `fs::rename` 存在 TOCTOU，而 `fs::rename` 在 Windows 上是**覆盖语义**，与注释"重名 (2)(3) 永不覆盖"不一致（并发转换时可能静默覆盖）。
**修法**：比较两侧都用规范化路径（或都用 `Path::new` 原样），发布用 `MoveFileExW` 不带 `REPLACE_EXISTING` / 或先 `create_new` 占位再改名。

### G11 转换引擎：`TempDir` 的 Drop 自清理在 release 下失效

`service.rs:55-60` 依赖 `Drop` 递归删临时目录，而 `Cargo.toml` 的 `[profile.release]` 设了 **`panic = "abort"`** —— 任何 panic 都不会 unwind，`Drop` 不执行 → 用户源文件目录里残留 `.bd-convert-*` 目录。同理，`panic = "abort"` 也让"进程内任何 `expect` 都是可接受的"这一预设不成立，崩溃即整进程死。
**修法**：要么去掉 release 的 `panic=abort`，要么启动时清扫同前缀历史目录 + 用显式 `catch_unwind` 包裹作业。

### G12 剪贴板引擎：autosave 每 800ms 无条件全量 DPAPI 重加密写盘

`engine/src/engine.rs:1571-1582`：注释自述"dirty 标志后续优化；每次周期保存"。实际是每 800ms 对**整库** `serde_json::to_vec` + `CryptProtectData` + 写临时文件 + `rename`，**无论是否有变更**。万条历史 + 大文本时是持续的 CPU/IO 与磁盘写入（`content.bin` 已是 DPAPI 加密的，主文件也再加密一遍）。建议补 dirty 标志（`Store` 内一个 `AtomicBool`，仅 `upsert/remove/pin/...` 置位）。

### G13 剪贴板引擎：消息循环线程内做最长 560ms 的阻塞探测

`engine/src/capture.rs:104-136` 的 `read_snapshot_progressive` 按 `[0,40,80,140,220,360,560]ms` 调度 `thread::sleep`，而它由 `WM_CLIPBOARDUPDATE` 在**窗口过程线程**里触发（`main.rs:206-213`）。Office/WPS 复制时消息循环被占住最长 560ms → 期间 `WM_HOTKEY`、`WM_ENDSESSION`（关机落盘）都排队。
**修法**：探测挪到工作线程，或用消息循环友好的等待（`MsgWaitForMultipleObjects`）。

### G14 转换引擎：多页 PDF → 图片的产物按字典序排序，≥10 页页序错乱

`native/convert-engine/src/exec.rs:145` `found.sort()`（字符串序）→ `doc-1, doc-10, doc-2, …`，且**单测把这个错序固化了**：

```202:206:better-desktop-cordis/native/convert-engine/src/exec.rs
        let names: Vec<String> = found
            .iter()
            .map(|p| p.file_name().unwrap().to_string_lossy().to_string())
            .collect();
        assert_eq!(names, vec!["doc-1.png", "doc-10.png", "doc-2.png"]);
```

**诚实标注**：这是从 C# 继承的缺陷，不是重写引入（C# `PopplerEngine.cs:110` 同样 `OrderBy(p => p, StringComparer.OrdinalIgnoreCase)`）。但既然本批就是"重写期集中体检"，建议一并改成自然序（按末尾数字排序），并把断言改成 `doc-1, doc-2, doc-10`。

### G15 转换引擎：PDF 加密只处理顶层对象，字典/数组内字符串未加密（与规范不符）

`native/convert-engine/src/pdf_security.rs:117-125` 的 `encrypt_object` 只匹配 `Object::String` 与 `Object::Stream`。而 PDF 规范要求**除 Encrypt 字典与 ID 之外的所有字符串**都加密（Info 元数据、注释 /Contents、链接 /URI 等都嵌在字典/数组里）。当前实现会把这些字符串**明文**留在"已加密"PDF 中，而任何按规范的阅读器（Adobe / pdf.js）会照 /Encrypt 声明去 RC4 解密它们 → **元数据乱码**。

自测之所以全绿，是因为 lopdf 0.34 的 `decrypt_object` 同样只支持顶层 `String`/`Stream`（`~/.cargo/registry/.../lopdf-0.34.0/src/encryption.rs:211-238`）—— **两侧对称所以往返通过，但不等于产出合规**。
**修法**：加密端递归遍历 `Dictionary` / `Array`（跳过 Encrypt 字典自身），或改为明确不支持 R=3 并回退到"用 C# 侧库加密"，别让菜单宣称的算法产出自测通过却对内不合规的文件。

（附：`pdf_security.rs:18-19` 注释「bit1/2 保留=1」与取值 `0xFFFF_FFFC`（bit1/2 = 0）不符，属注释错误；`"P" => PERMISSIONS as i64` 写出的是 `4294967292` 而非惯例的 `-4`，位等价但严格解析器可能挑剔。）

### G16 索引引擎：图标缓存记账含会杀掉连接线程的 `expect`

`engine-index/src/icon.rs:113` `self.order.back().expect("just pushed")`、`:130` `.expect("index from position()")`。这些在 `png_bytes` → IPC 连接线程内可达；一旦 panic 会 unwind 掉整个连接线程（`ipc.rs:71-114` 无 `catch_unwind`），4 个连接槽逐步耗尽 → 引擎"活着但谁也连不上"。
**修法**：记账直接用刚插入值的长度；连接处理加 `catch_unwind`。

### G17 日志无轮转、无上限，且与注释不符

`engine/src/log.rs:1-2` 声称「S5 升级为按日轮转 `engine-yyyyMMdd.log`」，实现却是**固定** `engine.log`（`:50-56`），且每行 `flush()`、无大小上限、无清理。长期运行会持续增长。索引引擎 `log.rs` 同类。

## 4. 优化建议

| # | 位置 | 建议 |
|---|---|---|
| O1 | 三工程 clippy 81 条 | 先跑 `cargo clippy --fix`（engine 45 / index 11 / convert 10 条机械可修），再人工处理 `div_ceil`、`collapsible_if`、无副作用位运算（`engine.rs:1474/1476` 的 `as u16 & 0xFFFF` 冗余掩码）。 |
| O2 | `engine.rs:1464-1479` | `uuid_v4` 实际是「时间纳秒 + PID」的确定性串，**不随机也不唯一防护**（同纳秒或时钟回拨会撞）；它同时用作文件名（撞则覆盖）。建议引入 `getrandom`（已在 lock 里）或加进程内单调计数器。 |
| O3 | `store.rs:745-750` | `old_enough` 闭包内每次重算 `now_iso_minus_days()` → 驱逐循环里 O(n²) 日期格式化；挪到闭包外算一次。 |
| O4 | `store.rs:1586-1589` | `unused_map_placeholder` 是死代码；`clear_all_files` / `push_entry` 标注"预留"未接线，建议要么接线要么删。 |
| O5 | `capture.rs:280-290` | `decode_text_bytes` 未在**首个 NUL** 处截断：`GlobalSize` 可能大于实际字符串（部分应用超额分配），尾部垃圾会经 `from_utf8_lossy` 混进 HTML/RTF。建议 `split(|&b| b == 0).next()` 后再解码。 |
| O6 | `capture.rs:840-857` | `set_html_entry` 对每个占位符索引 × 8 种扩展名做 `std::fs::read` 探活（最多 4096 次读磁盘）。建议改成 `read_dir` 一次枚举。 |
| O7 | `formats.rs:97` | 对**非 HGLOBAL** 的命名格式（如 `DataObject`、`Metafile`）也会 `GlobalLock`；虽多半返回 NULL 安全，但建议按格式类型跳过。 |
| O8 | `engine-index/src/engine.rs:145-146` | `cache_stats()` 被调用两次取锁两次，统计值可能来自两个瞬间；改一次性返回 `(usize, usize)`。 |
| O9 | `engine-index/src/ipc.rs:238-241` | 超长帧记 `Ok(None)`（= 对端正常关闭），把"恶意/异常帧"伪装成正常断开；应返回可诊断错误。 |
| O10 | `engine-index/src/main.rs:100/109-115` | 启动构建线程与 30s 补扫线程可能重叠写 `APPS`/`FILES`/`BUILDING`；用 `AtomicBool`/`Mutex` 串行化 `rebuild_indexes`。 |
| O11 | `convert-engine/src/run.rs:157` | `paths` 收集后从未使用（clippy 已报）；`Phase::Verifying` **永不构造**（进度阶段 `verifying` 永不发出）、`constants::SETTINGS_KEY_PREFIX` 与 `last_target_key` 未使用、`Error::None` 变体未构造 → 对照计划看"操作记忆 convert.last-target.<ext>"与阶段进度是否真的落地了。 |
| O12 | `convert-engine/src/service.rs:97-103` | `unique_target` 的 `for i in 2..` 无上限，理论上无限循环（可设 9999 上限）；多产物中途失败会留下"部分已发布"状态，建议先全部 rename 到临时名再逐个提交。 |
| O13 | `hotkey.rs:49` | `GlobalAddAtomW` 申请的原子从未 `GlobalDeleteAtom`（进程退出才回收）；注册/注销应配对。 |

## 5. 逐文件体检明细（节选）

| 文件 | 结论 | 要点 |
|---|---|---|
| `engine/src/capture.rs` | ⚠️ 严重 | C1（DROPFILES）；DIB 尺寸自洽校验、1502 alpha 归一化、`mask_shift` 除零防护都做得好；O5/O6 |
| `engine/src/store.rs` | ⚠️ 严重 | C2（upsert 不回收孤儿文件）；原子写/`storage_trusted` 拒写防线是亮点；O3/O4 |
| `engine/src/listener.rs` | ⚠️ 严重 | C3（seq TOCTOU）；echo 指纹抑制设计（替代计数令牌）与三态测试非常好 |
| `engine/src/engine.rs` | ⚠️ 一般 | G12/G13/O2；`catch_unwind` 无关的锁中毒统一处理到位 |
| `engine/src/ipc.rs` | ⚠️ 一般 | G2/G3/G4；首帧 5s 超时、日志分级、僵尸连接自愈（2026-09-12 修复）质量高 |
| `engine/src/main.rs` | ⚠️ 一般 | G1；单实例 Mutex/WM_ENDSESSION 落盘/热键配对释放齐全 |
| `engine/src/html.rs` | ⚠️ 一般 | G9；CF_HTML 头剥离有 8 个针对性单测（含幂等、SVG `</path>` 回归），质量高 |
| `engine/src/dpapi.rs` | ✅ 良好 | 加解密成对、`LocalFree` 正确、空输入契约与 C# 一致；仅 `cbData as u32` 无溢出防护（现实不可达） |
| `engine/src/fingerprint.rs` | ✅ 良好 | 像素级统一 + 解码失败回退字节哈希，测试覆盖"同像素不同压缩" |
| `engine/src/model.rs` / `formats.rs` | ✅ 良好 | 枚举数值/camelCase 与 C# 逐字段对齐有测试锁定；命名格式三重上限、超限跳过不截断 |
| `engine-index/src/*` | ⚠️ 严重 | C4；G5–G8/G16/O8–O10；FFI 结构体大小、HICON/DIB/DC 释放、错误码掩码、锁中毒、C# 契约（字段/枚举/错误码/管道名/magic）**全部核对一致** |
| `convert-engine/src/exec.rs` | ✅/⚠️ | 双管道线程防死锁、超时杀进程树、参数数组直传（不走 shell）都对；G14 排序 |
| `convert-engine/src/pdf_security.rs` | ⚠️ 一般 | G15；RC4/51 轮 MD5/算法 1·3·5 与 lopdf 逐字节对齐，自测往返有效 |
| `convert-engine/src/service.rs` | ⚠️ 一般 | G10/G11/O12；临时目录同卷 + 回读验证 + 原子 rename 的骨架是对的 |

**可读性评估**：注释密度与"为什么这么改"的记录远超一般工程（大量 2026-09-XX 真机结论 + 反向引用计划文档），可读性优秀；扣分项是**注释与实现漂移**（log 轮转、PERMISSIONS 位、max_entries 语义、`build_hdrop` 的"20+对齐 4"）——在这类"文档即契约"的项目里，漂移本身是一种缺陷。

## 6. 建议的后续动作（按优先级）

1. **立即修 C1**（用户可见的功能损坏，改动极小：补 4 字节 / 改两个偏移 + 一条往返单测）。
2. **C2/C3** 属数据与磁盘的持续劣化，建议同批修（`upsert` 返回是否命中新 id；`compare_exchange`）。
3. **C4** 需与 C# 侧确认降级消费逻辑后修（`degraded` 语义是跨进程契约）。
4. **G1–G4** 两个引擎同构缺陷，适合一次捞干净。
5. `cargo clippy --fix` 清机械告警，把 81 条降到人工可读的量级。
6. 修完请按本仓纪律补单测：`build_hdrop`→`parse_hdrop` 往返、`upsert` 命中不产生孤儿文件、`should_capture` 并发只放行一次、appindex 空结果必置 degraded。

> 依据 `AGENTS.md`「动工任何代码改动前必须走 `skills/cairo-pre-development-plan/SKILL.md`」，本报告只做审查与举证，**未改动生产代码**；上述修复建议应并入下一份计划文档的 §14 交接节再实施。

## 7. 修复记录（2026-09-14，用户拍板"直接修复"）

### 7.1 已修复

| 编号 | 缺陷 | 修法 | 落点 |
|---|---|---|---|
| **C1** | DROPFILES 头长度自相矛盾（已复现） | `build_hdrop` 的 `header_len` 24 → **20**（与 C# `HeaderSize = 20` 对齐）；`parse_hdrop` 的 `fWide` 偏移 20 → **16**；新增「构造 → 解析」往返单测锁死契约 | `engine/src/capture.rs` |
| **C2** | 去重命中时本次落盘文件永不回收 | `build_entry` 重构为「① 只填内容字段（零 IO）→ ② 指纹命中即直接返回、一个字节都不写 → ③ 才落盘原图/缩略图/副本/HTML 提取图/content.bin」；复用 `find_by_fingerprint`（与 `upsert` 同处 STORE 临界区，无竞态） | `engine/src/engine.rs`、`engine/src/store.rs` |
| **C3** | 序列号去重 load-then-store 非原子 | `should_capture` 改 **`compare_exchange` 循环**：只有把 `last_seq` 从旧值改成本次 seq 的线程放行 | `engine/src/listener.rs` |
| **C4** | 应用索引静默降级 | ① `appindex::build` 返回降级原因（无可用根目录 / 结果为空）；② 降级位**按索引拆成两路**（`APP_DEGRADED` / `FILE_DEGRADED`，`status` 取并集，`list_apps` 只报应用那路、`search_files` 只报文件那路）；③ C# 侧 `TryListAppsFromEngine` 补 `Degraded` 回退（与 `TrySearchFilesAsync` 同纪律） | `engine-index/src/{appindex,engine,main}.rs`、`packages/shell/shell-app-source/Services/AppSourceService.cs` |
| **G1** | `GetMessageW(-1)` 被当成"有消息" | 两个引擎的消息循环改**三态判断**（>0 处理 / 0 退出 / -1 记录并退出） | `engine/src/main.rs`、`engine-index/src/main.rs` |
| **G2** | `ERROR_MORE_DATA` 拼整块缓冲 | 两个引擎统一为 `chunk[..read]` | 两个 `src/ipc.rs` |
| **G6** | `shutdown` 恒返回 `ok:true` | `request_shutdown` 返回 `bool`，投递失败回 `-32603` | `engine-index/src/engine.rs` |
| **G9** | `svg+xml` / `x-icon` 被归一成 `png` → 粘贴坏图 | `normalize_ext` 先按 `+` 取主类型；新增 `mime_of_ext` 逆映射，还原时写回**原 MIME**（`svg→svg+xml`／`ico→x-icon`／`jpg→jpeg`） | `engine/src/html.rs` |
| **G10** | 发布守卫是死代码 + `exists→rename` 覆盖竞态 | 新 `reserve_target`：比较统一为「规范化父目录 + 文件名」（大小写不敏感）；用 `create_new` **原子占位**消 TOCTOU；与输入同名时让位到 `(2)` | `native/convert-engine/src/service.rs` |
| **G14** | 多页产物按字符串序 → ≥10 页页序错乱 | 新 `page_no` 取末尾数字做**自然序**（同号以路径兜底保证确定性）；断言由 `doc-1,10,2` 改为 `doc-1,2,10,11` | `native/convert-engine/src/exec.rs` |
| **G16** | 图标缓存 `expect` 可杀 IPC 连接线程 | `put` 记账改用本次插入值的长度（去掉 `order.back().expect`）；`touch` 去 `expect` | `engine-index/src/icon.rs` |

### 7.2 验证证据（可复跑）

| 项 | 结果 |
|---|---|
| `cargo test`（clipboard / index / convert） | **107 / 65 / 94 全绿**，含新增回归：`hdrop_build_parse_roundtrip`、`svg_and_ico_keep_their_mime`、`degraded_flag_round_trips_and_is_scoped_per_index`、`reserve_target_*`、`collect_prefixed_sorts_pages_naturally` |
| `cargo clippy --all-targets` 三工程告警数 | **51 / 11 / 19，与修复前完全一致**（无新增；期间出现的 1 条 `collapsible_if` 已就地消除） |
| C1 复现对照 | 修复前 `pFiles=24`、`roundtrip=["\\a.txt"]`（`C:` 被吃）；修复后被新单测锁死为 `pFiles=20`、往返一致 |
| `dotnet build packages/shell/shell-app-source` | **0 警告 0 错误** |

> 部署提示：Rust 引擎改动需 `cargo build --release` + `scripts/deploy-clipboard.ps1` / `deploy-index.ps1` 重新部署到 `%LOCALAPPDATA%\BetterDesktop` 才会生效（转换引擎随其分发链）。本次只完成**代码修复与验证**，未触发部署。

### 7.3 明确未修（需单独拍板，非本轮范围）

设计级 / 跨进程契约类，改一处要动协议或产品口径：

- **G3** 后续帧强制 magic（要确认所有客户端每帧都带，否则断连）
- **G4** 管道安全描述符（仅当前用户 SID）
- **G5** `get_icons` 同步慢处理撞客户端 3s 心跳（需重定跨进程超时契约）
- **G7** `to_string_lossy` 非 UTF-8 路径（要改 IPC 路径编码格式）
- **G11** release `panic = "abort"` 与 `TempDir` Drop 自清理冲突
- **G12** autosave 每 800ms 无条件全量 DPAPI 重加密（要补 dirty 位，且必须覆盖全部变更点）
- **G13** 消息循环内最长 560ms 的渐进探测（要挪工作线程）
- **G15** PDF 加密未覆盖字典/数组内字符串（与 lopdf 解密器的局限对称；改则自身往返测试失效，需在"规范合规"与"自测闭环"之间拍板）
- **G17** 日志无轮转/无上限（要定切分策略与保留期）
- **O1–O13** 优化项（含 `cargo clippy --fix` 机械清账、`uuid_v4` 随机性、`evict` 内 O(n²) 日期计算等）

另：**C4 的 C# 侧分支目前无单测覆盖** —— `IndexIpcClient` 是 `sealed` 类，要覆盖需为 `shell-app-source-tests` 搭一个 fake `IIndexTransport`（`shell-index-ipc-tests/FakeTransport.cs` 是现成范式）。Rust 侧契约已由 `degraded_flag_round_trips_and_is_scoped_per_index` 锁死。

### 7.4 追加：面板"引擎未连接"误报（用户实测，2026-09-14）

**用户报告**：主程序未启动、只用剪贴板历史面板时，**点暂停记录会显示"与引擎断开连接"**，但实际是显示错误（引擎连接正常）。

**根因（两处叠加，均为可证的代码事实）**：

1. `PanelMainWindow.LoadNextPage` 的 `catch (Exception)` **把所有分页异常都当成断线**：
   `ShowEngineBanner()` → Danger 横幅"引擎未连接，正在重试…" +「启动引擎」按钮。
   而 RPC 报错 / 单次超时 / 解析失败都走这里 —— 引擎明明活着也报断线。
2. 该横幅**只由 `Reconnected` 清除**（一次性闩锁）：误报后连接从未真正断开 → 永远等不到重连 →
   **横幅永久挂着**；而 `StatusLane.Engine` 是**最高优先级**，于是点暂停时状态槽只显示引擎卡片，
   "已暂停捕获（剩余 60s）"被折叠成 `+1 条提醒` —— 这正是用户看到的"暂停时显示与引擎断开连接"。
   （`StatusLane` 裁决逻辑见 `UpdateStatusSlot`：`active.Count > 1` 时只显示 `active[0]`。）
   另：`_engineDown` 还会连带抑制"暂无历史"空状态，误报期间列表该显示的空态也被吃掉。

**可能触发闩锁的真实原因**（供观察）：引擎侧 autosave 每 800ms 全量 DPAPI 重加密并持 STORE 锁
（本轮 §3-G12），`query` 排在锁后 → 大历史下可能超过客户端 3s 请求超时 → 报"ipc timeout" →
在"已连接"状态下误挂断线横幅。修复后这类超时只给瞬时提示，不再污染引擎状态。

**修复**：

| 文件 | 改动 |
|---|---|
| `shell-clipboard-ipc/ClipboardIpcClient.cs` | 新增只读 `IsConnected`（连接态真源，此前 `_connected` 为私有，调用方只能按异常文案猜） |
| `shell-clipboard-panel/PanelMainWindow.cs` | ① `catch` 分两类：`client.IsConnected` 为真 → `ShowToast("加载失败：…")`（瞬时、不占状态位、不谎报引擎）；为假才 `ShowEngineBanner()`。② **成功加载即收起残留横幅**（自愈，不再依赖 `Reconnected`）。③ 暂停/恢复失败补 `ShowToast`（该文件注释已声明"失败路径必须提示"，此前只落日志 = 点了没反应） |

**验证**：`dotnet build` 面板工程 **0 警告 0 错误**；`shell-clipboard-ipc-tests` **88/88 绿**
（新增 `IsConnected_TracksPipeLifecycle`：连接前 false → 连接后 true → 对端断开后回到 false）。

### 7.5 追加：全局热键语义对齐 legacy（方案 A）+ 部署记录

**问题（用户拍板选项 A）**：引擎与宿主内实现（legacy）对同样三个组合键绑了**不同动作**，而设置页与面板 tooltip
写的都是 legacy 那套 —— 后果是"照着提示按反而删数据"：

| 组合键 | legacy（`ClipboardManager.HotKeyId*`） | 引擎（修复前） | 引擎（修复后） |
|---|---|---|---|
| Ctrl+Shift+V | 打开面板 | 打开面板 | 打开面板 |
| Ctrl+Shift+P | 收藏视图 | ~~暂停/恢复~~ | **收藏视图** |
| Ctrl+Shift+Backspace | 暂停/恢复 | ~~**删除最近一条**~~ | **暂停/恢复** |

修复前：用户按屏幕提示的"Ctrl+Shift+Backspace 暂停/恢复捕获"会**静默删掉最近一条**（连级联文件一起删）；
而"收藏视图"入口在引擎里根本不存在（P 被暂停占着）。

**改动**：

| 文件 | 改动 |
|---|---|
| `engine/src/hotkey.rs` | `HotkeyAction`：`DeleteLast` → 移除；新增 `OpenFavorites`；`VK_BACK` 从 DeleteLast 改为 `TogglePause`、`VK_P` 改为 `OpenFavorites`；新增回归测试 `vk_bindings_match_legacy_semantics` 把映射钉死 |
| `engine/src/engine.rs` | `OpenFavorites` 分支 → `open_panel_with(true)`；`open_panel()` 拆出 `open_panel_with(favorites)`（追加 `--favorites` 参数）；移除已无人调用的 `delete_last()` |
| `packages/shell/shell-clipboard-panel/App.xaml.cs` | 新增 `OpenFavorites` 命名事件（与 `--open` **分开**——信号不带载荷，主实例只能靠事件名区分意图）；`--favorites` 解析；非首实例按参数置对应事件；首实例两个 watcher |
| `packages/shell/shell-clipboard-panel/PanelMainWindow.cs` | `ShowWithFilter(key)` 登记 + `ShowRightAligned()` 收尾应用（首显时 chip 才刚建出来）；`pinned` = 收藏筛选 |

> **为什么"删除最近一条"直接摘掉而不另配键**：legacy 本就没有该热键；破坏性动作占用易误触的组合键是这次踩的坑。
> 能力本身仍在（IPC `delete`），只是不再有全局热键入口。

**验证**：

| 项 | 结果 |
|---|---|
| `cargo test`（clipboard 引擎） | **108/108 绿**（含新增 `vk_bindings_match_legacy_semantics`） |
| `dotnet build` 面板 | **0 警告 0 错误** |
| **真机引擎日志**（新引擎启动） | `hotkey registered: Ctrl+Shift+V / Ctrl+Shift+P / Ctrl+Shift+Backspace` 三键全部注册成功 |
| **真机闭环（C2 修复同时被验证）** | 日志出现 `duplicate fingerprint; skipping external file writes` + `updated entry … (copy_count=2)` —— 重复内容不再落盘孤儿文件，正是 §7.1-C2 的修复路径 |

**部署记录（2026-09-14）**：

| 目标 | 方式 | 结果 |
|---|---|---|
| 剪贴板引擎 + 面板 | `scripts/deploy-clipboard.ps1 -StopEngine` | 已部署到 `%LOCALAPPDATA%\BetterDesktop`，引擎已重启（新 PID），面板已重新拉起 |
| 索引引擎 | `scripts/deploy-index.ps1` | 已部署并重启（新 PID） |
| 转换引擎 | `cargo build --release` + 拷入宿主输出目录 `host\bin\x64\Debug\net8.0-windows10.0.19041.0\convert-engine.exe` | 已替换（`RustConvertRunner` 的定位路径 = `AppContext.BaseDirectory`） |
| 宿主 / 面板（C#） | `dotnet build host\BetterDesktop.Host.csproj -p:Platform=x64` + 面板同参数 | 0 警告 0 错误，产物落在 `bin\x64\Debug`（用户实际运行的目录） |

> 环境坑（记录备查）：全量 `dotnet build BetterDesktop.slnx` 被一个**僵尸 `testhost.exe`（PID 17220）**锁住
> `shell-status-tests` 输出，`taskkill /F` 报 "no running instance"（进程已退出但句柄/快照残留）→ 本轮改为只构建
> 受影响的两个工程。另：`cmd /c "... & echo EXIT=%errorlevel%"` 的 `%errorlevel%` 在**解析期**展开，恒显示 0，
> 判断构建成败必须看日志而不是这个回显。

## 附录 A：skill 规则与 Rust 的映射

| skill 检查维度 | Rust 等价手段 | 本次结果 |
|---|---|---|
| 代码规范性 / 排版 | `cargo fmt --check`（本次未跑，仓库已 rustfmt 一致）+ clippy style | 81 条 style 级告警 |
| 潜在 Bug | clippy `correctness`/`suspicious` + 人工 FFI/契约核对 | clippy 0 条；人工发现 C1–C4 |
| 资源泄漏 | 句柄/内存/文件生命周期人工核对 | FFI 侧未发现泄漏；**发现孤儿文件泄漏（C2）** |
| 性能与可靠性 | 热路径/锁时序/磁盘 IO 人工分析 | G12/G13/O3/O6 等 |
| 注释与可读性 | 注释覆盖率 + 注释/实现一致性 | 密度优秀；发现 4 处漂移 |
| 华为 Java 规范评分 | 不适用（非 Java） | 改用第 1 节 Rust 五维评分：66/100 |

## 附录 B：审查 / 修复期间的临时产物（已全部删除）

- 审查轮：`Temp/clippy-*.txt`、`Temp/hdrop-audit.txt`；用于复现 C1 的临时测试已从 `engine/src/capture.rs` 回滚（复现结论已固化为正式单测 `hdrop_build_parse_roundtrip`）。
- 修复轮：`Temp/{engine,index,convert}-test.txt`、`Temp/*clippy*.txt`、`Temp/csharp-build.txt`（各轮构建/测试输出，结论已摘录进 §7.2）。
