// BetterDesktop.Shell.Dock.Tests — DockVisualSettings 构造冒烟（W3-2：零用例测试项目补冒烟）。

using BetterDesktop.Shell.Dock.Services;
using Xunit;

namespace BetterDesktop.Shell.Dock.Tests;

public class DockVisualSettingsSmokeTests
{
    [Fact]
    public void Ctor_WithoutSettings_DoesNotThrow_DefaultsSane()
    {
        var settings = new DockVisualSettings(settings: null);

        // 空闲隐藏阈值：默认 20 分钟，且落在设计区间（1-240）
        Assert.InRange(settings.IdleHideMinutes, 1, 240);
        // 视觉参数可读且为非负
        Assert.True(settings.ReflectionOpacity >= 0);
        Assert.True(settings.ReflectionDistance >= 0);
    }
}
