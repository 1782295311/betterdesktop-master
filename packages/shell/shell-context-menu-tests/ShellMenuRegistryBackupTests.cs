using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterDesktop.Shell.ContextMenus.Services;
using Microsoft.Win32;
using Xunit;

namespace BetterDesktop.Shell.ContextMenu.Tests;

/// <summary>
/// 注册表备份 / 回滚的回归守卫（真实 HKCU，**每个用例都用备份自身兜底还原**）。
/// </summary>
/// <remarks>
/// <para>
/// 【为什么必须先有这套能力】写 <c>HKCU\Software\Classes</c> 是改**系统状态**而不是改代码：
/// "从零到有再到零"的验收需要"到零"是**可验证**的，真机出错时也需要一条不靠手工清理注册表的退路。
/// </para>
/// <para>
/// 【测试自己就是第一个用户】每个用例开头对**原始状态**拍照、`finally` 里回滚 ——
/// 这既是安全网，也是这套能力最直接的验收（若回滚不可靠，这些用例会把机器留在脏状态，
/// 而它们自己的断言会先失败）。
/// </para>
/// </remarks>
public sealed class ShellMenuRegistryBackupTests
{
    /// <summary>对原始状态拍照，跑用例，**无论成败**都回滚到拍照时。</summary>
    private static void WithOriginalStateRestored(Action body)
    {
        var original = ShellMenuRegistryBackup.Capture();
        try
        {
            body();
        }
        finally
        {
            var report = ShellMenuRegistryBackup.Restore(original);
            Assert.True(report.Ok, "回滚原始状态失败：" + string.Join("; ", report.Problems));
        }
    }

