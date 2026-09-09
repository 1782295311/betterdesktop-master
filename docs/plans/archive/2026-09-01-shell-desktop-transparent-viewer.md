# Cairo 开发计划 · shell-desktop 回归透明文件显示器 + 完整交互

> Task: 修复 shell-desktop 自绘桌面的核心隐患（自画壁纸违反生死线 #1），回归透明文件显示器；补齐图标真实化 + 右键菜单 + 拖放 + 内联重命名完整交互。
> 证据基于 commit `952f117`（HEAD）验证；技术力文档命中：`TECH-KNOWLEDGE/63-桌面渲染/desktop-progman-embed.md`（L2，含正确性不变量清单）。
> 证据头 schema 2；工作树 dirty（含未提交改动）；cited-path manifest = shell-desktop 全部源文件 + shell-menu-bar 消费方。

## 1. Objective

- **根因修复**：`DesktopWindow` 从"自绘壁纸 + 不透明"回归技术库生死线 #1 的**透明文件显示器**（`AllowsTransparency=true`、背景 `#01000000` alpha=1 近透明、壁纸归 explorer 原生桌面渲染）。[verified] 当前实现 `AllowsTransparency=false` + `BuildWallpaperBrush` 自画壁纸 = 生死线 #1 明令禁止的错误方案（重复渲染、换壁纸不同步、丢失壁纸引擎兼容）。
- **交互补齐**：图标真实化（目录用 `IconHelper.GetIconByFilename`，与文件同管线）、图标/空白右键菜单（MENU-SPECS §1/§2 实用子集）、拖放（拖出 FileDrop / 拖入复制移动）、内联重命名。

## 2. Current Behaviour

- `packages/shell/shell-desktop/Windows/DesktopWindow.cs`：`AllowsTransparencyDefault=>false`；构造器 `Background = BuildWallpaperBrush(DesktopWallpaperNative.Read())`；`UserPreferenceChanged` 热跟随换壁纸；挂载 `TryEmbedDesktop()` → `FindDesktopHostWindow()` 返回 **SHELLDLL_DefView**（正确），3s 看门狗重挂；`BETTERDESKTOP_DESKTOP_TOPLEVEL=1` 顶层降级分支。
- `Controls/DesktopIconsControl.cs`：`WrapPanel(Orientation=Vertical)` 瀑布列；目录 = 自绘 `FolderGlyph()`（黄色矩形），文件 = `IconHelper.GetIconByFilename`；单击选中（Ctrl 多选）/ 双击启动；无右键菜单、无拖放、无内联重命名。
- `Services/DesktopBrowser.cs` + `Contracts/IDesktopBrowser.cs`：Location 导航历史栈 + 后台枚举 + 剪切/复制/粘贴/重命名/删除（回收站）；**无 NewFolder / 外部文件导入**。
- 消费方：`shell-menu-bar/Windows/MenuBarLeftZone.cs` + `FolderToolbar.cs` 依赖 `IDesktopBrowser`（导航/操作按钮）——契约加法不影响它们（只加方法，不删改签名）。

## 3. Relevant Architecture

- 插件模式 `IPlugin + IContext`；窗口基类 `ShellWindow`（外观/vibrancy 统一驱动）；`DesktopPlugin` 装配 + `ShellHelper.ToggleDesktopIcons(false/true)` 隐藏/还原 explorer 图标。
- 桌面窗口关键约束 [verified]：`UseSkinBackground=false`（皮肤不顶掉壁纸）、`ApplyWindowMaterial()` 空实现（桌面不 blur）、`DefaultTopmost=false`、`ShowInTaskbar=false`、禁 `WindowState.Maximized`（防 `ABN_FULLSCREENAPP`）、`ChromeBorder` 必须包透明根 Border（基类 DEBUG 断言）。
- 挂载点 [verified]：DefView（非 Progman）——壁纸引擎 DComp 层压不住 DefView 子树。

## 4. Technical-Knowledge Findings

- 命中 `desktop-progman-embed.md`（L2，cairoshell 源码 + 本机实证闭环）。生死线（§Phase 3 提取，实现必须逐条遵守）：
  1. **桌面窗口 = 透明"文件显示器"，非 shell 模式不画壁纸**（`#01000000` alpha=1），壁纸永远由 explorer 渲染。自画壁纸是错误方案。
  2. 挂 DefView（含 `SHELLDLL_DefView` 的窗口），WS_CHILD+SetParent。
  3. 禁 Maximized；手动 SetWindowPos 铺 VirtualScreen，高度 -1。
  4. 隐藏原生图标（`WM_COMMAND 0x7402` toggle），退出恢复。
  5. `ShowInTaskbar=false` + `WS_EX_TOOLWINDOW`；`ShowActivated=false`。
  6. SetParent 后停止一切 Z 序管理（子窗口天然最低）。
  7. 图标层 = 横向滚动纵向禁用 + WrapPanel 瀑布列 + Margin(7,13,0,0)。
  8. 基类 ChromeBorder 必须包透明根 Border（防 FailFast 断言）。
  9. **壁纸引擎下挂 Progman 不上屏（PrintWindow 有图≠屏幕可见），挂 DefView 才是正解**——当前代码挂载点已正确，缺的是透明度。
