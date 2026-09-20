// BetterDesktop 启动器单测 —— 系统整合状态解析。
//
// 【为什么值得测】这段解析决定启动器要不要**自动重新注册系统右键菜单**：
// 把 False 读成 True（或整行认不出来）→ 该修的没修，用户继续看到"注册了但点了没反应"；
// 反过来把未知读成 False → 每次启动都重注册（无害但刷日志、且会覆盖用户显式注销的意图）。

using BetterDesktop.Launcher.Services;
using Xunit;

namespace BetterDesktop.Launcher.Tests;

/// <summary><see cref="ComponentBootstrapper.ParseStatus"/> 的解析守卫。</summary>
public sealed class ComponentBootstrapperTests
{
    /// <summary>真实输出样本（字段顺序与 CLI 的 Describe 一致；CRLF 行尾）。</summary>
    private const string RealStatus =
        "installRoot=C:\\Users\\x\\AppData\\Local\\BetterDesktop\\app\\2026.09.18.0649\r\n"
        + "version=1.3.0\r\n"
        + "build=2026.09.18.0649\r\n"
        + "msixMode=loose\r\n"
        + "comRegistered=True\r\n"
        + "comDllPath=C:\\app\\native\\BetterDesktopShellMenu.dll\r\n"
        + "comRegisteredPath=C:\\other\\native\\BetterDesktopShellMenu.dll\r\n"
        + "comPathDrifted=True\r\n"
        + "snapshotPresent=False\r\n"
        + "snapshotPath=C:\\Users\\x\\AppData\\Roaming\\BetterDesktop\\shellmenu.json\r\n"
        + "autostartTray=True\r\n"
        + "autostartWatchdog=True\r\n"
        + "autostartShell=False\r\n";

    [Fact]
    public void 解析真实状态文本()
    {
        var status = ComponentBootstrapper.ParseStatus(RealStatus);

        Assert.True(status.ComRegistered);
        Assert.True(status.ComPathDrifted);
        Assert.False(status.SnapshotPresent);
        Assert.True(status.TrayAutostart);
    }

    [Fact]
    public void 缺失字段必须为未知而不是false()
    {
        // 关键区分：CLI 换版本后少了字段，不能当成"没注册"（否则会去重注册，
        // 而那正好会覆盖用户显式注销系统右键菜单的意图）。
        var status = ComponentBootstrapper.ParseStatus("version=1.3.0\ninstallRoot=C:\\x\n");

        Assert.Null(status.ComRegistered);
        Assert.Null(status.ComPathDrifted);
        Assert.Null(status.SnapshotPresent);
        Assert.Null(status.TrayAutostart);
    }

    [Fact]
    public void 非布尔值视为未知()
    {
        var status = ComponentBootstrapper.ParseStatus("comRegistered=unknown\ncomPathDrifted=\n");

        Assert.Null(status.ComRegistered);
        Assert.Null(status.ComPathDrifted);
    }

    [Fact]
    public void 空文本不抛异常且全为未知()
    {
        var status = ComponentBootstrapper.ParseStatus(string.Empty);

        Assert.Null(status.ComRegistered);
        Assert.Null(status.TrayAutostart);
    }
}
