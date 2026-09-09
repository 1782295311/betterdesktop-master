using BetterDesktop.Shell.Convert.Contracts;

namespace BetterDesktop.Shell.Convert.Services.Engines;

/// <summary>
/// 一次转换作业（普通转换单源；合成/合并类多输入）。
/// Sources：源路径集（普通转换恰为 1 个）；Target：目标描述；
/// TempDir：产物必须落此目录（同卷临时目录，服务校验后原子发布）。
/// </summary>
public sealed record ConversionJob(
    IReadOnlyList<string> Sources,
    ConversionTarget Target,
    string TempDir)
{
    public string PrimarySource => Sources[0];
}

/// <summary>
/// 转换引擎契约（EngineRegistry 唯一消费面；新增引擎实现本接口 + 矩阵登记，不改菜单与服务）。
/// </summary>
public interface IConversionEngine
{
    /// <summary>引擎种类（与 ConversionTarget.Prefer/Fallback 匹配）。</summary>
    EngineKind Kind { get; }

    /// <summary>引擎名（事件/日志用，如 "soffice"、"office-com"）。</summary>
    string Name { get; }

    /// <summary>能否处理该源与目标（语义判定；不含可用性探测）。</summary>
    bool CanHandle(IReadOnlyList<string> sources, ConversionTarget target);

    /// <summary>执行转换：产物写 TempDir，返回产物文件全路径集（失败抛 ConvertException）。</summary>
    Task<IReadOnlyList<string>> RunAsync(ConversionJob job, CancellationToken ct);

    /// <summary>可用性探测（进程级缓存；实时执行版探测见各引擎 EnsureProbed——dependency-on-demand 红线 2）。</summary>
    EngineAvailability Probe();
}

/// <summary>稳定错误（携带六分类；禁止把引擎缺失伪装成转换失败——local-engine-orchestration 红线）。</summary>
public sealed class ConvertException(ConvertError error, string message) : Exception(message)
{
    public ConvertError Error { get; } = error;
}
