using System;
using System.IO;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Clipboard.Contracts;

namespace BetterDesktop.Shell.Clipboard.Tests;

/// <summary>静默日志（测试用）。</summary>
internal sealed class NullLogger : IKernelLogger
{
    public static readonly NullLogger Instance = new();

    public void Log(LogLevel level, string message)
    {
    }

    public void Info(string message)
    {
    }

    public void Warn(string message)
    {
    }

    public void Error(string message)
    {
    }
}

/// <summary>测试接缝子类：注入固定快照（可更换），不触碰真实剪贴板。</summary>
internal sealed class TestClipboardManager : ClipboardManager
{
    public TestClipboardManager(ClipboardSnapshot? snapshot, string? storageDir = null)
        : base(NullLogger.Instance, storageDir ?? Path.Combine(Path.GetTempPath(), "bdt-clipboard-tests", Guid.NewGuid().ToString("N")))
    {
        NextSnapshot = snapshot;
    }

    /// <summary>当前快照（测试可更换以注入不同内容）。</summary>
    public ClipboardSnapshot? NextSnapshot { get; set; }

    public int SnapshotReadCount { get; private set; }

    internal override ClipboardSnapshot ReadClipboardSnapshot()
    {
        SnapshotReadCount++;
        return NextSnapshot ?? new ClipboardSnapshot { HasContent = false };
    }
}

/// <summary>快照与数据工厂。</summary>
internal static class SnapshotFactory
{
    public static ClipboardSnapshot Text(string text) => new()
    {
        HasContent = true,
        Kind = ClipboardItemKind.Text,
        Text = text,
    };

    public static ClipboardSnapshot Html(string html, string text) => new()
    {
        HasContent = true,
        Kind = ClipboardItemKind.Html,
        Html = html,
        Text = text,
    };

    public static ClipboardSnapshot RichText(string html, string rtf, string text) => new()
    {
        HasContent = true,
        Kind = ClipboardItemKind.RichText,
        Html = html,
        Rtf = rtf,
        Text = text,
    };

    public static ClipboardSnapshot Image(byte[] png) => new()
    {
        HasContent = true,
        Kind = ClipboardItemKind.Image,
        ImagePng = png,
    };

    public static ClipboardSnapshot Files(params string[] paths) => new()
    {
        HasContent = true,
        Kind = ClipboardItemKind.Files,
        Files = paths,
    };

    public static ClipboardSnapshot Empty() => new() { HasContent = false };

    /// <summary>最小假 PNG（含合法签名 + IHDR 宽高，供尺寸解析与落盘，不用于解码）。</summary>
    public static byte[] FakePng(int width = 10, int height = 10)
    {
        byte[] png = new byte[24];
        png[0] = 0x89;
        png[1] = 0x50;
        WriteBigEndian(png, 16, width);
        WriteBigEndian(png, 20, height);
        return png;
    }

    /// <summary>指定大小的假 PNG（图片预算测试用）。</summary>
    public static byte[] PngOfSize(long size)
    {
        byte[] png = FakePng(10, 10);
        Array.Resize(ref png, (int)size);
        png[0] = 0x89;
        png[1] = 0x50;
        return png;
    }

    private static void WriteBigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)((value >> 24) & 0xFF);
        buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 3] = (byte)(value & 0xFF);
    }
}
