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

    /// <summary>按类型/分类/关键词/来源过滤历史条目（时间倒序）。</summary>
    IReadOnlyList<ClipboardEntry> GetFilteredEntries(
        ClipboardItemKind? kind = null,
        ContentCategory? category = null,
        string? keyword = null,
        string? sourceApp = null);

    /// <summary>来源应用列表（按出现次数降序，面板筛选栏用）。</summary>
    IReadOnlyList<string> GetSourceApps();

    /// <summary>最近复制内容（1301 融合；timeLimit 为空则最近一条）。</summary>
    LastCopiedContent? GetLastCopiedContent(TimeSpan? timeLimit = null);

    // ---------- 变更 ----------

    /// <summary>收藏条目（固定，不参与驱逐/过期）。</summary>
    void PinEntry(ClipboardEntry entry);

    /// <summary>取消收藏。</summary>
    void UnpinEntry(ClipboardEntry entry);

    /// <summary>切换收藏。</summary>
    void TogglePin(ClipboardEntry entry);

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

    /// <summary>写回并粘贴到前台窗口（基础路径，自动分段等增强属 v1.1）。</summary>
    void PasteEntryToActiveWindow(ClipboardEntry entry);

    /// <summary>文件条目：在资源管理器中打开位置并选中。</summary>
    void OpenFileLocation(ClipboardEntry entry);

    // ---------- 录入（OCR/外部来源，M1 契约预留） ----------

    /// <summary>批量录入外部条目（复用去重/分类/落盘管线）。</summary>
    int ImportEntries(IEnumerable<ClipboardImportItem> items);

    // ---------- 状态与控制 ----------

    /// <summary>监控是否开启（扩展中心开关控制监控活性，服务本体常驻）。</summary>
    bool IsMonitoringEnabled { get; }

    /// <summary>临时暂停（默认 60 秒）后自动恢复。</summary>
    void PauseTemporarily(int seconds = 60);

    /// <summary>立即恢复监控。</summary>
    void Resume();

    /// <summary>打开历史面板（UI 后续阶段接线，契约现就绪）。</summary>
    void OpenHistoryWindow();

    // ---------- 事件 ----------

    /// <summary>历史变更（新增/更新/删除/清空）。</summary>
    event Action<ClipboardHistoryChangedEventArgs>? HistoryChanged;

    /// <summary>暂停状态变更。</summary>
    event Action<bool>? PauseStateChanged;

    /// <summary>监控状态变更。</summary>
    event Action<bool>? MonitoringStateChanged;
}
