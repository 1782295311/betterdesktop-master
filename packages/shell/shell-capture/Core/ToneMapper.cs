using System;
using System.Buffers;
using System.Runtime.InteropServices;

namespace BetterDesktop.Shell.Capture.Core;

/// <summary>
/// HDR → SDR 色调映射（FP16 scRGB → BGRA8 sRGB）。
/// 策略：scRGB 线性值 clamp [0,1] → sRGB 伽马编码（对照 Windows 系统截图工具输出口径；
/// 真机基准见计划 §6，逐像素差异记录留证）。性能：65536 项查找表（FP16 值域全覆盖），
/// 单帧 4K ≈ 数千万次查表，远快于逐像素 pow。
/// </summary>
public static class ToneMapper
{
    private static readonly byte[] SRgbLut = BuildSrgbLut();

    private static byte[] BuildSrgbLut()
    {
        var lut = new byte[65536];
        for (int i = 0; i < 65536; i++)
        {
            Half h = BitConverter.UInt16BitsToHalf((ushort)i);
            float f = (float)h;
            // NaN/Inf/负值 → 0；>1 的高光 → clamp 到 1（scRGB 超出 SDR 白点的部分按系统截图口径裁掉）。
            if (float.IsNaN(f) || float.IsInfinity(f) || f < 0f)
            {
                f = 0f;
            }
            else if (f > 1f)
            {
                f = 1f;
            }

            float c = f <= 0.0031308f
                ? 12.92f * f
                : 1.055f * MathF.Pow(f, 1f / 2.4f) - 0.055f;
            lut[i] = (byte)Math.Clamp((int)(c * 255f + 0.5f), 0, 255);
        }
        return lut;
    }

    /// <summary>
    /// RGBA FP16（每像素 4×Half，scRGB 线性）→ BGRA8 sRGB。
    /// <paramref name="src"/> 长度必须为像素数×4；<paramref name="dst"/> 必须能容纳 像素数×4 字节。
    /// </summary>
    public static void ScRgbFp16ToBgra8(ReadOnlySpan<Half> src, Span<byte> dst)
    {
        var srcBits = MemoryMarshal.Cast<Half, ushort>(src);
        int pixelCount = srcBits.Length >> 2;
        int dstBytes = pixelCount * 4;
        if (dst.Length < dstBytes)
        {
            throw new ArgumentException("目标缓冲过小", nameof(dst));
        }

        var lut = SRgbLut;
        for (int i = 0, s = 0, d = 0; i < pixelCount; i++, s += 4, d += 4)
        {
            // Half 布局：R G B A；输出 BGRA。
            dst[d] = lut[srcBits[s + 2]];      // B
            dst[d + 1] = lut[srcBits[s + 1]];  // G
            dst[d + 2] = lut[srcBits[s]];      // R
            dst[d + 3] = 255;                  // A（屏幕帧不透明）
        }
    }

    /// <summary>
    /// 诊断模式：clamp 到 [0,1] 但不做 sRGB 伽马编码（输出偏暗，仅用于对照验证色调映射效果）。
    /// </summary>
    public static void ScRgbFp16ToBgra8Clamp(ReadOnlySpan<Half> src, Span<byte> dst)
    {
        var srcBits = MemoryMarshal.Cast<Half, ushort>(src);
        int pixelCount = srcBits.Length >> 2;
        if (dst.Length < pixelCount * 4)
        {
            throw new ArgumentException("目标缓冲过小", nameof(dst));
        }

        for (int i = 0, s = 0, d = 0; i < pixelCount; i++, s += 4, d += 4)
        {
            dst[d] = ClampByte(ToLinear(srcBits[s + 2]));
            dst[d + 1] = ClampByte(ToLinear(srcBits[s + 1]));
            dst[d + 2] = ClampByte(ToLinear(srcBits[s]));
            dst[d + 3] = 255;
        }
    }

    private static float ToLinear(ushort bits)
    {
        Half h = BitConverter.UInt16BitsToHalf(bits);
        float f = (float)h;
        return float.IsNaN(f) || float.IsInfinity(f) || f < 0f ? 0f : Math.Min(f, 1f);
    }

    private static byte ClampByte(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);

    /// <summary>
    /// R8G8B8A8 → BGRA8（DXGI 偶发 RGBA 布局时的通道交换；纯拷贝路径由调用方跳过）。
    /// </summary>
    public static void Rgba8ToBgra8(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int pixels = src.Length >> 2;
        if (dst.Length < pixels * 4)
        {
            throw new ArgumentException("目标缓冲过小", nameof(dst));
        }

        for (int i = 0; i < pixels; i++)
        {
            int s = i << 2;
            int d = s;
            dst[d] = src[s + 2];
            dst[d + 1] = src[s + 1];
            dst[d + 2] = src[s];
            dst[d + 3] = src[s + 3];
        }
    }
}
