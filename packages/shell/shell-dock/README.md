# shell-dock

Dock 任务栏插件：负责固定应用展示、开始菜单应用发现雏形、Dock 布局骨架接口与 UI 渲染占位。

## 职责

- 维护 `DockItemData` 数据模型与 Dock 相关服务接口（`IDockAppsService` / `IDockIconService` / `IDockLayoutService`）。
- 提供开始菜单基础扫描与快捷方式解析雏形（LNK / URL / EXE）。
- 在 Dock 窗口接入应用发现结果，并保留后续真实图标与自动隐藏能力的扩展点。

## 依赖

- `BetterDesktop.Kernel`
- `BetterDesktop.Shell.Core`（统一毛玻璃与动画服务）

## 扩展点

- `IDockAppsService`：固定应用录入、持久化、排序与扫描。
- `IDockIconService`：真实图标加载、缓存与预取（本阶段仅定义接口）。
- `IDockLayoutService`：布局度量、边缘触发与全屏隐藏策略（本阶段仅定义接口）。

## Known Limitations

- 当前 UI 仍使用占位图标（Name 首字符），真实图标由后续 `IDockIconService` 接管。
- 应用发现仅覆盖本地开始菜单基础路径，尚未支持 UWP AppX 完整枚举。
- 自动隐藏、全屏感知、拖拽排序等交互能力尚未落地实现。
