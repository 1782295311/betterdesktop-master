// BetterDesktop.Kernel — ActionDisposable（内核内部工具）
// 一次性动作的 IDisposable 包装

namespace BetterDesktop.Kernel.Core;

/// <summary>一次性动作的 IDisposable 包装（线程安全，幂等）。</summary>
internal sealed class ActionDisposable : IDisposable
{
    private Action? _action;

    /// <summary>构造。</summary>
    public ActionDisposable(Action action)
    {
        _action = action;
    }

    /// <summary>执行一次并置空。</summary>
    public void Dispose()
    {
        var action = Interlocked.Exchange(ref _action, null);
        action?.Invoke();
    }
}
