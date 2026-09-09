using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Convert.Contracts;
using BetterDesktop.Shell.Convert.Services.Engines;

namespace BetterDesktop.Shell.Convert.Services;

/// <summary>
/// 引擎注册与路由（local-engine-orchestration 多引擎一张表）：
/// 按 target 的 Prefer → Fallback 候选链选第一个「CanHandle 且 Probe 可用」的引擎；
/// 探测进程级缓存（菜单零阻塞），启动时由 ConvertPlugin 预热真实 --version 探测。
/// </summary>
public sealed class EngineRegistry
{
    private readonly object _gate = new();
    private readonly List<IConversionEngine> _engines = [];

    /// <summary>注册引擎（同类引擎按注册序取第一个可用者）。</summary>
    public EngineRegistry Add(IConversionEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        lock (_gate)
        {
            _engines.Add(engine);
        }
        return this;
    }

    /// <summary>候选引擎链（Prefer 优先，Fallback 兜底；md→docx 的 P3 自动切 pandoc 即由此实现，调用方无感）。</summary>
    internal static IEnumerable<EngineKind> CandidatesOf(ConversionTarget target)
    {
        yield return target.Prefer;
        if (target.Fallback is { } fallback)
        {
            yield return fallback;
        }
    }

    /// <summary>
    /// 解析执行引擎；无可执行者返回 null（调用方按「引擎未就绪」分类，隐藏优先由菜单层用同口径过滤）。
    /// </summary>
    public IConversionEngine? Resolve(IReadOnlyList<string> sources, ConversionTarget target)
    {
        foreach (var kind in CandidatesOf(target))
        {
            IConversionEngine?[] snapshot;
            lock (_gate)
            {
                snapshot = [.. _engines];
            }
            foreach (var engine in snapshot.OfType<IConversionEngine>())
            {
                if (engine.Kind != kind || !engine.Probe().Available)
                {
                    continue;
                }
                if (engine.CanHandle(sources, target))
                {
                    return engine;
                }
            }
        }
        return null;
    }

    /// <summary>按种类查引擎（含不可用者；按需下载钩子用）。</summary>
    public IConversionEngine? FindEngine(EngineKind kind)
    {
        lock (_gate)
        {
            return _engines.FirstOrDefault(e => e.Kind == kind);
        }
    }

    /// <summary>某类引擎是否有已注册且可用者（菜单整组显隐的快速判定）。</summary>
    public bool HasAvailable(EngineKind kind)
    {
        IConversionEngine[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _engines];
        }
        return snapshot.Any(e => e.Kind == kind && e.Probe().Available);
    }

    /// <summary>静默预热全部引擎探测（ConvertPlugin LoadAsync 后台触发；真实 --version 校验入缓存）。</summary>
    public void WarmupProbes()
    {
        IConversionEngine[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _engines];
        }
        foreach (var engine in snapshot)
        {
            try
            {
                var availability = engine.Probe();
                DiagnosticLog.Trace("shell-convert", $"引擎探测 {engine.Name}: {(availability.Available ? "可用" : "缺失")}"
                    + (availability.Version is { } v ? $" ({v})" : string.Empty));
            }
            catch (Exception ex)
            {
                DiagnosticLog.Trace("shell-convert", $"引擎探测 {engine.Name} 异常(忽略): {ex.Message}");
            }
        }
    }
}
