using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Capture.Contracts;
using BetterDesktop.Ocr.Contracts;
using SkiaSharp;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using WinOcr = Windows.Media.Ocr;

namespace BetterDesktop.Shell.Capture.Ocr;

/// <summary>
/// OCR L1 实现（Windows.Media.Ocr，Win10+ 内置，离线、零新增依赖）。
/// 红线：离线、不落盘副本（直接读图片字节）、不改图片语义、OCR 文本不进诊断日志、
/// 部分失败语义 = 绝不返回空文本冒充成功（引擎不可用/无语言包/图片缺失/超时各有明确原因）。
/// 准确度（2026-09-15）：识别前经 SkiaSharp 预处理管线——小图放大（<700px 短边 → ~1200）、
/// 灰度化 + 自适应对比度拉伸（1%/99% 百分位）——对低对比/彩色背景/小字截图的 WinRT OCR
/// 准确率显著提升；预处理任何一步失败回退原解码路径，绝不丢识别。
/// </summary>
public sealed class WindowsMediaOcr : IOcrService
{
    private readonly WinOcr.OcrEngine _engine;

    /// <summary>创建引擎：优先指定语言 → 用户语言包（全语言 OCR）→ 中文 → 英文；全失败抛异常（可读原因）。</summary>
    public WindowsMediaOcr(string? languageTag = null)
    {
        _engine = CreateEngine(languageTag)
            ?? WinOcr.OcrEngine.TryCreateFromUserProfileLanguages()
            ?? WinOcr.OcrEngine.TryCreateFromLanguage(new Language("zh-Hans-CN"))
            ?? WinOcr.OcrEngine.TryCreateFromLanguage(new Language("en-US"));
        if (_engine is null)
        {
            throw new InvalidOperationException("系统未安装任何 OCR 语言包（设置 → 时间和语言 → 语言 → 添加 OCR 语言）");
        }
    }

