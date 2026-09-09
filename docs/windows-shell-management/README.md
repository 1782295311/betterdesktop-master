# 原生 Windows 任务栏与开始菜单管理 — 设计文档

> 目标：实现「原生 Windows 任务栏和开始菜单的管理」。
> 定位：我们**不重写** Explorer 任务栏/开始菜单（那是 Open-Shell 的路线，要注入 Explorer.exe），
> 而是站在一个已做好的 C# 桌宠/桌面环境（better-desktop-cordis）之上，
> 对原生 Shell 部件做**外观改造 + 行为接管 + 可选的替换层**。
>
> 灵感来源：`参考/` 目录下的三个开源项目 + 现有 `shell-core/Windowing/NativeTaskbarManager.cs`。

---

## 0. 范围澄清（先对齐"管理"指什么）

"管理原生任务栏和开始菜单"至少可拆成 4 个层级，成本递增：

| 层级 | 含义 | 风险/成本 | 参考项目 |
|---|---|---|---|
| **L1 可见性** | 显示/隐藏原生任务栏、自动隐藏、独占底部区域 | 极低（纯 ShowWindow） | 我们已有 `NativeTaskbarManager` |
| **L2 外观** | 透明/亚克力/模糊、颜色、圆角、边距 | 低（外部进程即可，无需注入） | **TranslucentTB** |
| **L3 行为接管** | 拦截开始按钮点击、托盘/通知区、任务栏按钮右键 | 中（需 WinEventHook + 子类化，或注入） | Open-Shell / EarTrumpet |
| **L4 替换层** | 用自绘 UI 完全替代开始菜单/任务栏交互 | 高（注入 Explorer 或自绘覆盖层） | **Open-Shell-Menu** |

**本项目的推荐落点：L1 + L2 + 部分 L3（开始按钮接管），L4 仅在用户明确要"替换开始菜单"时做。** 理由：L1/L2 不碰 Explorer 进程，稳定且符合我们"已是独立桌面环境"的定位；L3 的开始按钮接管用覆盖层+消息拦截即可，不必注入。

---

## 1. 参考项目提炼（实现灵感，已读源码）

### 1.1 TranslucentTB（C++，最贴合 L2）

- **找任务栏窗口**：用 `CLSID_ImmersiveShell` + `IServiceProvider::QueryService` 拿到真正的任务栏 HWND； 多屏遍历 `m_Taskbars: unordered_map<HMONITOR, MonitorInfo>`（按显示器句柄分桶，主屏 = `MonitorFromPoint(0,0)`）。
- 我们现状：用 `EnumWindows` + 类名匹配 `Shell_TrayWnd` / `Shell_SecondaryTrayWnd`。**更稳**，但拿不到"主屏 vs 副屏"的语义——后续可补 `MonitorFromWindow` 区分。
- **改外观（核心 API）**：`SetWindowCompositionAttribute(hwnd, &data)` + `ACCENT_POLICY`：
- `ACCENT_NORMAL` / `ACCENT_ENABLE_BLURBEHIND`（模糊）/ `ACCENT_ENABLE_ACRYLICBLURBEHIND`（亚克力）/ `ACCENT_ENABLE_GRADIENT`（渐变）。
- `WCA_ACCENT_POLICY` 是 undocumented 但长期稳定的开关（TranslucentTB/StartIsBack 都靠它）。
- **副屏任务栏**：外部进程只能改主屏；副屏要 `ExplorerTAP`/`ExplorerHooks`（注入 Explorer.exe + Microsoft Detours 钩 `SetWindowCompositionAttribute`）——**这是 L2 的代价点，副屏需注入或放弃**。
- **追踪 Explorer 重启**：`WinEventHook`（EVENT_OBJECT_SHOW/HIDE/NAMECHANGE on `Shell_TrayWnd`）监听任务栏重建，自动重设属性。

### 1.2 Open-Shell-Menu（C++，L3/L4 范本）

- **架构**：Explorer BHO（Browser Helper Object）DLL 注入 `explorer.exe`，接管开始按钮、开始菜单、任务栏右键菜单。
- **关键手法**：
  - 注册表 `HKCR\CLSID\{...}\InprocServer32` 注入；`ClassicExplorer.dll` 作为 BHO。
  - 子类化（`SetWindowSubclass`）任务栏的 `RebarWindow32` / `StartButton` 拦截消息。
  - 开始菜单用自绘 XAML/HTML 皮肤（`Skins/` 目录：Classic/Metro/Immersive/Glass 多套）。
- **教训**：注入 Explorer 是**高维护成本**路线——每次 Windows 大版本更新都可能 breaking。除非要做 L4 替换，否则避免。

### 1.3 EarTrumpet（C# + WinUI3，L3 轻量范本）

- **做法**：不注入 Explorer。自绘 XAML Island flyout，监听 `WindowWatcher`（WinEventHook 跟踪前台窗口/任务栏位置），把自己的 flyout 锚定到任务栏托盘区。
- **启示**：C# 项目要"接管任务栏交互"的正确姿势是 **覆盖层（overlay）+ 位置跟随**，而非注入。这与我们"独立桌面环境"的定位完全契合。

### 1.4 我们已有（shell-core）

