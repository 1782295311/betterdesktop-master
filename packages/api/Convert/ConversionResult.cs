namespace BetterDesktop.Shell.Convert.Contracts;

/// <summary>转换错误六分类（继承自旧 ConvertEngine 契约，禁止合并分类——local-engine-orchestration 红线）。</summary>
public enum ConvertError
{
    None,
    /// <summary>引擎缺失（不是用户文件错误——提示语必须区分，禁止伪装成转换失败）。</summary>
    EngineMissing,
    /// <summary>引擎崩溃（进程非零退出且无产物）。</summary>
    EngineCrashed,
    /// <summary>超时（超时控制必须存在）。</summary>
    Timeout,
    /// <summary>转换失败（引擎正常退出但无输出/业务失败）。</summary>
    ConversionFailed,
    /// <summary>输入非法（空路径/\0/不存在/不支持类型）。</summary>
    InputInvalid,
    /// <summary>输出发布失败（写/移动失败/输出与输入冲突）。</summary>
    OutputFailed,
}

/// <summary>引擎可用性（探测结果；版本信息供诊断）。</summary>
public sealed record EngineAvailability(bool Available, string? Version = null, string? Detail = null)
{
    public static readonly EngineAvailability Missing = new(false);

    public static EngineAvailability Ok(string? version = null) => new(true, version);
}

/// <summary>单文件转换结果（逐项报告成败——禁止把部分成功当全成功，红线 13）。</summary>
public sealed record ConversionResult(
    string Source,
    bool Success,
    string? Output,
    ConvertError Error,
    string Engine,
    long ElapsedMs,
    string? Message = null);
