using BetterDesktop.Shell.ContextMenu.Tests.Support;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.ContextMenus.Services;
using Xunit;

namespace BetterDesktop.Shell.ContextMenu.Tests;

/// <summary>
/// FileClassifier 判定链单测（确定性路径；lnk 目标继承走实机手测，COM 依赖不入单测）。
/// </summary>
public sealed class FileClassifierTests : IDisposable
{
    private readonly FileClassifier _classifier = new();
    private readonly TempFileScope _files = new();

    [Fact]
    public void ShellNamespace_ClsidPath_ClassifiedWithoutFileAccess()
    {
        var id = _classifier.Classify("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}");
        Assert.Equal(FileKind.ShellNamespace, id.Kind);
        Assert.Equal(FileCapabilities.Open, id.Caps);
    }

    [Fact]
    public void RecycleBinPath_RestoreCapability()
    {
        var id = _classifier.Classify(@"C:\$Recycle.Bin\S-1-5-21\\$RABCDEF.txt");
        Assert.Equal(FileKind.InRecycleBin, id.Kind);
        Assert.True(id.Caps.HasFlag(FileCapabilities.Restore));
        Assert.False(id.Caps.HasFlag(FileCapabilities.Rename));
    }

    [Fact]
    public void Directory_FolderCaps_WithPasteInto()
    {
        var dir = _files.CreateDirectory();
        var id = _classifier.Classify(dir);
        Assert.Equal(FileKind.Folder, id.Kind);
        Assert.True(id.Caps.HasFlag(FileCapabilities.PasteInto));
        Assert.True(id.Caps.HasFlag(FileCapabilities.Browse));
    }

    [Fact]
    public void DriveRoot_DriveKind()
    {
        var sysRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
        var id = _classifier.Classify(sysRoot);
        Assert.Equal(FileKind.Drive, id.Kind);
    }

    [Fact]
    public void Exe_RunAsAdminCapability()
    {
        var exe = _files.CreateFile("app", ".exe");
        var id = _classifier.Classify(exe);
        Assert.Equal(FileKind.Executable, id.Kind);
        Assert.True(id.Caps.HasFlag(FileCapabilities.RunAsAdmin));
        Assert.True(id.Caps.HasFlag(FileCapabilities.OpenFileLocation));
    }

    [Fact]
    public void WordDocument_EditPrintCaps()
    {
        var doc = _files.CreateFile("报告", ".docx");
        var id = _classifier.Classify(doc);
        Assert.Equal(FileKind.WordDocument, id.Kind);
        Assert.True(id.Caps.HasFlag(FileCapabilities.Edit));
        Assert.True(id.Caps.HasFlag(FileCapabilities.Print));
    }

    [Fact]
    public void UnregisteredExtension_Unknown_OpenWith()
    {
        var file = _files.CreateFile("data", ".zzqx7test"); // HKCR 无此键（假定干净机器）
        var id = _classifier.Classify(file);
        Assert.Equal(FileKind.Unknown, id.Kind);
        Assert.True(id.Caps.HasFlag(FileCapabilities.OpenWith));
        Assert.False(id.Caps.HasFlag(FileCapabilities.Edit));
    }

    [Fact]
    public void ExtensionlessFile_Unknown()
    {
        var file = _files.CreateFile("README", "");
        var id = _classifier.Classify(file);
        Assert.Equal(FileKind.Unknown, id.Kind);
    }

    [Fact]
    public void ReadOnlyFile_FlaggedButDeleteRetained()
    {
        var file = _files.CreateFile("locked", ".txt", readOnly: true);
        var id = _classifier.Classify(file);
        Assert.True(id.IsReadOnly);
        // 定版：只读 → 删除/重命名置灰而非隐藏（能力位保留，由模板置灰）
        Assert.True(id.Caps.HasFlag(FileCapabilities.Delete));
        Assert.True(id.Caps.HasFlag(FileCapabilities.Rename));
    }

    [Fact]
    public void ClassifyMany_MixedKinds_IntersectionCaps()
    {
        var exe = _files.CreateFile("a", ".exe");
        var doc = _files.CreateFile("b", ".docx");
        var id = _classifier.ClassifyMany([exe, doc]);
        Assert.Equal(FileKind.File, id.Kind); // 混合类型降级 File
        Assert.False(id.Caps.HasFlag(FileCapabilities.RunAsAdmin)); // exe 专属被交集剔除
        Assert.False(id.Caps.HasFlag(FileCapabilities.Edit));       // 文档专属被交集剔除
        Assert.True(id.Caps.HasFlag(FileCapabilities.Copy));        // 公共能力保留
    }

    [Fact]
    public void ClassifyMany_SameKind_KeepsKind()
    {
        var a = _files.CreateFile("a", ".docx");
        var b = _files.CreateFile("b", ".docx");
        var id = _classifier.ClassifyMany([a, b]);
        Assert.Equal(FileKind.WordDocument, id.Kind);
    }

    public void Dispose() => _files.Dispose();
}
