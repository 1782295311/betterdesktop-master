# 任务 01：拆 shell-app-center（P0）

> **✅ 已以独立插件形式落地（2026-08-25 修正）**
> Step 8 曾一度废弃 AppGrabberWindow（标记本任务作废），经 review 纠正：**应用提取器的独立窗口交互不可替代**
> （常驻窗口 vs 弹出式开始菜单、全盘盘点、手动添加文件、来源筛选）。
> 现恢复为独立包 `packages/shell/shell-app-center/`（`AppGrabberWindow` → `AppCenterWindow`）：
> - 与 Dock 经 IEventBus `shell.app-center.show` 解耦，Dock 停用不影响管理窗口
> - 与开始菜单 allapps 布局共存（Dock 右键两个入口：应用管理中心 / 所有应用）
> - 右键卸载（cmd /c）、管理员运行、打开位置已补回（自 StartMenuService/AppItemActions 逻辑复刻）
> - `FolderInputWindow` 不恢复（Launchpad 分类早已融合，死代码）
> 详见 `packages/OPEN_SHELL_INTEGRATION_STEPS.md` Step 8 修正记录。

## 目标

Dock 卸载不再连带关闭 AppGrabber / Launchpad / 新装通知；CairoMenu 的"打开应用中心"由独立插件服务提供。

## 新建 `packages/shell/shell-app-center/`

| 文件 | 说明 |
|------|------|
| `BetterDesktop.Shell.AppCenter.csproj` | TFM net8.0-windows10.0.19041.0；引用 kernel / shell-core / shell-app-source / shell-dock |
| `AppCenterPlugin.cs` | `IPlugin`，`Name="shell.app-center"`，`Inject=[IVibrancyService, IAppSourceService, IAppIconService, IDockAppsService]`；`LoadAsync` 建窗口，`UnloadAsync` 关闭 |
| `IAppCenterService.cs` | `void ShowAppGrabber(); void ShowLaunchpad();`（供 menu-bar 的 CairoMenu 命令调用） |
| 自 shell-dock 移入 | `AppGrabberWindow.xaml(.cs)`、`LaunchpadWindow.xaml(.cs)`、`NewAppsNotificationWindow.cs`、`FolderInputWindow.cs` |

## 修改 shell-dock

- `DockPlugin.cs`：删除 `_grabberWindow/_launchpadWindow/_newAppsNotification` 及 Show*/CheckNewApps 逻辑（新装检测迁到 app-center）；`Provide<IDockPinnedService>` 保留。
- 移除对已搬走窗口的引用；`DockWindow` 右键"在应用管理中心查看"改经 `IAppCenterService`。

## 关键接口

```csharp
public interface IAppCenterService
{
    void ShowAppGrabber();    // 单实例，已开则 Activate
    void ShowLaunchpad();     // 单实例，已开则关闭
}
```

## 依赖方向

shell-app-center → shell-dock（只消费 `IDockAppsService`/`IDockPinnedService` 契约）；shell-dock **不再**引用 app-center。

## 验收

- [ ] 构建 0 警告 0 错误
- [ ] 卸载 dock 插件后 AppGrabber/Launchpad 仍在
- [ ] AppGrabber 固定/取消固定与 Dock 实时联动正常

## 注意

原 `DockPlugin.ShowAppGrabber()` 的调用方（CairoMenu 命令 / Win 键）统一改为 `IAppCenterService.ShowAppGrabber()`，勿留死引用。
