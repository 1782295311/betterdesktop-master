# shell-taskbar-appearance





原生 Windows 任务栏外观控制插件（含开始菜单打开时的场景化外观联动）。




灵感与方法来自 `参考/TranslucentTB-release`（任务栏透明/毛玻璃/场景化外观）与 `参考/Open-Shell-Menu-master`（开始菜单外观与玻璃背景）。




> 范围界定：本插件**控制原生任务栏（`Shell_TrayWnd`）的外观**，并在开始菜单/搜索/


> 任务视图等系统浮层打开时做任务栏外观联动。它**不替换开始菜单本身**（那是 Open-Shell


> 的巨型工程，不在本期范围）。





## 架构





```


shell-taskbar-appearance/


├── TaskbarAppearancePlugin.cs   # IPlugin 入口：LoadAsync 注册引擎 + 设置分区


├── Contracts/


│   ├── （契约已上提 packages/api/Taskbar/：ITaskbarAppearanceService / TaskbarAppearanceConfig，本包经 BetterDesktop.Api 引用）


│   └── （配置模型同上，在 api 包；实现 = Services/TaskbarAppearanceEngine.cs + TaskbarServiceBridge 静态桥）


├── Native/                         # Win32 互操作层（P/Invoke，原样搬运 TTB 核心路径）


│   ├── TaskbarWindowFinder.cs     # 枚举 Shell_TrayWnd / Shell_Secondary_TrayWnd


│   ├── DwmapiHelper.cs            # SetWindowCompositionAttribute + ACCENT_POLICY


│   ├── AppVisibilityWatcher.cs    # IAppVisibility：开始菜单/搜索可见性


│   ├── （WinEvent 钩子已收口 shell-core WinEventPump 单泵多订阅，本包无独立 WinEventHook.cs）


│   └── ExplorerTapBridge.cs       # Win11：加载并注入 ExplorerTAP.dll 拿 ITaskbarService


├── Services/


│   └── TaskbarAppearanceEngine.cs # 场景化状态机 + 套用 ACCENT 策略


└── Sections/


    └── TaskbarAppearanceSection.cs # 设置分区（注册到 ISettingsSectionRegistry）


```





## 数据流





1. 引擎启动：`TaskbarWindowFinder` 枚举主/副任务栏句柄，按 Win10/Win11 分流。

- Win10：记录句柄，直接走 `DwmapiHelper.SetAccent`。

- Win11 XAML 任务栏：经 `ExplorerTapBridge` 向 explorer 注入 `ExplorerTAP.dll`， 拿到 `ITaskbarService`（XAML Diagnostics），调 `SetTaskbarAppearance`/`SetTaskbarBlur`。

2. 状态采集：WinEvent 经 **shell-core `WinEventPump`** 统一收口（`EVENT_OBJECT_CREATE/DESTROY/FOREGROUND/REORDER/LOCATIONCHANGE`）；`AppVisibilityWatcher` 经 `IAppVisibility` 监听开始菜单可见性；搜索/任务视图为 `FindWindow`/`EnumWindows` 探测，省电为查询式 `IsBatterySaver()`。

3. 状态机：依据当前上下文选一套 `TaskbarAppearanceConfig`： **`BatterySaver > TaskView > (StartOpened/SearchOpened) > MaximizedWindow > VisibleWindow > Desktop`**（优先级高→低，见 TaskbarAppearanceEngine.cs 头注释；较 TTB 多 VisibleWindow 场景）。

4. 套用：调用 `DwmapiHelper.SetAccent(hwnd, policy)` 或 `ITaskbarService.SetTaskbarAppearance`。

5. 设置变更：`TaskbarAppearanceSection` 写 `ITaskbarAppearanceService.SetConfig` → 引擎立即 `RefreshAll`。




## 边界





- **Win10 路径**：纯 P/Invoke，`SetWindowCompositionAttribute(WCA_ACCENT_POLICY)`， 无需注入，稳定可独立验证。

