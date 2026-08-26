using System;
using System.IO;
using BetterDesktop.Kernel.Loader;
using Xunit;

namespace BetterDesktop.Kernel.Loader.Tests;

/// <summary>
/// ResolveConfigPath 方法测试。
/// </summary>
public sealed class ResolveConfigPathTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFile;

    public ResolveConfigPathTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"loader-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _configFile = Path.Combine(_tempDir, "cordis.yml");
        File.WriteAllText(_configFile, "plugins: []");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact(DisplayName = "绝对路径原样返回")]
    public void ResolveConfigPath_AbsolutePath_ReturnsSame()
    {
        // Arrange
        var absolutePath = Path.GetFullPath(_configFile);

        // Act
        var result = InvokeResolveConfigPath(absolutePath);

        // Assert
        Assert.Equal(absolutePath, result);
    }

    [Fact(DisplayName = "相对路径在当前目录存在时返回当前目录路径")]
    public void ResolveConfigPath_RelativePathExistsInCwd_ReturnsCwdPath()
    {
        // Arrange
        var originalCwd = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _tempDir;
            var relativePath = "cordis.yml";

            // Act
            var result = InvokeResolveConfigPath(relativePath);

            // Assert
            Assert.Equal(_configFile, result);
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
        }
    }

    [Fact(DisplayName = "相对路径在当前目录不存在时返回程序基目录路径")]
    public void ResolveConfigPath_RelativePathNotExistsInCwd_ReturnsBaseDirectoryPath()
    {
        // Arrange
        var originalCwd = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = Path.GetTempPath();
            var relativePath = "cordis.yml";

            // Act
            var result = InvokeResolveConfigPath(relativePath);

            // Assert
            var expected = Path.Combine(AppContext.BaseDirectory, relativePath);
            Assert.Equal(expected, result);
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
        }
    }

    private static string InvokeResolveConfigPath(string configPath)
    {
        // 使用反射调用私有静态方法
        var method = typeof(LoaderService).GetMethod("ResolveConfigPath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        if (method == null)
        {
            throw new InvalidOperationException("ResolveConfigPath method not found");
        }

        return (string)method.Invoke(null, new object[] { configPath })!;
    }
}
