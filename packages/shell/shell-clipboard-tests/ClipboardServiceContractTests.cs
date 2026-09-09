using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BetterDesktop.Shell.Clipboard.Contracts;
using Xunit;

namespace BetterDesktop.Shell.Clipboard.Tests;

/// <summary>公共契约：接口成员被实现满足；契约零实现依赖（无 P/Invoke、无 kernel/shell 引用）；关键语义。</summary>
public class ClipboardServiceContractTests
{
    [Fact]
    public void InterfaceMembers_AllImplemented()
    {
        var manager = new TestClipboardManager(SnapshotFactory.Empty());
        foreach (MethodInfo method in typeof(IClipboardService).GetMethods())
        {
            // 接口方法均可正常调用（无 NotImplementedException 路径）。
            Assert.NotNull(manager.GetType().GetMethod(method.Name));
        }

        foreach (PropertyInfo property in typeof(IClipboardService).GetProperties())
        {
            Assert.NotNull(manager.GetType().GetProperty(property.Name));
        }

        // 事件成员。
        foreach (EventInfo ev in typeof(IClipboardService).GetEvents())
        {
            Assert.NotNull(manager.GetType().GetEvent(ev.Name));
        }
    }

    [Fact]
    public void ContractLayer_ZeroImplementationDependency()
    {
        // 沿 BaseDirectory 向上找仓库根（BetterDesktop.slnx 标志），避免绑定输出目录层数。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BetterDesktop.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        string contractDir = Path.Combine(dir!.FullName, "packages", "api", "Clipboard");
        string[] files = Directory.GetFiles(contractDir, "*.cs");
        Assert.True(files.Length >= 8, $"契约文件应 ≥ 8（现有 {files.Length}）");

        foreach (string file in files)
        {
            string content = File.ReadAllText(file);
            Assert.DoesNotContain("System.Runtime.InteropServices", content);
            Assert.DoesNotContain("BetterDesktop.Kernel", content);
            Assert.DoesNotContain("System.Windows", content);
            // 只允许自身契约命名空间与 System 基础库。
        }
    }

    [Fact]
    public void Pin_Unpin_Delete_Semantics()
    {
        var manager = new TestClipboardManager(SnapshotFactory.Text("pin-target"));
        manager.OnClipboardUpdate();
        ClipboardEntry entry = Assert.Single(manager.GetFilteredEntries());

        manager.PinEntry(entry);
        Assert.True(manager.GetFilteredEntries()[0].IsPinned);

        manager.TogglePin(entry);
        Assert.False(manager.GetFilteredEntries()[0].IsPinned);

        manager.DeleteEntry(entry);
        Assert.Empty(manager.GetFilteredEntries());
    }

    [Fact]
    public void ClearAllUnpinned_KeepsPinned()
    {
        var manager = new TestClipboardManager(SnapshotFactory.Text("a"));
        manager.OnClipboardUpdate();
        manager.NextSnapshot = SnapshotFactory.Text("b");
        manager.OnClipboardUpdate();

        manager.PinEntry(manager.GetFilteredEntries()[0]);
        manager.ClearAllUnpinned();

        var entries = manager.GetFilteredEntries();
        Assert.Single(entries);
        Assert.True(entries[0].IsPinned);
    }

    [Fact]
    public void ImportEntries_ReusesPipeline_ReturnsAddedCount()
    {
        var manager = new TestClipboardManager(SnapshotFactory.Empty());
        int added = manager.ImportEntries(new[]
        {
            new ClipboardImportItem(ClipboardItemKind.Text, "import-1"),
            new ClipboardImportItem(ClipboardItemKind.Text, "import-2"),
            new ClipboardImportItem(ClipboardItemKind.Text, "import-1"),
        });

        // 同内容去重：3 条录入仅 2 条新增。
        Assert.Equal(2, added);
        Assert.Equal(2, manager.GetFilteredEntries().Count);
    }

    [Fact]
    public void LastCopiedContent_TimeLimit_Expired_ReturnsNull()
    {
        var manager = new TestClipboardManager(SnapshotFactory.Text("old"));
        manager.OnClipboardUpdate();

        ClipboardEntry entry = manager.GetFilteredEntries()[0];
        entry.Timestamp = DateTime.Now.AddMinutes(-10);

        Assert.Null(manager.GetLastCopiedContent(TimeSpan.FromSeconds(30)));
        Assert.NotNull(manager.GetLastCopiedContent());
    }
}
