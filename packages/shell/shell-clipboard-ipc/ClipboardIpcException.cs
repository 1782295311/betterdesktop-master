namespace BetterDesktop.Shell.Clipboard.Ipc;

/// <summary>
/// IPC 调用失败（JSON-RPC 错误响应 / 超时 / 断线）。
/// </summary>
public sealed class ClipboardIpcException : Exception
{
    public ClipboardIpcException(string message) : base(message) { }

    public ClipboardIpcException(string message, Exception inner) : base(message, inner) { }

    /// <summary>JSON-RPC 错误码（引擎侧：-32601/-32602/-32603 等）；超时/断线时为 null。</summary>
    public long? RpcCode { get; init; }

    public static ClipboardIpcException Rpc(long code, string message) =>
        new(message) { RpcCode = code };
}
