# Cairo 开发计划 · 自绘桌面（shell-desktop）

> Task: 新增自绘桌面插件——壁纸自绘 + 可导航桌面/文件夹浏览器（图标展示、启动、文件操作），替代 explorer 桌面层。
> 证据基于 commit `952f117`（+ 本会话未提交改动）；技术力文档命中：拆析 cairoshell-最初开源版（desktop-embed）、05-图标 501/502。
> 证据头 schema 2。

## 0. P1 范围扩展（2026-08-31 用户补充）

自绘桌面 = **可导航的文件夹浏览器**（cairoshell DesktopIcons 同款语义，Location 可变）：
- **与左区导航联动**：菜单栏"位置/下载/文档"点击 → 桌面浏览器导航到对应文件夹（自绘桌面启用时），不再开 explorer。
- **左区新增可折叠工具条**（"文档"右侧）：
  - 路径显示（当前 Location）
  - 导航行：后退 / 前进 / 向上 / 刷新（← → ↑ ⟳）
  - 操作行：剪切 / 复制 / 粘贴 / 重命名 / 删除（中文单字按钮，对选中项操作；删除走回收站 SHFileOperation）
  - 整条可折叠收纳（默认收起），图标行参照用户截图（导航行 + 操作行）。
- 契约拆分：`IDesktopBrowser`（shell-desktop.Contracts）由 DesktopPlugin Provide；菜单栏仅依赖契约。

## 1. Objective

新增 `packages/shell/shell-desktop` 插件：全屏自绘桌面窗口（壁纸 + 可导航桌面/文件夹浏览器），接管 explorer 桌面层的视觉与交互，与菜单栏（AppBar）/其他插件协同。

## 2. Current Behaviour

- 无桌面插件；explorer 原生桌面（壁纸 + 图标）原样显示，菜单栏浮在其上。
- cairoshell 原版桌面（参照实现）[verified]：
  - `DesktopWindow`：全屏窗口，Z 序最底；壁纸 = 注册表 `HKCU\Control Panel\Desktop` 的 `Wallpaper`/`WallpaperStyle`/`TileWallpaper` → ImageBrush（映射：Fill=10 → UniformToFill、Fit=6 → Uniform、Stretch=2 → Fill、Center=00 → None、Tile=01 → Tile）。
  - `ShellHelper.ToggleDesktopIcons(false)`（ManagedShell，本包已引用）隐藏 explorer 原桌面图标；退出时还原 true。
  - 图标层：ManagedShell.ShellFolders 枚举桌面 SH 项。

## 3. Relevant Architecture

- 插件模式：`IPlugin` + `IContext`（同 MenuBarPlugin/WindowTrackerPlugin）[verified]。
- 窗口基类：`ShellWindow`（外观/vibrancy 统一驱动）[verified]；桌面窗口特性：全屏、`Topmost=false`、Z 序底、`ShowInTaskbar=false`、`ShowActivated=false`（不抢焦点）。
- 图标管线复用 [verified]：`.lnk` 解析 = `ShellLinkResolver`（app-source）；图标 = `IconHelper.GetIconByFilename`（IconScheduler 调度，搜索面板同款）；启动 = `Process.Start`（UseShellExecute）。
- 菜单栏 AppBar 已申请顶部工作区 [本会话 verified]：桌面图标网格从工作区（菜单栏下方）开始排布。

## 4. Technical-Knowledge Findings

- cairoshell desktop-embed 拆析 [verified]：桌面 = DesktopManager（壁纸/开关）+ Desktop 窗口 + DesktopIcons（SH 枚举）；壁纸注册表映射表完整可抄。
- 本仓 P/Invoke 收口纪律：桌面插件自持 Native（注册表读壁纸）。
- Z 序关键：桌面窗口必须比普通窗口低——`Topmost=false` 且最早创建；菜单栏/Dock 均 Topmost ✓。

## 5. Constraint Findings

