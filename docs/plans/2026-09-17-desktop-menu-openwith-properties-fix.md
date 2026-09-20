# 计划 · 桌面自绘右键菜单修复（打开方式 / 属性）

> 状态：**已落地**（2026-09-17）｜构建 0 警告 0 错误｜shell-context-menu-tests 107/107 绿
> 触发：用户实测「打开方式」子菜单只列应用名无图标、点了不唤起对应应用；「属性」点了完全没反应。

---

## 1. 根因（全部有实证，非推测）

### R1 属性：`SHObjectProperties` 传了非法 `shopObjectType`（stype = 0）

`DesktopIconsControl.ShowProperties` 原本 `_ = SHObjectProperties(hwnd, 0, path, null);`。

本机 SDK 头文件实证（`C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um\ShlObj_core.h:2377-2379`）：

```c
#define SHOP_PRINTERNAME 0x00000001
#define SHOP_FILEPATH    0x00000002   /* ← 文件路径要用这个 */
#define SHOP_VOLUMEGUID  0x00000004
```

`0` 不是任何合法值 → SHObjectProperties **不抛异常、不弹窗，直接返回 FALSE**；
外层 `catch{}`（M10 静默）把返回值也丢了 → 用户侧恒为"点了完全没反应"。

**修复（双路）**：主路径改用 shell 原生 `properties` 动词（与 explorer 右键「属性」同源，也与
`AppEntryActions.ShowProperties`（dock / 应用提取器）同一实现口径）；失败才退回
`SHObjectProperties`（**stype 改 `SHOP_FILEPATH = 0x2`**）。两条路的失败都落 `DiagnosticLog`。

### R2 打开方式：丢弃了注册表命令行的参数，只留 exe

`ToolCatalog` 解析候选时用 `ExtractExeFromCommand(cmd)` 只取 exe，然后**一律**填 `"%file%"`。
但真实 `shell\open\command` 常常必须带参数才认文件（本机注册表实读）：

| 候选 | 注册表原样命令 |
|------|----------------|
| WPS Office | `"...\wps.exe" /prometheus /pdf "%1"` |
| Microsoft Edge | `"...\msedge.exe" --single-argument %1` |
| Google Chrome | `"...\chrome.exe" --single-argument %1` |
| Quark | `"...\quark.exe" --brand-clouddrive "%1"` |
| Okular | `"...\okular.exe" -- "%1"` |

丢掉 `--single-argument` / `/pdf` / `--brand-clouddrive` 后，Windows 传给应用的命令行不是它认得的
形态 → 起来是空窗口或直接退出 = "点了没反应"。

**修复**：`ToolCatalog.SplitCommandLine` 改为拆出 `(exe, 参数模板)` 并把**注册表原样尾部**带回；
新增 `ToolCatalog.BuildLaunchArgs` 做占位符替换（`%1/%L/%V/%*/%file%` → 带引号文件路径、
`%w/%dir%` → 目录）。模板里没有占位符时按 shell 语义补 `%1`。

### R3（顺带）菜单贴边：底部菜单项可能落在屏幕外 = "点不到"

`DesktopMenuPopup` 用 `PlacementMode.AbsolutePoint` + 光标坐标定位，**完全不做工作区约束**。
19 项菜单（约 500 DIP）在屏幕下缘右键时会向下溢出，底部项（属性 / 解压到…）肉眼不可见也点不到——
这会把 R1 的症状进一步放大成"这两个功能都不能用"。

**修复**：构建期预测量（`Measure`）→ 越界则贴边收敛（右侧向左收、下方翻到光标上方）→
打开后按真实 `ActualWidth/Height` 复核一次。工作区经 `MonitorFromPoint/GetMonitorInfo` 取，
用触发控件的 `CompositionTarget` 做物理↔逻辑换算（多屏 / PerMonitorV2 正确）。

### R4 用户明确诉求：候选没有应用图标

