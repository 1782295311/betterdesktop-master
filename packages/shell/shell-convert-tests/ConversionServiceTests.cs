using System.IO;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Convert.Contracts;
using BetterDesktop.Shell.Convert.Services;

namespace BetterDesktop.Shell.Convert.Tests;

/// <summary>
/// 服务层测试：S9 后执行核心全在 Rust 侧，本套测试用 FakeRustRunner 打桩 IRustConvertRunner，
/// 只测 ConversionService 的 C# 侧行为（InFlight 去重 / 批量事件 / 错误分类 / 输入校验）。
/// </summary>
public class ConversionServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bd-conv-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeEventBus _events = new();

    public ConversionServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理失败不阻断 */ }
    }

    private string WriteInput(string name, string content = "x")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>打桩 Rust 侧：RunAsync 返回预设结果，记录调用次数。</summary>
    private sealed class FakeRustRunner : IRustConvertRunner
    {
        public int Calls;
        public Func<IReadOnlyList<string>, ConversionResult>? Handler;
        public int DelayMs; // 模拟耗时，用于 InFlight 去重测试

        public async Task<ConversionResult> RunAsync(
            IReadOnlyList<string> paths, string targetFormat, string? password,
            string fallbackEngine, long startedAt, CancellationToken ct)
        {
            Calls++;
            if (DelayMs > 0)
            {
                await Task.Delay(DelayMs, ct);
            }
            var result = Handler?.Invoke(paths)
                ?? new ConversionResult(paths[0], true, Path.Combine(Path.GetDirectoryName(paths[0])!, "output.pdf"),
                    ConvertError.None, "fake", 1);
            return result;
        }
    }

    [Fact]
    public async Task 单文件成功_发出finished事件()
    {
        var input = WriteInput("doc.docx");
        var runner = new FakeRustRunner();
        var service = new ConversionService(new EngineRegistry(), _events, rustRunner: runner);

        var results = await service.ConvertAsync([input], "pdf");

        var result = Assert.Single(results);
        Assert.True(result.Success, result.Message);
        Assert.Equal(1, runner.Calls);
        Assert.Contains(_events.Emitted, e => e.Name == "convert/batch-finished");
        var batch = Assert.IsType<ConvertBatchEventPayload>(_events.Emitted.First(e => e.Name == "convert/batch-finished").Payload);
        Assert.Equal(1, batch.Succeeded);
        Assert.Equal(0, batch.Failed);
    }

    [Fact]
    public async Task 批量一项失败不影响其他_计数正确()
    {
        var good = WriteInput("good.docx");
        var bad = WriteInput("bad.docx");
        var runner = new FakeRustRunner
        {
            Handler = paths => paths[0].Contains("good")
                ? new ConversionResult(paths[0], true, "good.pdf", ConvertError.None, "fake", 1)
                : new ConversionResult(paths[0], false, null, ConvertError.ConversionFailed, "fake", 1, "模拟失败"),
        };
        var service = new ConversionService(new EngineRegistry(), _events, rustRunner: runner);

        var results = await service.ConvertAsync([good, bad], "pdf");

        Assert.Equal(2, results.Count);
        Assert.True(results[0].Success);
        Assert.False(results[1].Success);
        Assert.Equal(ConvertError.ConversionFailed, results[1].Error);
        var batch = Assert.IsType<ConvertBatchEventPayload>(_events.Emitted.First(e => e.Name == "convert/batch-finished").Payload);
        Assert.Equal(2, batch.Total);
        Assert.Equal(1, batch.Succeeded);
        Assert.Equal(1, batch.Failed);
    }

    [Fact]
    public async Task 不支持的类型_InputInvalid()
    {
        var input = WriteInput("a.exe");
        var runner = new FakeRustRunner();
        var service = new ConversionService(new EngineRegistry(), _events, rustRunner: runner);

        var result = Assert.Single(await service.ConvertAsync([input], "pdf"));

        Assert.False(result.Success);
        Assert.Equal(ConvertError.InputInvalid, result.Error);
    }

    [Fact]
    public async Task 并发同文件同目标_InFlight去重()
    {
        var input = WriteInput("doc.docx");
        var runner = new FakeRustRunner { DelayMs = 100 };
        var service = new ConversionService(new EngineRegistry(), _events, rustRunner: runner);

        var first = service.ConvertAsync([input], "pdf");
        var second = service.ConvertAsync([input], "pdf");
        var results = await Task.WhenAll(first, second);

        Assert.True(results[0][0].Success || results[1][0].Success);
        Assert.False(results[0][0].Success && results[1][0].Success);
        Assert.Equal("已在转换中", (results[0][0].Success ? results[1] : results[0])[0].Message);
    }
}
