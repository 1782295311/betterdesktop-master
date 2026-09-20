using System.Text;
using System.Text.Json;

namespace BetterDesktop.Kernel.Core;

/// <summary>
/// BDMC1 协议编解码（控制管道的第二实现，第一实现在 Rust 侧 <c>core/src/protocol.rs</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>契约的唯一权威是仓库根的 <c>protocols/bdmc1-test-vectors.json</c></b>，不是本文件。
/// 两侧各自实现解析器，但跑<b>同一批共享向量</b>（见 <c>kernel-tests/Bdmc1ProtocolContractTests.cs</c>
/// 与 Rust 的 <c>protocol::tests::matches_shared_vectors</c>）。目的：消灭"两侧各自猜边界、
/// 各自单测都绿、真机对不上"这一失败模式。**改协议必须先改共享向量**，两侧实现随后跟上。
/// </para>
/// <para>
/// 两种形态：<c>legacy</c>（<c>BDMC1|&lt;action&gt;|&lt;arg&gt;</c>，写后即忘）与
/// <c>control</c>（<c>BDMC1|@ctl|&lt;verb&gt;|&lt;arg&gt;</c>，请求/应答）。
/// </para>
/// <para>
/// 切分规则：首个 <c>|</c> 之后按 <c>|</c> 切；head 取第 0 段，<b>arg = 其余段用 <c>|</c> 重新拼接</b>，
/// 因此 arg 内可安全包含 <c>|</c>（Windows 文件名与设置值里都可能出现）。
/// </para>
/// <para>
/// 保真约定：只剥行终止符（CRLF / LF / CR），<b>其余字符一律保留</b>（含首尾空格）——
/// "顺手 Trim 一下"会让含尾空格的路径静默变短。
/// </para>
/// </remarks>
public static class MenuCommandPipeCodec
{
    /// <summary>协议魔数前缀（大小写敏感）。</summary>
    public const string MagicPrefix = "BDMC1|";

    /// <summary>控制形态的哨兵头。</summary>
    public const string ControlSentinel = "@ctl";

    /// <summary>单条消息字节上限（与共享向量的 <c>maxMessageBytes</c> 一致）。</summary>
    public const int MaxMessageBytes = 1_048_576;

    /// <summary>严格 UTF-8 解码器：非法字节必须报错，不得静默替换成 U+FFFD。</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>解析失败原因。取值与共享向量的 <c>invalidReasons</c> 逐字对应（跨语言按字符串比对）。</summary>
    public enum InvalidReason
    {
        /// <summary>去掉行终止符后为空。</summary>
        Empty,

        /// <summary>字节数超过 <see cref="MaxMessageBytes"/>。</summary>
        TooLarge,

        /// <summary>不是合法 UTF-8。</summary>
        InvalidUtf8,

        /// <summary>不以 <c>BDMC1|</c> 开头。</summary>
        BadMagic,

        /// <summary>action 或 verb 为空。</summary>
        EmptyHead,
    }

    /// <summary>消息形态。</summary>
    public enum MessageKind
    {
        /// <summary><c>BDMC1|&lt;action&gt;|&lt;arg&gt;</c>。</summary>
        Legacy,

        /// <summary><c>BDMC1|@ctl|&lt;verb&gt;|&lt;arg&gt;</c>。</summary>
        Control,
    }

    /// <summary>解析结果：<paramref name="Head"/> = action（legacy）或 verb（control）。</summary>
    public readonly record struct Message(MessageKind Kind, string Head, string Arg)
    {
        /// <summary>共享向量里的 <c>kind</c> 字面量。</summary>
        public string KindWire => Kind == MessageKind.Control ? "control" : "legacy";

        /// <summary>回编码为规范行（总以 <c>LF</c> 结尾、总带 arg 段）。</summary>
        public string ToLine() => Kind == MessageKind.Control
            ? EncodeControl(Head, Arg)
            : EncodeLegacy(Head, Arg);
    }

