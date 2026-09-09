using System;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Shell.Core.Animation;

// ============================================================
// 【白话导航 · 动画域】凭白话需求定位到精确文件：
//   "动画播放/缓动/时长" → AnimationService.cs（IAnimationService，全壳统一动画能力，供各 UI 包 Inject）
// ============================================================

/// <summary>
/// 动画插件，提供动画服务。
/// </summary>
public sealed class AnimationPlugin : IPlugin
{
    /// <inheritdoc />
    public string Name => "shell.animation";

    /// <inheritdoc />
    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    /// <inheritdoc />
    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        var animationService = new AnimationService();
        context.Provide<IAnimationService>(animationService);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