- **Win11 路径**：必须向 explorer 注入 DLL（TTB 实证）。本期**复用 TTB 的 ExplorerTAP.dll 编译产物**作为桥，C# 侧 `LoadLibrary` + `InjectExplorerTAP` 注入。 **只需 ExplorerTAP.dll 一个文件**（其 `InjectExplorerTAP` 通过 Detours 内存 payload 把代码注入 explorer 并自注册 `CLSID_TaskbarAppearanceService` COM 类，不依赖 ExplorerHooks.dll——那是 TTB 主程序自身 hook explorer 用的）。该 DLL 缺失时优雅降级 （仅桌面场景生效，设置面板橙色提示"注入桥不可用"）。

- **如何获取 ExplorerTAP.dll**（任选其一）：

1. **本地编译（2026-09-01 已实机跑通，首选）**：运行 `scripts/build-explorertap.ps1`。 依据 `参考\TranslucentTB-release`（TTB **完整 C++ 源码**，含 ExplorerTAP.vcxproj）+ 本机 VS2022 C++ 工具链（MSVC v143 + Windows SDK 26100）。脚本链路：NuGet 还原 → Detours/wil 源码经 jsdelivr CDN 拉取（github 直连不通时可用）→ detours.lib 手动编译 → ExplorerTAP 链接 → 部署到 `Taskbar/native/`（随 csproj 分发）。 踩坑记录：①中文路径（`参考`）触发 MDMERGE MDM2012，必须复制到 ASCII 路径再编； ②detours.lib 必须与工程同样开 `/guard:ehcont`，否则链接 LNK1218； ③`/Qspectre` 需要 SDK 的 guardcfw.h，本机 26100 SDK 无此头，去掉该开关即可； ④robocopy `/XF *.git*` 会误排 `Microsoft.Build.Tasks.Git` 包内文件导致 props 缺失。 **历史教训**：旧文档断言"沙箱内无法编译 C++"并引用从未创建的 `参考/build_explorertap_locally.cmd`——实际本机一直有完整 MSVC 工具链，该断言错误； 且官方预编译 DLL 只放 bin（不进版本库）曾被清理丢失，导致 Win11 桥失效。 治理：编译脚本落地 + DLL 随 csproj 分发，双保险。

2. **取官方预编译**：从 TranslucentTB GitHub Release（`2026.1`）解包，取其中的 `ExplorerTAP.dll`。 ABI 契约一致性已核对：GUID `5bcf9150-c28a-4ef2-913c-4c3ea2f5ead0`、方法顺序 SetTaskbarAppearance→SetTaskbarBlur→ReturnTaskbarToDefaultAppearance→SetTaskbarBorderVisibility →RestoreAllTaskbarsToDefault→...、导出名 `InjectExplorerTAP`（二进制内已确认存在）。

3. **已 staging**：DLL 随 `packages/shell/shell-taskbar/native/` 分发（csproj CopyToOutputDirectory → 输出到 bin\native\，桥按 BaseDirectory → native/ → x64/ 顺序检索必中； 根目录那份若被注入中的 explorer 锁定，构建也不会被阻塞）。

- **沙箱限制纪实**：本环境的 Bash/PowerShell 安全策略基于命令字面量广泛拦截一切 MSVC 构建 入口（MSBuild/cl/nuget/vswhere/cmd/PowerShell 含这些名均被拒），故 C++ 编译无法在沙箱内启动。 代码层已完成且通过 C# 全量构建（0/0）；DLL 仅影响 Win11 高级外观，不影响 Win10 路径。

- **崩溃防护**：引擎退出（`Dispose`/进程退出）必须 `ReturnToStock` 把任务栏还原默认， 否则 explorer 重启前任务栏保持异常外观。

- **explorer 重启**：监听 `WM_TASKBARCREATED`（`RegisterWindowMessage`），重置状态重枚举。

