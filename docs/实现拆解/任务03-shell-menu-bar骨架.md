# 任务 03：shell-menu-bar 骨架 + 首期右区组件（P1）

## 目标

顶栏可见：左区（图标 / 程序菜单 / 位置 / 下载 / 文档）+ 右区 4 个自绘组件（时间 / 内存 / 音量 / 电量）。

## 新建 `packages/shell/shell-menu-bar/`

| 文件 | 说明 |
|------|------|
| `MenuBarPlugin.cs` | `Name="shell.menu-bar"`，`Inject=[IVibrancyService, IAppSourceService, IAppIconService, IDockAppsService]`（+ 任务 1 的 `IAppCenterService`） |
| `MenuBarWindow.xaml(.cs)` | `: ShellWindow`；左区容器 + 右区组件栏；高度 23px 基线（DPI 用 `IDesktopSurface`） |
| `Controls/StatusButton.cs` | 自绘基类：图标 + hover 变亮 + 点击缩放 95% + 弹出面板统一入口 |
| `Extensions/DateTimeExtension.cs` | 时间显示 1s 走秒；点击弹自绘日历（后续） |
| `Extensions/MemoryUsageExtension.cs` | 内存占用率 + 迷你走势图（消费 `IMemoryMonitor`） |
| `Extensions/VolumeExtension.cs` | 音量条/静音（消费 `IVolumeMonitor`） |
| `Extensions/BatteryExtension.cs` | 电池图标 + 百分比（消费 `IBatteryMonitor`） |

## 左区构成（首期）

- Cairo 按钮：占位 → CairoMenu 命令（任务 4 后接入 `IMenuService`）。
- 程序菜单：分组 Tab 数据来自 `IAppSourceService`（ScanStartMenu/ScanInstalledApps）。
- 位置：下载 / 文档快捷入口（打开系统文件夹）。

## 关键接口

```csharp
public interface IMenuBarExtension
{
    string Id { get; }
    int Order { get; }                                  // 从左往右
    UIElement CreateControl(MenuBarContext ctx);        // 自绘
}
```

## 依赖方向

shell-menu-bar → kernel + shell-core + shell-app-source + shell-dock（IDockAppsService 契约）+ shell-status（任务 2）。

## 验收

- [ ] 构建 0 警告 0 错误
- [ ] 顶栏显示，组件自绘，高度对齐 23px 基线
- [ ] 内存 / 电量数值真实（本机验证）
- [ ] 点击组件弹出面板（滑块/详情）
