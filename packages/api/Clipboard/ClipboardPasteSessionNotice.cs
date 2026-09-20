// BetterDesktop api — 剪贴板「按序粘贴 / 按格粘」会话状态（跨进程只读镜像）
//
// 【为什么放 api】生产方是面板 exe（经 MenuCmd 管道 → 宿主），消费方是灵动岛（宿主内插件），
// 两边都不该互相引用实现包，因此载荷与事件名落在公共契约层。

using System;

namespace BetterDesktop.Shell.Clipboard.Contracts;

/// <summary>
/// 剪贴板按序粘贴会话的**进度快照**。
/// <para>
/// 【隐私红线】本载荷**只含进度**（第几项 / 共几项 / 是否按格粘），**绝不含剪贴板内容**。
/// 会话状态需要从面板进程跨到壳进程，只要带一个内容字段，"粘贴/复制就上屏"立刻变成隐私事故
/// ——用户在 2026-09-16 明确要求过这一点。
/// </para>
/// </summary>
/// <param name="Active">会话是否进行中；false = 已结束/已取消，订阅方应收起呈现。</param>
/// <param name="Index">当前序号（1 起，表示"下一次粘贴将是第几项"）。</param>
/// <param name="Total">会话总项数（按序粘贴 = 选中条数；按格粘 = 单元格数）。</param>
/// <param name="Cell">true = 「按格粘」（表格逐格）而不是「按序粘贴」（多选逐条）。</param>
/// <param name="Preview">
/// **下一次 Ctrl+V 将粘出的内容预览**（截断后的单行文本；仅会话进行中携带，会话结束/取消时为 null）。
/// 这是 2026-09-14 岛文档里 D2「按序粘贴预览」的落地：用户主动发起会话时才显示，属"用户自己要看的"，
/// 与"随便复制就上屏"是两件事（隐私红线只禁后者）。
/// </param>
public sealed record ClipboardPasteSessionNotice(bool Active, int Index, int Total, bool Cell, string? Preview = null)
{
    /// <summary>进度 0..1（<see cref="Index"/> / <see cref="Total"/>）；未激活或总数为 0 时为 0。</summary>
    public double Progress => Active && Total > 0
        ? Math.Clamp((double)Index / Total, 0.0, 1.0)
        : 0.0;
}