- 隐藏 explorer 图标必须可还原（退出 `ToggleDesktopIcons(true)`）——用户环境不可破坏 [inferred]。
- 壁纸文件可能失效 → 加载失败回退纯色背景（M10）。
- 图标双击启动 `.lnk`/`.url`/文件：`Process.Start` UseShellExecute 即可（.lnk 由 shell 解析目标）。
- 桌面目录：`Environment.SpecialFolder.DesktopDirectory`（MVP 先用户桌面）。

## 6. Proposed Changes

| # | 文件 | 符号 | 职责 |
|---|------|------|------|
| 1 | `packages/shell/shell-desktop/`（新包） | csproj + `DesktopPlugin` | 装配：创建桌面窗口、隐藏原图标、Unload 还原 |
| 2 | `Windows/DesktopWindow.cs` | `DesktopWindow : ShellWindow` | 全屏窗口：壁纸 ImageBrush 背景 + 图标网格 + 右键刷新 |
| 3 | `Native/DesktopWallpaperNative.cs` | 壁纸读取 | 注册表 Wallpaper/Style/Tile → (path, style) |
| 4 | `Controls/DesktopIconsControl.cs` | 图标网格 | 枚举桌面目录 → 图标+名称（竖排网格），单击选中/双击启动 |
| 5 | `host/Bootstrap.cs` | 注册 `DesktopPlugin` | 在 menu-bar 之前（桌面 Z 序底） |

## 7. Implementation Sequence

1. 建包 shell-desktop（csproj 依赖 kernel/shell-core/shell-app-source）。
2. `DesktopWallpaperNative`：注册表读壁纸路径+样式 → ImageBrush（cairoshell 映射表）。
3. `DesktopWindow`：全屏定位（MVP=主屏）+ 壁纸背景。
4. `DesktopIconsControl`：枚举 + 图标提取（IconScheduler）+ 网格 + 双击启动。
5. `DesktopPlugin`：装配 + `ToggleDesktopIcons(false/true)` + Bootstrap 注册。
6. 构建验证 + 实机。

## 8. Test Strategy

- 编译 0 警告 0 错误；实机验收：壁纸与系统一致、图标显示/双击启动、退出后原桌面图标还原。

## 9. Risk and Impact Analysis

- **Z 序**：桌面窗口须在所有普通窗口之下；WPF 无直接"贴桌面"——用最早创建 + Topmost=false；若有窗口压桌问题 → P2 用 Progman child（cairoshell AllowProgmanChild 模式）。
- **explorer 图标隐藏**：ToggleDesktopIcons 即刻生效；退出必须还原。
- **性能**：壁纸 BitmapImage Freeze；图标提取走 IconScheduler 后台。

## 11. Reusable Implementation Context

- 壁纸注册表映射：`WallpaperStyle`/`TileWallpaper` 组合 → 样式（cairoshell Desktop.xaml.cs:400-426 [verified]）。
- 图标提取：`IconHelper.GetIconByFilename(path, IconSize.ExtraLarge)` @ IconScheduler（搜索面板同款管线）。
- `.lnk` 解析：`ShellLinkResolver.Resolve(path)`（app-source）。

## 12. Assumptions and Open Questions

- [assumed] MVP 主屏单桌面；多屏/拖动定位/框选/自动排列/完整 SH 右键菜单 → P2。
- [assumed] 壁纸 MVP 只读系统当前壁纸（不提供换壁纸 UI）。
- [open] 图标右键菜单 MVP 用"打开/刷新"简化菜单，还是先不做右键？

## 13. Definition of Done

- 全屏自绘桌面：壁纸与系统一致（样式映射正确）、explorer 原图标隐藏、桌面图标显示并可双击启动。
- 退出后原桌面完整还原（图标重现）。
- 与菜单栏 AppBar 共存：菜单栏在最上、图标网格避开菜单栏区域。
- 构建 0 警告 0 错误。
