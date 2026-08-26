using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.AppSource.Services;

namespace BetterDesktop.Shell.AppSource;

/// <summary>
/// 应用数据来源插件入口。
/// 加载时向内核 Provide 全部的扫描/图标服务，使 Dock / AppGrabber / Launchpad / 新装通知
/// 等 UI 插件通过 Inject 统一消费，消除"Dock 手动 new AppSourceService"的强耦合。
/// </summary>
public sealed class AppSourcePlugin : IPlugin
{
    /// <inheritdoc />
    public string Name => "shell.app-source";

    /// <inheritdoc />
    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    private AppSourceService? _appSourceService;

    /// <inheritdoc />
    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BetterDesktop");

        var appSourceService = new AppSourceService(dataDir);
        _appSourceService = appSourceService;
        var iconService = new Win32ShellIconService();

        context.Provide<IAppSourceService>(appSourceService);
        context.Provide<IAppIconService>(iconService);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        _appSourceService?.Dispose();
        _appSourceService = null;
        return Task.CompletedTask;
    }
}
