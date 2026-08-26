using System;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Shell.Core.Vibrancy;

/// <summary>
/// 毛玻璃服务实现。同时是插件：加载时向内核 Provide&lt;IVibrancyService&gt;(this)，
/// 使宿主与 bar/dock 都能 Inject 到同一份实现（DwmHelper 仅此一处）。
/// </summary>
public class VibrancyService : IPlugin, IVibrancyService
{
    /// <inheritdoc />
    public string Name => "shell.vibrancy";

    /// <inheritdoc />
    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    /// <inheritdoc />
    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        context.Provide<IVibrancyService>(this);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public virtual void Apply(IntPtr hWnd, VibrancyStyle style, bool roundCorners = true, bool smallRadius = false)
    {
        // 圆角跟随 FrostedGlassDemo 实证基准：DWM 系统默认圆角（DWMWA_WINDOW_CORNER_PREFERENCE），
        // 由 DwmHelper 在应用模糊/亚克力后设置，与 XAML 内容层 CornerRadius 重合。
        var mode = VibrancyMapping.ToParams(style);
        switch (mode)
        {
            case VibrancyMode.Acrylic:
                DwmHelper.EnableAcrylic(hWnd, roundCorners, smallRadius);
                break;
            case VibrancyMode.BlurBehind:
                DwmHelper.EnableBlurBehind(hWnd, roundCorners, smallRadius);
                break;
            default:
                DwmHelper.Disable(hWnd);
                break;
        }
    }

    /// <inheritdoc />
    public virtual void Disable(IntPtr hWnd)
    {
        DwmHelper.Disable(hWnd);
    }
}
