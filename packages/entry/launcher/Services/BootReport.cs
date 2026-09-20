// BetterDesktop 启动器 —— 启动过程报告（每一步的结果都要**可见**）。

using System.Collections.Generic;

namespace BetterDesktop.Launcher.Services;

/// <summary>单步状态。</summary>
internal enum StepState
{
    /// <summary>就绪 / 已完成。</summary>
    Ok,

    /// <summary>完成但有保留（降级、跳过、需用户知晓）。</summary>
    Warn,

    /// <summary>失败（缺件、无法拉起）——功能不可用，必须显式提示。</summary>
    Fail,
}

/// <summary>一步的结果。</summary>
/// <param name="State">状态。</param>
/// <param name="Title">一句话标题（用户可见）。</param>
/// <param name="Detail">细节（缺什么/做了什么/为什么跳过）。</param>
internal sealed record BootStep(StepState State, string Title, string Detail);

/// <summary>启动过程报告。</summary>
internal sealed class BootReport
{
    private readonly List<BootStep> _steps = new();

    public IReadOnlyList<BootStep> Steps => _steps;

    /// <summary>失败步数（>0 = 有功能不可用）。</summary>
    public int Failures { get; private set; }

    /// <summary>告警步数。</summary>
    public int Warnings { get; private set; }

    public void Add(StepState state, string title, string detail)
    {
        _steps.Add(new BootStep(state, title, detail));
        if (state == StepState.Fail)
        {
            Failures++;
        }
        else if (state == StepState.Warn)
        {
            Warnings++;
        }
    }
}
