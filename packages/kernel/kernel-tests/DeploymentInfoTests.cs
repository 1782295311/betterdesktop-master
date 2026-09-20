// DeploymentInfo 单测（安装器级 2026-09-17）：安装根指针的读写与失败语义。
//
// 为什么用显式路径重载：默认路径是真实用户的 %LOCALAPPDATA%\BetterDesktop\deployment.json，
// 单测绝不能碰它（写坏 = 所有消费者定位错安装）。显式路径重载让每个分支都能在临时目录里跑。

using System;
using System.IO;
using BetterDesktop.Kernel.Deployment;
using Xunit;

namespace BetterDesktop.Kernel.Tests;

public sealed class DeploymentInfoTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bdt-deployment-" + Guid.NewGuid().ToString("N")[..8]);

    public DeploymentInfoTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    private string PointerPath => Path.Combine(_root, DeploymentInfo.FileName);

    [Fact]
    public void Read_MissingFile_ReturnsFalseWithoutThrowing()
    {
        var ok = DeploymentInfo.TryRead(PointerPath, out var record, out var error);

        Assert.False(ok);
        Assert.Null(record);
        Assert.NotNull(error);
    }

    [Fact]
    public void Read_BrokenJson_ReturnsFalseWithReason()
    {
        File.WriteAllText(PointerPath, "{ this is not json");

        var ok = DeploymentInfo.TryRead(PointerPath, out var record, out var error);

        Assert.False(ok);
        Assert.Null(record);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Read_JsonWithoutInstallRoot_IsRejected()
    {
        File.WriteAllText(PointerPath, "{\"schemaVersion\":1,\"version\":\"1.3.0\"}");

        var ok = DeploymentInfo.TryRead(PointerPath, out _, out var error);

        Assert.False(ok);
        Assert.Contains("installRoot", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriteThenRead_RoundTripsAllFields()
    {
        var installRoot = Path.Combine(_root, "app", "2026.09.17.2140");
        Directory.CreateDirectory(installRoot);

        var written = DeploymentInfo.Write(
            new DeploymentRecord
            {
                Version = "1.3.0",
                Build = "2026.09.17.2140",
                InstallRoot = installRoot,
                MsixMode = "loose",
            },
            PointerPath,
            out var writeError);

        Assert.True(written, writeError);

        var read = DeploymentInfo.TryRead(PointerPath, out var record, out var readError);

        Assert.True(read, readError);
        Assert.NotNull(record);
        Assert.Equal("BetterDesktop", record!.Product);
        Assert.Equal(DeploymentInfo.SchemaVersion, record.SchemaVersion);
        Assert.Equal("1.3.0", record.Version);
        Assert.Equal("2026.09.17.2140", record.Build);
        Assert.Equal(installRoot, record.InstallRoot);
        Assert.Equal("loose", record.MsixMode);
        Assert.False(string.IsNullOrWhiteSpace(record.InstalledAt));
    }

    [Fact]
    public void Write_EmptyInstallRoot_IsRejectedAndWritesNothing()
    {
        var ok = DeploymentInfo.Write(new DeploymentRecord(), PointerPath, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.False(File.Exists(PointerPath));
    }

    [Fact]
    public void Write_LeavesNoTempFileBehind()
    {
        var installRoot = Path.Combine(_root, "app");
        Directory.CreateDirectory(installRoot);

        _ = DeploymentInfo.Write(
            new DeploymentRecord { InstallRoot = installRoot },
            PointerPath,
            out _);

        Assert.False(File.Exists(PointerPath + ".tmp"));
    }

    [Fact]
    public void ResolveInstallRoot_MissingRootDirectory_ReturnsNull()
    {
        // 指针存在但目录已删除 = 升级/清理后的悬空指针：必须当"没有安装"处理，
        // 否则所有消费者会一起指向一个不存在的目录。
        var gone = Path.Combine(_root, "app", "removed-build");
        _ = DeploymentInfo.Write(new DeploymentRecord { InstallRoot = gone }, PointerPath, out _);

        Assert.Null(DeploymentInfo.ResolveInstallRoot(PointerPath));
    }

    [Fact]
    public void ResolveInstallRoot_ExistingDirectory_ReturnsIt()
    {
        var installRoot = Path.Combine(_root, "app", "current");
        Directory.CreateDirectory(installRoot);
        _ = DeploymentInfo.Write(new DeploymentRecord { InstallRoot = installRoot }, PointerPath, out _);

        Assert.Equal(installRoot, DeploymentInfo.ResolveInstallRoot(PointerPath));
    }

    [Fact]
    public void ResolveInstallRoot_NoPointer_ReturnsNull()
    {
        Assert.Null(DeploymentInfo.ResolveInstallRoot(PointerPath));
    }
}