- 反模式对照 [verified]：当前 `AllowsTransparency=false` 正是"自画壁纸错误方案"；注释里的"分层不上屏"实证是 **Progman 挂载** 时代的旧结论，挂载点已改 DefView，结论不适用于当前挂载点。

## 5. Constraint Findings（验收标准 + 生死线）

- 透明化后验证上屏必须 `CopyFromScreen`（红线 #9：PrintWindow 有图≠屏幕可见）——真机验收唯一判据。
- `UseSkinBackground=false` 保持：基类 `ApplyAppearance` 不会覆盖 `#01000000` 背景。
- `ApplyWindowMaterial()` 空实现保持：透明窗口一旦套 DWM blur 会把壁纸糊掉。
- `OnUserPreferenceChanged` / `DesktopWallpaperNative` 随自画壁纸一并删除（不再读注册表）。
- `IDesktopBrowser` 契约只增不删（`NewFolder` / `ImportFiles`），`FolderToolbar` 编译不受影响。
- 右键菜单用 WPF 原生 `ContextMenu` 自包含实现（shell-context-menu 仅规格未实现，不跨包引入）。

## 6. Proposed Changes

| # | 文件 | 符号 | 职责 |
|---|------|------|------|
| 1 | `Windows/DesktopWindow.cs` | `DesktopWindow` | 删除 `AllowsTransparencyDefault=>false` override（回基类默认 true）；背景改 `#01000000`（alpha=1）；删除 `BuildWallpaperBrush`/`OnUserPreferenceChanged`/`UserPreferenceChanged` 订阅及 `System.IO`/`Imaging` using；重写文件头注释（透明文件显示器 + DefView + CopyFromScreen 验收）；保留挂载/看门狗/降级 |
| 2 | `Native/DesktopWallpaperNative.cs` | 整个文件 | **删除**（自画壁纸唯一读者；留则成为"错误方案"活文档） |
| 3 | `Controls/DesktopIconsControl.cs` | `DesktopIconsControl` | 目录改用 `IconHelper.GetIconByFilename`（失败回退 `FolderGlyph`）；图标右键菜单（打开/剪切/复制/重命名/删除/属性）；空白右键菜单（新建文件夹/粘贴/刷新/显示设置/个性化）；拖出（`DoDragDrop` FileDrop）；拖入（`AllowDrop` + `DragOver`/`Drop` → `ImportFiles`）；内联重命名（label↔TextBox 交换） |
| 4 | `Contracts/IDesktopBrowser.cs` | `IDesktopBrowser` | 新增 `void NewFolder();` + `void ImportFiles(IEnumerable<string> paths, bool move);` |
| 5 | `Services/DesktopBrowser.cs` | `DesktopBrowser` | 实现 `NewFolder`（唯一名"新建文件夹"/"(2)"，刷新）；`ImportFiles`（复制/移动外部路径进当前 Location，`UniquePath` 去重，刷新） |
| 6 | `DesktopPlugin.cs` | `DesktopPlugin` | 更新加载日志文案（不再提"壁纸 + 可导航图标层"），不改装配逻辑 |

## 7. Implementation Sequence

1. `IDesktopBrowser.cs` + `DesktopBrowser.cs`：先加契约方法（后续 UI 依赖）。
2. `DesktopWindow.cs`：回归透明 + 删壁纸渲染（核心修复）。
3. 删除 `DesktopWallpaperNative.cs`（确认无其他引用后）。
4. `DesktopIconsControl.cs`：图标真实化 → 右键菜单 → 拖放 → 内联重命名。
5. `DesktopPlugin.cs`：文案。
6. `dotnet build` shell-desktop + shell-menu-bar（消费方编译验证）；全仓 `dotnet build BetterDesktop.slnx`。

## 8. Test Strategy

- 无 shell-desktop 现有测试工程 [verified：无 *Test* 文件]；改动以**编译 0 警告 0 错误 + 真机验收**为准（WPF 窗口嵌入桌面无法 headless 单测）。
- 真机验收清单（红线 #9：必须 CopyFromScreen）：
  - [ ] 启动后 `CopyFromScreen` 截屏可见图标 + 系统壁纸（含壁纸引擎动画时仍可见）——透明文件显示器上屏。
  - [ ] 图标为真实文件/文件夹图标；双击目录导航、文件 ShellExecute。
  - [ ] 图标右键菜单：打开/剪切/复制/重命名/删除/属性（属性 = SHObjectProperties）。
  - [ ] 空白右键：新建文件夹（桌面出现唯一名文件夹）/粘贴（CanPaste 时有）/刷新/显示设置/个性化。
  - [ ] 拖图标出桌面到资源管理器 → 复制/移动生效；从资源管理器拖文件进桌面 → 落到当前 Location。
  - [ ] 内联重命名：Enter 提交 / Esc 取消 / 焦点丢失提交。
  - [ ] 退出后 explorer 原生图标还原（`ToggleDesktopIcons(true)`）。
