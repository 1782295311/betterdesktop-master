using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.AppSource.Services;

namespace BetterDesktop.Shell.AppSource;

// ============================================================
// 【白话导航 · 应用数据来源域】凭白话需求定位到精确文件：
//   "扫描已安装应用（开始菜单/开始屏幕文件夹）" → Services/AppSourceService.cs + Services/StartMenuWatcher.cs（变更监听）
//   "Win+X / AppsFolder 虚拟应用列表"            → Services/AppsFolderSource.cs
//   "解析 .lnk 快捷方式（目标/图标/参数）"       → Services/ShellLinkResolver.cs
//   "启动应用 / 以管理员运行"                    → Services/AppLauncher.cs
//   "应用高清图标提取"                          → Native/HighResIconExtractor.cs + Services/Win32ShellIconService.cs（IAppIconService）
//   消费方：Dock / 开始菜单 / 搜索 / 新装通知均通过 Inject 统一取用。
// ============================================================

/// <summary>
/// 应用数据来源插件入口。
/// 加载时向内核 Provide 全部的扫描/图标服务，使 Dock / 应用提取器 / 新装通知
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