    /// <summary>失败原因 → 共享向量字面量（改名即破约）。</summary>
    public static string ToWire(InvalidReason reason) => reason switch
    {
        InvalidReason.Empty => "empty",
        InvalidReason.TooLarge => "too-large",
        InvalidReason.InvalidUtf8 => "invalid-utf8",
        InvalidReason.BadMagic => "bad-magic",
        InvalidReason.EmptyHead => "empty-head",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    /// <summary>
    /// 解析一行消息（<paramref name="raw"/> <b>含</b>行终止符，本方法负责剥离）。
    /// </summary>
    /// <remarks>
    /// 顺序有意如此：<b>先判大小、再判 UTF-8、最后判魔数</b> —— 大小判定必须在解码前，
    /// 否则超长输入会在解码阶段先吃内存。
    /// </remarks>
    public static bool TryParse(ReadOnlySpan<byte> raw, out Message message, out InvalidReason reason)
    {
        message = default;
        reason = default;

        if (raw.Length > MaxMessageBytes)
        {
            reason = InvalidReason.TooLarge;
            return false;
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            reason = InvalidReason.InvalidUtf8;
            return false;
        }

        return TryParseDecoded(text, out message, out reason);
    }

    /// <summary>解析一行文本（已解码）。供客户端与测试直接调用。</summary>
    public static bool TryParseDecoded(string text, out Message message, out InvalidReason reason)
    {
        message = default;
        reason = default;

        var line = StripTerminator(text);
        if (line.Length == 0)
        {
            reason = InvalidReason.Empty;
            return false;
        }

        if (!line.StartsWith(MagicPrefix, StringComparison.Ordinal))
        {
            reason = InvalidReason.BadMagic;
            return false;
        }

        var rest = line[MagicPrefix.Length..];
        var parts = rest.Split('|');
        var head = parts[0];
        var tail = parts.Length > 1 ? parts[1..] : Array.Empty<string>();

        if (head == ControlSentinel)
        {
            var verb = tail.Length > 0 ? tail[0] : string.Empty;
            if (verb.Length == 0)
            {
                reason = InvalidReason.EmptyHead;
                return false;
            }

            var arg = tail.Length > 1 ? string.Join('|', tail[1..]) : string.Empty;
            message = new Message(MessageKind.Control, verb, arg);
            return true;
        }

        if (head.Length == 0)
        {
            reason = InvalidReason.EmptyHead;
            return false;
        }

        message = new Message(MessageKind.Legacy, head, string.Join('|', tail));
        return true;
    }

    /// <summary>只剥行终止符（CRLF / LF / CR）；不 Trim 其它字符。</summary>
    private static string StripTerminator(string s)
    {
        if (s.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return s[..^2];
        }

        if (s.Length > 0 && (s[^1] == '\n' || s[^1] == '\r'))
        {
            return s[..^1];
        }

        return s;
    }

    /// <summary>编码 legacy 行（含结尾 LF）。<b>参数直接拼接但保证不产生歧义</b>：arg 内的 <c>|</c> 由解析侧还原。</summary>
    public static string EncodeLegacy(string action, string arg) => $"{MagicPrefix}{action}|{arg}\n";

    /// <summary>编码 control 行（含结尾 LF）。</summary>
    public static string EncodeControl(string verb, string arg) => $"{MagicPrefix}{ControlSentinel}|{verb}|{arg}\n";

    /// <summary>控制响应解码结果。</summary>
    public readonly record struct Response(bool Ok, string Verb, string? Error, string? Message, JsonElement Data);

    /// <summary>
    /// 解码控制响应（单行紧凑 JSON）。
    /// </summary>
    /// <remarks>
    /// 失败（JSON 不合法/形状不符）返回 <c>false</c> 并给出 <paramref name="failure"/> 供日志。
    /// <b>不得抛异常</b>：对端进程崩溃时会写回半条或空数据，客户端必须能优雅降级而不是崩。
    /// </remarks>
    public static bool TryDecodeResponse(string json, out Response response, out string? failure)
    {
        response = default;
        failure = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            failure = "empty response";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out var okProp))
            {
                failure = "response is not an object with 'ok'";
                return false;
            }

            var ok = okProp.ValueKind == JsonValueKind.True;
            var verb = root.TryGetProperty("verb", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? string.Empty
                : string.Empty;
            var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString()
                : null;
            var msg = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;
            // JsonElement 的生命周期绑定在 doc 上 → 深拷贝一份交给调用方
            var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;

            response = new Response(ok, verb, error, msg, data);
            return true;
        }
        catch (JsonException ex)
        {
            failure = $"invalid json: {ex.Message}";
            return false;
        }
    }
}
