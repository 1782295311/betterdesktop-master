using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Capture.Contracts;

namespace BetterDesktop.Ocr.Contracts;

/// <summary>OCR 引擎档位（优先级链 L1 → L2 → L3；L2/L3 为预留，当前仅 L1 在盒）。</summary>
public enum OcrEngineKind
{
    /// <summary>Windows.Media.Ocr（WinRT 在盒引擎，离线、零依赖）。</summary>
    WinRtInBox,

    /// <summary>PP-OCR ONNX + DirectML（预留 L2）。</summary>
    PpOcrOnnx,

    /// <summary>Windows AI / NPU 加速（预留 L3）。</summary>
    WindowsAiNpu,
}

/// <summary>识别出的单个词（含像素框与置信度；UI 可对低置信区域做可视标记）。</summary>
public sealed record OcrWord(string Text, PixelRect Box, float Confidence);

/// <summary>识别出的文本块（按行聚合；Box 为块级像素框）。</summary>
public sealed record OcrBlock(string Text, PixelRect Box, int LineCount);

/// <summary>
/// 识别结果。失败时 <see cref="Success"/> = false，<see cref="DegradeReason"/> 为可读原因——
/// 绝不返回空文本冒充成功（引擎不可用/无语言包/图片不存在/超时，各有明确原因）。
/// </summary>
public sealed record OcrResult(
    bool Success,
    string Text,
    IReadOnlyList<OcrBlock> Blocks,
    IReadOnlyList<OcrWord> Words,
    OcrEngineKind EngineUsed,
    string? DegradeReason)
{
    public static OcrResult Failed(string reason, OcrEngineKind engine) =>
        new(false, string.Empty, Array.Empty<OcrBlock>(), Array.Empty<OcrWord>(), engine, reason);
}

/// <summary>OCR 选项。</summary>
/// <param name="LanguageTag">BCP-47 语言标签；null = 使用系统用户语言。无匹配语言包时引擎不可用（可读原因）。</param>
/// <param name="MaxDimension">识别前长边缩放上限（px，0 = 不缩放；超大图降采样可显著提速，代价是小字精度）。</param>
/// <param name="TimeoutMs">识别超时（默认 10s；超时按失败处理并给可读原因）。</param>
public sealed record OcrOptions(string? LanguageTag = null, int MaxDimension = 0, int TimeoutMs = 10000);

/// <summary>
/// OCR 服务契约（离线、全本地；识别只读条目图片，不落盘副本、不改图片语义）。
/// <para>实现位于 capture exe（L1 = Windows.Media.Ocr）；面板与 capture exe 都只依赖本接口。</para>
/// </summary>
public interface IOcrService
{
    /// <summary>识别本地图片文件（PNG/JPEG 等）。</summary>
    Task<OcrResult> RecognizeAsync(string imagePath, OcrOptions options, CancellationToken ct);

    /// <summary>
    /// 识别剪贴板历史条目图片，成功后经剪贴板客户端回写 <c>set_ocr_text</c>——
    /// 调用方不需要碰引擎；文本挂到同一条目后参与 keyword 搜索命中。
    /// </summary>
    Task<OcrResult> RecognizeEntryAsync(string entryId, OcrOptions options, CancellationToken ct);
}
