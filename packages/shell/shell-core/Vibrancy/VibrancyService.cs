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
                // 【2026-09-18】无色纯模糊走"多档尝试 + 每档留痕"的主路径：
                // 哪一档真的出图在不同 Windows build/虚拟机上并不一致，故档位可配（见 BlurCapability.ReadBlurPreference），
                // 且实际用到的档位落一次日志——出现"毛玻璃没生效"时先看这条，再对照能力探测结论。
                var path = DwmHelper.EnableColorlessBlur(
                    hWnd, roundCorners, smallRadius, BlurCapability.ReadBlurPreference());
                LogBlurPathOnce(path);
                break;
            default:
                DwmHelper.Disable(hWnd);
                break;
        }
    }

    /// <summary>记录"实际生效的模糊档位"（仅在档位变化时记一条，避免主题切换时刷屏）。</summary>
    private static void LogBlurPathOnce(DwmHelper.BlurPath path)
    {
        if (_lastLoggedPath == path)
        {
            return;
        }

        _lastLoggedPath = path;
        var capability = BlurCapability.Current;
        if (path == DwmHelper.BlurPath.Mica)
        {
            BetterDesktop.Kernel.Core.DiagnosticLog.Trace("Vibrancy",
                $"无色模糊两档均未接受，已退回 Mica（带系统色调）。能力探测: {capability.Describe()}");
            return;
        }

        BetterDesktop.Kernel.Core.DiagnosticLog.Trace("Vibrancy",
            $"毛玻璃档位={path}；能力探测: {capability.Describe()}");
    }

    /// <summary>上一次已记录的档位（仅用于日志去重）。</summary>
    private static DwmHelper.BlurPath _lastLoggedPath = DwmHelper.BlurPath.None;

    /// <inheritdoc />
    public virtual void Disable(IntPtr hWnd)
    {
        DwmHelper.Disable(hWnd);
    }
}
