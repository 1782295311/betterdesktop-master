using System.Collections.Generic;

namespace BetterDesktop.Shell.Clipboard.Contracts;

/// <summary>
/// 表情包导入结果（逐文件回报，供 UI 明确告知"哪几个进来了、哪几个为什么没进来"）。
/// 【为什么要逐条错误 · 2026-09-12 纪律】导入失败必须可见 —— 静默跳过会让用户以为"图已经存好了"。
/// </summary>
/// <param name="Added">实际新增条数。</param>
/// <param name="Upgraded">
/// 已在剪贴板历史里（此前作为图片/文件条目存在）而被**原地升级为表情包**的条数 —— **不新增条目**。
/// 【2026-09-13】用户反馈："你就没有考虑过有些表情包已经被我们的剪贴板历史给记录了吗？"
/// 升级而非复制，列表里才不会出现两条同内容（一条"图片"、一条"表情包"）。
/// </param>
/// <param name="Skipped">因内容重复（同一张图已经是表情包）而跳过的条数。</param>
/// <param name="Errors">失败原因（每条含文件路径与原因，直接可显示给用户）。</param>
public sealed record StickerImportResult(
    int Added,
    int Upgraded,
    int Skipped,
    IReadOnlyList<string> Errors);
