namespace BetterDesktop.Shell.IndexIpc;

/// <summary>
/// 索引引擎 IPC 协议常量（与 <c>engine-index/src/ipc.rs</c>、<c>engine-index/src/engine.rs</c> 对齐，
/// **勿单侧改动**；改动需两侧同批）。
/// </summary>
internal static class IndexIpcProtocol
{
    /// <summary>命名管道名（引擎 CreateNamedPipeW；NamedPipeClientStream 自动拼 \\.\pipe\ 前缀，故不携带）。</summary>
    public const string PipeName = "BetterDesktop.Index.Engine";

    /// <summary>引擎日志中的完整管道名（诊断用）。</summary>
    public const string PipeFullName = @"\\.\pipe\" + PipeName;

    /// <summary>每帧 magic 前缀（引擎首帧强制校验；客户端恒携带以对齐引擎剥离逻辑）。</summary>
    public const string Magic = "BDIX1|";

    /// <summary>RPC 默认超时（毫秒）。</summary>
    public const int DefaultTimeoutMs = 5000;

    // ---------------- RPC methods ----------------
    public const string M_Ping = "ping";
    public const string M_Status = "status";
    public const string M_ApplySettings = "apply_settings";
    public const string M_Shutdown = "shutdown";

    /// <summary>应用索引快照（raw candidate；消费者经 C# 升格为 AppItem）。</summary>
    public const string M_ListApps = "list_apps";

    /// <summary>文件索引查询（只返回文件事实，不排序）。</summary>
    public const string M_SearchFiles = "search_files";

    /// <summary>
    /// 批量取图标（**一档 256×256 PNG**，用户硬约束：不做「用多大就申请多大」的多档）。
    /// 返回体为 base64 in JSON——与剪贴板引擎的二进制搬运同法；纪律相同：**绝不进列表载荷**。
    /// </summary>
    public const string M_GetIcons = "get_icons";

    /// <summary>
    /// `get_icons` 单次允许的最大键数（与引擎 <c>MAX_ICON_BATCH</c> **必须一致**：引擎对超限批次返回 -32602）。
    /// 客户端 <c>GetIconsAsync</c> 会自动按此切分，调用方无需关心。
    /// </summary>
    public const int MaxIconBatch = 64;

    // ---------------- JSON-RPC 错误码（与引擎同值） ----------------
    public const int ErrParse = -32700;
    public const int ErrInvalidRequest = -32600;
    public const int ErrMethodNotFound = -32601;
    public const int ErrInvalidParams = -32602;
    public const int ErrInternal = -32603;
}
