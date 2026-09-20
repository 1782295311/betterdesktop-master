# better-desktop-cordis 未提交改动 · 代码审查报告

- 日期：2026-09-17
- 审查对象：`better-desktop-cordis` 工作区全部未提交改动（777 个文件，其中待审 546 个，约 +128,786 行）
- 审查方式：按功能域切成 19 组，每组由独立 AI 审查员通读该组 diff 并回读源文件交叉验证；标准为**通用软件工程最佳实践**（正确性 / 错误处理 / 资源管理 / 并发线程 / 安全 / 性能 / 可维护性 / 契约 / 测试），不依赖项目内部约定
- 已排除：`docs/**`、`LICENSE` 等纯文档；`scripts/manifests/dotnet-format-baseline.txt` 等数据基线

## 结果总览

| 严重度 | 数量 | 说明 |
|---|---|---|
| **critical** | **3** | 会导致功能整体失效、数据损坏或误删用户数据，建议立即修 |
| **high** | **25** | 特定条件下明显出错或长期运行必然劣化 |
| medium | 122 | 健壮性/可维护性缺陷，含 8 条标注「疑似」需人工确认 |

19 组明细：

| 组 | 文件数 | C | H | M | 组 | 文件数 | C | H | M |
|---|---|---|---|---|---|---|---|---|---|
| 剪贴板面板 | 29 | 0 | 1 | 5 | 剪贴板 IPC | 48 | 0 | 1 | 5 |
| 截屏捕获 | 55 | 0 | 1 | 9 | 右键菜单 | 56 | 0 | 0 | 8 |
| 热键侧板 | 28 | 0 | 2 | 7 | shell 核心/内核 | 65 | **1** | 3 | 8 |
| 动态岛 | 30 | 0 | 2 | 8 | 桌面 | 27 | 0 | 0 | 7 |
| 菜单栏/Dock | 57 | 0 | 0 | 3 | 格式转换 | 48 | **1** | 1 | 4 |
| 索引/应用来源 | 46 | 0 | 1 | 6 | Rust 剪贴板引擎 | 20 | 0 | 1 | 5 |
| 宿主/托盘/CLI | 44 | 0 | 3 | 3 | 状态栏/窗口追踪 | 43 | 0 | 1 | 10 |
| 搜索/通知等 | 26 | 0 | 2 | 4 | 更新器/看门狗 | 10 | 0 | 1 | 12 |
| 安装部署脚本 | 9 | **1** | 3 | 6 | 发布脚本 | 2 | 0 | 0 | 6 |
| 验证脚本 | 4 | 0 | 2 | 6 | | | | | |

---

# 一、Critical（3 条，建议立即修）

### C1. `packages/shell/shell-core/Hotkeys/HotkeyCenterWindow.cs:110-146` — 热键线程没有消息循环，所有系统热键静默失效

- **问题**：`ThreadMain` 创建 `HWND_MESSAGE` 窗口后，直接进入 `foreach (var command in _commands.GetConsumingEnumerable())`——这只是**消费命令队列**，全程没有 `GetMessage`/`TranslateMessage`/`DispatchMessage`。而 `RegisterHotKey` 触发时系统是**投递** `WM_HOTKEY` 到注册线程的消息队列，必须由该线程的消息循环取出并派发才会进入 `WndProc`（本文件已实现了处理 `WM_HOTKEY` 的 `WndProc`，但没有任何东西把消息交给它）。**已在源码确认**：110-146 行确无消息泵；第 175 行注释自称「非 GetMessage 泵」。
- **后果**：`DispatchSystemHotkey` 永不执行 → 所有 `HotkeySource.SystemHotkey` 类型的绑定「注册成功但按键无反应」。因为注册本身成功（无 0x581 错误），侧板会显示「可用」，用户按了却没反应，极难排查。
- **建议**：改成真正的消息循环；命令到达时用 `PostThreadMessage` 唤醒。可直接复用同仓已有的 `Native/MessagePump.cs`：
  ```csharp
  while (!_commands.IsAddingCompleted)
  {
      while (NativeMethods.PeekMessage(out var m, IntPtr.Zero, 0, 0, PM_REMOVE))
      {
          _ = NativeMethods.TranslateMessage(ref m);
          _ = NativeMethods.DispatchMessage(ref m);
      }
      if (_commands.TryTake(out var cmd, 50)) cmd();
  }
  ```

### C2. `native/convert-engine/src/pdf_ops.rs:30,33,34,36` — PDF 合并使用重编号前的页引用，产出损坏文件

- **问题**：`merge_pdfs` 在 `doc.renumber_objects_with(max_id + 1)` **之前**克隆了 `Kids` 数组；renumber 只重写 `doc.objects` 内部引用，改不到这份独立克隆。随后把旧 ID 的 Kids 克隆追加进主文档页树 → 指向的是第一份 PDF 的对象空间。同时 `extend` 之后未同步 `out.max_id = doc.max_id`，合并第 3 份起对象号重叠，`BTreeMap::extend` 会静默覆盖。
- **后果**：合并结果中后一份 PDF 的页面内容错误（显示成第一份的页）或结构非法；合并 3 份以上会丢对象。现有测试只断言页数 == 2，恰好被「重复页」满足，无法发现。
- **建议**：在 renumber **之后**用 `doc.get_pages()` 取新 ID 的页引用重建 Kids，并在每轮合并后 `out.max_id = doc.max_id`。

### C3. `scripts/uninstall-betterdesktop.ps1:262` — 对未校验的路径执行递归强制删除，可能误删用户数据

