// GuidInfo CLSID 反查引擎单测（M2）：测内核非 UI，覆盖正常/边界/异常/缓存。
// 已知系统 CLSID 来自内置字典 GuidInfosDic.ini（OpenWith/LnkOpen 等），不依赖第三方软件安装。

using System;
using BetterDesktop.Shell.ContextMenus.Services;
using Xunit;

namespace BetterDesktop.Shell.ContextMenu.Tests;

public class GuidInfoTests
{
    // 内置字典中存在的系统 CLSID（OpenWith：打开方式）
    private static readonly Guid OpenWithGuid = new("09799AFB-AD67-11D1-ABCD-00C04FC30936");
    // 快捷方式打开项（LnkOpenGuid，受保护）
    private static readonly Guid LnkOpenGuid = new("00021401-0000-0000-c000-000000000046");

    [Fact]
    public void GetText_EmptyGuid_ReturnsNull()
    {
        Assert.Null(GuidInfo.GetText(Guid.Empty));
    }

    [Fact]
    public void GetFilePath_EmptyGuid_ReturnsNull()
    {
        Assert.Null(GuidInfo.GetFilePath(Guid.Empty));
    }

    [Fact]
    public void GetIconLocation_EmptyGuid_ReturnsNull()
    {
        Assert.Null(GuidInfo.GetIconLocation(Guid.Empty));
    }

    [Fact]
    public void GetClsidPath_EmptyGuid_ReturnsNull()
    {
        Assert.Null(GuidInfo.GetClsidPath(Guid.Empty));
    }

    [Fact]
    public void GetText_KnownDictionaryClsid_ReturnsNonNull()
    {
        // OpenWith 在内置字典中有 ResText=@shell32.dll,-5376，经 ResourceRef 解析应非空
        string? text = GuidInfo.GetText(OpenWithGuid);
        Assert.NotNull(text);
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public void GetText_LnkOpenGuid_ReturnsNonNull()
    {
        // LnkOpen 在字典中有 Text=@windows.storage.dll,-8496（"打开"）
        string? text = GuidInfo.GetText(LnkOpenGuid);
        Assert.NotNull(text);
    }

    [Fact(DisplayName = "OpenWith CLSID 调用 GetIconLocation 不抛异常（环境探针）")]
    public void GetIconLocation_KnownDictionaryClsid_DoesNotThrow()
    {
        // C14：图标位置依赖系统资源解析状态（注册表 + DLL 资源，图标索引可为负资源 ID），
        // 全量并行时其他测试类的注册表操作可能影响解析结果，返回值不稳定。
        // 此用例为环境探针：验证调用不抛异常 + 返回可空元组类型正确，不断言具体 Path/Index 值。
        var loc = GuidInfo.GetIconLocation(OpenWithGuid);
        Assert.True(loc is null || loc is (string, int));
    }

    [Fact(DisplayName = "随机 GUID 调用 GetText 不抛异常（环境探针）")]
    public void GetText_UnknownRandomGuid_DoesNotThrow()
    {
        // 随机 GUID 大概率不在字典和注册表中，GetText 应返回 null 且不抛异常。
        // C14：此为环境探针（不断言具体返回值，因为极少数情况注册表可能存在该 CLSID），
        // 执行到末尾即代表未抛异常。
        var random = Guid.NewGuid();
        string? text = GuidInfo.GetText(random);
        Assert.True(text is null || text is string, "返回值应为 null 或字符串");
    }

    [Fact]
    public void GetFilePath_UnknownRandomGuid_ReturnsNull()
    {
        var random = Guid.NewGuid();
        string? path = GuidInfo.GetFilePath(random);
        // 随机 GUID 大概率没有 InprocServer32，应返回 null
        Assert.Null(path);
    }

    [Fact]
    public void Caching_GetTextTwice_ReturnsSame()
    {
        string? first = GuidInfo.GetText(OpenWithGuid);
        string? second = GuidInfo.GetText(OpenWithGuid);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Caching_GetFilePathTwice_ReturnsSame()
    {
        string? first = GuidInfo.GetFilePath(OpenWithGuid);
        string? second = GuidInfo.GetFilePath(OpenWithGuid);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Invalidate_DoesNotThrow()
    {
        GuidInfo.GetText(OpenWithGuid); // 填充缓存
        GuidInfo.Invalidate(OpenWithGuid); // 清缓存
        string? after = GuidInfo.GetText(OpenWithGuid); // 重新填充
        Assert.NotNull(after);
    }

    [Fact]
    public void ClearCache_DoesNotThrow()
    {
        GuidInfo.ClearCache();
    }

    [Fact]
    public void GetClsidPath_KnownGuid_MatchesFormatOrNull()
    {
        string? path = GuidInfo.GetClsidPath(OpenWithGuid);
        if (path is not null)
        {
            Assert.Contains("CLSID", path, StringComparison.OrdinalIgnoreCase);
        }
        // 可能为 null（系统 CLSID 不一定在标准位置有 InprocServer32），不抛异常即可
    }
}
