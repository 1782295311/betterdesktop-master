using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.AppSource.Services;
using BetterDesktop.Shell.Core;
using BetterDesktop.Shell.IndexIpc;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.AppSource;

// ============================================================
// 【白话导航 · 应用数据来源域】凭白话需求定位到精确文件：
//   "扫描已安装应用（开始菜单/开始屏幕文件夹）" → Services/AppSourceService.cs + Services/StartMenuWatcher.cs（变更监听）
//   "Win+X / AppsFolder 虚拟应用列表"            → Services/AppsFolderSource.cs
//   "解析 .lnk 快捷方式（目标/图标/参数）"       → Services/ShellLinkResolver.cs
//   "启动应用 / 以管理员运行"                    → Services/AppLauncher.cs
//   "应用高清图标提取（引擎优先/本地回退）"      → Services/Win32ShellIconService.cs（IAppIconService）
//                                                  + Native/HighResIconExtractor.cs（本地回退链）
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
    private IndexIpcClient? _indexClient;

    /// <inheritdoc />
    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BetterDesktop");

        // 【M2 · 2026-09-13】索引引擎后端：可用时 AppSourceService 的两源扫描改查常驻索引
        //（省掉全盘扫描——M0 基线：ScanAllPrograms 冷 53,107ms）；不可用则**自动回退**本地实现。
        // 装配失败绝不阻断启动（不抛、只 Warn）——应用源是壳的基础能力，不能因引擎缺席而挂。
        IndexIpcClient? indexClient = null;
        if (IsEngineBackendEnabled())
        {
            try
            {
                IndexEngineLauncher.EnsureEngine(msg => context.Logger.Warn($"[app-source] {msg}"));
                // CA2000：transport 的所有权**移交**给 IndexIpcClient（其 Dispose 会 Dispose transport）——
                // 分析器识别不了这种"构造期所有权转移"，故局部豁免（与 clipboard 侧同写法）。
#pragma warning disable CA2000
                indexClient = new IndexIpcClient(new NamedPipeTransport());
#pragma warning restore CA2000
                indexClient.Connect();
                context.Logger.Info("[app-source] 索引引擎客户端已装配（后端=engine）");
            }
            catch (Exception e)
            {
                context.Logger.Warn($"[app-source] 索引引擎客户端装配失败（{e.Message}）→ 走本地扫描");
                indexClient?.Dispose();
                indexClient = null;
            }
        }
        else
        {
            context.Logger.Warn("[app-source] 索引后端被环境变量强制为 local → 走本地扫描");
        }

        _indexClient = indexClient;

        var appSourceService = new AppSourceService(dataDir, indexClient, context.Logger);
        _appSourceService = appSourceService;

        // 【M4 · 2026-09-14】图标也接管为引擎数据源：引擎有则取「一档 256×256 PNG」，C# 按显示尺寸
        // 倍缩解码；引擎缺席 / 后端被强制 local → 服务内部**逐字回退**本地 Shell 提取，行为与 M4 前一致。
        // 门控：BETTERDESKTOP_ICON_SOURCE=local（禁引擎） / BETTERDESKTOP_ICON_DECODE_PX（倍率）——A/B 对照用。
        var iconService = new Win32ShellIconService(indexClient);

        context.Provide<IAppSourceService>(appSourceService);
        context.Provide<IAppIconService>(iconService);

        // 【P3 · 2026-09-14】口袋目录（应用扫描目录）与桌面快捷方式纳入：
        // 设置契约（ISettingsService）定义在 packages/api，且 Bootstrap 在插件加载前已 Provide，
        // 故这里无需 Inject 声明、也不引入 shell-settings 依赖。
        var settings = context.Get<ISettingsService>();
        AppSourceSettings.Apply(settings, appSourceService, context.Logger);
        if (settings is not null)
        {
            // 设置变更即时生效（订阅经 context.Effect 托管生命周期，卸载自动退订）
            context.Effect(() => context.Events.On<SettingsChangedEventArgs>(
                ShellEvents.SettingsChanged,
                (e, _) =>
                {
                    if (e.Key is AppSourceSettings.ExtraRootsKey or AppSourceSettings.ScanDesktopShortcutsKey)
                    {
                        AppSourceSettings.Apply(settings, appSourceService, context.Logger);
                    }

                    return Task.CompletedTask;
                }));
        }

        // 设置中心「应用来源」分区（功能域自贡献，不在 shell-settings 里硬编码本包设置）
        context.Get<ISettingsSectionRegistry>()?.Register(new Sections.AppSourceSection());

        return Task.CompletedTask;
    }

    /// <summary>
    /// 是否启用索引引擎后端。默认启用；设环境变量
    /// <c>BETTERDESKTOP_INDEX_BACKEND=local</c> 可强制回退（排障 / M0↔M2 对照用）。
    /// <para>
    /// 【为什么用环境变量而不是设置键】本包未引用 shell-settings（避免为一个开关引入新包依赖）；
    /// 设置中心的可见开关与状态行属 **M5**（届时在设置分区读同一语义）。此处环境变量是零依赖门控，
    /// 与仓库既有 <c>CAIRO_*</c> / <c>BETTERDESKTOP_*</c> 门控惯例一致。
    /// </para>
    /// </summary>
    private static bool IsEngineBackendEnabled()
    {
        var value = Environment.GetEnvironmentVariable("BETTERDESKTOP_INDEX_BACKEND");
        return !string.Equals(value, "local", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        _appSourceService?.Dispose();
        _appSourceService = null;
        // 只断开客户端，不关停引擎进程（引擎是常驻服务，退出策略归宿主/launcher；与剪贴板同纪律）
        _indexClient?.Dispose();
        _indexClient = null;
        return Task.CompletedTask;
    }
}
