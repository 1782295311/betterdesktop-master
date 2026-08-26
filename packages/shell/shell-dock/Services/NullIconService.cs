using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.AppSource.Models;

namespace BetterDesktop.Shell.Dock.Services;

/// <summary>
/// 空图标服务：当外部未注入真实 <see cref="IAppIconService"/> 时使用的无操作实现。
/// 返回 null，不抛异常，保证 DockFlyoutWindow 等在无图标源场景下仍可运行。
/// </summary>
public sealed class NullIconService : IAppIconService
{
    /// <summary>
    /// 单例。
    /// </summary>
    public static readonly NullIconService Instance = new();

    private NullIconService()
    {
    }

    /// <inheritdoc />
    public Task<ImageSource?> GetIconAsync(AppItem item, CancellationToken ct = default)
        => Task.FromResult<ImageSource?>(null);

    /// <inheritdoc />
    public void Invalidate(AppItemId id)
    {
    }

    /// <inheritdoc />
    public Task PrefetchAsync(IEnumerable<AppItem> items, CancellationToken ct = default)
        => Task.CompletedTask;
}
