using System;
using System.Collections.Generic;

namespace BetterDesktop.Shell.Clipboard.Contracts;

/// <summary>
/// 剪贴板系统级服务契约（公共 API 信息源，注册于内核服务图 ADR-002 D1）。
/// 任意板块（含第三方扩展，仅引用 BetterDesktop.Api）经 <c>IContext.Get&lt;IClipboardService&gt;()</c> 消费。
/// 契约可加性：v1.1+ 新能力以新增方法/事件扩展，不改既有成员。
/// </summary>
public interface IClipboardService
{
    // ---------- 查询 ----------

    /// <summary>最近复制内容（1301 融合；timeLimit 为空则最近一条）。</summary>
    LastCopiedContent? GetLastCopiedContent(TimeSpan? timeLimit = null);

    // ---------- 变更 ----------

    /// <summary>收藏条目（固定，不参与驱逐/过期）。</summary>
    void PinEntry(ClipboardEntry entry);

    /// <summary>取消收藏。</summary>
    void UnpinEntry(ClipboardEntry entry);

    /// <summary>
    /// 设置/取消「表情包」标记（**与收藏同级的独立标记**，2026-09-13）。
    /// <para>
    /// 用户口径："跟收藏一样的机制，这样就不管是图片还是颜文字都可以了" —— 任何条目都能被标记。
    /// 标记不改变条目内容，只让它出现在「表情包」筛选里、并豁免驱逐与「清理未收藏」。
    /// </para>
    /// </summary>
    void SetSticker(ClipboardEntry entry, bool value);

    /// <summary>删除单条。</summary>
    void DeleteEntry(ClipboardEntry entry);

    /// <summary>删除多条。</summary>
    void DeleteEntries(IEnumerable<ClipboardEntry> entries);

    /// <summary>清除非收藏条目。</summary>
    void ClearAllUnpinned();

    /// <summary>设置标签（可搜索）。</summary>
    void SetEntryTags(ClipboardEntry entry, string tags);

    // ---------- 粘贴 ----------

    /// <summary>按分类写回剪贴板（代码强制纯文本；富文本多格式；混合保留完整 HTML）。</summary>
    void CopyEntryToClipboard(ClipboardEntry entry);

    /// <summary>纯文本写回。</summary>
    void CopyEntryAsPlainText(ClipboardEntry entry);

    /// <summary>写回并粘贴到前台窗口（基础路径；大段自动分段，代码/图片/文件单次）。</summary>
    void PasteEntryToActiveWindow(ClipboardEntry entry);

    /// <summary>
    /// 临时粘贴：写回并粘贴到前台窗口，随后**还原**用户原剪贴板（2026-09-13 P2-3）。
    /// <para>
    /// 用于"只想粘一下、不想污染剪贴板"的场景。原剪贴板快照在引擎侧（纯内存、不落盘）；
    /// 超过有效期（10s）则不复原（避免覆盖用户期间的新复制）。还原失败不影响已完成的粘贴。
    /// </para>
    /// <para>契约可加性：本成员为新增，不改动既有成员（旧消费方零影响）。</para>
    /// </summary>
    void PasteEntryTemporarilyToActiveWindow(ClipboardEntry entry);

    /// <summary>多选合并粘贴：多条纯文本以分隔符拼接后一次粘贴（面板多选合并）。</summary>
    void MergePasteToActiveWindow(IEnumerable<ClipboardEntry> entries, string? separator = null);

    // ---------- L 按序粘贴状态机（v1.3；消费方按 Enter 依次触发 PasteNextSequential） ----------

    /// <summary>按序粘贴是否处于激活且未完成状态。</summary>
    bool IsSequentialPasteActive { get; }

    /// <summary>剩余待粘贴条数。</summary>
    int SequentialRemaining { get; }

    /// <summary>开始按序粘贴（列表非空；覆盖已有会话）。</summary>
    void BeginSequentialPaste(IReadOnlyList<ClipboardEntry> entries);

    /// <summary>粘贴下一条（未激活/已完成时无操作并记日志）。</summary>
    void PasteNextSequential();

    /// <summary>取消按序粘贴会话。</summary>
    void CancelSequentialPaste();

    // ---------- 表情包（用户主动填入的动图；2026-09-12 新增） ----------

    /// <summary>
    /// 导入表情包：把本地动图文件（gif/webp/apng/png/jpg/bmp）**原文件字节级复制**进剪贴板存储，
    /// 条目归入 <see cref="ContentCategory.Sticker"/>；之后可像普通条目一样复制/粘贴跨应用使用。
    /// <para>
    /// 语义：① **内容哈希去重** —— 同一张图重复导入计入 <see cref="StickerImportResult.Skipped"/>，
    /// 不产生副本堆积；② 表情包是用户珍藏 —— **不参与**过期/容量驱逐，也**不被**「清理未收藏」删除；
    /// ③ 失败**逐条回报**（<see cref="StickerImportResult.Errors"/>），绝不静默跳过。
    /// </para>
    /// <para>契约可加性：本成员为新增，不改动既有成员（旧消费方零影响）。</para>
    /// </summary>
    StickerImportResult AddStickers(IEnumerable<string> filePaths);

    // ---------- 状态与控制 ----------

    /// <summary>当前是否处于临时暂停。</summary>
    bool IsTemporarilyPaused { get; }

    /// <summary>暂停剩余秒数（未暂停为 0）。</summary>
    int PauseRemainingSeconds { get; }

    /// <summary>临时暂停（默认 60 秒）后自动恢复。</summary>
    void PauseTemporarily(int seconds = 60);

    /// <summary>立即恢复监控。</summary>
    void Resume();

    /// <summary>打开历史面板（engine 后端 = 拉起面板 exe）。</summary>
    void OpenHistoryWindow();

    // ---------- 事件 ----------

    /// <summary>历史变更（新增/更新/删除/清空）。</summary>
    event Action<ClipboardHistoryChangedEventArgs>? HistoryChanged;

    /// <summary>暂停状态变更。</summary>
    event Action<bool>? PauseStateChanged;
}
