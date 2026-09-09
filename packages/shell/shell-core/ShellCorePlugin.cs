using System;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;

namespace BetterDesktop.Shell.Core;

// ============================================================
// 【白话导航 · 内核级外壳公共域 shell-core】凭白话需求定位到精确文件：
//   "所有窗口的统一基类（无边框/缩放/材质传导）" → Surface/ShellWindow.cs
//   "弹层窗口基类 / 弹出定位（防溢出屏幕）"      → Windows/PopupWindowBase.cs + Windows/PopupPositioningService.cs
//   "Win32 消息泵 / 全局鼠标钩子 / 窗口事件泵"   → Native/MessagePump.cs、Native/MouseHook.cs、Native/WinEventPump.cs、Native/NativeMethods.cs
//   "窗口材质（亚克力/云母/透明）"              → Vibrancy/（VibrancyService + DwmHelper + VibrancyMapping）
//   "外观/桌面表面/窗口句柄契约"                → Surface/IAppearanceService.cs、IDesktopSurface.cs、IWindowHandleService.cs、DesktopSurface.cs
//   "菜单栏扩展注册（第三方加状态图标）"         → Services/MenuBarExtensionRegistry.cs + Contracts/IMenuBarExtension.cs
//   "原生任务栏管理 / 窗口样式工具"             → Windowing/NativeTaskbarManager.cs、Windowing/WindowStyleHelper.cs
//   "动画能力"                                  → Animation/（见 AnimationPlugin）
// ============================================================

/// <summary>
/// shell-core 插件：在桌面主窗口上应用透亮毛玻璃（无圆角），并向内核 Provide&lt;IDesktopSurface&gt;，
/// 供 bar/dock 取屏幕几何。依赖 IWindowHandleService（宿主 Provide）与 IVibrancyService（VibrancyService Provide）。
/// </summary>
public sealed class ShellCorePlugin : IPlugin
{
    /// <inheritdoc />
    public string Name => "shell.core";

    /// <inheritdoc />
    public IReadOnlyList<Type> Inject => new[] { typeof(IWindowHandleService), typeof(IVibrancyService) };

    /// <inheritdoc />
    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        var hWnd = context.Get<IWindowHandleService>()!.Handle;
        var vib = context.Get<IVibrancyService>()!;
        vib.Apply(hWnd, VibrancyStyle.Transparent, roundCorners: false);
        context.Provide<IDesktopSurface>(new DesktopSurface(hWnd));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
