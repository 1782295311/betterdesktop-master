// BetterDesktop.Kernel — EffectRegistration（内核内部工具）
// 托管清理器登记：显式注销抛异常；fiber 卸载走 TryDispose 隔离

namespace BetterDesktop.Kernel.Core;

/// <summary>托管清理器登记（ADR-002 D1 effect 语义）。</summary>
internal sealed class EffectRegistration
{
    private IDisposable? _disposer;
    private bool _disposed;

    /// <summary>构造。</summary>
    public EffectRegistration(string? label, IDisposable disposer)
    {
        Label = label;
        _disposer = disposer;
    }

    /// <summary>清理器标签（诊断用）。</summary>
    public string? Label { get; }

    /// <summary>显式注销：异常向上抛（调用方自担）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _disposer?.Dispose();
        _disposer = null;
    }

    /// <summary>fiber 卸载路径：单条异常隔离，异常返回给调用方记录。</summary>
    public Exception? TryDispose()
    {
        if (_disposed)
        {
            return null;
        }
        _disposed = true;
        try
        {
            _disposer?.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
        finally
        {
            _disposer = null;
        }
    }
}