    private static WinOcr.OcrEngine? CreateEngine(string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag))
        {
            return null;
        }
        try
        {
            var lang = new Language(languageTag);
            return WinOcr.OcrEngine.IsLanguageSupported(lang) ? WinOcr.OcrEngine.TryCreateFromLanguage(lang) : null;
        }
        catch
        {
            return null;
        }
    }

    public OcrEngineKind Kind => OcrEngineKind.WinRtInBox;

    public Task<OcrResult> RecognizeAsync(string imagePath, OcrOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return Task.FromResult(OcrResult.Failed($"图片不存在：{imagePath}", Kind));
        }
        return RecognizeCoreAsync(imagePath, options, ct);
    }

    /// <summary>
    /// 识别剪贴板历史条目图片（引擎存储根 %LOCALAPPDATA%\BetterDesktop + 条目 ImagePath），
    /// 成功后回写 <c>set_ocr_text</c> 挂到同一条目（OCR 计划红线：识别结果与图片同一历史条目）。
    /// </summary>
    public async Task<OcrResult> RecognizeEntryAsync(string entryId, OcrOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entryId))
        {
            return OcrResult.Failed("条目 ID 为空", Kind);
        }

        string? imagePath = null;
        try
        {
#pragma warning disable CA2000 // 所有权转移：transport 由 ClipboardIpcClient.Dispose 释放（仓库既有模式）
            using var client = new BetterDesktop.Shell.Clipboard.Ipc.ClipboardIpcClient(
                new BetterDesktop.Shell.Clipboard.Ipc.NamedPipeTransport());
#pragma warning restore CA2000
            client.Connect();
            var entry = client.GetEntryById(entryId);
            if (entry is null)
            {
                return OcrResult.Failed($"条目不存在：{entryId}", Kind);
            }
            if (string.IsNullOrEmpty(entry.ImagePath))
            {
                return OcrResult.Failed("该条目不是图片", Kind);
            }
            imagePath = Path.IsPathRooted(entry.ImagePath)
                ? entry.ImagePath
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BetterDesktop", entry.ImagePath);
        }
        catch (Exception ex)
        {
            return OcrResult.Failed($"条目读取失败：{ex.Message}", Kind);
        }

        if (!File.Exists(imagePath))
        {
            return OcrResult.Failed($"条目图片缺失：{imagePath}", Kind);
        }

        var result = await RecognizeCoreAsync(imagePath, options, ct).ConfigureAwait(false);
        if (result.Success && !string.IsNullOrWhiteSpace(result.Text))
        {
            try
            {
#pragma warning disable CA2000 // 所有权转移：transport 由 ClipboardIpcClient.Dispose 释放（仓库既有模式）
                using var client = new BetterDesktop.Shell.Clipboard.Ipc.ClipboardIpcClient(
                    new BetterDesktop.Shell.Clipboard.Ipc.NamedPipeTransport());
#pragma warning restore CA2000
                client.Connect();
                client.SetEntryOcrText(entryId, result.Text);
            }
            catch (Exception ex)
            {
                // 文本仍随识别结果返回（面板可展示/复制）；回写失败不冒充识别失败。
                return new OcrResult(true, result.Text, result.Blocks, result.Words, Kind,
                    $"识别成功但回写失败：{ex.Message}");
            }
        }
        return result;
    }

    private async Task<OcrResult> RecognizeCoreAsync(string imagePath, OcrOptions options, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(Math.Clamp(options.TimeoutMs > 0 ? options.TimeoutMs : 10000, 1, 120000));
            var token = timeoutCts.Token;

            byte[] bytes = await File.ReadAllBytesAsync(imagePath, token).ConfigureAwait(false);
            SoftwareBitmap bitmap;
            try
            {
#pragma warning disable CA2000 // 所有权转移：返回后由调用方 using(bitmap) 释放（仓库既有模式）
                bitmap = await PrepareBitmapAsync(bytes, options.MaxDimension, token).ConfigureAwait(false);
#pragma warning restore CA2000
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 预处理失败（异常格式等）→ 回退原解码路径，不丢识别
                try
                {
#pragma warning disable CA2000 // 所有权转移：返回后由调用方 using(bitmap) 释放（仓库既有模式）
                    bitmap = await DecodeAsync(bytes, options.MaxDimension, token).ConfigureAwait(false);
#pragma warning restore CA2000
                }
                catch (Exception ex2)
                {
                    return OcrResult.Failed($"图片解码失败：{ex2.Message}", Kind);
                }
            }

            using (bitmap)
            {
                // OcrEngine.RecognizeAsync 要求 Bgra8 + Premultiplied/Ignore alpha；异常时转 Gray8 重试。
                WinOcr.OcrResult winResult;
                try
                {
                    winResult = await _engine.RecognizeAsync(bitmap).AsTask(token).ConfigureAwait(false);
                }
                catch (ArgumentException)
                {
                    using var gray = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Gray8, BitmapAlphaMode.Ignore);
                    winResult = await _engine.RecognizeAsync(gray).AsTask(token).ConfigureAwait(false);
                }

                if (winResult is null)
                {
                    return OcrResult.Failed("OCR 引擎未返回结果", Kind);
                }

                // 映射为契约模型（调用方不碰 WinRT 类型）。Windows.Media.Ocr 不提供置信度 → 1.0（诚实标注）。
                var words = new List<OcrWord>();
                var blocks = new List<OcrBlock>();
                foreach (var line in winResult.Lines)
                {
                    var lineWords = new List<OcrWord>();
                    foreach (var w in line.Words)
                    {
                        lineWords.Add(new OcrWord(
                            w.Text,
                            new PixelRect(
                                (int)w.BoundingRect.X,
                                (int)w.BoundingRect.Y,
                                (int)w.BoundingRect.Width,
                                (int)w.BoundingRect.Height),
                            1.0f));
                    }
                    // 词间智能拼接：CJK/全角/标点相邻不加空格（WinRT OCR 把每个汉字拆成独立词，
                    // 直接 Join(" ") 会输出「你 好 世 界」）；纯英文/数字词间保留空格。
                    var text = JoinWords(lineWords.Select(x => x.Text)).Trim();
                    if (text.Length == 0 && lineWords.Count == 0)
                    {
                        continue;
                    }
                    words.AddRange(lineWords);
                    // OcrLine 无 BoundingRect → 行框取词框并集。
                    var lineBox = lineWords.Count > 0
                        ? Union(lineWords.Select(x => x.Box))
                        : new PixelRect(0, 0, 0, 0);
                    blocks.Add(new OcrBlock(text, lineBox, 1)); // 本实现按行出块；跨行聚合是预留后处理（OCR 计划 Post-OCR）。
                }

                string fullText = string.Join("\n", blocks.Select(b => b.Text));
                // 有引擎结果但全文为空 = 原图无文字（合法空，Success=true）；引擎/解码/超时失败已走 Failed。
                return new OcrResult(true, fullText, blocks, words, Kind, null);
            }
        }
        catch (OperationCanceledException)
        {
            return OcrResult.Failed(ct.IsCancellationRequested ? "已取消" : "识别超时", Kind);
        }
        catch (Exception ex)
        {
            return OcrResult.Failed($"识别异常：{ex.Message}", Kind);
        }
    }

    /// <summary>词间智能拼接：任一侧为 CJK/全角/标点 → 不加空格；两侧均为字母数字 → 空格。</summary>
    private static string JoinWords(IEnumerable<string> words)
    {
        var sb = new System.Text.StringBuilder();
        string? prev = null;
        foreach (var w in words)
        {
            if (prev is not null && prev.Length > 0 && w.Length > 0 && NeedSpace(prev[^1], w[0]))
            {
                sb.Append(' ');
            }
            sb.Append(w);
            prev = w;
        }
        return sb.ToString();
    }

    private static bool NeedSpace(char a, char b)
    {
        return !(IsCjk(a) || IsCjk(b) || IsPunct(a) || IsPunct(b));
    }

    /// <summary>CJK 基本区/扩展 A/扩展 B 起止 + 全角字符（含全角标点、日文假名）。</summary>
    private static bool IsCjk(char c)
    {
        return (c >= 0x3400 && c <= 0x4DBF)   // 扩展 A
            || (c >= 0x4E00 && c <= 0x9FFF)   // 基本区
            || (c >= 0xF900 && c <= 0xFAFF)   // 兼容表意
            || (c >= 0xFF00 && c <= 0xFFEF)   // 全角/半角变体（含 ：）
            || (c >= 0x3040 && c <= 0x30FF);  // 日文假名
    }

    private static bool IsPunct(char c)
    {
        return !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c);
    }

    /// <summary>词框并集（行框）。空输入返回 0 矩形。</summary>
    private static PixelRect Union(IEnumerable<PixelRect> boxes)
    {
        int x1 = int.MaxValue, y1 = int.MaxValue, x2 = int.MinValue, y2 = int.MinValue;
        foreach (var b in boxes)
        {
            x1 = Math.Min(x1, b.X);
            y1 = Math.Min(y1, b.Y);
            x2 = Math.Max(x2, b.Right);
            y2 = Math.Max(y2, b.Bottom);
        }
        return x2 > x1 && y2 > y1 ? PixelRect.FromLTRB(x1, y1, x2, y2) : new PixelRect(0, 0, 0, 0);
    }

    /// <summary>
    /// 识别前预处理管线（提升 WinRT OCR 准确率）：
    /// 解码 → 缩放（小图短边&lt;700 放大至~1200；超引擎上限缩至 cap）→ 灰度 + 自适应对比度拉伸（1%/99% 百分位）
    /// → SoftwareBitmap(BGRA8, Premultiplied)。任何一步抛异常由调用方回退原解码路径。
    /// </summary>
    private static async Task<SoftwareBitmap> PrepareBitmapAsync(byte[] bytes, int maxDimension, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var original = SKBitmap.Decode(bytes)
            ?? throw new InvalidOperationException("SkiaSharp 无法解码图片");

        int w = original.Width;
        int h = original.Height;
        int cap = (int)WinOcr.OcrEngine.MaxImageDimension;
        if (maxDimension > 0)
        {
            cap = Math.Min(cap, maxDimension);
        }

        // 缩放：小图放大（小字区域识别率显著提升）；超大图缩到引擎上限（引擎对超大图抛 E_INVALIDARG）
        double scale = 1.0;
        if (Math.Max(w, h) > cap)
        {
            scale = (double)cap / Math.Max(w, h);
        }
        else if (Math.Min(w, h) > 0 && Math.Min(w, h) < 700)
        {
            scale = Math.Min(2.0, 1200.0 / Math.Min(w, h));
        }

        SKBitmap working;
        if (Math.Abs(scale - 1.0) > 1e-6)
        {
            int nw = Math.Max(1, (int)Math.Round(w * scale));
            int nh = Math.Max(1, (int)Math.Round(h * scale));
            using var resized = original.Resize(new SKImageInfo(nw, nh), SKFilterQuality.High);
            working = (resized ?? original).Copy();
        }
        else
        {
            working = original.Copy();
        }

        using (working)
        {
            PreprocessGrayContrast(working);
            return ToSoftwareBitmap(working);
        }
    }

    /// <summary>
    /// 灰度化（BT.601 加权）+ 自适应对比度拉伸（1%/99% 百分位 → 线性映射到 0-255）。
    /// 仅当动态范围足够（hi-lo≥8）才拉伸，避免把本就清晰的白底黑字图过度处理。
    /// 直接就地改 BGRA8 像素（alpha 保留）；非 BGRA8 布局跳过（极少见，走引擎原样识别）。
    /// </summary>
    private static unsafe void PreprocessGrayContrast(SKBitmap bmp)
    {
        if (bmp.ColorType != SKColorType.Bgra8888)
        {
            return;
        }
        IntPtr px = bmp.GetPixels();
        if (px == IntPtr.Zero)
        {
            return;
        }
        int width = bmp.Width;
        int height = bmp.Height;
        long stride = bmp.RowBytes;
        byte* p = (byte*)px.ToPointer();

        // pass 1：灰度 + 亮度直方图（BT.601 加权，就地写回）
        var hist = new int[256];
        for (int y = 0; y < height; y++)
        {
            byte* row = p + y * stride;
            for (int x = 0; x < width; x++)
            {
                byte* q = row + x * 4L;
                int gy = (q[0] * 114 + q[1] * 587 + q[2] * 299 + 500) / 1000;
                q[0] = (byte)gy;
                q[1] = (byte)gy;
                q[2] = (byte)gy;
                hist[gy]++;
            }
        }

        // pass 2：1%/99% 百分位 → 线性拉伸
        int lo = Percentile(hist, 1);
        int hi = Percentile(hist, 99);
        if (hi - lo < 8)
        {
            return; // 动态范围太窄/已接近二值：不处理
        }
        double k = 255.0 / (hi - lo);
        for (int y = 0; y < height; y++)
        {
            byte* row = p + y * stride;
            for (int x = 0; x < width; x++)
            {
                byte* q = row + x * 4L;
                int ny = (int)((q[0] - lo) * k);
                ny = ny < 0 ? 0 : (ny > 255 ? 255 : ny);
                q[0] = (byte)ny;
                q[1] = (byte)ny;
                q[2] = (byte)ny;
            }
        }
    }

    /// <summary>直方图百分位亮度（0-255）。</summary>
    private static int Percentile(int[] hist, int pct)
    {
        long total = 0;
        for (int i = 0; i < 256; i++)
        {
            total += hist[i];
        }
        long target = total * pct / 100;
        long acc = 0;
        for (int i = 0; i < 256; i++)
        {
            acc += hist[i];
            if (acc >= target)
            {
                return i;
            }
        }
        return 255;
    }

    /// <summary>SKBitmap(BGRA8) → SoftwareBitmap(BGRA8, Premultiplied)（memcpy 逐行，处理行距差异）。</summary>
    private static unsafe SoftwareBitmap ToSoftwareBitmap(SKBitmap bmp)
    {
        var sb = new SoftwareBitmap(BitmapPixelFormat.Bgra8, bmp.Width, bmp.Height, BitmapAlphaMode.Premultiplied);
        using (var buffer = sb.LockBuffer(BitmapBufferAccessMode.Write))
        using (var reference = buffer.CreateReference())
        {
            // CsWinRT 投影对象 cast 到自定义 COM 接口 = 运行时 QueryInterface
            var memory = (IMemoryBufferByteAccess)reference;
            memory.GetBuffer(out byte* dst, out uint dstCap);
            byte* src = (byte*)bmp.GetPixels().ToPointer();
            long srcRow = bmp.RowBytes;
            uint dstRow = (uint)(bmp.Width * 4);
            uint copy = Math.Min((uint)srcRow, dstRow);
            for (int y = 0; y < bmp.Height; y++)
            {
                Buffer.MemoryCopy(src + y * srcRow, dst + y * dstRow, dstCap - (ulong)y * dstRow, copy);
            }
        }
        return sb;
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        unsafe void GetBuffer(out byte* buffer, out uint capacity);
    }

    /// <summary>
    /// 图片字节 → SoftwareBitmap（BGRA8）。超出引擎最大尺寸（通常 2600px）或 options.MaxDimension 时
    /// 用 WinRT BitmapTransform 等比缩小（OcrEngine 对超大图抛 E_INVALIDARG，必须先降采样；
    /// 「不改图片语义」指不改原图文件——缩略仅影响识别输入）。
    /// </summary>
    private static async Task<SoftwareBitmap> DecodeAsync(byte[] bytes, int maxDimension, CancellationToken ct)
    {
        using var stream = new MemoryStream(bytes);
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream()).AsTask(ct).ConfigureAwait(false);
        var source = await decoder.GetSoftwareBitmapAsync().AsTask(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        int w = source.PixelWidth;
        int h = source.PixelHeight;
        int cap = (int)WinOcr.OcrEngine.MaxImageDimension;
        if (maxDimension > 0)
        {
            cap = Math.Min(cap, maxDimension);
        }

        if (Math.Max(w, h) <= cap)
        {
            return SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }

        using var src2 = source;
        double scale = (double)cap / Math.Max(w, h);
        var tf = new BitmapTransform
        {
            ScaledWidth = Math.Max(1u, (uint)(w * scale)),
            ScaledHeight = Math.Max(1u, (uint)(h * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, tf,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct).ConfigureAwait(false);
    }
}
