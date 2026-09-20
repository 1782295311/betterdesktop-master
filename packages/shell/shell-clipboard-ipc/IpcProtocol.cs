namespace BetterDesktop.Shell.Clipboard.Ipc;

/// <summary>
/// 引擎 IPC 协议常量（与 engine/src/ipc.rs 对齐，勿单侧改动）。
/// </summary>
internal static class IpcProtocol
{
    /// <summary>命名管道名（引擎 CreateNamedPipeW；NamedPipeClientStream 会自动拼 \\.\pipe\ 前缀，故不携带）。</summary>
    public const string PipeName = "BetterDesktop.Clipboard.Engine";

    /// <summary>引擎日志中的完整管道名（诊断用）。</summary>
    public const string PipeFullName = @"\\.\pipe\" + PipeName;

    /// <summary>每帧可选 magic 前缀（首帧强制校验；客户端恒携带以对齐引擎剥离逻辑）。</summary>
    public const string Magic = "BDCB1|";

    /// <summary>RPC 默认超时（毫秒）。</summary>
    public const int DefaultTimeoutMs = 5000;

    /// <summary>引擎 query 单页上限。</summary>
    public const int MaxPageSize = 500;

    // ---------------- RPC methods ----------------
    public const string M_Ping = "ping";
    public const string M_Query = "query";
    public const string M_GetLast = "get_last";
    public const string M_GetEntry = "get_entry";
    public const string M_GetContent = "get_content";

    /// <summary>
    /// 存储占用与预算（面板据此提醒用户清理）。
    /// 总存储预算为**软限制**：超限不驱逐条目，仅上报 <c>overBudget</c> 由 UI 提醒。
    /// </summary>
    public const string M_StorageStatus = "storage_status";
    public const string M_Pin = "pin";
    public const string M_Unpin = "unpin";

    /// <summary>设置/取消「表情包」标记（与收藏同级的独立标记，任意条目可标记 · 2026-09-13）。</summary>
    public const string M_SetSticker = "set_sticker";
    public const string M_Delete = "delete";
    public const string M_DeleteMany = "delete_many";
    public const string M_ClearUnpinned = "clear_unpinned";
    public const string M_SetTags = "set_tags";

    /// <summary>【截图 OCR · 2026-09-14】把 OCR 识别文本写回图片条目（搜索 + 面板展示）。</summary>
    public const string M_SetOcrText = "set_ocr_text";
    public const string M_Copy = "copy_to_clipboard";

    /// <summary>【P2-3 临时粘贴】写回目标条目并暂存当前剪贴板（供还原）。</summary>
    public const string M_CopyTemp = "copy_temp_to_clipboard";

    /// <summary>【P2-3 临时粘贴】还原临时粘贴前的剪贴板内容。</summary>
    public const string M_RestoreTemp = "restore_temp_clipboard";


    /// <summary>表情包导入（用户主动填入本地动图；原文件字节级复制 + 内容哈希去重）。</summary>
    public const string M_AddSticker = "add_sticker";
    public const string M_Pause = "pause";
    public const string M_Resume = "resume";
    public const string M_ApplySettings = "apply_settings";
    public const string M_OpenPanel = "open_panel";
    public const string M_Subscribe = "subscribe_events";

    // ---------------- 事件通知 method ----------------
    public const string N_HistoryChanged = "history_changed";
    public const string N_PauseChanged = "pause_changed";
    public const string N_ClipboardChanged = "clipboard_changed";

    // ---------------- history_changed kind ----------------
    public const string K_Added = "added";
    public const string K_Updated = "updated";
    public const string K_Deleted = "deleted";
    public const string K_ClearedUnpinned = "cleared_unpinned";
}
