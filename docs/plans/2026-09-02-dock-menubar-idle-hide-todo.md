# 待办任务清单：Dock/菜单栏空闲隐藏 + AppBar 集成（2026-09-02 移交）

> 背景：上一会话完成大部分工作后，在 dock 底部 AppBar 集成处遇到协商矩形负高度问题，
> 未及收敛。本文件供新会话接续，包含已完成项、当前卡点的完整技术细节与验收标准。

## 用户需求原文（验收基准）

1. Dock 与菜单栏：**用户无操作 20 分钟后才自动隐藏**（20 分钟是默认值，可在设置里改阈值）。
2. **用户操作时，菜单栏和 dock 绝不隐藏**。
3. 注意层级：**不要让 dock 栏始终显示在窗口上，用到的时候才启动**。
4. Dock 右键菜单的"所有程序"应指向**应用提取器**；应用提取器入口也要加到图标子菜单里。
5. **把现在的设置参数定位初始值**（用户当前 settings.json 的调优值固化进代码默认值）。
6. 任务栏常态效果：**不透明纯色模式（Opaque），白色，不透明度=0**（用户 2026-09-02 定稿）。

## 已完成（本会话已落地，勿重做）

| 项 | 文件 | 状态 |
|---|---|---|
| 空闲阈值制隐藏逻辑（GetLastInputInfo + shell.idleHideMinutes 默认 20） | shell-dock/DockWindow.xaml.cs OnAutoHideTick/SetDockVisible | ✅ 逻辑已写 |
| 菜单栏空闲隐藏（同阈值 + 顶部 6px 热区唤出；不 Hide 窗口保留 AppBar，仅 Opacity） | shell-menu-bar/Windows/MenuBarWindow.cs | ✅ 已修 FillBehavior 弹回 bug，**待实测** |
| 全屏误判修复（点桌面→Progman/WorkerW/本进程窗口不算全屏，dock 不再消失） | shell-dock/Services/DockLayoutService.cs ShouldHideOnFullscreen | ✅ |
| DockWindow 默认不置顶（DefaultTopmost=false）+ 悬停/热区临时置顶 | shell-dock/DockWindow.xaml.cs | ✅ |
| 应用提取器窗口（AppSourceWindow：ScanAllPrograms 列表/过滤/启动/固定） | shell-dock/Windows/AppSourceWindow.cs | ✅ 新文件 |
| dock 右键菜单"所有程序（应用提取器）"→ AppSourceWindow | DockWindow.ShowItemContextMenu | ✅ |
| dock.* 与 desktop.* / appearance.windowOpacity 默认值固化为用户实测值 | DockVisualSettings.cs / DesktopIconsControl.cs / AppearanceService.cs | ✅ |
| 设置 → 菜单栏分区加"空闲阈值（分钟）"滑块（120ms 防抖） | shell-menu-bar/Sections/MenuBarSection.cs | ✅ |
| 任务栏外观默认值固化 Opaque + 0x00FFFFFF（**全部场景统一**，2026-09-02 二次定稿） | Taskbar/Contracts/TaskbarAppearanceConfig.cs | ✅ **已验证**（日志 Apply Solid color=00FFFFFF => True，Win11 桥直传） |
| dock 底部 AppBar 集成（DockAppBarReservation + DockWindow.AppBar.cs partial） | shell-dock/Native/DockAppBarReservation.cs、DockWindow.AppBar.cs | ✅ **已修复并验收**（见下） |
| 运行区点击资源管理器无反应（Progman/WorkerW 污染 + 选窗顺序反 + 前台锁） | RunningAppDetector.cs（排除桌面宿主窗 + AttachThreadInput 前台解锁）、DockWindow.ActivateFirstWindow（First 选窗 + 启动兜底） | ✅ 已修复，**待实测** |
| 桌面缺 shell 虚拟项图标（此电脑/回收站/控制面板/网络） | DesktopBrowser（注入 ::{CLSID} 项 + shell 显示名）、BrowserEntry.IsShellNamespace、DesktopIconsControl（PIDL 图标/explorer 启动/菜单裁剪）、新增 ShellNamespaceHelper.cs | ✅ 已修复，**待实测** |

## 当前卡点（新会话第一优先级）

### 卡点 1：Dock AppBar 协商返回负高度（✅ 已解决，2026-09-02 验收通过）

> **根因**：坐标域混乱（非协商时机问题）。`PositionToBottomCenter` 用 `SystemParameters.PrimaryScreen*`
> 取工作区（返回 100% DPI 域），而窗口按 PerMonitorV2 实际 DPI 物理化（125% 屏上窗口被放到物理
> 1679px，工作区底仅 1380px）→ 窗口矩形整个落在工作区下方 → QUERYPOS 只把 Bottom 拉回工作区底、
> 不回写 Top → 负高度 `W=769 H=-299`，条带永不生效。
>
> **修复三件套**：
> 1. **坐标域修复**：`PositionToBottomCenter` 坐标源改为「注册前缓存工作区 `_appBarWorkArea`（GetMonitorInfo 物理域）÷ TransformToDevice scale → 兜底实时 GetMonitorWorkArea → 兜底 SystemParameters」。
> 2. **期望矩形协商**：协商输入从 GetWindowRect 当前位置改为基于缓存工作区的"期望矩形"
>    （`Top = work.Bottom - margin - ph` 保证落在工作区内）。
> 3. **防 ABN_POSCHANGED 自触发循环**：注册前缓存工作区（注册后 work.Bottom=dock.Top 会抬升，
>    读实时值形成"协商→抬升→再定位"循环，dock 被抬到屏幕顶 T=-58 的回归根因）；
>    SETPOS 仅在首次（forceSetPos）或协商结果≠当前窗口矩形时执行。
>
> **验收结果（实测日志，干净环境）**：
> ```
> Dock: AppBar 注册: True (work=0,0,2560,1380)
> Dock: PositionToBottomCenter: L=717 T=1007 W=615 H=87 visible=True topmost=False
> Dock: AppBar 协商诊断: query=True desired=(895,1259,1664,1368) W=769 H=109 agreed=(895,1259,1664,1368) W=769 H=109 work=(0,25,2560,1259)
> Dock: 布局定位 PositionToBottomCenter 完成
> ```
> - 协商 H=109 正值，desired == agreed（系统接受矩形）
> - 系统工作区底 1380 → 1259（= dock 顶），**AppBar 条带真实生效**：最大化窗口必然避让 dock
> - dock 窗口物理矩形 (895,1259,1664,1368) = 协商矩形（GetWindowRect×1.25 吻合），不置顶
> - 协商稀疏触发不循环，dock 位置稳定收敛 L=717 T=1007（物理 895,1259）
>
> **注意**：强杀 Host（Stop-Process -Force）不触发 Unregister，残留条带会永久压占工作区
> （曾出现 work 被压到 24 物理），需重启 explorer 清理。后续调试停 Host 必须先正常退出走
> ReleaseAppBar/OnClosed。

