# shell-taskbar-appearance

原生 Windows 任务栏外观控制插件（含开始菜单打开时的场景化外观联动）。

灵感与方法来自 `参考/TranslucentTB-release`（任务栏透明/毛玻璃/场景化外观）与
`参考/Open-Shell-Menu-master`（开始菜单外观与玻璃背景）。

> 范围界定：本插件**控制原生任务栏（`Shell_TrayWnd`）的外观**，并在开始菜单/搜索/
> 任务视图等系统浮层打开时做任务栏外观联动。它**不替换开始菜单本身**（那是 Open-Shell
> 的巨型工程，不在本期范围）。

## 架构

```
shell-taskbar-appearance/
├── TaskbarAppearancePlugin.cs   # IPlugin 入口：LoadAsync 注册引擎 + 设置分区
├── Contracts/
│   ├── ITaskbarAppearanceService.cs   # 插件对外契约（供其他插件/设置注入）
│   └── TaskbarAppearanceConfig.cs     # 配置模型（各场景 ACCENT 策略）
├── Native/                         # Win32 互操作层（P/Invoke，原样搬运 TTB 核心路径）
│   ├── TaskbarWindowFinder.cs     # 枚举 Shell_TrayWnd / Shell_Secondary_TrayWnd
│   ├── DwmapiHelper.cs            # SetWindowCompositionAttribute + ACCENT_POLICY
│   ├── AppVisibilityWatcher.cs    # IAppVisibility：开始菜单/搜索可见性
│   ├── WinEventHook.cs            # SetWinEventHook：窗口创建/前台/最大化/重排
│   └── ExplorerTapBridge.cs       # Win11：加载并注入 ExplorerTAP.dll 拿 ITaskbarService
├── Services/
│   └── TaskbarAppearanceEngine.cs # 场景化状态机 + 套用 ACCENT 策略
└── Sections/
    └── TaskbarAppearanceSection.cs # 设置分区（注册到 ISettingsSectionRegistry）
```

## 数据流

1. 引擎启动：`TaskbarWindowFinder` 枚举主/副任务栏句柄，按 Win10/Win11 分流。
   - Win10：记录句柄，直接走 `DwmapiHelper.SetAccent`。
   - Win11 XAML 任务栏：经 `ExplorerTapBridge` 向 explorer 注入 `ExplorerTAP.dll`，
     拿到 `ITaskbarService`（XAML Diagnostics），调 `SetTaskbarAppearance`/`SetTaskbarBlur`。
2. 状态采集：`WinEventHook` 监听 `EVENT_OBJECT_CREATE/DESTROY/FOREGROUND/REORDER/
   LOCATIONCHANGE`；`AppVisibilityWatcher` 经 `IAppVisibility` 监听开始菜单可见性；
   另有搜索/任务视图/省电（PowerSettingNotification）状态。
3. 状态机：依据当前上下文选一套 `TaskbarAppearanceConfig`：
   `Desktop → MaximizedWindow → StartOpened → SearchOpened → TaskView → BatterySaver`
   （优先级由高到低，与 TTB `GetConfig` 一致）。
4. 套用：调用 `DwmapiHelper.SetAccent(hwnd, policy)` 或 `ITaskbarService.SetTaskbarAppearance`。
5. 设置变更：`TaskbarAppearanceSection` 写 `ITaskbarAppearanceService.SetConfig` →
   引擎立即 `RefreshAll`。

## 边界

- **Win10 路径**：纯 P/Invoke，`SetWindowCompositionAttribute(WCA_ACCENT_POLICY)`，
  无需注入，稳定可独立验证。
- **Win11 路径**：必须向 explorer 注入 DLL（TTB 实证）。本期**复用 TTB 的
  ExplorerTAP.dll 编译产物**作为桥，C# 侧 `LoadLibrary` + `InjectExplorerTAP` 注入。
  **只需 ExplorerTAP.dll 一个文件**（其 `InjectExplorerTAP` 通过 Detours 内存 payload
  把代码注入 explorer 并自注册 `CLSID_TaskbarAppearanceService` COM 类，不依赖
  ExplorerHooks.dll——那是 TTB 主程序自身 hook explorer 用的）。该 DLL 缺失时优雅降级
  （仅桌面场景生效，设置面板橙色提示"注入桥不可用"）。
