using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.Taskbar.Contracts;
using BetterDesktop.Shell.Taskbar.Sections;
using BetterDesktop.Shell.Taskbar.Services;

namespace BetterDesktop.Shell.Taskbar;

// ============================================================
// 【白话导航 · 任务栏外观域】凭白话需求定位到精确文件：
//   "任务栏透明 / 模糊 / 亚克力效果"   → Services/TaskbarAppearanceEngine.cs（DWM 外观状态机）
//   "找不到任务栏窗口 / 多显示器任务栏" → Native/TaskbarWindowFinder.cs
//   "全屏应用时任务栏自动恢复/隐藏"    → Native/AppVisibilityWatcher.cs（窗口可见性事件）
//   "任务栏外观设置页"                → Sections/TaskbarAppearanceSection.cs
//   实现方法参考 TranslucentTB / Open-Shell（见引擎内注释署名）。
// ============================================================

/// <summary>
/// 任务栏外观控制插件：接管原生 Windows 任务栏的透明/模糊/亚克力与场景化外观联动。
/// 灵感与方法来自 参考/TranslucentTB-release 与 参考/Open-Shell-Menu-master。
/// </summary>
public sealed class TaskbarAppearancePlugin : IPlugin
{
    /// <inheritdoc />
    public string Name => "shell.taskbar.appearance";

    /// <inheritdoc />
    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    private TaskbarAppearanceEngine? _engine;

    /// <inheritdoc />
    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        // 引擎在 UI 线程外启动（WinEventHook / COM 初始化需在 UI 线程或 MTA）。
        _engine = new TaskbarAppearanceEngine(context.Logger);

        // 对外提供任务栏外观服务（设置分区经此实时套用）。
        context.Provide<ITaskbarAppearanceService>(_engine);
        // 设置分区（Build 仅接 ISettingsService）经此桥取回服务实例。
        Services.TaskbarServiceBridge.Bind(_engine);

        // 注册设置分区（侧栏"任务栏外观"一项）。
        var registry = context.Get<ISettingsSectionRegistry>();
        registry?.Register(new TaskbarAppearanceSection());

        // 启动引擎（枚举任务栏、连接 Win11 桥、订阅事件）。
        try
        {
            _engine.Start();
        }
        catch (Exception ex)
        {
            context.Logger.Warn($"[TaskbarAccent] 引擎启动失败，外观控制未生效：{ex.Message}");
        }

        // 进程退出/插件卸载时还原任务栏到默认（防止 explorer 残留异常外观）。
        //
        // 【2026-09-17 L1 收敛：任务栏外观 = 常态化接管，**壳关不还原**】
        // 归属表（L1 系统接管）里任务栏管理由「壳 / Agent」轮值持有：壳在 → 壳持有；壳走 → Agent 接管
        // （agent/Capabilities/ExclusiveCapabilityHost + HostPresenceWatcher）。
        // 因此壳**优雅退出**时不得先还原再等 Agent 重刷——那是肉眼可见的一闪，也与"壳关不还原"相悖。
        // 判据：退出/卸载当刻 Agent 是否在运行 = 交班有没有人接。
        //   · Agent 在   → 跳过还原（外观连续，由 Agent 无缝接手）；
        //   · Agent 不在 → 仍然还原（没人接手就别留着我们的外观）。
        // 进程**崩溃/强杀**那条路不由这里管：explorer 侧的 RestoreAllWhenProcessDies 兜底（见 ExplorerTapBridge）。
        context.Effect(() =>
        {
            return new DisposableAction(() =>
            {
                if (IsAgentRunning())
                {
                    context.Logger.Info("[TaskbarAccent] 壳退出：Agent 在场（L1 常驻接管）→ 保留当前外观，不还原");
                    return;
                }

                _engine?.ReturnToStock();
            });
        }, "RestoreTaskbarOnExit");

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        // 同 RestoreTaskbarOnExit 的 L1 判据：交班给 Agent 时不还原（否则"卸载 → 再装载"之间会一闪）。
        // 注意本卸载也发生在 Agent 侧（壳回来时它让出）——那时"Agent 在运行"必然为真 → 不还原，
        // 由回来的壳直接套用自己的外观，视觉连续。
        if (!IsAgentRunning())
        {
            _engine?.ReturnToStock();
        }

        _engine?.Dispose();
        _engine = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// L1 常驻接管者（Agent 常驻能力宿主，BetterDesktop.Agent）是否在运行 ——
    /// 决定"壳退出/卸载时要不要把任务栏外观还原成系统默认"。
    /// 只探测进程名（与 agent/Capabilities/HostPresenceWatcher.cs 同款廉价做法），不引入跨包依赖。
    /// </summary>
    private static bool IsAgentRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("BetterDesktop.Agent").Length > 0;
        }
        catch
        {
            // 探测失败按"没人接手"处理 → 还原，保证 explorer 不残留异常外观（保守侧）
            return false;
        }
    }

    private sealed class DisposableAction : IDisposable
    {
        private readonly Action _action;
        public DisposableAction(Action action) => _action = action;
        public void Dispose() => _action();
    }
}
