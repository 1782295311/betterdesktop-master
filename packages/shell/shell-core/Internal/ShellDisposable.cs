using System;
using System.Threading;

namespace BetterDesktop.Shell.Core.Internal;

/// <summary>基于一次性动作的 IDisposable（供插件在 Effect 中注册窗口/资源清理）。</summary>
public sealed class ShellDisposable : IDisposable
{
    private Action? _action;

    /// <summary>构造。</summary>
    public ShellDisposable(Action action)
    {
        _action = action;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
