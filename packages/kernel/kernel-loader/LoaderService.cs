// BetterDesktop.Kernel.Loader — LoaderService 实现（ADR-002 loader 落地）
// 一切皆插件：loader 自身也是插件；读取 cordis.yml → 按启用标记装配

using System.Linq;
using BetterDesktop.Kernel.Contracts;
using YamlDotNet.Serialization;

namespace BetterDesktop.Kernel.Loader;

/// <summary>插件树加载器（自身是插件，经 ctx.Plugin 注册）。</summary>
public sealed class LoaderService : IPlugin
{
    private readonly LoaderOptions _options;
    private readonly List<IPluginHandle> _handles = new();

    /// <summary>构造。</summary>
    public LoaderService(LoaderOptions options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public string Name => "kernel.loader";

    /// <inheritdoc />
    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    /// <summary>本次装配报告（LoadAsync 后可用）。</summary>
    public IReadOnlyList<LoaderEntryReport> Reports { get; private set; } = Array.Empty<LoaderEntryReport>();

    /// <inheritdoc />
    /// <remarks>
    /// 全程不使用 ConfigureAwait(false)：保留调用方同步上下文（通常为 UI 线程），
    /// 确保每个子插件 LoadAsync 的同步段（含 WPF 窗口构造）在 UI 线程执行，
    /// 与原 Bootstrap.cs 硬编码 context.Plugin() 的线程行为等价。
    /// 串行加载语义不变：逐个 await 子插件句柄，不并行。
    /// </remarks>
    public async Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        var reports = new List<LoaderEntryReport>();
        LoaderConfig config;
        try
        {
            var text = await File.ReadAllTextAsync(ResolveConfigPath(_options.ConfigPath), cancellationToken);
            var deserializer = new DeserializerBuilder().Build();
            config = deserializer.Deserialize<LoaderConfig>(text) ?? new LoaderConfig();
        }
        catch (Exception ex)
        {
            context.Logger.Error($"cordis.yml 读取/解析失败：{ex}");
            throw;
        }

        foreach (var entry in config.Plugins)
        {
            if (entry.Enabled == false)
            {
                reports.Add(new LoaderEntryReport(entry, LoaderEntryStatus.Skipped));
                continue;
            }
            if (string.IsNullOrWhiteSpace(entry.Name) || !_options.Factories.TryGetValue(entry.Name, out var factory))
            {
                context.Logger.Error($"插件条目 {entry.Id} 的工厂「{entry.Name}」未注册（fail-closed）");
                reports.Add(new LoaderEntryReport(entry, LoaderEntryStatus.UnknownFactory));
                continue;
            }
            IPlugin plugin;
            try
            {
                plugin = factory();
            }
            catch (Exception ex)
            {
                context.Logger.Error($"插件条目 {entry.Id} 工厂执行失败：{ex}");
                reports.Add(new LoaderEntryReport(entry, LoaderEntryStatus.Failed));
                continue;
            }
            var handle = context.Plugin(plugin);
            await handle.AwaitAsync();
            _handles.Add(handle);

            LoaderEntryStatus status;
            if (handle.State == PluginState.Failed)
            {
                status = LoaderEntryStatus.Failed;
            }
            else if (handle.State == PluginState.Pending)
            {
                var pendingDeps = string.Join(", ", plugin.Inject.Select(t => t.Name));
                context.Logger.Warn(
                    $"插件条目 {entry.Id}（{plugin.Name}）停在 Pending，等待依赖注入：{pendingDeps}；"
                    + "依赖服务由后续插件 Provide 后内核将自动重启加载。");
                status = LoaderEntryStatus.Pending;
            }
            else
            {
                status = LoaderEntryStatus.Loaded;
            }
            reports.Add(new LoaderEntryReport(entry, status) { Handle = handle });
        }

        Reports = reports;
    }

    /// <summary>
    /// 解析配置路径：绝对路径原样返回；相对路径先按当前工作目录，找不到则回退到程序基目录，
    /// 避免「从任意工作目录启动都找不到 cordis.yml」的启动期故障。
    /// </summary>
    private static string ResolveConfigPath(string configPath)
    {
        if (Path.IsPathRooted(configPath))
        {
            return configPath;
        }
        var cwdCandidate = Path.Combine(Environment.CurrentDirectory, configPath);
        if (File.Exists(cwdCandidate))
        {
            return cwdCandidate;
        }
        return Path.Combine(AppContext.BaseDirectory, configPath);
    }

    /// <inheritdoc />
    public async Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        foreach (var handle in Enumerable.Reverse(_handles))
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }
        _handles.Clear();
    }
}
