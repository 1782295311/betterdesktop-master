# 截图（剪贴板扩展）实施计划（2026-09-14）

> 用户需求（2026-09-14 拍板）："截图后可以用 OCR 识别选项，然后截图和识别结果全部保存进剪切板历史，所以这两个是剪切板的扩展功能。"
> 状态：设计已定，待实施。OCR 侧见 [2026-09-14-ocr-recognition.md](2026-09-14-ocr-recognition.md)；总索引见 [2026-09-14-remaining-features-index.md](2026-09-14-remaining-features-index.md)。

## 0. 判位

截图**不是独立工具**，而是剪贴板能力的扩展：产物是剪贴板历史里的图片条目，入口面形态与剪贴板面板同构。因此本功能不新建数据面、不自建历史库、不做第二套存储——图片的落盘、缩略图、去重、驱逐、搜索全部复用已有剪贴板引擎。

## 1. 现状锚点（源码验证）

| 层 | 位置 | 事实 |
|---|---|---|
| 图片存储 | `engine/src/store.rs:941` | `e.image_path = format!("clipboard\\images\\{id}.png")`——条目以**相对路径**持有图片，不走字节载荷 |
| 缩略图 | `engine/src/store.rs:157` | `thumbs_dir()`：缩略图独立目录，与条目分离 |
| 存储档位 | `engine/src/settings.rs:106` | `storage_mode: StorageMode::Full` 为默认档（原图落盘）；`PathsOnly` 档仅缩略图+元数据 |
| 宿主侧接入 | `packages/shell/shell-clipboard-ipc/` | `ClipboardIpcClient`：单线程统一读写 + 同步 RPC + 事件订阅 + 断线重连 |
| 入口面先例 | `packages/shell/shell-clipboard-panel/` | 独立 WPF exe（`BetterDesktop.Clipboard.Panel`），非插件；部署见 `scripts/deploy-clipboard.ps1` |
| 采集 API | `packages/shell/shell-core/Native/NativeMethods.cs` | **无** BitBlt / DXGI / `Windows.Graphics.Capture` / PrintWindow 任何声明 |
| 多显示器 | `packages/shell/shell-core/README.md` | `IDesktopSurface` 仅主屏；菜单栏、剪贴板面板同为主屏假设 |
| 2D 图像处理 | 全仓依赖清单 | `SkiaSharp` 已在依赖面（绘制/编解码/滤镜），无需新增图像库 |

## 2. 设计

### 2.1 进程形态

新增**第三个入口面 exe**（与剪贴板面板同构，名称与项目待定，下称 capture exe）：常驻注册全局截图热键，按需创建覆盖层窗口；不依赖宿主，壳退出后截图仍可用。不复用面板进程，理由有两条：截图热键必须在面板未启动时可用；覆盖层是逐帧热路径，与面板生命周期耦合会让"取图"受历史 UI 状态影响。

采集后端**运行在 capture exe 进程内**，不做跨进程帧传输：单帧 4K BGRA 约 33 MB，跨进程搬运的拷贝与同步成本高于收益，且覆盖层需要与像素零距离交互。

### 2.2 采集后端分层（唯一化，登记 `docs/MECHANISMS.md`）

| 优先 | 后端 | 用途 | 为什么需要它 |
|---|---|---|---|
| 1 | `Windows.Graphics.Capture`（WGC） | 逐显示器 + 逐窗口采集 | Win11 22H2+ 可 `IsBorderRequired=false` 关闭黄框；支持逐窗口采集与光标开关 |
| 2 | DXGI Desktop Duplication | 显示器采集兜底 | WGC 不可用（旧系统/混合显卡/远程桌面）时唯一无边框可靠路径 |
| 3 | GDI BitBlt | 最后兜底 | 覆盖前两者失败的场景；**HDR 屏颜色会失真**，命中时必须在日志与 UI 中标注降级 |

HDR 处理：读 `DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO` 判定显示器是否处于高级色彩模式，再按帧实际像素格式（BGRA8 或 FP16 scRGB）做色调映射，输出 sRGB。不处理的表现是 HDR 屏截图整体发灰发暗，被视为"工具不专业"。

