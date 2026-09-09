using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Convert.Contracts;
using BetterDesktop.Shell.Convert.Services.Engines;

namespace BetterDesktop.Shell.Convert.Tests;

/// <summary>测试替身：可编程的假引擎（不起任何真进程）。</summary>
public sealed class FakeEngine : IConversionEngine
{
    private readonly EngineAvailability _availability;

    public FakeEngine(EngineKind kind, bool available = true, string name = "fake")
    {
        Kind = kind;
        Name = name;
        _availability = available ? EngineAvailability.Ok(name) : EngineAvailability.Missing;
    }

    /// <summary>产物写入器（同步；模拟引擎输出；null = 模拟失败不产文件）。</summary>
    public Func<ConversionJob, IReadOnlyList<string>>? Runner { get; init; }

    /// <summary>异步产物写入器（延迟场景；优先于 Runner）。</summary>
    public Func<ConversionJob, Task<IReadOnlyList<string>>>? RunnerAsync { get; init; }

    public EngineKind Kind { get; }

    public string Name { get; }

    public bool CanHandle(IReadOnlyList<string> sources, ConversionTarget target) =>
        target.Prefer == Kind || target.Fallback == Kind;

    public EngineAvailability Probe() => _availability;

    public async Task<IReadOnlyList<string>> RunAsync(ConversionJob job, CancellationToken ct)
    {
        if (RunnerAsync is not null)
        {
            return await RunnerAsync(job);
        }
        if (Runner is not null)
        {
            return Runner(job);
        }
        throw new ConvertException(ConvertError.ConversionFailed, "fake engine 未配置 Runner");
    }
}

/// <summary>测试替身：记录型 IEventBus（只实现 EmitAsync；其余不参与）。</summary>
public sealed class FakeEventBus : IEventBus
{
    public List<(string Name, object Payload)> Emitted { get; } = [];

    public Task EmitAsync<T>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull
    {
        Emitted.Add((name, (object)payload));
        return Task.CompletedTask;
    }

    public IDisposable On<T>(string name, Func<T, CancellationToken, Task> handler) where T : notnull => throw new NotSupportedException();

    public IDisposable OnResult<T, TResult>(string name, Func<T, CancellationToken, Task<TResult>> handler) where T : notnull => throw new NotSupportedException();

    public Task<IReadOnlyList<TResult>> ParallelAsync<T, TResult>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull => throw new NotSupportedException();

    public Task<IReadOnlyList<TResult>> SerialAsync<T, TResult>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull => throw new NotSupportedException();

    public Task<TResult?> BailAsync<T, TResult>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull => throw new NotSupportedException();

    public Task<T> WaterfallAsync<T>(string name, T payload, CancellationToken cancellationToken = default) where T : notnull => throw new NotSupportedException();
}
