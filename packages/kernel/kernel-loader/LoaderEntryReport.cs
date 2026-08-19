// BetterDesktop.Kernel.Loader — 加载报告模型
// 每个条目的装配结果（诊断/状态中心数据源）

using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Kernel.Loader;

/// <summary>条目装配结果。</summary>
public enum LoaderEntryStatus
{
    /// <summary>已加载。</summary>
    Loaded,

    /// <summary>已跳过（enabled=false）。</summary>
    Skipped,

    /// <summary>工厂未注册（fail-closed）。</summary>
    UnknownFactory,

    /// <summary>加载失败（插件进入 Failed 状态）。</summary>
    Failed
}

/// <summary>单个条目的装配报告。</summary>
public sealed class LoaderEntryReport
{
    /// <summary>构造。</summary>
    public LoaderEntryReport(LoaderEntry entry, LoaderEntryStatus status)
    {
        Entry = entry;
        Status = status;
    }

    /// <summary>对应条目。</summary>
    public LoaderEntry Entry { get; }

    /// <summary>装配结果。</summary>
    public LoaderEntryStatus Status { get; }

    /// <summary>插件句柄（Loaded 时非空）。</summary>
    public IPluginHandle? Handle { get; init; }
}
