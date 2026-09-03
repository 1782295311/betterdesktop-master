// 注册表静态 verb 解析（M2 计划 §4）：显示名链 / 可见性 / 命令替换 / 图标提取——纯函数缝，不碰注册表。

using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.ContextMenus.Services;
using Xunit;

namespace BetterDesktop.Shell.ContextMenu.Tests;

public class RegistryVerbNameTests
{
    [Fact]
    public void MuiVerb_Wins_OverDefault()
    {
        RegistryVerbs.IndirectStringResolver = s => s;
        try
        {
            Assert.Equal("用 XX 打开", RegistryVerbs.ResolveDisplayName("xx", "用 XX 打开", "默认名", false));
        }
        finally
        {
            RegistryVerbs.IndirectStringResolver = null;
        }
    }

    [Fact]
    public void BuiltinDictionary_FallsBack_ToKeyName()
    {
        RegistryVerbs.IndirectStringResolver = s => s;
        try
        {
            Assert.Equal("@windows.storage.dll,-8496", RegistryVerbs.ResolveDisplayName("open", null, null, false));
            Assert.Equal("某键名", RegistryVerbs.ResolveDisplayName("某键名", null, null, false)); // 兜底永不空
        }
        finally
        {
            RegistryVerbs.IndirectStringResolver = null;
        }
    }

    [Fact]
    public void IndirectString_Resolved_ViaShlwapi()
    {
        // 真实 SHLoadIndirectString：@dll,-id → 可见文本（Win 系统自带资源，确定性）
        var text = RegistryVerbs.ResolveIndirect("@windows.storage.dll,-345"); // 系统资源
        Assert.NotNull(text);
    }

    [Fact]
    public void TextOver80Chars_Invalid()
    {
        Assert.False(RegistryVerbs.IsTextValid(new string('长', 80)));
        Assert.True(RegistryVerbs.IsTextValid(new string('长', 79)));
    }
}

public class RegistryVerbVisibilityTests
{
    [Fact]
    public void HideMarkers_AllHidden()
    {
        Assert.False(RegistryVerbs.IsVerbVisible(hideBasedOnVelocity: true, false, false, 0));
        Assert.False(RegistryVerbs.IsVerbVisible(false, legacyDisable: true, false, 0));
        Assert.False(RegistryVerbs.IsVerbVisible(false, false, programmaticAccessOnly: true, 0));
        Assert.False(RegistryVerbs.IsVerbVisible(false, false, false, commandFlags: 0x8)); // %16>=8
        Assert.True(RegistryVerbs.IsVerbVisible(false, false, false, 0));
    }
}

public class RegistryVerbCommandTests
{
    [Fact]
    public void Placeholders_ReplacedWithQuotedTarget()
    {
        Assert.Equal("\"C:\\tool.exe\" \"C:\\a b.txt\"", RegistryVerbs.BuildCommand("\"C:\\tool.exe\" %1", "C:\\a b.txt"));
        Assert.Equal("\"C:\\tool.exe\" \"C:\\a b.txt\"", RegistryVerbs.BuildCommand("\"C:\\tool.exe\" %V", "C:\\a b.txt"));
        Assert.Equal("\"C:\\tool.exe\" \"C:\\a b.txt\"", RegistryVerbs.BuildCommand("\"C:\\tool.exe\" %L", "C:\\a b.txt"));
    }

    [Fact]
    public void EmptyCommand_Empty()
    {
        Assert.Equal(string.Empty, RegistryVerbs.BuildCommand("", "C:\\x.txt"));
    }

    [Fact]
    public void IconExecutable_Extracted_OnlyWhenExists()
    {
        Assert.Equal("C:\\Windows\\System32\\notepad.exe",
            RegistryVerbs.ExtractIconExecutable("\"C:\\Windows\\System32\\notepad.exe\",-1"));
        Assert.Null(RegistryVerbs.ExtractIconExecutable("\"C:\\nope\\x.exe\",0"));
        Assert.Null(RegistryVerbs.ExtractIconExecutable("imageres.dll,-2"));
        Assert.Null(RegistryVerbs.ExtractIconExecutable(null));
    }
}
