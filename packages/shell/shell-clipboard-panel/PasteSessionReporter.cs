// BetterDesktop 剪贴板面板 — 按序粘贴会话进度上报（面板 → 宿主 → 灵动岛）
//
// 【为什么需要它】按序粘贴 / 按格粘的状态机活在**本进程**（ClipboardIpcClient），壳进程看不见；
// 而灵动岛要呈现"第 2/5 项"的进度感。通道用既有的 `BetterDesktop.MenuCmd` 命名管道
// （CLI / tray 同款"辅助进程 → 宿主"通道，协议常量单点维护在 kernel 的 MenuCommandPipeClient）。
//
// 【隐私红线 · 2026-09-16 用户明确要求】只上报**进度**（第几项 / 共几项 / 是否按格粘），
// **绝不上报剪贴板内容**。岛上也就不存在"随便复制/粘贴就把内容显示出来"的路径。

using System;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Clipboard.Panel;

/// <summary>会话进度上报（宿主未运行时静默降级：面板交互绝不受影响）。</summary>
internal static class PasteSessionReporter
{
    /// <summary>命令名（与 host/Bootstrap.cs 的 switch case 对齐）。</summary>
    private const string Action = "paste-session";

    /// <summary>
    /// 上报一次会话进度。<paramref name="index"/> &lt;= 0 或 <paramref name="total"/> &lt;= 0 表示会话已结束/已取消。
    /// </summary>
    /// <param name="index">下一次粘贴将是第几项（1 起）。</param>
    /// <param name="total">会话总项数。</param>
    /// <param name="cell">true = 按格粘（表格逐格），false = 按序粘贴（多选逐条）。</param>
    /// <param name="preview">
    /// 下一次 Ctrl+V 将粘出的内容预览（岛显示用）。null/空 = 无文本预览（图片、文件等）。
    /// 【必须单行且不含 '|'】管道按行读、按 '|' 分段 —— 换行会截断消息，竖线会错位字段，故在此统一净化。
    /// </param>
    public static void Publish(int index, int total, bool cell, string? preview = null)
    {
        try
        {
            var safe = Sanitize(preview);
            MenuCommandPipeClient.TrySend(Action, $"{index}|{total}|{(cell ? 1 : 0)}|{safe}");
        }
        catch (Exception ex)
        {
            // 宿主不在（面板独立运行/正在启动）属常态：记一条痕迹即可，绝不冒泡影响面板交互。
            PanelLog.Trace($"会话进度上报失败（宿主可能未运行）: {ex.Message}");
        }
    }

    /// <summary>预览净化：压成单行、去掉字段分隔符、截断（管道协议是"按行 + 按 | 分段"）。</summary>
    private static string Sanitize(string? preview)
    {
        if (string.IsNullOrWhiteSpace(preview))
        {
            return string.Empty;
        }

        var flat = preview.ReplaceLineEndings(" ").Replace('|', '/').Trim();
        return flat.Length <= PreviewLimit ? flat : flat[..PreviewLimit] + "…";
    }

    /// <summary>预览截断长度（岛胶囊单行约 20 字，卡片详情行更长，取 60 兼顾两者）。</summary>
    private const int PreviewLimit = 60;
}
