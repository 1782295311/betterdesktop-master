namespace BetterDesktop.Kernel.Contracts;

/// <summary>电源与资源管理服务：控制进程优先级、允许系统休眠、通知内核空闲状态。</summary>
public interface IPowerManagement
{
    /// <summary>将进程优先级降为 BelowNormal，减少对前台应用的抢占。</summary>
    void LowerProcessPriority();

    /// <summary>允许系统在程序运行期间正常进入休眠/屏保。</summary>
    void AllowSystemSleep();

    /// <summary>通知内核进入空闲状态（减少定时器频率、降低事件轮询间隔）。</summary>
    void EnterIdle();

    /// <summary>通知内核退出空闲状态（恢复定时器频率）。</summary>
    void ExitIdle();

    /// <summary>当前是否处于空闲状态。</summary>
    bool IsIdle { get; }
}
