using System;
using BetterDesktop.Shell.AppSource.Services;
using Xunit;

namespace BetterDesktop.Shell.AppSource.Tests;

/// <summary>
/// 【S6 · 2026-09-14】口袋目录设置的序列化回归。
/// <para>
/// 为什么值得测：<c>ISettingsService</c> 只支持基本类型，目录列表必须经 JSON 字符串往返；
/// 解析一旦抛异常，整个「应用来源」分区的渲染就会崩（设置窗口白屏），故坏数据必须降级为空列表。
/// </para>
/// </summary>
public class AppSourceSettingsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseRoots_EmptyInput_ReturnsEmpty(string? json)
    {
        Assert.Empty(AppSourceSettings.ParseRoots(json));
    }

    [Fact]
    public void ParseRoots_JsonArray_ReturnsPaths()
    {
        var roots = AppSourceSettings.ParseRoots(@"[""D:\\Tools"",""C:\\Games\\MAA""]");

        Assert.Equal(new[] { @"D:\Tools", @"C:\Games\MAA" }, roots);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just-a-string\"")]
    public void ParseRoots_BadData_ReturnsEmptyWithoutThrowing(string json)
    {
        // 设置文件被手改坏 / 旧版本残留：必须降级为空，不得让设置窗口崩掉
        Assert.Empty(AppSourceSettings.ParseRoots(json));
    }

    [Fact]
    public void SerializeRoots_TrimsAndSkipsBlank()
    {
        var json = AppSourceSettings.SerializeRoots(new[] { @"  D:\Tools  ", "", "   ", @"C:\Games\MAA" });

        Assert.Equal(new[] { @"D:\Tools", @"C:\Games\MAA" }, AppSourceSettings.ParseRoots(json));
    }

    [Fact]
    public void SerializeRoots_RoundTrips()
    {
        var original = new[] { @"D:\便携工具", @"C:\Program Files (x86)\Some App" };

        var restored = AppSourceSettings.ParseRoots(AppSourceSettings.SerializeRoots(original));

        Assert.Equal(original, restored);
    }

    [Fact]
    public void SerializeRoots_Empty_ProducesParseableEmpty()
    {
        Assert.Empty(AppSourceSettings.ParseRoots(AppSourceSettings.SerializeRoots(Array.Empty<string>())));
    }
}
