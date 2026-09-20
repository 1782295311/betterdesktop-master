using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.AppSource.Services;
using BetterDesktop.Shell.IndexIpc;
using Xunit;

namespace BetterDesktop.Shell.AppSource.Tests;

/// <summary>
/// 【M2 · 2026-09-13】引擎缺席 / 未连接时的**降级路径**回归（DoD D5/D7 的自动化部分）。
/// <para>
/// 应用源是壳的基础能力：索引引擎不在（未部署 / 崩了 / 还在构建）时必须**照常工作**（走本地实现）
/// 且**不得抛异常**，并把降级原因记成 Warn（"降级必须可见"，不许静默）。
/// </para>
/// </summary>
public class AppSourceServiceFallbackTests
{
    /// <summary>只关心 Warn 的极简日志（避免依赖 LogLevel 枚举成员名）。</summary>
    private sealed class WarnCollector : IKernelLogger
    {
        public List<string> Warnings { get; } = new();

        public void Log(LogLevel level, string message)
        {
        }

        public void Info(string message)
        {
        }

        public void Warn(string message) => Warnings.Add(message);

        public void Error(string message)
        {
        }
    }

    [Fact]
    public void ScanStartMenu_WithoutEngineClient_UsesLocalAndDoesNotThrow()
    {
        using var service = new AppSourceService(dataDirectory: null, indexClient: null, logger: null);

        var apps = service.ScanStartMenu();

        Assert.NotNull(apps);
    }

    [Fact]
    public void ScanStartMenu_WithDisconnectedClient_FallsBackAndLogsWarn()
    {
        var logger = new WarnCollector();
#pragma warning disable CA2000 // transport 所有权移交 IndexIpcClient（其 Dispose 会释放 transport）
        using var client = new IndexIpcClient(new NamedPipeTransport());
#pragma warning restore CA2000
        using var service = new AppSourceService(dataDirectory: null, indexClient: client, logger: logger);

        var apps = service.ScanStartMenu();

        Assert.NotNull(apps);
        Assert.Contains(logger.Warnings, m => m.Contains("回退本地"));
    }
}
