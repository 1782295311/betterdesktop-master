using System.IO;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Convert.Contracts;
using BetterDesktop.Shell.Convert.Services;
using BetterDesktop.Shell.Convert.Services.Engines;

namespace BetterDesktop.Shell.Convert.Tests;

/// <summary>服务层批量/事件/InFlight/回读验证（mock 引擎，不起真进程）。</summary>
public class ConversionServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bd-conv-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeEventBus _events = new();

    public ConversionServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理失败不阻断 */ }
    }

    private ConversionService CreateService(params IConversionEngine[] engines)
    {
        var registry = new EngineRegistry();
        foreach (var engine in engines)
        {
            registry.Add(engine);
        }
        return new ConversionService(registry, _events);
    }

    private string WriteInput(string name, string content = "x")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task 单文件成功_发布到源目录并发出finished事件()
    {
        var input = WriteInput("doc.docx");

        var service = CreateService(new FakeEngine(EngineKind.Soffice, name: "fake-soffice")
        {
            Runner = job => WriteProduct(job, "doc.pdf"),
        });

        var results = await service.ConvertAsync([input], "pdf");

        var result = Assert.Single(results);
        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(result.Output));
        Assert.Equal(Path.Combine(_dir, "doc.pdf"), result.Output);
        Assert.Contains(_events.Emitted, e => e.Name == "convert/finished");
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
        var service = CreateService(new FakeEngine(EngineKind.Soffice, name: "fake")
        {
            Runner = job =>
                job.PrimarySource.Contains("good")
                    ? WriteProduct(job, "good.pdf")
                    : throw new ConvertException(ConvertError.ConversionFailed, "模拟引擎失败"),
        });

        var results = await service.ConvertAsync([good, bad], "pdf");

        Assert.Equal(2, results.Count);
        Assert.True(results[0].Success);
        Assert.False(results[1].Success);
        Assert.Equal(ConvertError.ConversionFailed, results[1].Error);
        var batch = Assert.IsType<ConvertBatchEventPayload>(_events.Emitted.First(e => e.Name == "convert/batch-finished").Payload);
        Assert.Equal(2, batch.Total);
        Assert.Equal(1, batch.Succeeded);
        Assert.Equal(1, batch.Failed); // 部分成功不当全成功（红线 13）
    }

    [Fact]
    public async Task 无引擎_按EngineMissing分类不伪装成文件错误()
    {
        var input = WriteInput("doc.docx");
        var service = CreateService(new FakeEngine(EngineKind.Soffice, available: false));

        var result = Assert.Single(await service.ConvertAsync([input], "pdf"));

        Assert.False(result.Success);
        Assert.Equal(ConvertError.EngineMissing, result.Error);
        Assert.Contains(_events.Emitted, e => e.Name == "convert/failed");
    }

    [Fact]
    public async Task 不支持的类型_InputInvalid()
    {
        var input = WriteInput("a.exe");
        var service = CreateService(new FakeEngine(EngineKind.Soffice));

        var result = Assert.Single(await service.ConvertAsync([input], "pdf"));

        Assert.False(result.Success);
        Assert.Equal(ConvertError.InputInvalid, result.Error);
    }

    [Fact]
    public async Task 并发同文件同目标_InFlight去重()
    {
        var input = WriteInput("doc.docx");
        var service = CreateService(new FakeEngine(EngineKind.Soffice, name: "fake")
        {
            RunnerAsync = async job =>
            {
                await Task.Delay(100);
                var product = Path.Combine(job.TempDir, Path.GetFileNameWithoutExtension(job.PrimarySource) + ".pdf");
                await File.WriteAllTextAsync(product, "x");
                IReadOnlyList<string> products = [product];
                return products;
            },
        });

        var first = service.ConvertAsync([input], "pdf");
        var second = service.ConvertAsync([input], "pdf");
        var results = await Task.WhenAll(first, second);

        Assert.True(results[0][0].Success || results[1][0].Success);
        Assert.False(results[0][0].Success && results[1][0].Success); // 恰有一个执行
        Assert.Equal("已在转换中", (results[0][0].Success ? results[1] : results[0])[0].Message);
    }

    [Fact]
    public async Task 已有目标文件_自动序号不覆盖()
    {
        var input = WriteInput("doc.docx");
        File.WriteAllText(Path.Combine(_dir, "doc.pdf"), "旧文件"); // 预占目标
        var service = CreateService(new FakeEngine(EngineKind.Soffice)
        {
            Runner = job => WriteProduct(job, "doc.pdf"),
        });

        var result = Assert.Single(await service.ConvertAsync([input], "pdf"));

        Assert.True(result.Success);
        Assert.Equal(Path.Combine(_dir, "doc (2).pdf"), result.Output); // (2) 序号，绝不覆盖
        Assert.Equal("旧文件", File.ReadAllText(Path.Combine(_dir, "doc.pdf")));
    }

    [Fact]
    public async Task 多PDF合并_单输出单事件()
    {
        var a = WriteInput("a.pdf");
        var b = WriteInput("b.pdf");
        var service = CreateService(new FakeEngine(EngineKind.PdfCompose)
        {
            Runner = job => WriteProduct(job, "merged.pdf"),
        });

        var result = Assert.Single(await service.ConvertAsync([a, b], "pdf"));

        Assert.True(result.Success);
        Assert.EndsWith("a（合并）.pdf", result.Output);
    }

    [Fact]
    public async Task 产物验证失败_恰好重试一次后报ConversionFailed()
    {
        var input = WriteInput("doc.docx");
        var attempts = 0;
        var service = CreateService(new FakeEngine(EngineKind.Soffice)
        {
            Runner = _ =>
            {
                attempts++;
                return []; // 始终无产物
            },
        });

        var result = Assert.Single(await service.ConvertAsync([input], "pdf"));

        Assert.False(result.Success);
        Assert.Equal(ConvertError.ConversionFailed, result.Error);
        Assert.Equal(2, attempts); // 恰好重试一次（红线 12）
    }

    private static IReadOnlyList<string> WriteProduct(ConversionJob job, string fileName)
    {
        var path = Path.Combine(job.TempDir, fileName);
        File.WriteAllText(path, "product");
        return [path];
    }
}