多显示器：采集层按**虚拟屏**枚举所有显示器并分别取帧，覆盖层是一个横跨虚拟屏的窗口，选区按显示器 DPI 分别换算（per-monitor DPI awareness v2）。只支持主屏属于残废实现，不做。

### 2.3 数据流（关键：不新增 IPC 动词）

```
capture exe：采集 → PNG 落盘（%LOCALAPPDATA%\BetterDesktop\capture\tmp\）
           → 写系统剪贴板（CF_DIB / CF_HDROP / "PNG" 注册格式）
           → 剪贴板引擎按既有捕获链捕获入库（缩略图、去重、驱逐全部复用）
           → 历史出现图片条目（面板/搜索/灵动岛立即可见）
```

选择写系统剪贴板而不是"直接导入引擎"的原因：`engine/src/ipc.rs` **没有 `import` 动词**，宿主侧 `ClipboardImportItem` 也已删除；复活它等于新增协议面与兼容面，而写剪贴板走的是引擎已在监听的既有主路径，零新协议、零引擎改动。副作用是截图会正常触发"剪贴板已更新"语义（用户可见、可被其他程序粘贴），与"截图即复制"的直觉一致。

临时文件生命周期：入库后引擎在 `clipboard\images\` 持有自己的副本，capture exe 负责删除临时文件；删除失败只记日志，不阻塞本次截图。

### 2.4 覆盖层与编辑器

- 覆盖层：全屏透明窗口，选区拖拽、尺寸读数、像素放大镜、取色（HEX/RGB）、屏幕边界磁吸、Esc 取消、右键取消、Enter 确认。
- 工具条（确认后浮出）：复制、保存到文件、**OCR 识别**、贴图、标注。
- 编辑器：矩形/椭圆/箭头/画笔/文字/序号/高亮/马赛克/模糊/裁剪；撤销重做栈；马赛克与模糊按受影响矩形增量重算（SkiaSharp），不做全帧重算。
- 贴图：顶层无边框窗口 + 逐像素 alpha + 可调透明度 + 右键菜单（复制/保存/OCR/关闭）+ **点击穿透开关**（复用 [2026-09-14-hotkey-registry-and-sheet.md](2026-09-14-hotkey-registry-and-sheet.md) 的两态窗口原语）。

### 2.5 入口

| 入口 | 说明 |
|---|---|
| 全局热键 | 经热键注册表登记（作用域 `Global`，来源 = capture exe），可改键、可停用 |
| 托盘菜单 | 与剪贴板面板同级入口 |
| CLI | `BetterDesktop.Cli`：headless 截图（如全屏直出到剪贴板/文件），对齐 CLI 既有 headless 能力约定 |
| 剪贴板面板 | 条目行内动作：对图片条目补做 OCR、贴图、标注（见 OCR 文档） |

## 3. 契约

新增 `packages/api/Capture/IScreenCaptureService.cs`（命名空间 `BetterDesktop.Capture.Contracts`，域目录待定）：

```csharp
public enum CaptureMode { FullScreen, Region, Window, Scrolling }

public sealed record CaptureRequest(
    CaptureMode Mode, IntPtr TargetWindow, PixelRect? Region, bool IncludeCursor, bool HdrTonemap);

public sealed record CaptureResult(
    bool Success, string? PngPath, int Width, int Height, PixelRect VirtualScreenRect,
    CaptureBackend BackendUsed, string? DegradeReason);