    [Fact(DisplayName = "备份 → 落盘 → 读回：内容一致（含 Exists=false 的键）")]
    public void SaveAndLoad_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bd-shellmenu-bak-{Guid.NewGuid():N}.json");
        try
        {
            var captured = ShellMenuRegistryBackup.Capture();
            Assert.NotEmpty(captured.Keys);

            Assert.True(ShellMenuRegistryBackup.Save(captured, path, out var saveError), saveError);
            Assert.True(File.Exists(path), "备份文件应已写出");

            Assert.True(ShellMenuRegistryBackup.TryLoad(path, out var loaded, out var loadError), loadError);
            Assert.NotNull(loaded);

            // 键路径集合必须**逐条相同**（含 Exists=false 的那些 —— 它们决定回滚时要不要删键）
            Assert.Equal(
                captured.Keys.Select(k => k.Path).ToList(),
                loaded!.Keys.Select(k => k.Path).ToList());
            Assert.Equal(
                captured.Keys.Select(k => k.Exists).ToList(),
                loaded.Keys.Select(k => k.Exists).ToList());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>这是 S4-2 第 2 步动手前的**入场验收**：改一个值 → 回滚 → 值回到原样。</summary>
    [Fact(DisplayName = "改一个值 → 回滚 → 值回到原样")]
    public void Restore_PutsBackAModifiedValue()
    {
        WithOriginalStateRestored(() =>
        {
            var path = ComShellExtensionRegistrar.GetOwnedKeyPaths()[1]; // InprocServer32
            const string sentinel = @"C:\sentinel\definitely-not-ours.dll";

            using (var key = Registry.CurrentUser.CreateSubKey(path))
            {
                key!.SetValue(string.Empty, sentinel);
            }
            Assert.Equal(sentinel, ReadDefault(path));

            var backup = ShellMenuRegistryBackup.Capture();
            // 拍照时已经是哨兵值 → 回滚到"哨兵值"这一步先自证机制可用
            Assert.True(ShellMenuRegistryBackup.Restore(backup).Ok);

            using (var key = Registry.CurrentUser.CreateSubKey(path))
            {
                key!.SetValue(string.Empty, @"C:\sentinel\another-one.dll");
            }
            Assert.Equal(@"C:\sentinel\another-one.dll", ReadDefault(path));

            // 用**改之前**那份备份回滚 → 必须回到哨兵值，而不是"我们以为对的值"
            Assert.True(ShellMenuRegistryBackup.Restore(backup).Ok);
            Assert.Equal(sentinel, ReadDefault(path));
        });
    }

    /// <summary>
    /// 回滚必须能**删掉备份里不存在的键** —— 否则"从零到零"永远验证不了：
    /// 零状态是"键不存在"，不是"键存在但值为空"。
    /// </summary>
    [Fact(DisplayName = "备份里不存在的键，回滚时必须被删掉（从零到零）")]
    public void Restore_RemovesKeysAbsentFromTheBackup()
    {
        WithOriginalStateRestored(() =>
        {
            ComShellExtensionRegistrar.Unregister(); // 先到"零"
            var zero = ShellMenuRegistryBackup.Capture();
            Assert.All(zero.Keys, k => Assert.False(k.Exists, $"卸载后不该还有 {k.Path}"));
            Assert.False(ComShellExtensionRegistrar.IsRegistered());

            // 造出"有"的状态（不依赖 native DLL 是否部署：直接按生产的形状写键）
            var clsid = ComShellExtensionRegistrar.ClassicClsid.ToString("B");
            foreach (var keyPath in ComShellExtensionRegistrar.GetOwnedKeyPaths())
            {
                using var key = Registry.CurrentUser.CreateSubKey(keyPath);
                key!.SetValue(string.Empty, keyPath.Contains("InprocServer32", StringComparison.Ordinal)
                    ? @"C:\fake\native\BetterDesktopShellMenu.dll"
                    : clsid);
            }
            Assert.True(ComShellExtensionRegistrar.IsRegistered(), "造出的'有'状态应被 IsRegistered 认到");

            // 回滚到"零"：键必须**消失**，而不是留一个空键
            var report = ShellMenuRegistryBackup.Restore(zero);
            Assert.True(report.Ok, string.Join("; ", report.Problems));
            // removed 数的是"**真的删掉了几次**"，不是"备份里有几个键"：删 CLSID（父键）会连带删掉
            // InprocServer32（子键），于是轮到子键时它已经不存在 —— 6 键的备份得到 5 次删除，这是对的。
            // 真正的要求在下面两条断言（键必须全部消失），这里只防"一个都没删"。
            Assert.True(report.Removed >= 5, $"应逐个删除，实际 removed={report.Removed}");
            Assert.False(ComShellExtensionRegistrar.IsRegistered(), "回滚到零后不得仍被认成已注册");
            foreach (var keyPath in ComShellExtensionRegistrar.GetOwnedKeyPaths())
            {
                Assert.Null(Registry.CurrentUser.OpenSubKey(keyPath));
            }
        });
    }

    /// <summary>回滚是**覆盖式**：备份之后被人多加的值也要清掉（"回到当时那样"，不是"合并"）。</summary>
    [Fact(DisplayName = "回滚是覆盖式：备份之后多写进去的值会被清掉")]
    public void Restore_OverwritesInsteadOfMerging()
    {
        WithOriginalStateRestored(() =>
        {
            ComShellExtensionRegistrar.Unregister();
            var roomy = ComShellExtensionRegistrar.GetOwnedKeyPaths()[0];
            using (var key = Registry.CurrentUser.CreateSubKey(roomy))
            {
                key!.SetValue(string.Empty, "original-shape");
            }

            var backup = ShellMenuRegistryBackup.Capture();

            using (var key = Registry.CurrentUser.CreateSubKey(roomy))
            {
                key!.SetValue("StrayValue", "added-after-backup");
            }

            Assert.True(ShellMenuRegistryBackup.Restore(backup).Ok);

            using var restored = Registry.CurrentUser.OpenSubKey(roomy);
            Assert.NotNull(restored);
            Assert.Equal("original-shape", restored!.GetValue(string.Empty) as string);
            Assert.Null(restored.GetValue("StrayValue"));
        });
    }

    [Fact(DisplayName = "备份文件不存在时：报错而不是假装成功")]
    public void Load_MissingFileFailsLoudly()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"bd-nope-{Guid.NewGuid():N}.json");
        Assert.False(ShellMenuRegistryBackup.TryLoad(missing, out var backup, out var error));
        Assert.Null(backup);
        Assert.NotNull(error);
        Assert.Contains("不存在", error!, StringComparison.Ordinal);
    }

    private static string? ReadDefault(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key?.GetValue(string.Empty) as string;
    }
}
