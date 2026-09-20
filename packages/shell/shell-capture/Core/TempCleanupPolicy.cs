using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BetterDesktop.Shell.Capture.Core;

/// <summary>临时文件存储抽象（T3 测试用假文件系统替换）。</summary>
public interface ITempStorage
{
    IReadOnlyList<(string Path, DateTime LastWrite)> Enumerate(string dir);

    bool Delete(string path);
}

/// <summary>真实磁盘实现。</summary>
public sealed class DiskTempStorage : ITempStorage
{
    public IReadOnlyList<(string, DateTime)> Enumerate(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return Array.Empty<(string, DateTime)>();
        }

        return Directory.EnumerateFiles(dir, "*.png")
            .Select(p => (p, File.GetLastWriteTimeUtc(p)))
            .ToList();
    }

    public bool Delete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// 临时文件清理策略（T3 对象，纯策略 + 存储抽象）：
/// 超过 <see cref="MaxAge"/> 的残留文件在启动时清理（崩溃/进程被杀后兜底）；
/// 正常流程的文件在每次会话结束后即时删除（删除失败只记日志，不阻塞截图）。
/// </summary>
public sealed class TempCleanupPolicy
{
    public TimeSpan MaxAge { get; }

    public TempCleanupPolicy(TimeSpan? maxAge = null)
    {
        MaxAge = maxAge ?? TimeSpan.FromHours(24);
    }

    /// <summary>清理目录中超过 MaxAge 的文件，返回删除数量。</summary>
    public int Cleanup(string dir, DateTime nowUtc, ITempStorage storage)
    {
        int deleted = 0;
        foreach (var (path, lastWrite) in storage.Enumerate(dir))
        {
            if (nowUtc - lastWrite > MaxAge && storage.Delete(path))
            {
                deleted++;
            }
        }
        return deleted;
    }

    /// <summary>判断某文件是否应被清理（纯函数，单测直接断言）。</summary>
    public static bool ShouldCleanup(DateTime lastWriteUtc, DateTime nowUtc, TimeSpan maxAge) =>
        nowUtc - lastWriteUtc > maxAge;
}