`MenuItemDef` 只有 `IconKey`（主题图标键，渲染层从未使用），**没有可渲染的图像字段**。
现新增 `MenuItemDef.Icon`（`ImageSource?`，必须 Freeze），`DesktopMenuPopup` 按 16px 图标槽渲染；
图标由新增的 `MenuIconProvider` 解析（复用本包既有 ManagedShell `IconHelper` 通道，
`IconSize.Small`，进程级缓存 + Freeze）。「打开」项同时给条目自身的文件 / 夹图标。

---

## 2. 交付清单

| 文件 | 改动 |
|------|------|
| `packages/api/ContextMenu/MenuItemDef.cs` | 新增 `Icon`（ImageSource?，已 Freeze 契约） |
| `packages/shell/shell-context-menu/Services/ToolCatalog.cs` | `SplitCommandLine`（拆 exe + 参数模板）、`BuildLaunchArgs`（占位符替换）、候选带真实模板 |
| `packages/shell/shell-desktop/Services/MenuIconProvider.cs` | **新增**：16px 图标解析 + 缓存（失败返回 null，不占位） |
| `packages/shell/shell-desktop/Services/DesktopMenuPopup.cs` | 渲染 `MenuItem.Icon`；贴边收敛 + 打开后复核 |
| `packages/shell/shell-desktop/Controls/DesktopIconsControl.cs` | 打开方式：图标 + 真实模板唤起 + 「选择其他应用…」+ 失败全留日志 + WindowsApps 打包应用改走 openas；属性：双路修复；打开项图标 |
| `packages/shell/shell-context-menu-tests/OpenWithLaunchTests.cs` | **新增** 11 例：占位符替换 / 命令行拆分 / 候选带回注册表模板 |

「选择其他应用…」= shell 的 `openas` 动词（与 dock 的「打开方式…」同实现）：候选表只认识注册表里
登记过的应用，用户想用的程序不在表里时必须有一条路走到系统完整选择框——这是"确保都能唤起"的保底。

---

## 3. 验证

| 项 | 手段 | 结果 |
|----|------|------|
| 命令行替换旧缺陷（`""path""`） | 单测 `BuildArgs_QuotedPlaceholder_DoesNotDoubleQuote` | ✅ |
| 注册表参数不丢（`--single-argument` 等） | 单测 `SplitCommandLine_*` / `MatchOpenWith_ReturnsRegisteredCommandTemplate`（临时 ProgId + 唯一临时扩展名，测后删键） | ✅ |
| 8 个候选都能取到图标 | 反射直调 ManagedShell `IconHelper.GetIconByFilename(_, Small)` 逐一实测（含 MSIX 的 Firefox 路径） | ✅ 8/8 非零 |
| `SHOP_FILEPATH` 常量 | 本机 Windows SDK 头文件原文核对 | ✅ = 0x2 |
| 构建 | `dotnet build host/BetterDesktop.Host.csproj -c Debug` | ✅ 0 警告 0 错误（输出到 `host\bin\Debug\…`，即用户实际运行的目录） |
| 单测 | `dotnet test shell-context-menu-tests` | ✅ 107/107 |
| 真机走查（人工） | 桌面右键文件 → 打开方式（看图标 / 逐个点）→ 属性 / Alt+Enter | ⏳ 待用户确认 |

新增诊断日志（排查入口，`%LocalAppData%\BetterDesktop\logs\host-*.log`）：

- `打开方式候选 N 项，图标命中 M 项`——一眼区分"候选没图标"与"渲染没画"。
- `打开方式：<exe> <实际命令行>`——成功也记，可直接比对参数是否正确。
- `打开方式失败（exe，args=…）: <异常> → 回退…`——不再静默。
- `属性（shell properties 动词）失败 … → 回退 SHObjectProperties` / `属性（SHObjectProperties）返回失败 …`。

---

## 4. 第二轮：WPS 专项（用户复测：仅 WPS 仍"没反应"）

### 4.1 五路对照实验（全部失败，含 explorer 自己）

拿一份自建探测 PDF，分别用 5 种方式启动并逐窗口取证：

