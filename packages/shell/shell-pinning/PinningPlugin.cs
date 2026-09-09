using System;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.Pinning.Contracts;
using BetterDesktop.Shell.Pinning.Services;

namespace BetterDesktop.Shell.Pinning;

// ============================================================
// 【白话导航 · 固定项域】凭白话需求定位到精确文件：
//   "固定到任务栏/开始/Dock、取消固定" → Services/PinningService.cs（IPinningService，统一固定存储）
//   "固定项数据模型 / 变更事件"        → Contracts/PinnedItem.cs、Contracts/PinnedChangedEventArgs.cs
//   消费方：shell-dock、shell-start-menu 通过 Inject 取用。
// ============================================================

/// <summary>
/// 通用固定 / 收藏服务插件。
/// 依赖 IAppSourceService（由 AppSourcePlugin 先行注册），加载时 Provide IPinningService。
/// Dock / 未来开始菜单 / 任务栏通过 Inject 该服务消费，消除各自重复实现（M7）。
/// </summary>
public sealed class PinningPlugin : IPlugin
{
    /// <inheritdoc />
    public string Name => "shell.pinning";

    /// <inheritdoc />
    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    private PinningService? _service;

    /// <inheritdoc />
    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        var appSource = context.Get<IAppSourceService>()!;
        _service = new PinningService(appSource, context.Logger);
        context.Provide<IPinningService>(_service);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        _service = null;
        return Task.CompletedTask;
    }
}
