using System;
using System.Collections.Generic;
using System.IO;
using BetterDesktop.Shell.Capture.Core;
using Xunit;

namespace BetterDesktop.Shell.Capture.Tests;

/// <summary>临时文件清理策略（计划 T3）：过期判定 + 假存储驱动清理计数。</summary>
public sealed class TempCleanupPolicyTests
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    [Fact]
    public void ShouldCleanup_OnlyStrictlyOlderThanMaxAge()
    {
        var now = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

        // 恰好等于 MaxAge → 不清理（边界不误删）
        Assert.False(TempCleanupPolicy.ShouldCleanup(now - MaxAge, now, MaxAge));
        // 超过 1 毫秒 → 清理
        Assert.True(TempCleanupPolicy.ShouldCleanup(now - MaxAge - TimeSpan.FromMilliseconds(1), now, MaxAge));
        // 最新文件 → 不清理
        Assert.False(TempCleanupPolicy.ShouldCleanup(now - TimeSpan.FromMinutes(5), now, MaxAge));
    }

    [Fact]
    public void Cleanup_DeletesOnlyExpired_ReturnsCount()
    {
        var now = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
        var storage = new FakeStorage(new[]
        {
            ("old.png", now - TimeSpan.FromHours(30)),
            ("fresh.png", now - TimeSpan.FromMinutes(1)),
            ("older.png", now - TimeSpan.FromDays(3)),
        });

        var policy = new TempCleanupPolicy(MaxAge);
        int deleted = policy.Cleanup(@"C:\fake\tmp", now, storage);

        Assert.Equal(2, deleted);
        Assert.Equal(new[] { "old.png", "older.png" }, storage.Deleted);
    }

    [Fact]
    public void Cleanup_DeleteFailure_CountsAsNotDeleted()
    {
        var now = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
        var storage = new FakeStorage(new[] { ("locked.png", now - TimeSpan.FromHours(30)) }) { FailDelete = true };

        var policy = new TempCleanupPolicy(MaxAge);
        Assert.Equal(0, policy.Cleanup(@"C:\fake\tmp", now, storage));
    }

    [Fact]
    public void DiskStorage_RealFiles_EnumerateAndDelete()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bdt-capture-t3-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string old = Path.Combine(dir, "old.png");
            string other = Path.Combine(dir, "keep.txt");
            File.WriteAllText(old, "x");
            File.WriteAllText(other, "x");

            var disk = new DiskTempStorage();
            var files = disk.Enumerate(dir);
            Assert.Single(files, f => Path.GetFileName(f.Item1) == "old.png");

            Assert.True(disk.Delete(old));
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(other)); // 非 png 不动
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    private sealed class FakeStorage : ITempStorage
    {
        private readonly IReadOnlyList<(string Path, DateTime LastWrite)> _files;

        public FakeStorage(IReadOnlyList<(string, DateTime)> files) => _files = files;

        public bool FailDelete { get; init; }

        public List<string> Deleted { get; } = new();

        public IReadOnlyList<(string, DateTime)> Enumerate(string dir) => _files;

        public bool Delete(string path)
        {
            if (FailDelete)
            {
                return false;
            }
            Deleted.Add(Path.GetFileName(path));
            return true;
        }
    }
}
