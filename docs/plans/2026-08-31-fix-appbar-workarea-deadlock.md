# 计划：修复 AppBar 工作区死锁（桌面/菜单栏被压到屏幕底部）

- 日期：2026-08-31
- 类别：本地 Bug 修复（窄深度 · compact）
- 目标仓库：better-desktop-cordis（host 编译 `dotnet build host/BetterDesktop.Host.csproj -c Debug -r win-x64`）

## 1. 现象与实机诊断（[verified] 全部来自本机实测）

| # | 事实 | 来源 |
|---|---|---|
| F1 | `BetterDesktop.Desktop` 已成功嵌入 Progman，矩形 `(0,0)-(2048,1151)` 正确 | EnumChildWindows(Progman) |
| F2 | **WorkingArea = (0,1100) 2048x4**（顶部被"占用"1100px） | SystemInformation.WorkingArea |
| F3 | `BetterDesktop.MenuBar` 在**底部** `(0,1100)-(2048,1120)`；Dock 也在底部 | EnumWindows |
| F4 | `Reposition()` 直接 `Top = PrimaryWorkArea.Top`（=`SystemParameters.WorkArea`） | MenuBarWindow.cs:147 |
| F5 | 全库唯一 SHAppBarMessage 调用 = `AppBarReservation`（edge=Top），由 MenuBarWindow 注册 | 全库搜索 |
| F6 | 23:31 AppHangTransient（程序挂死，被用户手动关闭） | 事件日志 |
| F7 | 用户环境跑 Wallpaper Engine（`WPELiveWallpaper` + 多个 WorkerW，挂载点选择不受影响） | Progman 子窗口枚举 |

## 2. 根因（因果链）

**AppBar 注册与窗口定位因果倒置 + ABN_POSCHANGED 死循环**：

```
某时刻菜单栏矩形错乱(高≈1100) → ApplyPos 传该 rc → ABM_SETPOS edge=Top 保留高度
→ 顶部被占 1100px → WorkingArea=(0,1100) 4px
→ ABN_POSCHANGED → Reposition() 盲读 WorkArea.Top=1100 → 菜单栏跑到底部
→ ApplyPos 再传底部矩形 → 工作区再次震荡 → ABN_POSCHANGED → ……
→ (a) 菜单栏永远回不到顶部（视觉：所有东西压到底部）
   (b) SETPOS→POSCHANGED→SETPOS 高频震荡 → UI 线程饿死 → AppHang [inferred，解释 F6]
```

关键设计错误：**AppBar 自己就是工作区的定义者，却用 WorkingArea 来给自己定位**——循环依赖。
桌面窗口本体（F1）无辜；用户看到的"桌面往屏幕底下压缩"= 坏工作区把 AppBar 族窗口全部压底 +
Maximized 时期桌面窗口也曾被坏 WorkingArea 压缩的历史观感。

## 3. cairoshell 对照（[verified] 源码已读）

- cairoshell `AppBarWindow`（ManagedShell）：**ABM_QUERYPOS 返回的系统协商 rc 直接用于 SetWindowPos 定位窗口**（先协商、后定位、窗口矩形≡声明矩形恒等），从不读 WorkArea 定位 AppBar 自身。
- 桌面窗口 `Desktop.setSize/setGridPosition` 用 `VirtualScreen`（窗口）与 `GetUsableDesktopRect`（内容网格），与本仓库当前桌面实现一致。

## 4. 改动方案（3 处，全部在 shell-menu-bar）

### 4.1 `AppBarReservation.ApplyPos` → 返回协商矩形
- `ABM_QUERYPOS` 后系统已调整 `data.rc` → **返回该 rc**（新增 `TryApplyPos(hwnd, out Rect agreed)`）
- 调用方用协商 rc `SetWindowPos` 窗口 → 保证窗口矩形 ≡ AppBar 声明矩形

### 4.2 `MenuBarWindow.Reposition` 脱离 WorkArea 循环依赖
- 菜单栏的正确位置 = 屏幕顶部贴边（`x=主屏左, y=主屏顶, w=主屏宽, h=MenuBarHeight`）
- 数据源改为主屏边界（`Screen.PrimaryScreen.Bounds` 物理像素 / 或 SystemParameters.PrimaryScreenWidth 逻辑单位——与现有 DPI 约定一致），**不再读 `SystemParameters.WorkArea`**
- 宽度仍随主屏；顶部固定（AppBar edge=Top 的语义就是贴顶）

### 4.3 断开 ABN_POSCHANGED 震荡
- WndProc 收 `ABN_POSCHANGED` → 先 `Reposition()` 到**固定顶部**（4.2）→ `ApplyPos`
- `ApplyPos` 协商 rc 与当前窗口矩形一致时**跳过 SetWindowPos**（无变化不触发新的事件）→ 循环自然终止

## 5. 不动的东西（防回归）

- `DesktopWindow`（Progman 嵌入已验证正确，F1）——**不改**
- `DesktopIconsControl` / `FolderBrowserWindow` / `FolderToolbar`——不改
- `ShellWindow` 基类——不改

## 6. 验收标准

1. 启动后：菜单栏在**屏幕顶部**（贴顶条带）；WorkingArea 恢复 ≈ `(0,菜单栏高)` 到底部任务栏上沿
2. Dock 位置正常；桌面图标从顶部瀑布排列
3. 连续运行 60s：无 AppHang、CPU 稳定（无 POSCHANGED 震荡）
4. 重启 host：菜单栏仍归位顶部（坏状态被正确协商覆盖）

## 7. 开放问题（§12）

- [assumed] F6 AppHang 归因于 POSCHANGED 震荡——修复后连续运行观察验证
- [assumed] 顶部 1100px 残留的精确首次成因（历史某次错位 ApplyPos）——修复后由验收 1/4 覆盖，不再深挖
