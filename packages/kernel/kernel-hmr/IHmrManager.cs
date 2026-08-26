// BetterDesktop.Kernel.Hmr — IHmrManager 热重载管理器契约
// 动态加载 / 卸载 / 重载插件，含版本兼容、依赖校验、状态迁移与监控

using System.Collections.Generic;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Kernel.Hmr;

/// <summary>HMR 热重载管理器契约：动态加载 / 卸载 / 重载插件，含版本兼容、依赖校验、状态迁移与监控。</summary>
public interface IHmrManager : IResourceGovernor
{
    /// <summary>是否已启用。</summary>
    bool IsEnabled { get; }

    /// <summary>当前内核 ABI 版本。</summary>
    SemanticVersion KernelAbi { get; }

    /// <summary>启用热重载。</summary>
    void Enable();

    /// <summary>禁用热重载（禁用后 Load/Reload 请求被拒绝）。</summary>
    void Disable();

    /// <summary>加载插件（校验 ABI 兼容与必需依赖后激活）。</summary>
    Task<PluginLoadResult> LoadPluginAsync(PluginManifest manifest, CancellationToken cancellationToken = default);

    /// <summary>卸载插件（清理效应并释放程序集）。</summary>
    Task<bool> UnloadPluginAsync(string pluginId, CancellationToken cancellationToken = default);

    /// <summary>热重载插件：先立后破，失败自动回滚到旧版本。</summary>
    Task<PluginReloadResult> ReloadPluginAsync(string pluginId, CancellationToken cancellationToken = default);

    /// <summary>热重载全部已加载插件。</summary>
    Task<int> ReloadAllPluginsAsync(CancellationToken cancellationToken = default);

    /// <summary>查询插件状态。</summary>
    PluginReloadStatus GetPluginStatus(string pluginId);

    /// <summary>获取全部插件运行时信息（监控快照）。</summary>
    IReadOnlyList<PluginRuntimeInfo> GetPluginRuntimeInfos();

    /// <summary>导出受内存治理器监控的受控对象（供设置界面展示治理状态）。</summary>
    IReadOnlyList<IResourceSubject> GetResourceSubjects();

    /// <summary>热更新内存治理阈值（设置界面改阈值后即时生效，不重建治理器）。</summary>
    void UpdateGovernorOptions(ResourceGovernorOptions options);
}
