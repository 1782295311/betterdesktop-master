// BetterDesktop.Shell.ContextMenu.Tests — MenuIconCache（H1a 有界 LRU）A/B 门槛测试。
// 设计方案：docs/design-proposals/2026-09-03-H1-静态缓存治理.md
// 治理前：Dictionary 只增不逐，拖放/工具路径无界增长（单条目 ≈2-4KB）。
// 治理后：容量 512 封顶 LRU，逐出最久未用条目；逐出后可重提取。

using BetterDesktop.Shell.ContextMenus.Services;
using Xunit;

namespace BetterDesktop.Shell.ContextMenu.Tests;

public class MenuIconCacheTests : IDisposable
{
    private readonly string _dir;

    public MenuIconCacheTests()
    {
        MenuIconCache.Clear();
        _dir = Path.Combine(Path.GetTempPath(), "mbc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录清理尽力而为 */ }
        MenuIconCache.Clear();
    }

    private string MakeDummyFile(int index)
    {
        var path = Path.Combine(_dir, $"tool_{index}.txt");
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void BeyondCapacity_CountIsCapped()
    {
        // A/B 门槛：600 个不同路径灌入 → 驻留钉在 512（治理前 = 600 且继续涨）。
        for (var i = 0; i < 600; i++)
        {
            _ = MenuIconCache.Get(MakeDummyFile(i));
        }

        Assert.Equal(MenuIconCache.Capacity, MenuIconCache.Count);
    }

    [Fact]
    public void EvictedEntry_CanBeReExtracted_NoError()
    {
        var first = MakeDummyFile(0);
        for (var i = 0; i < MenuIconCache.Capacity + 50; i++)
        {
            _ = MenuIconCache.Get(MakeDummyFile(i));
        }
        // 首条目已被 LRU 逐出：重取必须走重提取路径且不抛异常、计数仍封顶
        _ = MenuIconCache.Get(first);

        Assert.True(MenuIconCache.Count <= MenuIconCache.Capacity);
    }

    [Fact]
    public void RepeatGet_SamePath_CountStable()
    {
        var path = MakeDummyFile(0);
        _ = MenuIconCache.Get(path);
        var afterFirst = MenuIconCache.Count;

        for (var i = 0; i < 10; i++)
        {
            _ = MenuIconCache.Get(path);
        }

        Assert.Equal(afterFirst, MenuIconCache.Count);
    }
}
