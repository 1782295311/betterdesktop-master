# BetterDesktop.Shell.Core

shell 层的共享内核：毛玻璃服务（IVibrancyService + DwmHelper 原样移植自 FrostedGlassDemo 基准）、桌面表面几何（IDesktopSurface）、主窗口句柄服务（IWindowHandleService）与窗口样式辅助（WindowStyleHelper）；同时为宿主与 bar/dock 提供插件装配基础。

## 依赖

- `BetterDesktop.Kernel`（插件与运行时契约）
- 运行时仅 BCL + WPF（UseWPF）；毛玻璃为 Win32 DwmApi 直接 P/Invoke，零第三方库

## 扩展点

- `IVibrancyService` / `VibrancyService`：毛玻璃共享服务（bar/dock 仅 Inject 依赖，不重复实现）
- `IDesktopSurface`：主屏几何（WPF 逻辑坐标，v1 仅主屏）
- `IWindowHandleService`：由宿主实现，提供主桌面窗口句柄
- `WindowStyleHelper.MakeFloatingNoActivate`：浮动不抢焦点的窗口样式

## Known Limitations

- v1 仅主屏，多显示器场景留 P1
- 点击窗口空白区域穿透未实现（P1）
- 毛玻璃依赖 Win11 DWM；非受支持系统降级为普通透明窗
