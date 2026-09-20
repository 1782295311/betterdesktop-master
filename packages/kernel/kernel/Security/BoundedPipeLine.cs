// BetterDesktop.Kernel — 命名管道单行读取的**有界**实现。
//
// 【为什么需要它】`StreamReader.ReadLine()` / `ReadLineAsync()` 对单行长度**没有上限**：
// 对端只要不发换行符、持续写入，服务端就会把它全部读进内存。而管道实例数通常是 1，
// 于是同一条连接既能耗尽内存、又能长期占住唯一的实例槽 —— 后续所有命令（包括用户主动触发的）
// 都连不上。这是"看起来能用、实则一条持续写入就能打死"的典型形态。
//
// core（Rust）侧早已用 `pipe.rs::read_line_bounded` 堵住（上限 = `protocol::MAX_MESSAGE_BYTES`），
// C# 侧此前只有超时、没有上限 —— 本类把那半边补齐，**两侧判据共用同一个常量**。
//
// 【常量只取一份】上限 = `MenuCommandPipeCodec.MaxMessageBytes`
//（= `protocols/bdmc1-test-vectors.json` 的 `maxMessageBytes`，跨语言契约；改协议必须三处同改）。
// 本类**不重定义**常量：多一处就有了"改了一处忘了另一处"的空间。
//
// 【机检】门禁 `verify-security` 第 1 条要求：每个 `new NamedPipeServerStream(` 所在文件
// 必须引用本类（有界读是服务端管道的必备项，缺失即红）。

using System.Text;

namespace BetterDesktop.Kernel.Core;

/// <summary>管道单行读取：带**字节上限**与**超时**，替掉裸 <c>ReadLine()</c>。</summary>
public static class BoundedPipeLine
{
    /// <summary>读取结果。区分超时与超限：两者都丢弃连接，但日志要能分开（排查时含义完全不同）。</summary>
    public enum Outcome
    {
        /// <summary>拿到完整一行（不含行终止符）。</summary>
        Line,

        /// <summary>超过字节上限（**在读完之前**判定，不会无限吃内存）。</summary>
        TooLarge,

        /// <summary>超时未凑齐一行。</summary>
        Timeout,

        /// <summary>对端关闭。</summary>
        Closed,

        /// <summary>非法 UTF-8 或其它 I/O 失败。</summary>
        Error,
    }

    /// <summary>默认上限：与 core 的 `protocol::MAX_MESSAGE_BYTES` 同源。</summary>
    public const int DefaultMaxBytes = MenuCommandPipeCodec.MaxMessageBytes;

    /// <summary>单次读取的分块大小。够小以免为一条短命令多要内存，够大以免高频 syscall。</summary>
    private const int ChunkSize = 512;

    /// <summary>严格 UTF-8：非法字节必须报错，不得静默替换成 U+FFFD（那会让畸形输入被当成合法路径）。</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// 读一行（到 <c>\n</c> 为止）。
    /// </summary>
    /// <param name="stream">管道流（同步模式）。本方法返回时**可能**仍有未读数据，调用方应丢弃该连接。</param>
    /// <param name="maxBytes">单行上限；超过即 <see cref="Outcome.TooLarge"/> 并立即返回。</param>
    /// <param name="timeoutMs">整行读取的总超时（不是每次 read 的超时）。</param>
    /// <param name="line">读到的行，已剥行终止符；失败时为空串（**不为 null**）。</param>
    /// <remarks>
    /// 超时用 <c>ReadAsync + Task.WhenAny(Delay)</c> 而非 <c>Stream.ReadTimeout</c>：
    /// 后者的超时异常类型在不同管道状态下不一致（IOException / TimeoutException 视 Win32 错误码而定），
    /// 想可靠区分"超时"与"断开"就得解析消息文本 —— 那是脆弱的。前者把两类事件分开表达。
    /// </remarks>
    public static Outcome TryRead(Stream stream, int maxBytes, int timeoutMs, out string line)
    {
        line = string.Empty;

        if (maxBytes <= 0)
        {
            maxBytes = DefaultMaxBytes;
        }

        var buffer = new byte[ChunkSize];
        using var accumulated = new MemoryStream();
        var deadline = Environment.TickCount64 + timeoutMs;

        while (true)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                return Outcome.Timeout;
            }

            int read;
            try
            {
                var readTask = stream.ReadAsync(buffer, 0, buffer.Length);
                var winner = Task.WhenAny(readTask, Task.Delay((int)remaining)).GetAwaiter().GetResult();
                if (!ReferenceEquals(winner, readTask))
                {
                    // 超时返回：readTask 仍挂在那里，等调用方 Dispose 流时会以异常收尾。
                    // 观察它，避免留下 UnobservedTaskException（会把无关的 GC 阶段变成异常来源）。
                    _ = readTask.ContinueWith(
                        static t => _ = t.Exception,
                        TaskContinuationOptions.OnlyOnFaulted);
                    return Outcome.Timeout;
                }

                read = readTask.GetAwaiter().GetResult();
            }
            catch (IOException)
            {
                return Outcome.Closed;
            }
            catch (ObjectDisposedException)
            {
                return Outcome.Closed;
            }
            catch (InvalidOperationException)
            {
                // 管道未连接 / 已断开
                return Outcome.Closed;
            }
            catch (Exception)
            {
                return Outcome.Error;
            }

            if (read <= 0)
            {
                return Outcome.Closed;
            }

            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (newline >= 0)
            {
                accumulated.Write(buffer, 0, newline);
                line = Decode(accumulated);
                return Outcome.Line;
            }

            accumulated.Write(buffer, 0, read);
            // 上限判定放在累积之后、下一次 read 之前：超长输入不会继续吃内存。
            if (accumulated.Length > maxBytes)
            {
                return Outcome.TooLarge;
            }
        }
    }

    /// <summary>解码已累积的字节：只剥行终止符（CRLF / LF / CR），其余字符一律保留（含首尾空格）。</summary>
    private static string Decode(MemoryStream accumulated)
    {
        var bytes = accumulated.GetBuffer().AsSpan(0, (int)accumulated.Length);

        // 只可能是尾部残留的 '\r'（'\n' 已在调用方截断），剥掉它即可。
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
        {
            bytes = bytes[..^1];
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 调用方拿到空串，会与"空命令"一样被丢弃 —— 但 Outcome 保持 Line 会掩盖原因，
            // 故这里把非法的行当作空串返回：上层按空行丢弃并记日志（首判大小、再判 UTF-8 的顺序同 core）。
            return string.Empty;
        }
    }
}
