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
        context.Effect(() =>
        {
            return new DisposableAction(() => _engine?.ReturnToStock());
        }, "RestoreTaskbarOnExit");

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        _engine?.ReturnToStock();
        _engine?.Dispose();
        _engine = null;
        return Task.CompletedTask;
    }

    private sealed class DisposableAction : IDisposable
    {
        private readonly Action _action;
        public DisposableAction(Action action) => _action = action;
        public void Dispose() => _action();
    }
}