- **问题**：`$root` 来自 `Resolve-InstallRoot`（114-131 行），其值直接取脚本参数 `-InstallRoot`（28 行）或 `deployment.json` 的 `installRoot`（119 行），**没有任何「必须位于 %LOCALAPPDATA%\BetterDesktop 之下」的校验**就被用于 `Remove-Item $root -Recurse -Force`（262 行）。**已在源码确认** 114-131 行无校验。
- **后果**：`-InstallRoot C:\`、或损坏/被篡改的 `deployment.json`（如 `installRoot = "C:\Users\17822"`）会让卸载器递归删除整棵用户目录，且 `-PurgeUserData` 会在同一次运行里继续清理，不可恢复。
- **建议**：删除前强制校验（同问题也存在于 `install-betterdesktop.ps1:353`，见 H18）：
  ```powershell
  $allowed = (Join-Path $env:LOCALAPPDATA $product).TrimEnd('\') + '\'
  $full = [IO.Path]::GetFullPath($root)
  if (-not $full.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) {
      Fail "拒绝删除产品目录之外的路径：$full"
  }
  ```

---

# 二、High（25 条）

## 热键 / 输入（4 条）

- **`shell-core/Hotkeys/HotkeyCenterWindow.cs:86-108`** [high] `RunOnThread` 在泵线程上被回调自己调用时**自等死锁**：`Register/Unregister` 把命令入队后同步 `tcs.Task.GetAwaiter().GetResult()`，而热键触发回调恰恰跑在泵线程上，只要回调里再注册/注销热键（例如「按一次即注销自己」）就永久卡死，随后 `_gate` 被持有导致宿主 UI 整体挂起。
  > 建议：`RunOnThread` 里判断「当前即泵线程」则直接执行；并给 `GetResult()` 加超时兜底。
- **`shell-core/Hotkeys/HotkeyCenterWindow.cs:50,122-125,144`** [high] 窗口创建失败被静默吞掉，且类名仅含 PID、从不 `UnregisterClass` → 同进程第二次创建**必然**失败，却把每个热键标成「被其他程序占用」，把用户引向错误排查方向。
  > 建议：失败时置 `_failed` 并记录 `Marshal.GetLastWin32Error()`；类名加 `Guid` 或 `Dispose` 时 `UnregisterClass`。
- **`shell-hotkey-panel/HotkeyPanelWindow.cs:182`** [high] 进入「已忽略管理」态后点击穿透不再恢复：`PollGate` 只在 `Mode==Interactive` 分支调 `SetPassThrough`，`Manage` 态永不恢复且无空闲超时 → 面板整块矩形（宽 340、可撑到屏高 85%）长期静默吞掉下方点击。
  > 建议：把穿透维护移到与模式无关的位置，`Manage` 态同样按「鼠标在卡片上才不穿透」判定，并补退出路径。
- **`shell-hotkey-panel/HotkeyDeclarations.cs:297`** [high] `start-menu.win-key` 声明的键位 `"Win"` 无法通过 `HotkeySpec.TryParse`（必须「至少一个修饰键 + 一个主键」）→ `Declare` 静默返回失败且不打日志，「打开开始菜单」这条永远进不了注册表/侧板。现有单测用 `Where(c => c.Length > 0)` 恰好把解析失败项滤掉，掩盖了问题。
  > 建议：改键位或显式跳过并日志，并补一条「Build 的每一项都必须能 TryParse 成功」的断言。

## 剪贴板（4 条）

- **`shell-clipboard/Sections/ClipboardSection.cs:939-946`** [high] 滑条输入框提交时对 `displayDivisor` 重复折算：`slider.Value`（显示单位）与 `onChange(actual)`（像素单位再乘 100 万）叠加，导致「图像素上限」被写成溢出值（可能为负），设置彻底失效；失焦路径还会把输入框覆写成 0。
  > 建议：Commit 内统一用显示单位，不要乘 `displayDivisor`。
- **`shell-clipboard-ipc/ClipboardIpcClient.cs:213`** [high]（疑似）成功连接/重连时未重置 `_lastInbound`：若上次断开超过心跳超时（3s）才重连成功，`WorkerLoop` 会用旧时间戳立即判定「心跳超时」而再次断开 → 引擎重启后客户端可能「连上即断」死循环，长期显示「引擎未连接」。
  > 建议：连接成功后补 `_lastInbound = DateTime.UtcNow;`。
- **`shell-capture/Core/CaptureFlow.cs:100,135,268`** [high] 选区定稿与写剪贴板全程在 **UI 线程**：读整张全屏 PNG（`File.ReadAllBytes`）+ `PngCodec.CropPng` 全图解码重编码 + 剪贴板写入，重试路径还有 `Thread.Sleep(150)`（最坏 300ms）→ 4K 全屏时卡顿数百毫秒。
  > 建议：裁剪与写入移到后台线程，完成后 `Dispatcher` 回主线程更新；`Thread.Sleep` 换 `await Task.Delay`。
- **`engine/src/settings.rs:88`** [high]（Rust）规则缺字段时静默变成「忽略一切」：`AppRuleAction` 的 `#[default]` 是 `Ignore` 且 `AppRule` 带 `#[serde(default)]`，`app=""` 又能匹配任何应用 → 用户在 settings.json 里漏写一个 `action` 就会让剪贴板**不再记录任何内容**，只留一行 info 日志；写错枚举值更会让整节设置回默认。
  > 建议：把「动作缺失」当非法（去掉 Default 或校验 `app` 为空），`action` 加 `#[serde(other)]` 落到 NoOp 避免拖垮整节。

## 桌面 / 动态岛（4 条）

- **`shell-island/Windows/IslandWindow.cs:454`** [high] 岛窗口关闭时未释放基类 `ShellWindow` 的外观订阅（`_appearanceSub` 只在 `DetachWindow()` 里 Dispose，基类 `OnClosed` 不自动调用），而岛窗口是运行期反复创建/销毁的 → 内核事件总线强引用已关闭窗口，窗口 + 可视树 + 订阅永久回收不掉，反复开关灵动岛内存持续增长。
  > 建议：`OnClosed` 里调用 `DetachWindow()`。
- **`shell-island/IslandController.cs:141`** [high] 同 Id 更新只按 `Progress`/`MergeCount` 去重，不比较 `Title/Body/Actions/Failed` → 无时间线的媒体源切歌/暂停时，岛上的标题与「暂停/播放」按钮**永不刷新**，卡片长期显示过期信息。
  > 建议：去重键扩为内容签名（含 Title/Body/Actions.Count/Failed），并补「同 Id 更新」单测。
- **`shell-hotkey-panel` 相关见上**；**`shell-desktop/...`** 见 medium。
- **`shell-desktop/Controls/DesktopIconsControl.cs`** 见 medium（框选看门狗泄漏）。

## 桌面与宿主进程（6 条）

- **`tray/ProcessBridge.cs:193,216,227`** [high] 「停止桌面服务」写的是 `desktop-service-stopped.flag`，而所有读取方（`tray/TrayApplicationContext.cs:829`、`agent/Capabilities/DesktopServiceSupervisor.cs:36`、`watchdog/Program.cs:316`）查的是 `desktop-stopped.flag` → 用户的「停止」被无视，服务立刻被自动拉回。
  > 建议：统一字面量或提取公共常量。
- **`BetterDesktop.Cli/HeadlessExecutor.cs:484-490`** [high] 拉起 `BetterDesktop.DesktopControl.exe` 时漏传 `--desktop-controls` → 该进程走「默认常驻服务」分支而非弹菜单，用户点「桌面控制」**看不到任何菜单**，还附带起了个后台服务。
  > 建议：补 `psi.ArgumentList.Add("--desktop-controls");`
- **`BetterDesktop.Cli/HeadlessExecutor.cs:356,446`** [high] CLI 直接对 settings.json 做「读整份 → 改一键 → 整份覆盖」，既不原子也不参与跨进程互斥（正规写者 `SettingsService` 有 mutex + 临时文件原子替换，`host/SettingsFileWriter.cs` 为此专门做过修复）→ 与宿主/托盘并发写时用户其余设置**永久丢失**。
  > 建议：复用 `SettingsService` 或至少对齐「互斥 + 临时文件 + 键数只增不减」。
- **`updater/UpdateSource.cs:163-165`** [high] 允许 `http://` 明文更新源，且 manifest 无签名/来源校验（SHA256 只证明「文件与 manifest 一致」）→ 中间人可篡改 manifest 实现任意文件写入，自更新场景下等于远程代码执行。
  > 建议：默认拒绝 http，或对 manifest 做签名校验。
- **`scripts/deploy-index.ps1:66`** [high] 停掉索引引擎后拷贝失败直接 `exit 1`，**不重启引擎**（对照 `deploy-clipboard.ps1` 明确写了「停过就必须拉起」）→ 一次失败的部署把运行中的服务也停了。
- **`scripts/install-betterdesktop.ps1:353` / `:189`** [high] ① 递归删除「上一次安装根」时路径来自 `deployment.json`，无范围校验；② `$fileVersion`（来自 exe 元数据）未做格式校验就 `Join-Path` 并用于 `Remove-Item -Recurse -Force` → 构造的 FileVersion（含 `..\`）可递归删除任意目录。

## 搜索 / 转换 / 索引 / 更新（4 条）

- **`shell-search/Services/FileSearchProvider.cs:100`** [high] 引擎 IPC 用 `GetAwaiter().GetResult()` 同步等待，而整条搜索链路实际跑在 **UI 线程** → 引擎慢/卡时开始菜单搜索框冻结最长 5 秒（比改动前的 1.5s 上限更差）。
  > 建议：移到后台或真正异步化。
- **`shell-search/Services/StartMenuSearchService.cs:72-78`** [high] 改成「App 固定在最前 + 类别分组」后，下游 `StartMenuService.SearchAsync` 仍做 `.Take(limit)`（默认 20）→ 单字符查询时匹配到几十个 App，文件/设置类结果被整体静默截掉（相对改动前按分数混排是**功能退化**）。
  > 建议：按类别做配额/交错截断。
- **`native/convert-engine/src/managed.rs:149,695`** [high]（Rust）用**字符下标当字节下标**切字符串（`chars` 收集后又 `s[i..]`）→ 含中文的 HTML 遇到 `<script>`/`<style>` 时 `byte index N is not a char boundary` panic，引擎进程崩溃，用户只看到「引擎异常退出」，同批其它文件结果一起丢失。
  > 建议：在 `chars` 空间内查找，或先把字符下标换算成字节偏移。
- **`engine-index/src/ipc.rs:84`** [high]（Rust，疑似）管道为**消息模式**但出站缓冲只有 64KB，而 `get_icons`（最多 64 张 256×256 PNG 的 base64）与 `search_files`（最多 1000 条）响应轻易超过 64KB → 写入失败、连接被判断开，「批量取图标/大结果检索」在真实负载下必然失败。
  > 建议：把 `nOutBufferSize` 提到 4–8MB，或对这两个响应做分片/收敛。

## 状态栏（1 条）

- **`shell-status/Native/MediaPlayerCore.cs:233-239`** [high]（疑似）`using (var writer = new DataWriter(stream))` 释放时会关闭底层流，之后又对同一 `stream` 调 `Seek(0)` 与 `CreateFromStream` → 抛 `RO_E_CLOSED`，被上层 catch 后返回 null，表现为「专辑封面永远显示占位、界面不报错」。
  > 建议：写完先 `writer.DetachStream()` 再结束 using。

## 门禁脚本（2 条）

- **`scripts/verify-dotnet-format.ps1:58-69`** [high] 门禁 **fail-open**：`dotnet format` 退出码非 0 但正则没解析到违规（SDK 版本过低、未还原、不在 PATH、诊断级别非 error）时，直接打印 PASS 并 `exit 0`；同时删掉了原来的原始输出尾巴，失败时无法定位。
  > 建议：解析结果为空时必须报红，并把工具输出尾部带进失败信息。
- **`scripts/verify-dotnet-format.ps1:57-59`** [high] 棘轮基线以「相对路径:行号」为 key，而基线是整文件逐行记录 → **在任意被覆盖的文件里插一行，其后所有行号偏移就全部变成「新增违规」**，门禁在无关改动上变红；反之同一行改写成另一种违规永远测不出。开发者唯一解法是重生成基线（等于清零检测力）。
  > 建议：key 换成 `路径 + 诊断代码` 集合，或先根除系统性违规（LF 化 + `.gitattributes`）。

---

# 三、Medium（122 条，按域分组）

## 剪贴板面板（5）

- `shell-clipboard-panel/PanelMainWindow.cs:1085-1088` [medium] 重复且不可达的 `case Key.Enter when entry is not null:` 分支（Silent dead code）→ 删除。
- `shell-clipboard-panel/PanelMainWindow.cs:316-334`（+ `App.xaml.cs:40`）[medium] 构造期订阅共享 `_client` 事件但 `OnClosed` 刻意不退订，而窗口**可被重建**（`EnsureMainWindow`）→ 旧窗口无法 GC，且每次事件会同时触发新旧实例（重复 Reload）。
- `shell-clipboard-panel/App.xaml.cs:22-34` [medium] `ShowMainWindow()` 先 `HidePanel()` 又无条件 `ShowRightAligned()`，隐藏分支被立即抵消 → 「切换隐藏」入口失效，只会闪一下。
- `shell-clipboard-panel/build_err.txt:1-207` [medium] 误提交的构建日志（97 条编译错误 + 本机绝对路径）→ 删除并加 `.gitignore`。
- `shell-clipboard/ClipboardPlugin.cs:229` [medium]（疑似）把 `ISettingsService` 强转具体类型 `SettingsService` 调 `FlushNow()`，类型不符时静默失效（会复现注释里那个 bug 且无日志）→ 契约上暴露 Flush 或至少记 Warn。

## 剪贴板 IPC（5）

- `shell-clipboard-ipc/ClipboardIpcClient.cs:406,386,390` [medium] 订阅者异常直接逃逸到工作线程 `catch` → 被误判「管道断开」触发断线重连；订阅者持续抛异常会形成重连风暴。
- `shell-clipboard-ipc/ClipboardIpcClient.cs:1717` [medium] `SetClipboardText` 未校验 `GlobalAlloc`/`GlobalLock` 返回值即 `Marshal.Copy` → 失败时空指针写入（`AccessViolationException`），异常路径还泄漏全局内存块。
- `shell-clipboard-ipc/ClipboardIpcClient.cs:1051` [medium] 按格粘贴的空单元格永远写不进剪贴板（`allowEmpty` 被 `BuildSnapshotBlocks` 空块短路）→ 每次都弹「第 N 格无法写入」假失败提示。
- `shell-clipboard-ipc/ClipboardIpcClient.cs:943` [medium] `MaxCells` 上限用**列表摘要** content 校验，实际拆分用引擎**全文** → 表格较大时上限被绕过（注释说摘要截断到 1024 字符）。
- `shell-clipboard-ipc/ClipboardTransport.cs:62` [medium]（疑似）`Connect()` 抛异常时局部 `stream` 未释放，重试路径持续泄漏管道客户端。

## 截屏捕获（9）

- `shell-capture/Native/MonitorApi.cs:230` [medium]（疑似）`QueryDisplayConfig` 把 `pathCount` 同时当 `numModeInfoArrayElements` 且 `modeInfoArray=Zero` → 返回 `ERROR_INVALID_PARAMETER` 被吞掉，HDR 检测恒 false，HDR 相关分支成死代码。
- `shell-capture/HotKeyManager.cs:120` [medium] 配置解析失败的 `catch` 回退到**已知与系统冲突**的 Win+Shift+S（正常默认是 Win+Shift+B）→ 配置损坏时用户从此没有截图热键。
- `shell-capture/Core/ScreenCaptureService.cs:331,334` [medium]（疑似）色调映射忽略帧 stride（`stride = AlignUp(width*4,16)`），宽度非 16 倍数时行尾填充被当像素 → HDR 输出图整体错位。
- `shell-capture/Core/WgcCapture.cs:140` / `DxgiCapture.cs:116` [medium] 合成抛异常时 `target` 帧（可达数十 MB 非托管内存）未释放，`catch` 只释放了 `parts` → 每次异常泄漏一整帧。
- `shell-capture/UI/EditorWindow.cs:747` [medium] 模糊/马赛克拖拽时每次 `MouseMove` 分配整块缓冲（大图可达数十 MB，进 LOH）并全图刷新 → 卡顿与 GC 抖动。
- `shell-capture/UI/EditorWindow.cs:38,584` [medium] 撤销栈无上限，马赛克/模糊每次操作压入两张全尺寸快照（4K 每次≈66MB）→ 反复操作内存线性增长直至 OOM。
- `shell-capture/UI/EditorWindow.cs:84` [medium] 构造内解码失败即回调 `_onFinished` 并 `Close()`，调用方随后仍 `Show()` → `InvalidOperationException` + 二次回调 + 用户收到矛盾提示。
- `shell-capture-tests/WindowsMediaOcrIntegrationTests.cs:27` [medium] 用 `Assert.True(true, "跳过")` 假装 skip → 缺语言包的 CI 上该测试永远「绿」但从未运行。
- `shell-capture/build_out.txt` [medium] 误提交的构建日志（41 条编译错误 + 本机路径）→ 删除。

## 右键菜单（8）

- `shell-context-menu/Services/ContextMenuRegistry.cs:91` [medium] `IsRegistered` 只查 `*\shell\<id>`，而 `Register` 按场景写 1~3 个路径 → `Directory`/`Background` 场景的项**永远显示「未注册」**，用户切不动。
- `shell-context-menu/Sections/SystemIntegrationSection.cs:170` [medium] `RestartExplorerAsync` 中 `GetProcessesByName`（循环最多 30 次）与 `Process.Start` 返回值均未 `Dispose` → 句柄泄漏。
- `shell-context-menu/Sections/SystemIntegrationSection.cs:92` [medium] `RefreshStatus` 在 UI 线程同步执行 `GetStatus()`（WinRT `FindPackagesForUser` + 多次注册表/文件探测）→ 设置页卡顿。
- `shell-context-menu/native/src/Launcher.cpp:142` [medium] `AppendJsonString` 逐 `wchar_t` 转 UTF-8 → 代理对（emoji / CJK 扩展 B 区汉字）被写成两个 U+FFFD，**含非 BMP 字符的文件路径会被写坏**，CLI 拿到损坏路径。
- `shell-context-menu/native/src/ShellBroker.cpp:417` [medium] 把 UTF-16 指针塞进 ANSI 槽 `lpVerb`（同时设了 `CMIC_MASK_UNICODE`）→ 不遵循 fMask 的第三方 handler 读到乱码 verb，调用错误动作。
- `shell-context-menu/native/src/ExplorerCommand.cpp:449` [medium]（疑似）`GetFlags` 直接读 `_spec` 而不自行 `Resolve`，依赖其它方法先被调用 → 若外壳先调 `GetFlags`，顶级项被当叶子项（不显示子菜单箭头，点了没反应）。
- `shell-context-menu/native/src/MenuModel.cpp:182` [medium] `highlight`（无损转换项高亮）被解析并序列化，但原生侧**从不渲染** → 承诺的加粗效果永不出现，字段是死数据。
- `shell-context-menu/native/BetterDesktopShellMenu.dll` + `BetterDesktopMenuBroker.exe` [medium] 编译产物二进制提交进源码树，与 `native/src` 可静默漂移（改了 C++ 不重编也不报错）。

## 热键侧板（7）

- `shell-hotkey-panel/Sections/HotkeySettingsSection.cs:1142` [medium] `RebindOrDeclare` 名不副实：条目不存在时只 `Rebind` 不 `Declare` → 首次录制「粘贴回原窗口」不写注册表，侧板不显示该行，需重启才出现。
- `shell-hotkey-panel/HotkeyDeclarations.cs:430` [medium] `IsProcessRunning` 未 Dispose `Process` 对象，且被 500ms 刷新在 **UI 线程**反复调用（枚举全系统进程）→ 句柄泄漏 + 周期性 UI 抖动。
- `shell-hotkey-panel/HotkeyPanelWindow.cs:659` [medium] `HidePanel` 停了两个定时器但没停 `_sceneWatcher` 的 500ms 前台轮询 → 隐藏期间仍持续回调。
- `shell-hotkey-panel/HotkeyPanelWindow.cs:1111` [medium]（疑似）窗口 `OnClosed` 不注销已声明条目，与插件注释矛盾 → 重建后这些 Id 永远进不了 `_declared`，键位同步分支成死代码。
- `shell-hotkey-panel/HotkeyPanelPlugin.cs:68` [medium] 卸载未重置静态桥 `SurfaceScopeBridge`（无 `Attach(null)`）→ 静态字段强引用 tracker 与注册表服务，插件无法释放；后续窗口 Report 打到已卸载插件。
- `shell-hotkey-panel/HotkeyPanelWindow.cs:681` [medium]（疑似）设置服务缺失时 `ClosePanel` 会把面板永久关掉且无恢复入口（所有写入路径都依赖 settings）。
- `shell-hotkey-panel-tests/HotkeyScanAndCaptureTests.cs:147,160` [medium] 依赖真实 Windows 会话、瞬时注册上千次全局热键、绕过生产侧互斥 → 与其他用例并行时互相把对方的试注册当成「被占用」，结果不稳定。

## shell 核心 / 内核（8）

- `shell-core/Activity/ActivityService.cs:97` [medium] `PriorityOrOrderChanged` 在该分支恒为 false（`TryFind` 返回 false 意味着 `Id != _current.Id`，而条件要求 `Id == _current.Id`）→ 队列中同 Id 条目的优先级变更永不通知订阅方，灵动岛停在旧状态。
- `shell-core/Native/NativeMethods.cs:586` [medium] `SendInput` 返回值被丢弃（返回 0 表示一个事件都没注入）→ 「按序粘贴」偶发丢粘时没有任何可判定依据。
- `kernel/Deployment/AutostartRegistrar.cs:85`（`MarkApproved` 163-169）[medium] `MarkApproved` 写失败被吞、`Set` 仍返回 true → 「装好了但开机不启动」这一文件头点名的场景下，调用方/UI 显示已开启。
- `shell-core/Hotkeys/HotkeysPlugin.cs:31` [medium] 设置服务缺失时静默降级为 `MemorySettingsService`，无任何日志 → 用户改键/隐藏/停用全部不落盘，重启即丢且无迹可查。
- `shell-core/Hotkeys/HotkeyCenterWindow.cs:177-186` [medium] `Join(2s)` 超时后仍 `Dispose` 命令集合与事件 → 仍在运行的泵线程抛 `ObjectDisposedException`，线程未捕获异常会**终止进程**（插件卸载偶发崩溃）。
- `shell-core-tests/Hotkeys/HotkeyRegistryServiceTests.cs:408,422` [medium] 断言用错键码（绑定是 F10=0x79，测试传 0x56）→ 「停用后不消费」「作用域不活跃不消费」两条红线**实际未被测试**；另有注释与代码不符、重复用例。
- `shell-core/BetterDesktop.Shell.Core.csproj:13,15` [medium] 同一 `ProjectReference` 与注释各重复一遍（复制粘贴残留）。
- `kernel-tests/AutostartRegistrarTests.cs:24-30,54-60` [medium] 单测直接读写用户**真实 HKCU** 的 Run / StartupApproved 键 → 进程中断会留下垃圾自启项，受限 CI 上整组失败。

## 动态岛（7）

- `shell-island/Sources/MediaActivitySource.cs:60` [medium] `Stop()` 与在飞 `RefreshAsync` 竞态 → 关掉「媒体播放控制」后媒体卡会重新出现且是 Sticky（不受 TTL 约束），长期挂在岛上。
- `shell-island/Rendering/IslandSurfaceElement.cs:156` [medium]（疑似）字形两层变换压栈顺序疑似反了（先 scale 后 translate）→ 高度 < 26 的动画期字形被缩放偏移，可能飞出胶囊轮廓。
- `shell-island/Rendering/PulseRing.cs:131` [medium]（疑似）进度到 100% 时弧段起止点重合、WPF 不渲染 → 显示「100%」却是空环。
- `shell-island/Rendering/IslandContent.cs:62` [medium] 动作回调 `_ = invoke();` 丢弃 Task → 点「暂停/下一首」失败时界面无反馈、日志无记录。
- `shell-island/Windows/IslandWindow.cs:336` [medium] 内容换成「无细节」活动时未复位展开态 → 卡片保持展开成一个内部空白的加高胶囊（244×34）。
- `shell-island-tests/Rendering/IslandIdlePoseTests.cs:19` [medium] 测试里休眠尺寸常量 76×20 与实现的 64×8 不一致，注释却写「必须同步」→ 该不变量实际没被锁住。
- `shell-island/Services/SuppressionWatcher.cs:23` [medium]（疑似）UI 线程 1s 定时器同步执行 `SHQueryUserNotificationState`（需枚举顶层窗口）→ 慢机/远程桌面下周期性 UI 卡顿。
- `shell-island/Windows/IslandWindow.cs:550` [medium] 百分比文本未 clamp，而环做了 `Math.Clamp` → 越界 Progress（契约 0..1）会显示「500%」而环是满圈。

## 桌面（7）

- `shell-desktop/Controls/DesktopIconsControl.cs:2499` [medium] 框选看门狗的 `Tick` 处理器被反复 `+=`（`??=` 只保证 timer 创建一次），且 `Dispose()` 不停表 → 委托链随框选次数无上限增长，被 Dispose 的控件无法 GC，回调还会摸已脱离树的老 canvas。
- `shell-desktop/Services/DesktopBrowser.cs:359` [medium] 枚举完成时用「枚举开始时的选中快照」覆盖选中集 → 用户在枚举期间新做的选择被静默回滚（桌面每秒约触发一次 Refresh，命中概率不低）。
- `shell-desktop/Services/DesktopBrowser.cs:234` [medium] `_busy` 只防重入，枚举期间的选中变更无版本校验（与上条同源）。
- `shell-desktop-control/DesktopControlEntry.cs:564` [medium] `ServiceRuntime.Dispose` 丢弃 `DisposeAsync()` 的 Task，并在其完成前就释放 `SettingsService` → 桌面插件卸载（恢复原生图标）可能来不及跑完，退出后原生图标停在隐藏态。
- `shell-desktop/Services/DesktopControlMenu.cs:159` [medium]（疑似）「隐藏任务栏」的勾选态传的是「任务栏当前**可见**」，与标签与自身注释矛盾 → 状态误读。
- `shell-desktop/Controls/DesktopIconsControl.cs:1812` [medium]（疑似）在 `Task.Run`（线程池 MTA）里调 `SHFileOperation` → 公寓模型敏感 API，可能静默失败或行为异常。
- `shell-desktop/DesktopPlugin.cs:1148` [medium] 日志语句整段重复（一次双击写两遍）。

## 菜单栏 / Dock（3）

- `shell-dock/Services/DockAppsServiceBridge.cs:21` [medium] `Unbind()` 定义了却**无任何调用点**（`UnloadAsync` 也没反注册 DockPinnedSection）→ 插件卸载后静态引用仍指向旧服务，设置中心可拿到已下线门面。
- `shell-dock/Services/DockAppsService.cs:450`（+ `Sections/DockPinnedSection.cs:64`）[medium]（疑似）读取固定项健康态时在 **UI 线程**同步做注册表多视图扫描 + Store 应用全量枚举 → 存在失效固定项时打开设置页卡顿。
- `shell-dock/DockWindow.DesktopLayer.cs:119` [medium]（疑似）桌面层看门狗用静态 `DispatcherTimer`，从不停用/不随窗口重建 → 进程内持续 Tick 并强引用首个 dock 窗口，新窗口拿不到看门狗（该功能受环境变量门控，默认关闭）。

## 格式转换（4）

- `shell-convert/Services/RustConvertRunner.cs:185` [medium] `ElapsedMs` 取 TimeSpan 的**毫秒分量**而非 `TotalMilliseconds` → 耗时统计恒 <1000ms（把 30s 显示成 <1s）。
- `native/convert-engine/src/engines_impl.rs:752` / `raw_engine.rs:69` / `pdf_text.rs:361` [medium] `&text[..200]` 按字节截断，中文错误信息落在非字符边界时 panic → 失败路径反而崩引擎，真实错误被掩盖。
- `native/convert-engine/src/service.rs:78,157,161` [medium] 输出路径带 Windows `\\?\` 前缀并透传到 UI → 用户看到怪异路径，「打开/定位」类 API 可能不识别。
- `shell-convert/Services/ConversionService.cs:52,62,68` [medium] 多输入/拆分/合成分支调用 `RunRustAsync` 无异常兜底（单文件分支有），异常会逃逸 `ConvertAsync`。

## 索引 / 应用来源（6）

- `engine-index/src/appindex.rs:128,156` [medium] `scan_roots` 被同时当作「开始菜单根」与「Program Files 根」（该字段文档是「文件索引根覆盖」）→ 下发后同一批目录遍历两次，候选全被标成 `start-menu` 来源。
- `engine-index/src/ipc.rs:162` [medium] 心跳 ping 也被算作「活动」→ 只要客户端保持连接，`IDLE_EXIT_SECS=600` 永不达到，「无人检索就退场」完全失效。
- `engine-index/src/engine.rs:424` [medium] `get_icons(refresh=true)` 清空**整个**图标缓存（上界 32MB）→ 刷新一个键要全量重提取。
- `engine-index/src/fileindex.rs:126` [medium] `search` 全程持 `FILES` 互斥锁并在锁内做字符串分配 → 查询与重建互相串行化（应改 RwLock 或缩短临界区）。
- `shell-app-source/Services/AppSourceService.cs:1005` [medium] 新增的 `ScanDesktopShortcuts` 用空 `catch { }` 吞掉全部异常且无日志（同批 `ScanExtraRoots` 有 Warn）→ 桌面来源静默失效。
- `shell-index-ipc/IndexEngineLauncher.cs:127` [medium]（疑似）启动器用正则匹配**扁平键**，引擎 `settings.rs` 读**嵌套节** → 键形状不一致，可能出现「关了还拉起」或「设置全回默认」。

## Rust 剪贴板引擎（5）

- `engine/src/store.rs:832,814` [medium] `backfill_html_text` / `strip_cf_html_headers` 改写了参与 fingerprint 的字段却不重建索引 → 当前会话索引与实际内容错配，落盘后下次启动判不出重复，且近似文本会无条件覆盖真实剪贴板纯文本。
- `engine/src/engine.rs:178`（经 `capture.rs:104`）[medium] 渐进探测在**窗口消息循环线程**上 `thread::sleep` 累计最多 560ms → `WM_HOTKEY` 与 `WM_ENDSESSION`（关机落盘）被推迟，Office 每次复制都触发。
- `engine/src/engine.rs:1413` [medium] `entry_summary_json` 先整条 `to_value`（含 MB 级大字段）再清空 → 剥离优化失效，列表页内存峰值。
- `engine/src/engine.rs:744` [medium] 关键词搜索对全库每条的大字段做 `to_lowercase()` 分配 → 每敲一个字都要按「全库内容总量」分配，上万条时明显卡顿。
- `engine/src/ipc.rs:26` [medium] 命名管道未设 `lpSecurityAttributes`，首帧仅校验静态 magic → 同一用户下任意本地进程都能读取明文剪贴板历史。

## 宿主 / 托盘 / CLI（3）

- `agent/Capabilities/ExclusiveCapabilityHost.cs:181-187` [medium] `Dispose()` 在调用线程直接 `Transition(...)`，与类头声明的「状态过渡固定在 Dispatcher 线程」矛盾（Agent `--stop` 在线程池执行）→ 任务栏外观的装载/卸载落到线程池线程。
- `host/MenuCommandPipe.cs:59-67` [medium] 读超时后遗弃 `ReadLineAsync()` 的 `readTask`（未 await 即 `continue` 触发流释放）→ 未观察的 Task 异常 + 读写竞争。
- `tray/TrayApplicationContext.cs:215` [medium] 菜单文案「打开设置中心（将启动主程序）」与实现（优先独立进程）不符。

## 更新器 / 看门狗（12）

- `updater/Program.cs:131`（+ `Applier.cs:327-370`）[medium] apply 阶段不校验暂存文件哈希，且自动取 `%TEMP%` 下最新目录 → 同用户进程可投毒暂存目录（TOCTOU）。
- `updater/Program.cs:194-198` [medium] Agent 重启失败的错误被丢弃，消息里只带 Host 的错误 → 用户看到「（重启异常：，可手动启动）」且 Agent 静默未起。
- `updater/Program.cs:156-188` [medium] apply 失败回滚后不恢复已停止的 Host/Agent，却提示「已自动回滚到替换前状态」。
- `updater/Program.cs:111-116` [medium] 下载/网络失败统一返回退出码 4（应为「源不可达 3」或独立码）→ 调用方误判失败类型。
- `updater/Program.cs:49,52-58` [medium] 未知命令返回 0（成功）；兜底 `catch` 对所有命令都返回 5（「应用失败已回滚」）→ 退出码语义失真。
- `updater/Program.cs:292-308` [medium] `Value()` 会把下一个开关当成值（`--source --quiet` → 源变成 `--quiet`）。
- `updater/Applier.cs:304-312` [medium] 备份单文件失败仅记日志继续 → 可能留下「旧版本 + 新文件」的半更新，与「禁止半更新」契约矛盾。
- `updater/Applier.cs:438-452` [medium] `Rollback` 忽略传入 `stamp`（永远取字典序最大的 `backup-*`），且备份目录位于可写目标目录内。
- `updater/Applier.cs:373-404` [medium] 文件替换非原子（`File.Copy` 覆盖、Move 后存在目标缺失窗口），崩溃无自动恢复。
- `watchdog/Program.cs:229-245` [medium] `IsRunning` 命中即 `return`，其余 `Process` 对象未 Dispose → 每 3s 每目标一批句柄等 GC。
- `watchdog/Program.cs:229-245,280-308` [medium] 按进程名全局匹配/查杀，不区分映像路径 → 开发 bin/另一安装的同名实例会让看门狗误判存活或误杀。
- `watchdog/Program.cs:51-76,95-149` [medium]（疑似）守护主循环无 try/catch，单次异常即退出守护 → 看门狗可能因一次瞬时异常永久失效。

## 安装 / 部署脚本（6）

- `scripts/uninstall-betterdesktop.ps1:149` [medium] 从 `%TEMP%` 自重启时未透传 `-InstallRoot`/`-InstallBase` → 实际删除的可能是另一个目录。
- `scripts/uninstall-betterdesktop.ps1:153` [medium]（疑似）`Start-Process -ArgumentList $argList` 未为含空格路径加引号（用户名含空格时子进程启动失败，而父进程已 `exit 0` → 静默失败）。
- `scripts/deploy-desktop-service.ps1:29` [medium] 输出目录硬编码 Debug 且无 `-Configuration` 参数 → 容易把 Debug 程序集部署进生产查找路径。
- `scripts/deploy-desktop-service.ps1:70` [medium] 部署前不清空目标目录 → 已被删除/改名的 dll 残留造成新旧混用（脚本头部正是为防这个）。
- `scripts/pack-shellmenu-msix.ps1:31` [medium] 硬编码 PFX 口令 `'BetterDesktop'` 且出现在 `signtool` 命令行（进程列表可见）。
- `scripts/deploy-clipboard.ps1:41` [medium]（疑似设计取舍）部署产物与用户数据同目录且覆盖式写入、无清理 → 新旧依赖混用；`-PurgeUserData` 会连部署产物一起删。

## 发布脚本（6）

- `scripts/publish-modules.ps1:82` [medium] 把 `manifest.json` 拷进 `01-主程序`，但该目录随后被改写（补引擎、排除 `engines/`）→ 分发出去的 manifest 与实际内容不符，更新器会误判。
- `scripts/publish-modules.ps1:18` [medium] 文件头声称「纯 ASCII 无 BOM」，实际含大量中文字面量且**依赖 BOM** 才能被 PS 5.1 正确解析 → 维护者据注释去掉 BOM 会导致模块目录名乱码且不报错。
- `scripts/publish-modules.ps1:11` [medium]（疑似）注释称 02/03 为 self-contained 发布，实际 `dotnet publish` 未加 `--self-contained -r win-x64`。
- `scripts/publish-modules.ps1:57` [medium]（疑似）`-Source` 相对路径按 cwd 解析，而 `-OutRoot` 按仓库根解析 → 从 `scripts\` 或 CI 调用时找不到源。
- `scripts/publish-modules.ps1:118` [medium]（疑似）源里缺 `engines/` 只黄字提示，仍以 `exit 0` 报 `MODULES OK` → 不完整分发被当成功。
- `scripts/publish-modules.ps1:65` [medium]（疑似）`dist` 下无匹配目录时 `$src` 为 `$null`，`Test-Path $src` 抛参数绑定错误而非清晰的「请先跑 publish.ps1」。

## 验证脚本（6）

- `scripts/verify-system-integration.ps1:118` [medium] 必检清单正则只认引号内 `*.exe|*.yml|*.dll` → 两边的 `.ps1` 条目与含 `\` 的路径被一起丢弃，门禁**假绿**（当前 publish 与 install 清单其实不一致）。
- `scripts/verify-system-integration.ps1:35,42` [medium]（疑似）`if ($null -eq $content)` 守卫无效（`[string]` 形参把 `$null` 绑成空串）→ 文件缺失时报出一堆噪声「缺少必需字面量」。
- `scripts/verify-dotnet-format.ps1:20-24` [medium] 仓库外文件被兜底成绝对路径当 key，与相对路径基线永不相等 → 门禁持续报「新增违规」且换机器就变。
- `scripts/verify-dotnet-format.ps1:33` [medium] 用 `-notin` 对约 2 万行基线做线性判断（O(N×M)），而失败路径是常态 → 门禁耗时被放大。
- `scripts/verify-dotnet-format.ps1:62-64` [medium] `"消息" + $detail`（数组）被压成一行 → 破坏「失败逐条输出」契约，20 条违规挤在一行。
- `scripts/verify-dotnet-format.Tests.ps1:16-45` [medium] 棘轮主流程（读基线/比对/退出码）零测试，文件头注释却称已覆盖 → 最危险的 fail-open 模式无回归保护。

## 状态栏 / 窗口追踪（10）

- `shell-status/Native/MediaCoreNative.cs:74,113,142` [medium] 公共同步 API 内部 `Task.Run(...).GetAwaiter().GetResult()` → 任何 UI 线程调用即阻塞，生产路径还形成「线程池线程再等线程池线程」的双层占用。
- `shell-status/Services/MediaPlaybackService.cs:36,94` [medium] 1s `Timer` 驱动的 `async void PollForChanges` 无重入保护 → 单次查询超 1s 就叠起并发轮询，事件重复触发、签名交错覆盖。
- `shell-status/Services/MediaPlaybackService.cs:105-114` [medium] 在 `lock (_sync)` 内同步 invoke 外部订阅者 → 订阅者耗时会阻塞其它线程，反向等待即死锁。
- `shell-status/Services/MediaPlaybackService.cs:55,62,75` [medium] `CancellationToken` 形参声明后完全未使用 → 契约承诺可取消但实际不可取消。
- `shell-status/Services/MediaPlaybackService.cs:62-73` [medium] 内联复制了 `MediaPlayerCore.PickActive` 的「活动会话」选择逻辑（自称单一来源）→ 双份真相易分叉。
- `packages/api/Music/MediaPlaybackSnapshot.cs:117` [medium] 契约 DTO 暴露 WinRT `IRandomAccessStreamReference`（文档自称「纯数据、不含 WinRT 句柄」）→ WinRT 依赖泄漏进唯一的对外契约面。
- `packages/api/Music/IMediaPlaybackService.cs:36` [medium] 契约暴露裸 `event`（后台轮询线程触发、需手动退订）且仓内实际无订阅方 → 无消费方又易误用的公共 API 面。
- `shell-window-tracker/Thumbnail/WindowPeek.cs:98,137,165` [medium] `DwmActivateLivePreview`（未文档化序号 `#113`）调用无异常防护，抛异常时 `_target` 已赋值 → 状态不一致。
- `shell-status-tests/NativeSmokeTests.cs:17-41` [medium] 依赖原生 DLL 存在（干净 CI 必失败），`GetSessions` 用例只 `Assert.NotNull` → 环境依赖 + 断言过弱。
- `shell-window-tracker/Native/RunningAppDetector.cs:416` [medium] `InterpreterExeNames` 里的 `"node.exe"` 永不命中（命名已去扩展名）→ 误导性死项。

## 搜索 / 通知等（4）

- `shell-search/Services/FileSearchProvider.cs:60-66` [medium] 引擎路径未施加 `MaxResults(100)` 上限，可返回最多 1000 条（WS/兜底两条路径都受约束）→ 上限契约被绕过。
- `shell-search/Services/FileSearchProvider.cs:268-274` [medium]（疑似）`ScoreFileName` 末尾 `return 30` 使「完全未命中」与「文件名包含命中」同分 → 依赖 `score > 0` 判命中的调用方会被误导。
- `shell-search/Services/FileSearchProvider.cs:40,44` [medium] 注入 sealed 具体类 `IndexIpcClient` → 新增的引擎分支（降级/构建中/异常三条回退路径）无法单测、零覆盖。
- `shell-search/README.md:8,26,29,30` [medium] 文档与实现不符（兜底扫描上限仍写 20 实为 100；称「三路并行始终合并」实为引擎可用时提前返回只跑两路）。

---

# 四、跨组共性模式（建议优先治理）

这些问题在多个包重复出现，修一处不如立个规范：

1. **误把构建产物/二进制提交进源码树**（4 处）：`shell-clipboard-panel/build_err.txt`、`shell-capture/build_out.txt`、`shell-context-menu/native/*.dll|*.exe`。建议补 `.gitignore`（`*_err.txt`、`build_out.txt`、`bin/`、`native` 编译产物）并清理。
2. **`Process` 对象未 `Dispose`**（3 处）：`SystemIntegrationSection`、`HotkeyDeclarations`、`watchdog/Program.cs`。建议统一 `foreach (var p in Process.GetProcessesByName(n)) { p.Dispose(); ... }` 或封装工具方法。
3. **UI 线程做同步 IO / 长任务**（6 处）：截屏裁剪写剪贴板、搜索 IPC 5s 同步等待、Dock 固定项健康检查、系统集成状态查询、`MediaCoreNative` 同步包装、`SuppressionWatcher`。建议：凡超过 ~50ms 的操作一律 `Task.Run` + 回投 Dispatcher。
4. **事件订阅 / 静态引用 / 定时器未清理**（6 处）：`IslandWindow` 外观订阅、`PanelMainWindow` 的 client 事件、`DockAppsServiceBridge`、`SurfaceScopeBridge`、`DesktopIconsControl` 看门狗、`HotkeyPanelWindow` 场景监听。建议：插件/窗口卸载清单化（订阅、定时器、钩子逐项注销）。
5. **静默吞异常导致功能失效不可见**（7 处）：`ScanDesktopShortcuts` 空 catch、`MarkApproved`、`HotkeysPlugin` 降级、`HotkeyDeclarations.Declare` 失败、`EditorWindow` 解码失败、`SendInput` 返回丢弃、`verify-dotnet-format` fail-open。建议：所有 `catch` 至少落一条 Warn 日志；关键路径失败要有用户可见反馈。
6. **定时器 / 轮询无重入保护**（4 处）：`MediaPlaybackService.PollForChanges`（async void）、`MediaActivitySource.Stop` 竞态、`HotkeyPanelWindow.PollGate`、`HotkeyScanAndCaptureTests`。建议统一用 `Interlocked` 门闩或「上一轮结束再排下一轮」。
7. **路径/命令/凭据构造缺少校验**（4 处）：卸载脚本 `Remove-Item`、安装脚本 `$fileVersion` 路径穿越、更新器 http 明文源、PFX 口令硬编码。建议涉及删除/替换/签名的地方先做白名单与前缀校验。

---

# 五、建议修复顺序

| 优先级 | 内容 |
|---|---|
| **P0（今天）** | C1 热键消息泵、C2 PDF 合并页引用、C3 卸载脚本删除路径校验 |
| **P1（本周）** | 热键 4 条（自等死锁 / 创建失败静默 / 穿透不恢复 / Win 键声明）；宿主 3 条（停止标记名不一致 / 漏传 `--desktop-controls` / CLI 并发写设置）；剪贴板 2 条（滑条折算 / 重连心跳）；搜索 2 条（UI 线程 5s / Take 截断）；截屏 1 条（UI 线程裁剪）；动态岛 2 条（订阅泄漏 / 同 Id 不刷新）；Rust 2 条（PDF 合并已含、中文切字符串 panic）；更新器 http 源；两处脚本删除路径校验；门禁 fail-open |
| **P2（随后）** | 其余 high + 共性模式 1~5 的规范化治理 |
| **P3** | 122 条 medium 按域分批清理（建议先做「资源释放」「吞异常」「UI 线程」三类） |

---

## 六、修复进展（2026-09-17 当日落地）

全仓构建 0 警告 0 错误；宿主冒烟通过（新日志格式与启动横幅实证）。

### 已修复 — critical（3/3）

| 编号 | 问题 | 落地 |
|---|---|---|
| C1 | 热键线程无消息泵，所有系统热键静默失效 | `HotkeyCenterWindow.ThreadMain` 改为「抽干消息 + 带超时取命令」混合循环；同文件一并修掉自等死锁（`RunOnThread` 识别泵线程）、类名重复导致二次创建必失败（加 GUID + 失败日志）、`Dispose` 超时后释放集合致线程崩溃 |
| C2 | PDF 合并使用重编号前的页引用，产出损坏文件 | `pdf_ops.rs` 改为 renumber 后再取 `get_pages()`，并同步 `out.max_id` |
| C3 | 卸载脚本对未校验路径递归强制删除 | 新增 `Test-SafeDeletePath`：目标必须是 `%LOCALAPPDATA%/%APPDATA%\BetterDesktop` 之下，否则拒绝执行 |

### 已修复 — high（10/25）

| 编号 | 问题 | 落地 |
|---|---|---|
| H5/H8 | 钩子回调/UI 热路径同步写盘 | `PopupWindowBase.DiagTrace` 改为默认关闭的环境变量开关 + 转投异步管道；`DebugLog`（Dock 悬停/缩略图链路 36 处调用）改为委托内核异步管道，不再写用户桌面 |
| H14（部分） | 脚本删除路径未校验（install 侧） | `$fileVersion` 加格式白名单（防 `..\` 穿越）+ 卸载旧安装根前校验范围 |
| H15 | 「停止桌面服务」标记名不一致，服务被自动拉回 | `tray/ProcessBridge.cs` 三处统一为 `desktop-stopped.flag`（与看门狗/Agent/托盘一致） |
| H16 | 拉起桌面控制进程漏传 `--desktop-controls`，菜单不弹还起了后台服务 | `HeadlessExecutor.LaunchDesktopControls` 补 `ArgumentList.Add("--desktop-controls")` |
| — | 各进程崩溃路径可能丢最后几条日志 | host/tray/agent/panel/capture/desktop-control/watchdog/recovery 的未处理异常全部改为「记录异常链 + 环境快照 + **同步刷盘**」；capture 补齐原先缺失的 `AppDomain.UnhandledException` |

### 新增 — 统一日志系统（面向分发实地测试）

- 新增 `shared/logging/DiagnosticLogger.cs`、`DiagnosticBundle.cs`：异步有界队列（满则丢弃并计数）、单文件 8MB 滚动、保留 7 天、目录 256MB 上限、启动环境横幅、崩溃同步刷盘、一键导出诊断包。
- 以**源码链接**（`<Compile Include Link>`）方式接入，不破坏 `tray/updater/watchdog/recovery` 的"零包引用"约束。
- 接入进程：host、tray、agent、updater、watchdog、recovery、clipboard-panel、capture、desktop-control、settings-host（kernel 与共享源各一份实现，格式/策略全一致）。
- 托盘新增「导出诊断包（收集日志给开发者）…」入口。
- 使用说明见 `docs/2026-09-17-diagnostics-guide.md`。

### 尚未修复（建议下一批）

- **H24/H25**（搜索）：引擎 IPC 在 UI 线程同步等 5 秒；分组排序叠加上游 `Take(limit)` 导致文件/设置结果被截掉。
- **H3**（截屏）：选区定稿在 UI 线程读全屏 PNG + 重编码（4K 下卡顿数百毫秒）。
- **H11**（转换引擎）：`managed.rs` 字符下标当字节下标，中文 HTML 遇 script/style 会 panic。
- **H17**（更新器）：允许 `http://` 明文更新源且 manifest 无签名。
- **H12**（索引引擎）：消息模式管道 64KB 出站缓冲装不下大响应。
- 其余 medium 122 条按域分批，建议优先「资源释放」「吞异常」「UI 线程」三类共性模式。

---

## 附：本报告的局限性

- 审查由 AI 完成，每条结论都回读了源文件，但**未做真机运行验证**；标注「疑似」的 8 条（见文中）需人工或实测确认。
- 未覆盖：真机行为差异（DPI / 多屏 / 特定 Windows 版本）、构建与测试是否通过、性能实测数据。
- 纯文档（`docs/**`）与数据基线文件未纳入审查。
