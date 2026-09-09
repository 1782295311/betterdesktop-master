// BetterDesktop.Shell.Convert — HEIC/HEIF/AVIF 输入引擎（2026-09-07 集成，对齐 flyingmouse imageInput）
// WPF WIC BitmapDecoder 解码（宿主为 WPF 应用，零下载）。依赖系统图像扩展：
// HEIC/HEIF → 微软商店「HEIF 图像扩展」（免费）；AVIF → 「AV1 视频扩展」。
// 无法无样本预探测，Probe 恒 Ok；解码失败给安装指引（诚实：不假装能力，也不误置灰已装扩展的机器）。

using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;
using BetterDesktop.Shell.Convert.Contracts;

namespace BetterDesktop.Shell.Convert.Services.Engines;

/// <summary>
/// HEIC/HEIF/AVIF 输入引擎：WIC 解码 → PNG 中转 → GDI Bitmap → ImageTargetWriter 保存
/// （png/jpg/bmp/gif/tif/tiff/webp/ico 八种目标）。AVIF 与 HEIF 同属 ISO-BMFF 容器，
/// 解码能力由系统扩展决定（HEIF 扩展 / AV1 视频扩展）。
/// </summary>
public sealed class HeicEngine : IConversionEngine
{
    /// <summary>HEIC 家族源扩展（只读输入；不做 HEIC/AVIF 输出——WIC 无可靠编码路径，诚实不登记）。</summary>
    public static readonly IReadOnlySet<string> HeicExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".heic", ".heif", ".avif",
    };

    public EngineKind Kind => EngineKind.Heic;

    public string Name => "heic";

    public bool CanHandle(IReadOnlyList<string> sources, ConversionTarget target) =>
        sources.Count == 1
        && HeicExtensions.Contains(Path.GetExtension(sources[0]))
        && (target.Prefer == EngineKind.Heic || target.Fallback == EngineKind.Heic);

    public EngineAvailability Probe() => EngineAvailability.Ok("WIC（需系统 HEIF/AV1 图像扩展）");

    public Task<IReadOnlyList<string>> RunAsync(ConversionJob job, CancellationToken ct)
    {
        var input = job.PrimarySource;
        var format = job.Target.Format;
        var product = Path.Combine(job.TempDir,
            Path.GetFileNameWithoutExtension(input) + "." + format);

        try
        {
            // BitmapDecoder 是 Freezable（非 IDisposable）：解码后由 GC 回收
            var decoder = BitmapDecoder.Create(
                new Uri(input), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                throw new ConvertException(ConvertError.ConversionFailed, "WIC 未返回图像帧");
            }
            using var pngStream = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
            encoder.Save(pngStream);
            pngStream.Position = 0;

            using var image = Image.FromStream(pngStream);
            using var bitmap = new Bitmap(image);
            ImageTargetWriter.Save(bitmap, product, format);
        }
        catch (ConvertException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 最常见根因：未安装 HEIF/AV1 图像扩展（WIC 找不到解码器）
            throw new ConvertException(ConvertError.EngineMissing,
                $"HEIC/AVIF 解码失败（{ex.GetType().Name}）。请安装微软商店免费扩展："
                + "『HEIF 图像扩展』（HEIC）或『AV1 视频扩展』（AVIF）后重试");
        }

        IReadOnlyList<string> products = [product];
        return Task.FromResult(products);
    }
}