### 卡点 2：任务栏 Opaque 模式 Win10 路径 alpha 冲突（低优先级）
`Taskbar/Native/DwmapiHelper.cs:71-75`：Opaque 分支强制 `color |= 0xFF000000`（不透明），
与用户要的"不透明度=0"冲突。用户是 Win11（走注入桥 color 直传，不受影响）；
若要 Win10 一致，需把 Opaque 分支改为尊重传入 alpha（GRADIENT 下 alpha=0 = 全透，注意别变黑，
参考同文件 Clear 分支注释：TTB 实证 alpha 置 1 避免全黑）。

## 后续待办（按优先级）

1. ~~**修 AppBar 协商负高度**（卡点 1）~~ → ✅ 已修复验收（见上）；余下：实测 dock 空闲隐藏时的
   释放/重注册行为（待办 4）与最大化窗口避让的肉眼确认（协商 work 已证明条带生效）。
2. **实测菜单栏 20 分钟空闲淡出**（FillBehavior 修复后未验证）→ 临时把
   shell.idleHideMinutes 调到 1 分钟观察；动鼠标立即恢复；鼠标贴顶部 6px 热区唤出。
3. ~~**实测任务栏常态**~~ → ✅ 已完成（2026-09-02 下午）：全部场景默认值统一 Opaque+白+alpha=0，
   Win11 桥 `Apply Solid color=00FFFFFF => True` 实测通过。附带发现：scene=Search 常驻误判
   （Win11 SearchHost 的 CoreWindow 可见即判搜索打开），因所有场景外观已统一暂无视觉影响，
   后续若恢复分场景外观需先修 IsSearchOpen 探测。
4. **实测 dock 空闲隐藏**：阈值 1 分钟挂机 → dock 淡出 + AppBar 释放（日志"AppBar 已释放"）；
   动鼠标 → 重注册 + 显示（日志"AppBar 重注册"）。**注意**：隐藏后 BottomMargin 区域工作区回收，
   唤出瞬间最大化窗口会重新避让，观察是否有闪烁/震荡（ABN_POSCHANGED 环）。
5. **验证应用提取器**：dock 图标右键 → "所有程序（应用提取器）" → 窗口列出全程序、
   搜索过滤、双击启动、右键固定。
6. **用户手动清理**：`betterdt\Temp`（安全删除机制拒绝，需手动 Shift+Del；
   ttb-2026.1 的 ExplorerTAP.dll 已存档进 packages/shell/Taskbar/native/，删了不丢）。
7. （可选）设置分区里给"空闲阈值"补充 Dock 一侧的说明文案；或把滑块同时挂到"桌面"分区。

## 关键文件索引

| 文件 | 内容 |
|---|---|
| packages/shell/shell-dock/DockWindow.xaml.cs | OnAutoHideTick（空闲判定）/ SetDockVisible（健壮状态机+日志）|
| packages/shell/shell-dock/DockWindow.AppBar.cs | AppBar partial（Register/SyncAppBarPosition/EnsureAppBar/ReleaseAppBar）|
| packages/shell/shell-dock/Native/DockAppBarReservation.cs | 底部 SHAppBarMessage 封装 |
| packages/shell/shell-dock/Services/DockLayoutService.cs | ShouldHideOnFullscreen（已修）/ ShouldShowOnEdgeHover（热区 4px）|
| packages/shell/shell-dock/Windows/AppSourceWindow.cs | 应用提取器窗口 |
| packages/shell/shell-dock/Services/DockVisualSettings.cs | dock.* 默认值 + IdleHideMinutes |
| packages/shell/shell-menu-bar/Windows/MenuBarWindow.cs | 菜单栏空闲隐藏（SetIdleHidden 已修弹回）|
| packages/shell/Taskbar/Contracts/TaskbarAppearanceConfig.cs | 任务栏场景默认值（Opaque 白 alpha=0）|
| 日志 | 桌面 BetterDesktop_debug.log（搜 "Dock:" / "TaskbarAccent"）|

## 调试提示

- DockWindow.xaml.cs 的 SetDockVisible/PositionToBottomCenter 已埋 DebugLog，日志可直接看
  显隐切换、定位坐标、AppBar 状态。
- 构建前先 Stop-Process BetterDesktop.Host（文件占用）。
- 本次会话 replace_in_file 工具对长文本频繁报"missing old_str"（疑似解析 bug），
  大段修改用 write_to_file 重写小文件或 partial 拆分更稳。
