# shell-context-menu（右键菜单插件）

> 状态：**已实现（v1.3 现行态，2026-09-10 同步）**。历史设计稿（MenuService/MenuHost 中央管线、三档呈现、ini 配置）已于 2026-09-05 起陆续退役，本文为现行架构；分场景历史规格见 [`MENU-SPECS.md`](MENU-SPECS.md)（仅作设计存档）。

## 1. 现行定位

本包现在承担三块能力：

1. **系统原生菜单接管**（`NativeMenuPopup`）：文件/目录/开始菜单条目等表面右键 → 经 Shell COM（`ILCreateFromPath → SHBindToParent → GetUIObjectOf(IContextMenu)`）渲染**系统原生菜单**，第三方扩展项完整保留。
2. **菜单管理器**（`MenuManagerService` M1-M3）：设置中心「菜单管理」分区——10 场景 × 静态/ShellEx 双载体枚举、隐藏三选一（负前缀键/影子开关）、新建右键项、Win11 经典样式（`{86ca1aa0}` 键）、注册表备份/恢复（`RegTreeBackup`）、夺权写 HKLM（`RegTakeover`，SeTakeOwnership+SeRestore 顺序红线）。
3. ~~桌面跨进程委托~~（**2026-09-10 整体移除**）：桌面图标/空白右键已全部回归 shell-desktop 本进程自绘（`DesktopMenuPopup`）——跨进程 `WriteProcessMemory`/DefView 转发因稳定性不达标（0xC0000005 GetUIObjectOf 聚合崩溃、菜单类型错乱）废弃，`DesktopMenuDelegation.cs` 已删除，勿复活。

**表面矩阵（最终拍板）**：

| 表面 | 菜单来源 |
|---|---|
| 桌面空白（自绘桌面） | shell-desktop 自绘（`DesktopMenuPopup`；跨进程转发已移除） |
| 桌面图标（自绘桌面） | 图标项自绘 WPF 菜单（`DesktopMenuPopup`） |
| 文件管理器条目 / 文件系统 | `NativeMenuPopup`（系统原生） |
| 开始菜单条目 | 系统原生（`AppItemActions.AttachNative`，UWP 无路径项不弹） |
| Dock 图标 / 应用提取器 | **自研例外**（dock 侧 `DockMenuPopup`，非本包） |

`context-menu.mode` 设置键仅作旧配置兼容保留，不参与现行行为判定。

## 2. 架构（当前真实文件语义）

```
shell-context-menu
├── ContextMenuPlugin              # Inject=[]；Provide<IFileClassifier>；注册 MenuManagerSection
├── Services/
│   ├── NativeMenuPopup.cs         # 原生菜单弹层（28KB）：SHCreateItem→BindToHandler→GetUIObjectOf
│   │                              #   →IContextMenu HMENU→TrackPopupMenuEx(TPM_RETURNCMD)
│   ├── StaComWorker.cs            # 常驻 STA 线程 + Dispatcher 消息泵（跨套间封送/异步派发）
│   ├── HandlerCrashGuard.cs       # 崩溃归因（broker 非 0 退出 → 每 CLSID 连续 3 次计数）
│   ├── HandlerCrashBreaker.cs     # 熔断执行端（达阈值经既有 Toggle 自动停用 + 设置键供 UI 显示）
│   ├── MenuBrokerClient.cs        # 进程外 broker 客户端（stdio JSON；崩溃归因；不可用则可见降级）
│   ├── ShellMenuInterop.cs        # 第三方 handler 查询入口（唯一实现 = 进程外 broker；无 in-proc 副本）
│   ├── （DesktopMenuDelegation.cs 已于 2026-09-10 删除：桌面右键回归 shell-desktop 自绘）
│   ├── MenuManagerService.cs      # 菜单管理器（+ Toggle/Create/Groups/Style/Helpers 五分片）
│   ├── RegTakeover.cs             # HKLM 夺权写（SeTakeOwnership/SeRestore，bool 可感知失败）
│   ├── RegTreeBackup.cs           # 注册表树备份/恢复（%LocalAppData%\BetterDesktop\MenuManager\backup）
│   ├── RegistryVerbs / CommandStore / ExplorerCommandInterop / ResourceRef / MenuAccessKeys / MenuText
│   ├── FileClassifier.cs          # IFileClassifier：SFGAO + 扩展名 + 回收站/快捷方式目标解析
│   └── ToolCatalog / ShellNewCatalog / ZipOps / RecycleRestore   # 右键工具/新建/压缩/回收站
└── Sections/MenuManagerSection.cs # 设置中心「菜单管理」分区
```

