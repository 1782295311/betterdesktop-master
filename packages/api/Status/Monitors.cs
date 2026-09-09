// BetterDesktop.Shell.Status — 各状态项的类型化契约
// 均继承 IStatusMonitor，语义快照统一由 StatusSnapshot 承载；
// 类型化接口仅作依赖注入时的强类型标识（消费方 Inject 更精确、可替换实现）。

namespace BetterDesktop.Shell.Status.Contracts;

/// <summary>物理内存占用监控。</summary>
public interface IMemoryMonitor : IStatusMonitor { }

/// <summary>电源 / 电池状态监控。</summary>
public interface IBatteryMonitor : IStatusMonitor { }

/// <summary>输出端点（扬声器/耳机）音量监控。</summary>
public interface IVolumeMonitor : IStatusMonitor { }

/// <summary>输入端点（麦克风）静音/状态监控。</summary>
public interface IMicrophoneMonitor : IStatusMonitor { }

/// <summary>网络连接状态监控。</summary>
public interface INetworkMonitor : IStatusMonitor { }

/// <summary>输入法 / 键盘布局状态监控。</summary>
public interface IImeMonitor : IStatusMonitor { }

/// <summary>CPU 利用率监控。</summary>
public interface ICpuMonitor : IStatusMonitor { }

/// <summary>
/// 显示器亮度监控（单一数据源）：读范围 / 写亮度，供控制中心、主题等所有面板共用并同步。
/// </summary>
public interface IBrightnessMonitor : IStatusMonitor
{
    /// <summary>环境是否支持亮度调节（无支持显示器时为 false）。</summary>
    bool IsBrightnessSupported { get; }

    /// <summary>读取亮度范围（min/current/max，均为原始值）。返回 false 表示不支持。</summary>
    bool TryGetRange(out int min, out int current, out int max);

    /// <summary>把亮度写入目标原始值（将自动夹紧到 [min,max]；不支持时忽略）。</summary>
    void SetValue(int value);
}
