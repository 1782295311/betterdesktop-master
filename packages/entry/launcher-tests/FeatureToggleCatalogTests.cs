// BetterDesktop 启动器单测 —— 开关目录与权威映射的一致性守卫。

using System.Collections.Generic;
using System.Linq;
using BetterDesktop.Launcher.Services;
using BetterDesktop.Shell.Core.DesktopControl;
using Xunit;

namespace BetterDesktop.Launcher.Tests;

/// <summary>
/// 开关目录守卫。
/// <para>
/// 【为什么必须有】启动器的开关是**翻转**落地（--toggle-key 取反）。命令名或键名一旦与权威表
/// (<see cref="DesktopToggleCatalog"/>) 漂移，后果是**静默错翻**：翻到别的开关上、或永远翻不对方向。
/// 用户只会看到"我改了一个，结果变的是另一个/它自己又变回来了"，而代码里一点痕迹都没有。
/// 这类漂移只能靠测试钉住（仓库对此有先例：命令名/键名映射此前已漂移过一次）。
/// </para>
/// </summary>
public sealed class FeatureToggleCatalogTests
{
    [Fact]
    public void 翻转式开关的命令名与键名必须与桌面控制目录逐字一致()
    {
        foreach (var toggle in FeatureToggleCatalog.All.Where(t => t.IsFlip))
        {
            var args = toggle.FlipArgs!;

            if (args[0] == "--toggle-desktop")
            {
                // 自绘桌面在权威表里是**独立动词**（不参与 --toggle-key 映射），只能长这样。
                Assert.Single(args);
                Assert.Equal("components.desktop", toggle.Key);
                continue;
            }

            Assert.Equal("--toggle-key", args[0]);
            Assert.Equal(2, args.Length);

            var commandName = args[1];
            Assert.True(
                DesktopToggleCatalog.TryGet(commandName, out var spec),
                $"命令名“{commandName}”不在 DesktopToggleCatalog 中（键 {toggle.Key}）");

            // 键名一致 → 读写的是同一个设置键（否则"读 A 的值、翻 B 的开关"）。
            Assert.Equal(spec.SettingsKey, toggle.Key);

            // 默认值一致 → 首次启动（设置文件不存在）时的初始勾选态与宿主实际行为一致。
            Assert.Equal(spec.Default, toggle.Default);
        }
    }

    [Fact]
    public void 幂等式开关必须同时给出启用与停用参数()
    {
        foreach (var toggle in FeatureToggleCatalog.All.Where(t => !t.IsFlip))
        {
            Assert.NotNull(toggle.EnableArgs);
            Assert.NotNull(toggle.DisableArgs);
            Assert.NotEmpty(toggle.EnableArgs!);
            Assert.NotEmpty(toggle.DisableArgs!);
        }
    }

    [Fact]
    public void 键不得重复()
    {
        var keys = FeatureToggleCatalog.All.Select(t => t.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Fact]
    public void 每行都必须有可读的标签与说明()
    {
        foreach (var toggle in FeatureToggleCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(toggle.Label));
            Assert.False(string.IsNullOrWhiteSpace(toggle.Description));
            Assert.False(string.IsNullOrWhiteSpace(toggle.Key));
        }
    }

    [Fact]
    public void 推荐默认值取自各行默认值()
    {
        IReadOnlyDictionary<string, bool> recommended = FeatureToggleCatalog.RecommendedDefaults();
        Assert.Equal(FeatureToggleCatalog.All.Count, recommended.Count);
        foreach (var toggle in FeatureToggleCatalog.All)
        {
            Assert.Equal(toggle.Default, recommended[toggle.Key]);
        }
    }
}