| 路线 | 结果 |
|------|------|
| ① `wps.exe /prometheus /pdf "f"`（我们的做法） | 文档**已打开**，窗口 `OpusApp` 标题 = 该文件名，但 `IsWindowVisible = False` |
| ② `wps.exe /pdf "f"`（去掉 /prometheus） | 同上 |
| ③ `wpspdf.exe /pdf "f"`（PDF 组件直启） | 同上 |
| ④ `ShellExecute` 默认关联（= 双击） | 同上 |
| ⑤ `explorer.exe "f"` | 同上 |

**结论：不是启动方式的问题——WPS 真的把文档打开了，只是把窗口留在不可见状态。**
窗口 style 实测 `0x060F0000`（缺 `WS_VISIBLE` 位），`ShowWindow(SW_SHOW)` 后变 `0x160F0000`、`IsWindowVisible=True`、
文档立即可见。机理：WPS 的 `/prometheus` 是**单实例交接**（`AppUserModelID=Kingsoft.Office.KPrometheus`，
子进程 `wpspdf.exe` 带 `--prome-pipe-token=kprometheus.<pid>…` 走内部管道），新文档交接到它**已运行的那个隐藏实例**
（该实例 `wps.exe` 主窗口不存在/不可见）里 → 用户侧就是"点了没反应"。这是 WPS 自身状态，五条路都一样。

### 4.2 修法：启动后把文档窗口叫出来（`LaunchedWindowPresenter`）

`packages/shell/shell-desktop/Services/LaunchedWindowPresenter.cs`（新增，internal）：

- **启动前** `GrantForegroundToNextApp()`（`AllowSetForegroundWindow(ASFW_ANY)`，best-effort）——
  否则 Windows 前台锁会让新窗口只在任务栏闪一下。
- **启动后** `EnsurePresented(appPathHint, filePath)`：后台线程 ≤10s（400ms/次，命中即退）枚举顶层窗口，
  命中条件收得很窄，只对**未显示（或最小化）**的窗口动手：
  ① `GetAncestor(GA_ROOT) == self`（顶层）；② 排除 `WS_EX_TOOLWINDOW`（工具窗/浮动条）；
  ③ 标题含本次文件名（完整名，或 ≥3 字符的主名）；④ 给了应用路径时还要求窗口所属进程名 == 该 exe 名。
  命中且未显示 → `ShowWindow(SW_SHOW / SW_RESTORE)` + `SetForegroundWindow` 并记日志。
  **已经可见的窗口一律不碰**（Edge/Chrome 等正常应用零影响，只做一次枚举就退）。
- 接线：`LaunchWithApp`（打开方式）与 `StartFile`（双击/打开）两条"用户要开文件"的路径都接上。

不是硬编码判 WPS，而是"交接给隐藏实例"这一类应用（WPS / 部分 Office / Adobe 系）通用；
WPS 只是本次实证对象（该类文件头完整记录了实验数据与命中条件）。

### 4.3 WPS 专项验证

| 项 | 手段 | 结果 |
|----|------|------|
| WPS 窗口确实被"开了但不显示" | 5 路对照 + 逐窗口 `IsWindowVisible`/`GetWindowLong(GWL_STYLE)` 取证 | ✅ style 0x060F0000 → 缺 WS_VISIBLE |
| 命中条件对真实 WPS 主窗成立 | 实测 `OpusApp`：`isRoot=True`、`exstyle=0x100`（无 TOOLWINDOW）、进程名 `wps`、标题含文件名 | ✅ 四条全中 |
| **生产代码**能否自己救回 | 人为 `SW_HIDE` 复现故障态 → 反射直调 `LaunchedWindowPresenter.EnsurePresented(wps.exe, 同名文件)` | ✅ **1 秒内窗口自动恢复可见**（脚本 finally 兜底恢复，跑完状态无损） |
| 构建 | `dotnet build host/BetterDesktop.Host.csproj -c Debug` | ✅ 0 警告 0 错误 |

新增日志：`启动后置前：应用未显示文档窗口（iconic=…）→ 已 ShowWindow+置前 hwnd=… 匹配='…'`。