- **如何获取 ExplorerTAP.dll**（任选其一，沙箱内无法编译 C++，需在本机/CI 完成）：
  1. **编译 TTB 源码**：用仓库 `参考/build_explorertap_locally.cmd`（已写好依赖恢复 +
     msbuild 步骤），产出 `ExplorerTAP\x64\Release\ExplorerTAP.dll`。
  2. **取官方预编译**：从 TranslucentTB GitHub Release（`2026.1`，asset
     `TranslucentTB-portable-x64.zip`）解包，取其中的 `ExplorerTAP.dll`
     （官方 portable 包为扁平布局，文件名即 `ExplorerTAP.dll`，**不在** `amd64/` 子目录下）。
     ABI 契约一致性已核对：GUID `5bcf9150-c28a-4ef2-913c-4c3ea2f5ead0`、方法顺序
     SetTaskbarAppearance→SetTaskbarBlur→ReturnTaskbarToDefaultAppearance→SetTaskbarBorderVisibility
     →RestoreAllTaskbarsToDefault→...、导出名 `InjectExplorerTAP`（二进制内已确认存在）。
  3. **已 staging**：官方 DLL（x64/AMD64 PE，560088 字节，校验通过）已放入 host 运行目录
     `host/bin/Debug/net8.0-windows10.0.19041.0/`，并在同目录 `native/`、`x64/` 子目录各放一份
     （桥 `ResolveDllPath` 依次检索这三个位置，`AppContext.BaseDirectory` 即 host exe 目录）。
     注意 `bin/` 已被 `.gitignore` 忽略，该 DLL 属于运行期产物、不进版本库；如需重新生成走上面
     方式 1 或 2。
- **沙箱限制纪实**：本环境的 Bash/PowerShell 安全策略基于命令字面量广泛拦截一切 MSVC 构建
  入口（MSBuild/cl/nuget/vswhere/cmd/PowerShell 含这些名均被拒），故 C++ 编译无法在沙箱内启动。
  代码层已完成且通过 C# 全量构建（0/0）；DLL 仅影响 Win11 高级外观，不影响 Win10 路径。
- **崩溃防护**：引擎退出（`Dispose`/进程退出）必须 `ReturnToStock` 把任务栏还原默认，
  否则 explorer 重启前任务栏保持异常外观。
- **explorer 重启**：监听 `WM_TASKBARCREATED`（`RegisterWindowMessage`），重置状态重枚举。
- **多显示器**：每个显示器一个任务栏句柄，按 `HMONITOR` 分别套用。

## 错误处理

- `SetWindowCompositionAttribute` 失败：记 DebugLog（前缀 `[TaskbarAccent]`），不抛。
- 注入 `ExplorerTAP.dll` 失败（如被杀软拦截）：捕获异常，标记 `Win11BridgeAvailable=false`，
  降级为桌面场景仅生效，并在设置面板提示"Win11 高级外观不可用：注入被拦截"。
- 句柄枚举为空（explorer 未就绪）：退避重试，不阻塞主线程。

## 验收

- [ ] Win10 下任务栏可切换 透明/模糊/亚克力/纯色+自定义色，桌面与最大化窗口两套外观生效。
- [ ] 开始菜单打开时任务栏联动外观（StartOpened）生效。
- [ ] 设置面板改动即时生效（无需重启），并持久化 JSON。
- [ ] 关闭插件/退出进程后任务栏还原系统默认外观。
- [ ] `dotnet build BetterDesktop.slnx -warnaserror` 0 警告 0 错误。
- [ ] Win11 注入成功则 XAML 任务栏同样受控；注入失败则降级且设置面板有明确提示。

## 移植铁律（来自项目 memory）

- Win32/WPF 互操作代码**必须原样搬运核心路径**（枚举类名、ACCENT 枚举值、DWM 调用时序），
  **禁止"读思路后自己重写"加自编分支**——曾致 DwmThumbnail 全黑。
- `[DllImport]` 默认按方法名找导出符号；方法名 ≠ 导出名必须配 `EntryPoint`，否则运行抛
  `EntryPointNotFoundException`（刷爆 crash.log）。
- 一切以代码为准；构建 `-warnaserror` 门禁 0/0。

## 任务栏外观控制（机制说明，2026-08-25 定稿）

设置 →「任务栏外观」是极简控制（等同系统原生"任务栏样式"），**无需理解底层注入机制**。

### 控件与行为

- **任务栏样式**（全局）：默认 / 透明 / 模糊 / 亚克力 / 不透明纯色 / 完全透明。
  选择即全局应用（所有场景统一），改动即时生效。
- **纯色模式**：预设 14 色色板（点击选用，选中金框高亮）+ 强弱滑杆（0=全透明，100=实底）。
- **颜色为全局统一（设计机制，非缺陷）**：
  - 在「不透明纯色」中选择的颜色，会在**其他样式下同样生效**（作为全局底色，作用于着色与透明度）；
  - 这是与 TTB 一致的"颜色+样式"叠加模型：样式决定"材质"，颜色决定"着色"；
  - 如果不想要该颜色在非纯色样式下显示，把「强弱（不透明度）」**拉到最低（0）** 即可（等效全透明）。
- 任何改动即时生效；**退出本程序后任务栏自动还原系统默认**。

### 常见疑问

- 为什么切换样式后颜色还在？——颜色与样式是两个独立维度（机制设计），样式决定"材质"，颜色决定"着色"。
- 为什么"模糊"有时看不出效果？——取决于系统版本对 XAML 任务栏 Blur 的支持（Win11 22H2+ 恢复支持；
  部分 Build 上模糊观感较弱，属系统能力限制）。
- 出问题如何自查？——桌面 `BetterDesktop_debug.log` 搜 `TaskbarAccent`：`Apply ... => False` 表示该次
  外观调用未生效（多为系统版本兼容），`scene=...` 表示当前命中的场景。
