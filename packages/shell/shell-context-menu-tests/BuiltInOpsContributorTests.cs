// BuiltInOpsContributor 门控矩阵（计划 §8）：不同 Caps/Kind → 断言出现/缺失的 ItemId 集合。

using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.ContextMenus.Services;
using Xunit;

namespace BetterDesktop.Shell.ContextMenu.Tests;

public class BuiltInOpsContributorTests
{
    private static MenuRequest Request(FileIdentity identity, string[]? selected = null, bool shift = false)
        => new(MenuScope.DesktopIcon, null, default,
            File: identity,
            SelectedPaths: selected,
            ShiftPressed: shift);

    private static string[] Ids(MenuRequest request)
    {
        var contributor = new BuiltInOpsContributor(MenuScope.DesktopIcon);
        return [.. contributor.Build(request).Select(i => i.Id)];
    }

    [Fact]
    public void PlainTextFile_NotepadEditZipCopyTo_Present_WallpaperExtract_Absent()
    {
        // 真实临时文件（记事本/压缩需要 File.Exists 判定）
        var path = Path.Combine(Path.GetTempPath(), $"bioc_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "x");
        try
        {
            var ids = Ids(Request(new FileIdentity(
                FileKind.Document,
                FileCapabilities.Open | FileCapabilities.Edit | FileCapabilities.Copy
                | FileCapabilities.Delete | FileCapabilities.Cut | FileCapabilities.Rename
                | FileCapabilities.Properties,
                IsReadOnly: false, IsHidden: false, Path: path)));

            Assert.Contains("builtin.notepad", ids);
            Assert.Contains("builtin.edit", ids);
            Assert.Contains("builtin.zip", ids);
            Assert.Contains("builtin.delete-permanent", ids); // 扩展项：贡献者原样产出，过滤归 MenuService
            Assert.Contains("builtin.copyto", ids);
            Assert.DoesNotContain("builtin.wallpaper", ids);
            Assert.DoesNotContain("builtin.extract", ids);
            Assert.DoesNotContain("builtin.runasuser", ids);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WallpaperCap_YieldsWallpaperItem()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bioc_{Guid.NewGuid():N}.png");
        File.WriteAllText(path, "x"); // 内容无关：门控只看 Kind/Caps
        try
        {
            var ids = Ids(Request(new FileIdentity(
                FileKind.Image,
                FileCapabilities.Open | FileCapabilities.SetAsWallpaper | FileCapabilities.Copy,
                IsReadOnly: false, IsHidden: false, Path: path)));

            Assert.Contains("builtin.wallpaper", ids);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Archive_YieldsExtractItem()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bioc_{Guid.NewGuid():N}.zip");
        File.WriteAllText(path, "x");
        try
        {
            var ids = Ids(Request(new FileIdentity(
                FileKind.Archive,
                FileCapabilities.Open | FileCapabilities.Extract | FileCapabilities.Delete,
                IsReadOnly: false, IsHidden: false, Path: path)));

            Assert.Contains("builtin.extract", ids);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Exe_YieldsRunAsUserExtended()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bioc_{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, "x");
        try
        {
            var ids = Ids(Request(new FileIdentity(
                FileKind.Executable,
                FileCapabilities.Open | FileCapabilities.RunAsAdmin,
                IsReadOnly: false, IsHidden: false, Path: path)));

            Assert.Contains("builtin.runasuser", ids);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RecycleBin_OnlyRestoreAndBrowse()
    {
        // $I/$R 配对不存在 → 还原降级隐藏（计划 §9：格式验证失败整项下掉）
        var ids = Ids(Request(new FileIdentity(
            FileKind.InRecycleBin,
            FileCapabilities.Restore | FileCapabilities.Browse | FileCapabilities.Delete,
            IsReadOnly: false, IsHidden: false, Path: "C:\\$Recycle.Bin\\S-1-5-21\\$R012345.txt")));

        Assert.DoesNotContain("builtin.notepad", ids);
        Assert.DoesNotContain("builtin.zip", ids);
        Assert.Contains("builtin.browse", ids); // 定位不需要 $I 配对
    }

    [Fact]
    public void Folder_WithPasteIntoCap_NoPasteItemWhenClipboardEmpty()
    {
        // 剪贴板无文件（测试环境 MTA 读剪贴板异常被吞 → HasFiles=false）→ 粘贴到隐藏
        var ids = Ids(Request(new FileIdentity(
            FileKind.Folder,
            FileCapabilities.PasteInto | FileCapabilities.Open,
            IsReadOnly: false, IsHidden: false, Path: "C:\\")));

        Assert.DoesNotContain("builtin.pasteinto", ids);
    }

    [Fact]
    public void MultiSelect_UsesSelectedPaths()
    {
        var a = Path.Combine(Path.GetTempPath(), $"bioc_{Guid.NewGuid():N}.txt");
        var b = Path.Combine(Path.GetTempPath(), $"bioc_{Guid.NewGuid():N}.txt");
        File.WriteAllText(a, "x");
        File.WriteAllText(b, "x");
        try
        {
            var ids = Ids(Request(
                new FileIdentity(FileKind.Document, FileCapabilities.Copy, false, false, a),
                [a, b]));

            Assert.Contains("builtin.zip", ids); // 多选打包
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
        }
    }
}