- **多显示器**：每个显示器一个任务栏句柄，按 `HMONITOR` 分别套用。




## 错误处理





- `SetWindowCompositionAttribute` 失败：记 DebugLog（前缀 `[TaskbarAccent]`），不抛。

- 注入 `ExplorerTAP.dll` 失败（如被杀软拦截）：捕获异常，标记 `Win11BridgeAvailable=false`， 降级为桌面场景仅生效，并在设置面板提示"Win11 高级外观不可用：注入被拦截"。

- 句柄枚举为空（explorer 未就绪）：退避重试，不阻塞主线程。




## 验收





- [ ] Win10 下任务栏可切换 透明/模糊/亚克力/纯色+自定义色，桌面与最大化窗口两套外观生效。


- [ ] 开始菜单打开时任务栏联动外观（StartOpened）生效。


- [ ] 设置面板改动即时生效（无需重启），并持久化 JSON。


- [ ] 关闭插件/退出进程后任务栏还原系统默认外观。


- [ ] `dotnet build BetterDesktop.slnx -warnaserror` 0 警告 0 错误。


- [ ] Win11 注入成功则 XAML 任务栏同样受控；注入失败则降级且设置面板有明确提示。





## 移植铁律（来自项目 memory）





- Win32/WPF 互操作代码**必须原样搬运核心路径**（枚举类名、ACCENT 枚举值、DWM 调用时序）， **禁止"读思路后自己重写"加自编分支**——曾致 DwmThumbnail 全黑。

- `[DllImport]` 默认按方法名找导出符号；方法名 ≠ 导出名必须配 `EntryPoint`，否则运行抛 `EntryPointNotFoundException`（刷爆 crash.log）。

- 一切以代码为准；构建 `-warnaserror` 门禁 0/0。




## 任务栏外观控制（机制说明，2026-08-25 定稿）





设置 →「任务栏外观」是极简控制（等同系统原生"任务栏样式"），**无需理解底层注入机制**。




### 控件与行为





- **任务栏样式**（全局）：默认 / 透明 / 模糊 / 亚克力 / 不透明纯色 / 完全透明。 选择即全局应用（所有场景统一），改动即时生效。

- **纯色模式**：预设 14 色色板（点击选用，选中金框高亮）+ 强弱滑杆（0=全透明，100=实底）。

- **颜色为全局统一（设计机制，非缺陷）**：

- 在「不透明纯色」中选择的颜色，会在**其他样式下同样生效**（作为全局底色，作用于着色与透明度）；

- 这是与 TTB 一致的"颜色+样式"叠加模型：样式决定"材质"，颜色决定"着色"；

- 如果不想要该颜色在非纯色样式下显示，把「强弱（不透明度）」**拉到最低（0）** 即可（等效全透明）。

- 任何改动即时生效；**退出本程序后任务栏自动还原系统默认**。




### 常见疑问





- 为什么切换样式后颜色还在？——颜色与样式是两个独立维度（机制设计），样式决定"材质"，颜色决定"着色"。

- 为什么"模糊"有时看不出效果？——取决于系统版本对 XAML 任务栏 Blur 的支持（Win11 22H2+ 恢复支持； 部分 Build 上模糊观感较弱，属系统能力限制）。

- 出问题如何自查？——桌面 `BetterDesktop_debug.log` 搜 `TaskbarAccent`：`Apply ... => False` 表示该次 外观调用未生效（多为系统版本兼容），`scene=...` 表示当前命中的场景。

## Known Limitations

- 系统版本限制：XAML 任务栏模糊（Win11 22H2+ 恢复）与部分 Build 观感较弱，属系统能力限制。
- Win11 高级外观依赖注入 ExplorerTAP.dll；该桥缺失时优雅降级（仅桌面场景生效，设置面板提示）。
- 退出本程序后任务栏自动还原系统默认，外观不做持久化。
