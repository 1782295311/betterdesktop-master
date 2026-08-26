// BetterDesktop.Kernel.Loader — LoaderService 实现（ADR-002 loader 落地）
// 一切皆插件：loader 自身也是插件；读取 cordis.yml → 按启用标记装配

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
    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        var reports = new List<LoaderEntryReport>();
        LoaderConfig config;
        try
        {
            var text = File.ReadAllText(ResolveConfigPath(_options.ConfigPath));
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
            handle.AwaitAsync().GetAwaiter().GetResult();
            _handles.Add(handle);
            var status = handle.State == PluginState.Failed ? LoaderEntryStatus.Failed : LoaderEntryStatus.Loaded;
            reports.Add(new LoaderEntryReport(entry, status) { Handle = handle });
        }

        Reports = reports;
        return Task.CompletedTask;
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