- 验证命令：`dotnet build packages/shell/shell-desktop/BetterDesktop.Shell.Desktop.csproj -c Debug` + 全仓 `dotnet build BetterDesktop.slnx -c Debug`（0 警告 0 错误）。

## 9. Risk and Impact Analysis

- **高风险符号**：`DesktopWindow` 透明度回归——真机若仍不上屏（本机壁纸引擎环境），回退方案 = `BETTERDESKTOP_DESKTOP_TOPLEVEL=1` 顶层模式 + 保留自画壁纸分支为降级（记录 ADR，不删代码路径）。
- **d=1 下游**：`shell-menu-bar`（`MenuBarLeftZone`/`FolderToolbar` 消费 `IDesktopBrowser`）——契约只加方法，编译零影响；行为零变化。
- **兼容性**：`TreatWarningsAsErrors=true`——删除文件需同步清理 using，避免 CS8019 死引用。
- **性能**：拖入大目录复制走后台 Task（沿用 `Paste` 模式）；`CopyFromScreen` 仅验收用不常驻。
- **可观测性**：`DesktopPlugin` 日志保留加载/卸载轨迹；看门狗日志沿用。

## 10. Files Expected to Change

| File | Symbols | Reason |
|---|---|---|
| `packages/shell/shell-desktop/Windows/DesktopWindow.cs` | `DesktopWindow` | 透明化 + 删自画壁纸 |
| `packages/shell/shell-desktop/Controls/DesktopIconsControl.cs` | `DesktopIconsControl` | 图标真实化 + 右键 + 拖放 + 内联重命名 |
| `packages/shell/shell-desktop/Contracts/IDesktopBrowser.cs` | `IDesktopBrowser` | 新增 NewFolder/ImportFiles |
| `packages/shell/shell-desktop/Services/DesktopBrowser.cs` | `DesktopBrowser` | 实现新契约 |
| `packages/shell/shell-desktop/DesktopPlugin.cs` | `DesktopPlugin` | 日志文案 |
| `packages/shell/shell-desktop/Native/DesktopWallpaperNative.cs` | — | 删除（错误方案活文档） |
| `docs/plans/2026-09-01-shell-desktop-transparent-viewer.md` | — | 本计划 |

## 11. Reusable Implementation Context

- 上下文包（已读，勿重复调研）：`DesktopWindow.cs` / `DesktopIconsControl.cs` / `DesktopBrowser.cs` / `IDesktopBrowser.cs` / `DesktopPlugin.cs` / `FolderToolbar.cs` / `MenuBarLeftZone.cs` / `ShellWindow.cs`（基类，`AllowsTransparencyDefault` 默认 true、`UseSkinBackground` 默认 true 需 override false）/ `shell-context-menu/MENU-SPECS.md`（§1/§2 菜单规格）。
- P/Invoke 纪律：`SHObjectProperties(hwnd, 0, path, null)`（属性页）；`ms-settings:display`/`ms-settings:personalization` 经 `Process.Start(UseShellExecute)`。
- 图标管线：`ManagedShell.Common.Helpers.IconHelper.GetIconByFilename(path, IconSize.ExtraLarge)` @ `IconHelper.IconScheduler`（目录/文件同管线）。

## 12. Assumptions and Open Questions

- [assumed] 本机壁纸引擎环境下 DefView + 分层透明可上屏（desktop-progman-embed 红线 #9 实证；真机验收兜底）。
- [assumed] 右键菜单实用子集（非 MENU-SPECS 全量）：属性 = SHObjectProperties；新建仅"文件夹"；无"以管理员运行/打开文件位置/固定到 Dock"（依赖 shell-context-menu 全量实现，deferred）。
- [deferred] shell-context-menu 全量 IContextMenu 集成（native verb 透传/unified 重绘）→ P2。
- [deferred] 图标自由排列/自动排列/对齐网格（当前瀑布列自动排列）→ P2。
- [open] 内联重命名与 Rebuild 竞态：重命名中触发 ItemsChanged 会丢编辑态（M10 可接受，不做队列化）。

## 13. Definition of Done

- `DesktopWindow` 为透明文件显示器：背景 `#01000000`、`AllowsTransparency=true`、零自画壁纸代码（`DesktopWallpaperNative.cs` 已删，无引用）。
- 图标真实化（目录非自绘 glyph）、图标+空白右键菜单、拖出拖入、内联重命名全部可用。
- `IDesktopBrowser` 新增 `NewFolder`/`ImportFiles` 实现完整，`FolderToolbar` 编译零影响。
- 全仓 `dotnet build` 0 警告 0 错误；真机 `CopyFromScreen` 验收通过（壁纸 + 图标上屏）。
