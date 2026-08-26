using System.Diagnostics;
using System.Runtime.InteropServices;
using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Kernel.Core;

/// <summary>电源与资源管理服务实现。</summary>
public sealed class PowerManagement : IPowerManagement, IPlugin
{
    private readonly IKernelLogger _logger;
    private bool _isIdle;

    public PowerManagement(IKernelLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => "kernel.power";

    /// <inheritdoc />
    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    /// <inheritdoc />
    public bool IsIdle => _isIdle;

    /// <inheritdoc />
    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        context.Provide<IPowerManagement>(this);

        // 启动时立即执行：降低优先级 + 允许系统休眠
        LowerProcessPriority();
        AllowSystemSleep();
        _logger.Info("电源管理已启动：进程优先级 BelowNormal，系统休眠已放行");

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        // 恢复系统休眠抑制（如果之前被其他程序抑制过）
        AllowSystemSleep();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void LowerProcessPriority()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            _logger.Info($"进程优先级已降为 BelowNormal (PID: {process.Id})");
        }
        catch (Exception ex)
        {
            _logger.Warn($"降低进程优先级失败（可能需要管理员权限）：{ex.Message}");
        }
    }

    /// <inheritdoc />
    public void AllowSystemSleep()
    {
        // 调用 Win32 API，允许系统正常休眠
        // ES_CONTINUOUS | ES_SYSTEM_REQUIRED 只在用户活跃时保持唤醒
        SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
        _logger.Info("已允许系统正常进入休眠/屏保");
    }

    /// <inheritdoc />
    public void EnterIdle()
    {
        if (_isIdle) return;
        _isIdle = true;
        _logger.Info("内核进入空闲状态：定时器降频、事件轮询间隔增大");
    }

    /// <inheritdoc />
    public void ExitIdle()
    {
        if (!_isIdle) return;
        _isIdle = false;
        _logger.Info("内核退出空闲状态：恢复正常运行");
    }

    // Win32 API：线程执行状态管理
    [DllImport("kernel32.dll")]
    private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

    [Flags]
    private enum EXECUTION_STATE : uint
    {
        ES_CONTINUOUS = 0x80000000,
        ES_SYSTEM_REQUIRED = 0x00000001,
        ES_DISPLAY_REQUIRED = 0x00000002,
        ES_AWAYMODE_REQUIRED = 0x00000040
    }
}
