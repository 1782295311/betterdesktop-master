using System;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;

namespace BetterDesktop.Shell.Core;

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