## 3. 公共契约

- `IFileClassifier`（Provide 到内核服务图）：文件属性精准识别（`FileKind`/`FileCapabilities`），供 dock/桌面等消费方过滤"无法操作该文件的选项"。
- 已删除：`IMenuService`/`MenuRequest`/`IContextMenuContributor` 中央管线（2026-09-05 退役，各表面自管或走系统原生，勿复活）。
- 保留模型：`MenuItemDef`/`MenuRequest` 等（供桌面自绘菜单等消费方使用）。

## 4. 关键红线（真机踩坑沉淀，勿回退）

1. **菜单 owner 必须是不可见顶层窗口**（WS_POPUP + WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE，从不 Show）——HWND_MESSAGE 消息窗口会让 TrackPopupMenuEx 静默不弹。
2. **模态菜单循环绝不阻塞 UI**：全程 `StaComWorker` 常驻 STA 线程派发（`Begin` 异步，不同步 `Run`）。
3. `IContextMenu3.HandleMenuMsg2` 的 `plResult` 必须持 `AllocHGlobal` 缓冲（传 IntPtr.Zero 被 owner-draw 写入即 AV）；`CMINVOKECOMMANDINFOEX` 结构体必须含 `lpDirectoryW`（尺寸错位 = handler 读指针 AV）。
4. 原生弹层路径用 `ILCreateFromPath → SHBindToParent → GetUIObjectOf`；`BHID_SFUIObject` 真机返回 MK_E_UNAVAILABLE 不可依赖。
5. 注册表操作：`Registry.SetValue` 根名必须全称（HKEY_CURRENT_USER，不认 HKCU 缩写）；负前缀键是 `shellex\` 的兄弟键层级；HKCR 合并视图按进程缓存（枚举弃用 HKCR，改 HKCU+HKLM 直读合并）。
6. 多选跨目录：取首组路径建 `IShellItemArray`，`InvokeCommand` 闭包不持查询期对象（防 use-after-free）。

## 5. 依赖与测试

- 依赖：`BetterDesktop.Kernel`、`BetterDesktop.Api`、`BetterDesktop.Shell.Core`（StaComWorker 基座/诊断）。
- 原生产物：`scripts\build-shellmenu.ps1`（CMake + MSVC，刻意不进 dotnet build 链路）产出 `BetterDesktopShellMenu.dll`（免宿主扩展）与 `BetterDesktopMenuBroker.exe`（进程外 broker），并跑 40 项冒烟 + broker 自检 9 项；未跑原生构建时 csproj 跳过复制，宿主回落 in-proc（不静默失效）。
- 测试：`shell-context-menu-tests` 97 例全绿（注册表语义/负前缀层级/RecycleRestore $I 头/FileClassifier/多选路径集/崩溃计数与熔断/broker 协议往返与降级决策等）。

## Known Limitations

- HKCU 影子开关对 explorer 生效需重启桌面（HKCR 合并视图按进程缓存）。
- Win11 经典样式（`{86ca1aa0}`）只认传统注册扩展，新式（MSIX 稀疏包 + IExplorerCommand）项会消失——样式卡已诚实告知。
- AppliesTo 谓词仅支持简单子集（FileExtension/FileSize/AND/OR）；复杂范围语法（如 `System.Size:0..6000000`）保守显示。
- 第三方 handler 崩溃：**进程边界 + 自动熔断**双保险。按 CLSID 直查（含设置里的扩展预览）只有一条实现——进程外 `BetterDesktopMenuBroker.exe`（与免宿主 B 路同一份 native source），被 handler 打崩的是 broker 而非宿主；broker 非 0 退出**当场**归因，同一扩展**连续 3 次**即自动停用（走既有 HKCU 影子/移键通道，写前备份、可逆），「菜单管理」显示"已因连续崩溃自动停用"，可一键恢复。宿主侧 in-proc 副本已删（重复实现 + 崩溃面）；broker 不可用时返回可见说明项，**不静默**、也不回宿主进程内重跑。
- 未覆盖：`NativeMenuPopup` 的系统聚合菜单（`GetUIObjectOf` 拿到的是 shell 聚合 `IContextMenu`，第三方 handler 在聚合内部跑）——既无法外置（HMENU 不能跨进程），也归属不到具体 CLSID。`ShellVerbItem.Invoke` 当前无生产消费方（预览红线只读），broker 侧 `invoke` 命令已实现并自检，但宿主未接线。
