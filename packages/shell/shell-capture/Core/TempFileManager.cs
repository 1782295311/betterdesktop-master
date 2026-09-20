using System;
using System.IO;

namespace BetterDesktop.Shell.Capture.Core;

/// <summary>
/// 临时文件生命周期：截图产物 PNG 先落 temp，写剪贴板/编辑器/贴图引用后由会话结束删除；
/// 启动时清理超过 24h 的残留（崩溃兜底）。删除失败只记日志，不阻塞截图（计划 §6 风险处置）。
/// </summary>
public static class TempFileManager
{
    private const int MaxLogLinesPerCleanup = 5;

    /// <summary>截图临时目录：%LOCALAPPDATA%\BetterDesktop\capture\tmp\</summary>
    public static string TempDir
    {
        get
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "BetterDesktop", "capture", "tmp");
        }
    }

    /// <summary>把 PNG 字节写入新的临时文件，返回路径。</summary>
    public static string WriteTempPng(byte[] png)
    {
        Directory.CreateDirectory(TempDir);
        string path = Path.Combine(TempDir, $"{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, png);
        return path;
    }

    /// <summary>尽力删除（失败只记日志）。</summary>
    public static void DeleteBestEffort(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // BitmapImage 仍持有文件句柄：覆盖层关闭后句柄释放需时间，延迟重试一次再放弃
            Thread.Sleep(300);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex2) when (ex2 is IOException or UnauthorizedAccessException)
            {
                CaptureLog.Warn($"临时文件删除失败（{path}）：{ex2.Message}");
            }
        }
    }

    /// <summary>启动清扫：删除超过 24h 的残留文件，返回删除数量。</summary>
    public static int CleanupStartup()
    {
        try
        {
            return new TempCleanupPolicy().Cleanup(TempDir, DateTime.UtcNow, new DiskTempStorage());
        }
        catch (Exception ex)
        {
            CaptureLog.Warn($"启动清扫失败：{ex.Message}");
            return 0;
        }
    }
}
