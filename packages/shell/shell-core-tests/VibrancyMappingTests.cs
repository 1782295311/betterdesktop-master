using BetterDesktop.Shell.Core.Vibrancy;
using Xunit;

namespace BetterDesktop.Shell.Core.Tests;

/// <summary>VibrancyMapping.ToParams 纯函数映射测试。</summary>
public sealed class VibrancyMappingTests
{
    [Fact(DisplayName = "Transparent → BlurBehind（全局默认毛玻璃，恢复语义）")]
    public void ToParams_Transparent_MapsToBlurBehind()
    {
        // Transparent 是 appearance.material 默认值，所有走基类的窗口依赖它获得默认毛玻璃。
        // 不能映射成 None，否则全局窗口失去磨砂。
        Assert.Equal(VibrancyMode.BlurBehind, VibrancyMapping.ToParams(VibrancyStyle.Transparent));
    }

    [Fact(DisplayName = "None → None（dock「清晰」档：真正关闭 DWM 磨砂）")]
    public void ToParams_None_MapsToNone()
    {
        Assert.Equal(VibrancyMode.None, VibrancyMapping.ToParams(VibrancyStyle.None));
    }

    [Fact(DisplayName = "Acrylic → Acrylic")]
    public void ToParams_Acrylic_MapsToAcrylic()
    {
        Assert.Equal(VibrancyMode.Acrylic, VibrancyMapping.ToParams(VibrancyStyle.Acrylic));
    }
}
