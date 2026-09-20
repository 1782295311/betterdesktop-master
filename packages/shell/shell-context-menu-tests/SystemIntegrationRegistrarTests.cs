// SystemIntegrationRegistrar 单测（安装器级 2026-09-17）
//
// 测什么 / 不测什么的边界（刻意，别把它当覆盖不全）：
//   · 测：Describe（CLI / 托盘 / 设置中心共用的唯一状态文案）——纯函数，字段漏一个就少一行，
//         是"状态面板说真话"的直接保证；GetStatus 不抛且字段自洽。
//   · 不测：Register / Unregister —— 它们**改动用户真实系统**（HKCU 注册表键树 + 自启项）。
//         单测里跑一次就会把使用者机器上的右键扩展注销掉（或把自启写歪），
//         属于"测了比不测更糟"。这两条链的证据在：安装/卸载脚本真机走查（DoD D1/D6/D7）
//         + scripts/verify-system-integration.ps1（字面量与四子命令一致性）。

using System;
using System.IO;
using BetterDesktop.Shell.ContextMenus.Services;
using Xunit;

namespace BetterDesktop.Shell.ContextMenu.Tests;

public sealed class SystemIntegrationRegistrarTests
{
    private static IntegrationStatus SampleStatus() => new()
    {
        InstallRoot = @"C:\Users\x\AppData\Local\BetterDesktop\app\2026.09.17.2140",
        Version = "1.3.0",
        Build = "2026.09.17.2140",
        MsixMode = "loose",
        ComRegistered = true,
        ComDllPath = @"C:\install\native\BetterDesktopShellMenu.dll",
        ComRegisteredPath = @"C:\install\native\BetterDesktopShellMenu.dll",
        ComPathDrifted = false,
        SnapshotPresent = true,
        SnapshotPath = @"C:\Users\x\AppData\Roaming\BetterDesktop\shellmenu.json",
        TrayAutostart = true,
        WatchdogAutostart = false,
        ShellAutostart = true,
        MsixRegistered = true,
        MsixVersion = "1.3.254.2351",
        MsixLocation = @"C:\install",
    };

    [Fact(DisplayName = "Describe 输出全部状态键（键=值，脚本可解析）")]
    public void Describe_EmitsEveryField()
    {
        var text = SystemIntegrationRegistrar.Describe(SampleStatus());

        foreach (var key in new[]
                 {
                     "installRoot=", "version=", "build=", "msixMode=", "comRegistered=",
                     "comDllPath=", "comRegisteredPath=", "comPathDrifted=", "snapshotPresent=",
                     "snapshotPath=", "autostartTray=", "autostartWatchdog=", "autostartShell=",
                     "msixRegistered=", "msixVersion=", "msixLocation=",
                 })
        {
            Assert.Contains(key, text, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "A 路查询不可用时显示 unknown（不冒充未注册）")]
    public void Describe_UnknownMsix_IsRenderedAsUnknown()
    {
        var status = SampleStatus() with { MsixRegistered = null, MsixError = "WinRT unavailable" };

        var text = SystemIntegrationRegistrar.Describe(status);

        Assert.Contains("msixRegistered=unknown", text, StringComparison.Ordinal);
        Assert.Contains("msixError=WinRT unavailable", text, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "未安装/未部署时状态文本给出可读占位而不是空白")]
    public void Describe_UninstalledState_IsReadable()
    {
        var text = SystemIntegrationRegistrar.Describe(new IntegrationStatus());

        Assert.Contains("(未安装", text, StringComparison.Ordinal);
        Assert.Contains("(未部署)", text, StringComparison.Ordinal);
        Assert.Contains("(未注册)", text, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Describe 拒绝 null 状态对象")]
    public void Describe_NullStatus_Throws()
        => Assert.Throws<ArgumentNullException>(() => SystemIntegrationRegistrar.Describe(null!));

    [Fact(DisplayName = "GetStatus 不抛且与真机注册表自洽（读取型断言，不写任何键）")]
    public void GetStatus_DoesNotThrowAndIsSelfConsistent()
    {
        var status = SystemIntegrationRegistrar.GetStatus();

        // 路径类字段：要么给出路径，要么明确为空 —— 不允许"有内容但为空串"
        Assert.False(status.ComDllPath == string.Empty);
        Assert.False(status.SnapshotPath == string.Empty);
        Assert.NotNull(status.SnapshotPath);

        // 漂移判定必须建立在两侧都有值之上（只有一边有值 = 未注册/未部署，不算漂移）
        if (status.ComPathDrifted)
        {
            Assert.False(string.IsNullOrWhiteSpace(status.ComDllPath));
            Assert.False(string.IsNullOrWhiteSpace(status.ComRegisteredPath));
        }

        // 快照存在性必须与文件系统一致（这是原生侧"显示什么"的直接开关）
        Assert.Equal(File.Exists(status.SnapshotPath), status.SnapshotPresent);
    }

    [Fact(DisplayName = "包名是跨进程契约（AppxManifest 与卸载脚本都按它找包）")]
    public void MsixPackageName_IsStableContract()
        => Assert.Equal("BetterDesktop.ShellMenu", SystemIntegrationRegistrar.MsixPackageName);
}