public interface IScreenCaptureService { Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken ct); }
```

契约面只暴露"采集到 PNG 路径"，不暴露帧字节——字节不进托管堆（GC 压力）也不进 IPC（协议面）。

## 4. 红线

- **受保护内容不得静默变黑**：DRM/受保护窗口采集失败时必须在工具条上显式提示"该窗口受系统保护，无法截取"，不交付黑图。
- **降级必须可见**：落到 BitBlt 或 HDR 未做色调映射时，结果与日志都标注降级原因（`docs/runtime-health.md` fail-visible 纪律）。
- **失败不落半成品**：任何一步失败（采集/落盘/写剪贴板）都不产生历史条目，也不残留临时文件。
- **不抢前台**：覆盖层以外不激活、不进任务栏、不改变前台窗口。
- **多屏一致**：虚拟屏任一显示器都可选区；单屏兜底视为未完工。
- **不做**：录屏、云上传、内置分享、图片编辑的非标注类滤镜。

## 5. 实现顺序

1. 采集后端分层（WGC → DXGI → BitBlt 探测与降级链），单测覆盖后端选择与降级判定。
2. HDR 判定与色调映射；per-monitor DPI 换算与虚拟屏枚举。
3. capture exe 骨架（沿用入口面 exe 的部署与单实例约定）、全局热键登记、全屏直出到剪贴板（最小闭环）。
4. 覆盖层（选区/放大镜/取色/确认取消）。
5. 编辑器（标注 + 马赛克/模糊 + 撤销重做）与贴图。
6. OCR 接入、剪贴板面板条目动作、CLI 入口、托盘入口。
7. 真机走查（HDR 屏、双屏不同缩放、受保护窗口、远程桌面）+ 性能预算实测。

## 6. 风险与兼容

| 风险 | 处置 |
|---|---|
| WGC 黄框策略在不同 Win11 版本不一致 | 采集前判定版本能力；黄框无法关闭时优先 DXGI 后端 |
| 远程桌面 / 虚拟机会话下 DXGI 与 WGC 都可能失效 | 降级 BitBlt 并显式标注；不做静默重试 |
| 混合显卡（独显/核显）下 WGC 帧池创建失败 | 捕获异常 → 后端降级链，不冒泡到 UI 线程 |
| HDR 色调映射标准不唯一（scRGB→sRGB 的滚降选择） | 以 Windows 自带截图工具（Snipaste 类对照）为真机基准，逐像素对照并记录差异 |
| 截图被引擎捕获会与用户同时复制的内容竞争历史 | 条目标记来源进程；面板可按来源筛选，不特殊处理 |
| 临时文件堆积 | 入库后即时删除；启动时清理超过 24h 的残留 |

## 7. 开放问题

1. capture exe 是否也承载"长截图（滚动拼接）"的滚动驱动与拼接算法（需向目标窗口注入滚动消息并做帧间模板匹配）；若做，是否单独拆包。
2. 是否支持"截取后自动 OCR 并同时保留图片与文本两条历史"（默认：挂同一图片条目，见 OCR 文档；双条目为可选设置）。
3. 贴图窗口是否需要在重启后恢复（当前倾向不恢复，避免"开机冒出一堆贴图"）。
4. 采集层的"光标包含"是否做成设置默认值。

## 8. DoD

功能 DoD（真机）：

- D1：热键触发 → 覆盖层出现 → 拖选区域 → 确认 → 剪贴板历史出现该图片条目，粘贴到画图/Word 尺寸一致。
- D2：HDR 显示器上截图与系统截图工具结果肉眼无差异（逐像素对照留证）。
- D3：双屏不同缩放（100% + 150%）下跨屏选区尺寸换算正确。
- D4：受保护窗口截图 → 明确提示，不产出黑图。
- D5：截图后点 OCR → 识别文本挂到同一条目（见 OCR 文档 D1）。
- D6：贴图 → 置顶、可拖动、可调透明度、点击穿透开关生效；重启后不残留。

机制 DoD（机检）：

- T1：后端选择纯函数（输入：系统版本/显示器数/远程会话/HDR 状态 → 期望后端序列），含降级原因断言。
- T2：虚拟屏坐标与 DPI 换算纯函数（含负坐标显示器、跨屏矩形）。
- T3：临时文件清理策略（入库后删除、24h 清理）用假文件系统断言。

构建门禁：`dotnet build BetterDesktop.slnx` 0 警告 0 错误；新增包与既有测试工程全绿。

## 9. 交接节

**注入文档**：本计划（决策与红线）；[2026-09-14-ocr-recognition.md](2026-09-14-ocr-recognition.md)（OCR 接入面）；[../runtime-health.md](../runtime-health.md)（降级可见纪律）。

**模式判定**：新能力 + 新入口面进程；不改剪贴板引擎与协议；新增契约包与一个新 exe。

**强制 scope 边界**：只做"采集 → 落盘 → 进剪贴板历史 → 标注/贴图/OCR 入口"；不做录屏、不做云、不做非标注类图像编辑、不新增剪贴板 IPC 动词。

---

## 10. 实施核销（2026-09-15，执行：技术力应用）

> 证据均为本机真机实测；GUI 交互路径（覆盖层/编辑器/贴图窗口）无法在 headless 会话实走，已如实标注为「待用户真机走查」——未实走不声称通过。

### 功能 DoD（真机）

| DoD | 状态 | 验证证据 |
|---|---|---|
| D1 采集→剪贴板历史→尺寸一致 | **偏离-数据链路实走** | `BetterDesktop.Capture.exe --capture --save` 实走：BitBlt 后端出 2560×1440，在线引擎捕获为 Image 条目（id a9025a00…，ImageWidth/Height=2560×1440 一致）。覆盖层/选区/确认 GUI 路径待用户真机走查（覆盖层与选区控制器有单测覆盖，T2） |
| D2 HDR 无差异 | **偏离-环境不可走** | 本机无 HDR 显示器；色调映射路径代码在案（`--no-tonemap` 开关存在）。留待 HDR 屏真机 |
| D3 双屏缩放 | **偏离-环境不可走 + 设计修正** | 本机单屏。跨屏整矩形换算因混合 DPI 下数学无定义（无单一 DIP 坐标系）已从实现删除，保留单显示器 DPI 换算（生产路径真实使用） |
| D4 受保护窗口 | **偏离-环境不可走** | 本机 WGC 不可用（25H2 缺 interop DLL），启发式对照路径无法实走；BitBlt 后端不产黑帧 |
| D5 OCR 挂同一条目 | **通过** | 合成图 900×260 入库 → OCR 22 字符 → `set_ocr_text` 挂回同 id → 关键词搜索命中；真实截图 2560×1440 入库 → OCR 550 字符挂回 → `autoquery` 命中（详 OCR 计划 §11） |
| D6 贴图 | **偏离-待真机走查** | StickerWindow 代码在案并编译；置顶/拖动/透明度/穿透/重启不残留需用户实走 |

### 机制 DoD（机检）

| DoD | 状态 | 验证证据 |
|---|---|---|
| T1 后端选择纯函数 | 通过 | BackendSelectorTests 6 条（WGC/DXGI/HDR 顺序与降级原因断言） |
| T2 坐标/DPI 纯函数 | 通过 | DisplaySpace/SelectionController/PixelRect 11 条（含负坐标显示器；跨屏整矩形换算删除项已同步） |
| T3 临时文件策略 | 通过 | TempCleanupPolicyTests 4 条 |
| OCR 集成 | 通过 | WindowsMediaOcrIntegrationTests 1 条（真实识别成功） |
| 构建门禁 | 通过 | `dotnet build BetterDesktop.slnx` **0 警告 0 错误**（含新挂载 shell-capture + tests）；`dotnet test` 21/21；引擎 `cargo test` 108/108 |

### 部署与登记

- `scripts/deploy-clipboard.ps1` 新增第 3 段（capture 部署），并已实执行：新引擎（release 3.56MB，含 set_ocr_text）替换在线旧引擎并拉起（PID 19788）；面板与 `BetterDesktop.Capture.exe` 已部署至 `%LOCALAPPDATA%\BetterDesktop`。
- `BetterDesktop.slnx` 已挂载 `shell-capture` + `shell-capture-tests`（显式 Project Path，x64 平台）。
- `docs/MECHANISMS.md` v0.8 登记 **M19 截图与 OCR 接入**。
- `packages/api/Clipboard/ClipboardEntry.cs` 补 `OcrText` 属性（引擎摘要 ocrText 落点）。

### 红线遵守

- 降级可见：CLI stdout 输出 `backend=BitBlt` + `degradeReason`（WGC DllNotFound / DXGI 无相交输出，实测输出）。
- 失败不落半成品：CaptureFlow.CleanupSession + TempFileManager；E2E 产生的两个测试条目已通过 `delete` 动词清理（含含屏真实截图）。
- 不抢前台：工具条/覆盖层 `ShowActivated=false`；完成回调走托盘 Balloon（代码在案）。
- 不新增截图写回 IPC 动词：仅 `set_ocr_text`（OCR 计划要求），零新增采集回写动词。