- `NativeTaskbarManager.SetTaskbarVisible(bool)`：L1 完整实现，覆盖主/副屏，失败静默。
- 缺失：L2 外观 API、L3 开始按钮/托盘接管、副屏语义区分、Explorer 重启自愈。

---

## 2. 推荐实现方案（分阶段）

### Phase A — L2 外观管理（C#，外部进程，无需注入）

新增 `shell-core/Windowing/TaskbarAppearanceManager.cs`：

- P/Invoke：`SetWindowCompositionAttribute` + `ACCENT_POLICY` 结构体（`WCA_ACCENT_POLICY`）。
- 方法：`SetTaskbarAccent(AccentType, Color?, double opacity)` → 应用到主任务栏。
- 枚举所有任务栏（复用 `NativeTaskbarManager` 的 EnumWindows 逻辑，按 `MonitorFromWindow` 区分主/副屏）。
- `WinEventHook` 监听 `Shell_TrayWnd` 重建 → 自动重设上次配置（Explorer 重启自愈）。
- **副屏限制**：Phase A 只做主屏；副屏透明化标注为"需注入，待定"（见 Phase C）。

### Phase B — L3 开始按钮接管（C# 覆盖层，不注入）

新增 `shell-start-button/` 插件或并入 `shell-dock`：

- 监听开始按钮位置：WinEventHook + `FindWindowEx(Shell_TrayWnd, ..., "Start", ...)` 找到 `Start` 按钮 HWND，用 `GetWindowRect` 拿屏幕坐标。
- 在我们 Dock/覆盖层上画一个"开始按钮区"，拦截点击 → 唤起我们的 Launchpad/开始菜单（已有 `shell-dock` 的 Launchpad）。
- **关键**：保留原生开始按钮但置为透明/禁用其默认菜单（`EnableWindow` 或覆盖层挡住点击），避免双开始菜单。
- 参考 EarTrumpet 的"位置跟随"：任务栏移动/分辨率变化时重新锚定。

### Phase C — L4 替换层（仅当用户要"替换开始菜单"时）

- 选项 1（轻）：纯 C# 覆盖层 + 自绘开始菜单（复用 Launchpad 已有组件），不注入。
- 选项 2（重）：仿 Open-Shell 注入 Explorer（**不推荐**，维护成本爆炸，违背"独立桌面环境"定位）。
- **默认推荐选项 1**。

### Phase D（可选）— 副屏外观注入

- 若用户坚持副屏也透明：引入轻量注入 DLL（仿 TranslucentTB 的 `ExplorerHooks`，用 Detours 钩 `SetWindowCompositionAttribute`），作为 `type: external` 跨语言插件（见 `docs/cross-language/`）。
- 这是跨语言工程的天然用例：C++ 注入 DLL + C# 宿主下发配置。

---

## 3. 技术要点清单（从参考项目学来的坑）

1. **任务栏 HWND 获取**：主屏 `Shell_TrayWnd`，副屏 `Shell_SecondaryTrayWnd`；`EnumWindows` 比 `FindWindow` 稳（多实例）。
2. **外观 API**：`SetWindowCompositionAttribute` + `ACCENT_POLICY` 是事实标准，undocumented 但 10+ 年稳定。
3. **Explorer 会重启**：任务栏进程偶尔重建，必须 WinEventHook 自愈，否则配置丢失。
4. **副屏要注入**：外部进程改不了副屏任务栏外观，这是 Windows 架构限制（副屏任务栏在另一个 Explorer 线程）。
5. **不要注入除非必要**：Open-Shell 证明注入 = 与 Windows 更新军备竞赛。
6. **C# 接管交互用覆盖层**：EarTrumpet 证明不注入也能做任务栏交互增强。
7. **干净退出**：我们已有 `SetTaskbarVisible(true)` 恢复；外观/接管也必须在退出/禁用时还原（记录原始 ACCENT 状态）。

---

## 4. 与现有架构的关系

- `NativeTaskbarManager`（L1）→ 升级为 `TaskbarManager` 门面，内部组合 可见性 + 外观 + 开始按钮接管。
- 不属于跨语言工程：L1/L2/L3 纯 C# P/Invoke 即可，只有 Phase D 副屏注入才进 `docs/cross-language/` 的 external 插件体系。
- 开始菜单替换层复用 `shell-dock` 的 Launchpad 组件，不重复造轮子。

---

## 5. 验收标准

- [ ] Phase A：可一键让主任务栏变透明/亚克力/模糊，Explorer 重启后自动恢复，退出时还原原始外观。
- [ ] Phase B：点击开始按钮区域唤起我们的 Launchpad，而非原生开始菜单；任务栏移动时按钮跟随。
- [ ] Phase C（若启用）：自绘开始菜单替代原生，覆盖层无注入崩溃风险。
- [ ] Phase D（若启用）：副屏任务栏外观同步，注入 DLL 崩溃隔离（external 插件）。

---

## 6. 优先级建议

**先做 Phase A（L2 外观）**——纯 C#、零注入、立刻有视觉收益，且能验证 `SetWindowCompositionAttribute` 在我们运行环境（Win10/11）的实际表现。 Phase B（开始按钮接管）紧随其后，因为它直接对接已有的 Launchpad。 Phase C/D 视用户是否要"替换开始菜单"再决定。
