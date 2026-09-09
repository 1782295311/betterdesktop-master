// ObjectPath 命令行路径提取单测（M2）：测内核非 UI，覆盖正常/边界/异常/缓存。

using System;
using System.IO;
using BetterDesktop.Shell.ContextMenus.Services;
using Xunit;

namespace BetterDesktop.Shell.ContextMenu.Tests;

public class ObjectPathTests
{
    [Fact]
    public void ExtractFilePath_QuotedExeWithPercent1_ReturnsFullPath()
    {
        string notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        string? result = ObjectPath.ExtractFilePath($"\"{notepad}\" \"%1\"");
        Assert.NotNull(result);
        Assert.Equal(notepad, result, ignoreCase: true);
    }

    [Fact]
    public void ExtractFilePath_Null_ReturnsNull()
    {
        Assert.Null(ObjectPath.ExtractFilePath(null));
    }

    [Fact]
    public void ExtractFilePath_Empty_ReturnsNull()
    {
        Assert.Null(ObjectPath.ExtractFilePath(""));
        Assert.Null(ObjectPath.ExtractFilePath("   "));
    }

    [Fact]
    public void ExtractFilePath_NonexistentExe_ReturnsNull()
    {
        Assert.Null(ObjectPath.ExtractFilePath("\"C:\\nonexistent\\xyz_12345.exe\" \"%1\""));
    }

    [Fact]
    public void ExtractFilePath_Caching_ReturnsSameResult()
    {
        string notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        string cmd = $"\"{notepad}\" \"%1\"";
        string? first = ObjectPath.ExtractFilePath(cmd);
        string? second = ObjectPath.ExtractFilePath(cmd);
        Assert.Equal(first, second);
    }

    [Fact]
    public void GetFullFilePath_NotepadExe_ReturnsSystem32Path()
    {
        bool ok = ObjectPath.GetFullFilePath("notepad.exe", out string? path);
        Assert.True(ok);
        Assert.NotNull(path);
        Assert.EndsWith("notepad.exe", path!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void GetFullFilePath_NotepadWithoutExtension_ReturnsPath()
    {
        bool ok = ObjectPath.GetFullFilePath("notepad", out string? path);
        Assert.True(ok);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void GetFullFilePath_Nonexistent_ReturnsFalse()
    {
        bool ok = ObjectPath.GetFullFilePath("nonexistent_xyz_12345_no_such_exe", out string? path);
        Assert.False(ok);
        Assert.Null(path);
    }

    [Fact]
    public void GetFullFilePath_Null_ReturnsFalse()
    {
        Assert.False(ObjectPath.GetFullFilePath(null, out _));
    }

    [Fact]
    public void ClearCache_Works()
    {
        ObjectPath.ClearCache(); // 不应抛异常
    }
}
