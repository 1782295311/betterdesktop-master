namespace BetterDesktop.Shell.IndexIpc;

/// <summary>
/// 索引引擎 IPC 异常（连接不可用 / 超时 / 引擎返回 error）。
/// 调用方据此走**回退路径**（本地实现），不得让异常逃逸到 UI 线程。
/// </summary>
public sealed class IndexIpcException : Exception
{
    /// <summary>JSON-RPC 错误码；非引擎返回（本地故障）时为 null。</summary>
    public int? ErrorCode { get; }

    public IndexIpcException(string message, int? errorCode = null)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public IndexIpcException(string message, Exception inner, int? errorCode = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }
}
