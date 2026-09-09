// ShadowPathOf 影子路径纯函数单测（M2 修复回归）：验证 HKLM→HKCU 影子映射无双重 SOFTWARE\Classes 前缀。
// 这是 2026-09-05 修复的核心 bug——修复前 sub 自带 SOFTWARE\Classes 前缀导致影子键落到 Explorer 读不到的位置。

using System;
using BetterDesktop.Shell.ContextMenus.Services;
using Xunit;

namespace BetterDesktop.Shell.ContextMenu.Tests;

public class ShadowPathOfTests
{
    private const string UserClassesRoot = @"HKEY_CURRENT_USER\Software\Classes";

    [Fact]
    public void ShadowPathOf_HKLM_ShellexHandler_NoDoublePrefix()
    {
        string input = @"HKLM\SOFTWARE\Classes\*\shellex\ContextMenuHandlers\TestHandler";
        string result = MenuManagerService.ShadowPathOf(input);
        string expected = $@"{UserClassesRoot}\*\shellex\ContextMenuHandlers\TestHandler";
        Assert.Equal(expected, result, ignoreCase: true);
        // 核心断言：无双重 SOFTWARE\Classes
        Assert.DoesNotContain(@"SOFTWARE\Classes\SOFTWARE\Classes", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShadowPathOf_HKCU_SelfMapping_NoDoublePrefix()
    {
        // HKCU 路径自映射：剥离 Software\Classes 前缀后落到同一位置
        string input = @"HKCU\Software\Classes\*\shellex\ContextMenuHandlers\TestHandler";
        string result = MenuManagerService.ShadowPathOf(input);
        string expected = $@"{UserClassesRoot}\*\shellex\ContextMenuHandlers\TestHandler";
        Assert.Equal(expected, result, ignoreCase: true);
        Assert.DoesNotContain(@"Software\Classes\Software\Classes", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShadowPathOf_DirectoryBackground_NoDoublePrefix()
    {
        string input = @"HKLM\SOFTWARE\Classes\Directory\Background\shell\TestVerb";
        string result = MenuManagerService.ShadowPathOf(input);
        string expected = $@"{UserClassesRoot}\Directory\Background\shell\TestVerb";
        Assert.Equal(expected, result, ignoreCase: true);
        Assert.DoesNotContain(@"SOFTWARE\Classes\SOFTWARE\Classes", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShadowPathOf_StaticVerb_AllFiles_NoDoublePrefix()
    {
        string input = @"HKLM\SOFTWARE\Classes\*\shell\TestVerb";
        string result = MenuManagerService.ShadowPathOf(input);
        string expected = $@"{UserClassesRoot}\*\shell\TestVerb";
        Assert.Equal(expected, result, ignoreCase: true);
    }

    [Fact]
    public void ShadowPathOf_FolderScene_NoDoublePrefix()
    {
        string input = @"HKLM\SOFTWARE\Classes\Folder\shell\TestVerb";
        string result = MenuManagerService.ShadowPathOf(input);
        string expected = $@"{UserClassesRoot}\Folder\shell\TestVerb";
        Assert.Equal(expected, result, ignoreCase: true);
    }

    [Fact]
    public void ShadowPathOf_DragDropHandlers_NoDoublePrefix()
    {
        string input = @"HKLM\SOFTWARE\Classes\*\shellex\DragDropHandlers\TestDrop";
        string result = MenuManagerService.ShadowPathOf(input);
        string expected = $@"{UserClassesRoot}\*\shellex\DragDropHandlers\TestDrop";
        Assert.Equal(expected, result, ignoreCase: true);
        Assert.DoesNotContain(@"SOFTWARE\Classes\SOFTWARE\Classes", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShadowPathOf_ResultAlwaysStartsWithUserClassesRoot()
    {
        string[] inputs =
        {
            @"HKLM\SOFTWARE\Classes\*\shell\X",
            @"HKCU\Software\Classes\*\shell\X",
            @"HKLM\SOFTWARE\Classes\Directory\shell\X",
            @"HKLM\SOFTWARE\Classes\Drive\shellex\ContextMenuHandlers\X",
        };
        foreach (string input in inputs)
        {
            string result = MenuManagerService.ShadowPathOf(input);
            Assert.StartsWith(UserClassesRoot, result, StringComparison.OrdinalIgnoreCase);
        }
    }
}
