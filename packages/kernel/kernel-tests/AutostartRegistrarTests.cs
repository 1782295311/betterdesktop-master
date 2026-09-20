// AutostartRegistrar 单测（安装器级 2026-09-17）：Run 键 + StartupApproved 双写语义。
//
// 为什么写真实 HKCU：这条链的价值全在"注册表实际长什么样"——
// 只写 Run 键会被任务管理器的"禁用"状态静默压制（技术力 7430 红线 1），
// 用假注册表就把被测行为换掉了。为不污染用户自启，全部用**临时值名**并在 finally 里清干净。

using System;
using Microsoft.Win32;
using BetterDesktop.Kernel.Deployment;
using Xunit;

namespace BetterDesktop.Kernel.Tests;

public sealed class AutostartRegistrarTests
{
    private static string NewTempName() => "BetterDesktop.Tests." + Guid.NewGuid().ToString("N")[..8];

    private static byte[]? ReadApproved(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutostartRegistrar.StartupApprovedKeyPath);
        return key?.GetValue(valueName) as byte[];
    }

    private static void WriteApproved(string valueName, byte state)
    {
        using var key = Registry.CurrentUser.CreateSubKey(AutostartRegistrar.StartupApprovedKeyPath);
        var bytes = new byte[12];
        bytes[0] = state;
        key!.SetValue(valueName, bytes, RegistryValueKind.Binary);
    }

    [Fact(DisplayName = "登记自启会同时写启用态（StartupApproved Byte0=2）")]
    public void Set_RegistersAndMarksApprovedEnabled()
    {
        var name = NewTempName();
        try
        {
            Assert.True(AutostartRegistrar.Set(name, @"C:\some\BetterDesktop.Tray.exe", out var error), error);

            Assert.True(AutostartRegistrar.IsEnabled(name));
            Assert.Equal("\"C:\\some\\BetterDesktop.Tray.exe\"", AutostartRegistrar.GetCommand(name));

            var approved = ReadApproved(name);
            Assert.NotNull(approved);
            Assert.Equal(2, approved![0]);
        }
        finally
        {
            AutostartRegistrar.Remove(name, out _);
        }
    }

    [Fact(DisplayName = "任务管理器禁用态（Byte0=3）必须被识别为不会自启")]
    public void IsEnabled_TaskManagerDisabled_ReturnsFalse()
    {
        var name = NewTempName();
        try
        {
            Assert.True(AutostartRegistrar.Set(name, @"C:\some\BetterDesktop.Tray.exe", out _));
            WriteApproved(name, 3); // 模拟用户在任务管理器里禁用它

            Assert.False(AutostartRegistrar.IsEnabled(name));

            // 再登记一次 = 用户明确要它自启：必须把禁用态清掉，否则"登记了也不生效"
            Assert.True(AutostartRegistrar.Set(name, @"C:\some\BetterDesktop.Tray.exe", out _));
            Assert.True(AutostartRegistrar.IsEnabled(name));
            Assert.Equal(2, ReadApproved(name)![0]);
        }
        finally
        {
            AutostartRegistrar.Remove(name, out _);
        }
    }

    [Fact(DisplayName = "取消自启会清掉 Run 值与 StartupApproved 条目")]
    public void Set_NullCommand_RemovesBothSides()
    {
        var name = NewTempName();
        try
        {
            Assert.True(AutostartRegistrar.Set(name, @"C:\some\BetterDesktop.Tray.exe", out _));
            Assert.NotNull(ReadApproved(name));

            Assert.True(AutostartRegistrar.Set(name, null, out var error), error);

            Assert.False(AutostartRegistrar.IsEnabled(name));
            Assert.Null(AutostartRegistrar.GetCommand(name));
            Assert.Null(ReadApproved(name));
        }
        finally
        {
            AutostartRegistrar.Remove(name, out _);
        }
    }

    [Fact(DisplayName = "删除不存在的自启项不算错误（ERROR_FILE_NOT_FOUND 不当失败）")]
    public void Remove_MissingValue_Succeeds()
    {
        var name = NewTempName();

        Assert.True(AutostartRegistrar.Remove(name, out var error));
        Assert.Null(error);
    }

    [Fact(DisplayName = "空值名被拒绝并给出原因")]
    public void Set_EmptyValueName_Rejected()
    {
        Assert.False(AutostartRegistrar.Set("  ", @"C:\x.exe", out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact(DisplayName = "未登记的值名 IsEnabled=false / GetCommand=null")]
    public void UnregisteredValue_ReportsNotEnabled()
    {
        var name = NewTempName();

        Assert.False(AutostartRegistrar.IsEnabled(name));
        Assert.Null(AutostartRegistrar.GetCommand(name));
    }

    [Fact(DisplayName = "KnownValueNames 覆盖托盘/看门狗/主程序三个值名（卸载依赖它逐个清）")]
    public void KnownValueNames_CoverAllOwnedEntries()
    {
        Assert.Contains(AutostartRegistrar.TrayValueName, AutostartRegistrar.KnownValueNames);
        Assert.Contains(AutostartRegistrar.WatchdogValueName, AutostartRegistrar.KnownValueNames);
        Assert.Contains(AutostartRegistrar.ShellValueName, AutostartRegistrar.KnownValueNames);
        Assert.Equal("BetterDesktop.Tray", AutostartRegistrar.TrayValueName);
    }
}
