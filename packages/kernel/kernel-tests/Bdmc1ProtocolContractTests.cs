using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BetterDesktop.Kernel.Core;
using Xunit;

namespace BetterDesktop.Kernel.Tests;

/// <summary>
/// BDMC1 协议**跨语言契约测试**：C# 实现必须通过 <c>protocols/bdmc1-test-vectors.json</c> 的全部用例。
/// </summary>
/// <remarks>
/// <para>
/// Rust 侧 <c>protocol::tests::matches_shared_vectors</c> 跑同一批向量。两侧都绿 ⇒ 两套实现对协议的理解一致；
/// 只跑各自手写的单测则完全不能保证这一点（那正是"各自猜边界"）。
/// </para>
/// <para>
/// 本用例**必须读文件**（而不是内嵌向量）：否则改了向量而没改实现，测试不会红。
/// </para>
/// </remarks>
public sealed class Bdmc1ProtocolContractTests
{
    private static readonly string VectorsPath = Path.Combine(
        FindRepoRoot(), "protocols", "bdmc1-test-vectors.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    [Fact(DisplayName = "共享向量文件存在（协议契约的唯一权威）")]
    public void VectorsFileExists()
    {
        Assert.True(File.Exists(VectorsPath), $"共享向量缺失：{VectorsPath}");
    }

    [Fact(DisplayName = "两侧的消息大小上限与共享向量一致")]
    public void MaxMessageBytesMatchesContract()
    {
        var doc = LoadVectors();
        Assert.Equal(MenuCommandPipeCodec.MaxMessageBytes, doc.MaxMessageBytes);
    }

    [Fact(DisplayName = "共享向量每一条都必须与 C# 实现一致")]
    public void MatchesSharedVectors()
    {
        var doc = LoadVectors();
        Assert.True(doc.Cases.Count >= 10, $"共享向量应覆盖足够边界（当前 {doc.Cases.Count} 条）");

        var failures = new List<string>();
        foreach (var c in doc.Cases)
        {
            var bytes = CaseBytes(c, doc.MaxMessageBytes);
            if (MenuCommandPipeCodec.TryParse(bytes, out var msg, out var reason))
            {
                var headOk = c.Expect.Head == msg.Head;
                var argOk = c.Expect.Arg == msg.Arg;
                var kindOk = c.Expect.Kind == msg.KindWire;
                if (!headOk || !argOk || !kindOk)
                {
                    failures.Add(
                        $"{c.Name}: expect kind={c.Expect.Kind} head={Show(c.Expect.Head)} arg={Show(c.Expect.Arg)}, " +
                        $"got kind={msg.KindWire} head={Show(msg.Head)} arg={Show(msg.Arg)}");
                }
            }
            else
            {
                var want = c.Expect.Reason ?? "<none>";
                if (c.Expect.Kind != "invalid" || want != MenuCommandPipeCodec.ToWire(reason))
                {
                    failures.Add(
                        $"{c.Name}: expect kind={c.Expect.Kind} reason={want}, got Err({MenuCommandPipeCodec.ToWire(reason)})");
                }
            }
        }

        Assert.True(failures.Count == 0,
            $"共享向量未通过（{failures.Count}/{doc.Cases.Count} 条）：\n" + string.Join("\n", failures));
    }

    [Fact(DisplayName = "编码器产出的行能被本侧解析器还原（round-trip）")]
    public void EncoderRoundTripsThroughParser()
    {
        foreach (var (line, wantKind, wantHead, wantArg) in new[]
                 {
                     (MenuCommandPipeCodec.EncodeLegacy("convert", @"C:\a|b.pdf"), MenuCommandPipeCodec.MessageKind.Legacy, "convert", @"C:\a|b.pdf"),
                     (MenuCommandPipeCodec.EncodeLegacy("open-settings", string.Empty), MenuCommandPipeCodec.MessageKind.Legacy, "open-settings", string.Empty),
                     (MenuCommandPipeCodec.EncodeControl("status", string.Empty), MenuCommandPipeCodec.MessageKind.Control, "status", string.Empty),
                     (MenuCommandPipeCodec.EncodeControl("start", "desktop"), MenuCommandPipeCodec.MessageKind.Control, "start", "desktop"),
                     (MenuCommandPipeCodec.EncodeControl("set", "k a|b"), MenuCommandPipeCodec.MessageKind.Control, "set", "k a|b"),
                     (MenuCommandPipeCodec.EncodeControl("get", "文档\\键"), MenuCommandPipeCodec.MessageKind.Control, "get", "文档\\键"),
                 })
        {
            Assert.True(MenuCommandPipeCodec.TryParse(Encoding.UTF8.GetBytes(line), out var msg, out var reason),
                $"编码结果无法解析：{line} ({MenuCommandPipeCodec.ToWire(reason)})");
            Assert.Equal(wantKind, msg.Kind);
            Assert.Equal(wantHead, msg.Head);
            Assert.Equal(wantArg, msg.Arg);
        }
    }

    [Fact(DisplayName = "控制响应 JSON 可解码；坏 JSON 返回 false 而不抛异常")]
    public void ResponseDecodingIsTotal()
    {
        Assert.True(MenuCommandPipeCodec.TryDecodeResponse(
            """{"ok":true,"verb":"status","data":{"desired":"running","restarts":2}}""",
            out var ok, out var f1), f1);
        Assert.True(ok.Ok);
        Assert.Equal("status", ok.Verb);
        Assert.Equal("running", ok.Data.GetProperty("desired").GetString());
        Assert.Equal(2, ok.Data.GetProperty("restarts").GetInt32());

        Assert.True(MenuCommandPipeCodec.TryDecodeResponse(
            """{"ok":false,"verb":"start","error":"unknown-component","message":"no such component"}""",
            out var err, out var f2), f2);
        Assert.False(err.Ok);
        Assert.Equal("unknown-component", err.Error);
        Assert.Equal("no such component", err.Message);

        // 对端崩溃时会写回空串或半条 JSON —— 客户端必须优雅降级
        foreach (var bad in new[] { string.Empty, "   ", "{", "[]", """{"nope":1}""" })
        {
            Assert.False(MenuCommandPipeCodec.TryDecodeResponse(bad, out _, out var failure));
            Assert.False(string.IsNullOrWhiteSpace(failure), $"应给出失败原因：{bad}");
        }
    }

    [Fact(DisplayName = "响应中的换行必须被转义（否则按行分帧会读到半条消息）")]
    public void ResponseNewlinesAreEscaped()
    {
        // 原生语义：响应由 Rust 侧编码；此处验证 C# 解码端对转义语义的期待一致
        var json = """{"ok":false,"verb":"x","error":"internal","message":"line1\nline2"}""";
        Assert.True(MenuCommandPipeCodec.TryDecodeResponse(json, out var r, out _));
        Assert.Equal("line1\nline2", r.Message);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static string Show(string? s) => s is null ? "<null>" : $"\"{s}\"";

    private static byte[] CaseBytes(VectorCase c, int max)
    {
        if (c.Input is not null)
        {
            return Encoding.UTF8.GetBytes(c.Input);
        }

        return c.Generate switch
        {
            "oversize" => Enumerable.Repeat((byte)'a', max + 1).ToArray(),
            "invalid-utf8" => new byte[] { 0xFF, 0xFE, (byte)'A' },
            _ => throw new InvalidOperationException($"case '{c.Name}' has unsupported generate: {c.Generate}"),
        };
    }

    private static VectorsDoc LoadVectors()
    {
        var json = File.ReadAllText(VectorsPath);
        return JsonSerializer.Deserialize<VectorsDoc>(json, JsonOptions)
               ?? throw new InvalidOperationException($"共享向量反序列化失败：{VectorsPath}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("找不到仓库根（AGENTS.md）");
    }

    // ───────────────────────────── DTO ─────────────────────────────

    private sealed class VectorsDoc
    {
        public int MaxMessageBytes { get; set; }

        public List<VectorCase> Cases { get; set; } = new();
    }

    private sealed class VectorCase
    {
        public string Name { get; set; } = string.Empty;

        public string? Input { get; set; }

        public string? Generate { get; set; }

        [JsonPropertyName("expect")]
        public Expect Expect { get; set; } = new();
    }

    private sealed class Expect
    {
        public string Kind { get; set; } = string.Empty;

        public string? Head { get; set; }

        public string? Arg { get; set; }

        public string? Reason { get; set; }
    }
}
