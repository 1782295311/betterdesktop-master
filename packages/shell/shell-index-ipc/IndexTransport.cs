using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterDesktop.Shell.IndexIpc;

/// <summary>
/// 传输抽象（内存 fake 供单测；NamedPipe 真机）。
/// 帧语义：一次 WriteFrame = 一条完整消息；ReadFrame 取出缓存中的一条完整行（无完整行返回 null）。
/// </summary>
public interface IIndexTransport : IDisposable
{
    bool IsConnected { get; }

    void Connect();

    /// <summary>写一条消息帧（消息模式管道，一次写入 = 一帧）。</summary>
    void WriteFrame(string json);

    /// <summary>非阻塞：是否已有一条完整消息可读（断开时返回 false，由 ReadFrame=null 判定）。</summary>
    bool IsDataAvailable();

    /// <summary>取出一条完整消息帧；缓存中无完整行时返回 null（对端断开亦为 null）。</summary>
    string? ReadFrame();

    void Disconnect();
}

/// <summary>
/// NamedPipe 实现：**字节模式写 + 自有行缓冲读**。
///
/// 【为什么不用 StreamReader（2026-09-12 剪贴板引擎真机血案，本包照抄结论）】
/// 早期实现用 <c>StreamReader.ReadLine()</c> 读帧、同时用 <c>PeekNamedPipe</c> 判「管道有没有数据」。
/// 两者会**打架**：StreamReader 内部有 8KB 预读缓冲，一次 ReadLine 会把管道里所有可用字节
/// 连同后续响应一起读进自己的缓冲 → 管道随即变空 → <see cref="IsDataAvailable"/> 误判「无数据」→
/// 后续响应永远闷在客户端缓冲里取不出来 → 该请求超时。症状极具迷惑性：
/// 心跳 ping 超时 → 连接反复掐断重连，而外部脚本单独探测管道却完全正常。
/// 故改为**自有行缓冲**：数据只有在被显式搬进 <see cref="_inbox"/> 后才算「已读」。
///
/// 写端**一次性写入完整字节**（见 WriteFrame 注释：StreamWriter 会把 &gt;8KB 的帧拆散）。
/// </summary>
public sealed class NamedPipeTransport : IIndexTransport
{
    private const byte LineFeed = (byte)'\n';

    private readonly string _pipeName;
    private readonly List<byte> _inbox = new();
    private NamedPipeClientStream? _stream;

    public NamedPipeTransport(string pipeName = IndexIpcProtocol.PipeName)
    {
        _pipeName = pipeName;
    }

    public bool IsConnected => _stream is { IsConnected: true };

    public void Connect()
    {
        // 【生死线】重复 Connect 必须先释放旧流：重试路径（Connect 成功但后续写首帧失败）不会走
        // Disconnect，旧管道实例在客户端被覆盖丢引用、在**引擎侧永不释放** → 每次重试泄漏一个连接槽，
        // 4 槽耗尽后所有客户端都连不上（现象：请求超时 / engine not connected）。
        Disconnect();

        var stream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.None);
        stream.Connect(TimeSpan.FromSeconds(3));
        _stream = stream;
    }

    /// <summary>
    /// 写一帧（JSON + '\n'）。
    /// 【生死线】必须一次性 <c>Write</c> 完整字节，**绝不能用 StreamWriter**：消息模式管道
    /// 「一次写入 = 一帧」，而 StreamWriter 自带 8192 缓冲，会把 &gt;8KB 的帧拆成多次 WriteFile →
    /// 引擎只读到半截 JSON → <c>-32700 Parse error</c>，残片甚至被当成新请求。
    /// </summary>
    public void WriteFrame(string json)
    {
        var stream = _stream ?? throw new InvalidOperationException("pipe not connected");
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    /// <summary>非阻塞探测：自有缓冲已有完整行 → true；否则看管道是否有字节可搬。</summary>
    public bool IsDataAvailable()
    {
        var stream = _stream;
        if (stream is not { IsConnected: true })
        {
            return false;
        }

        if (HasCompleteLine())
        {
            return true;
        }

        // PeekNamedPipe 非阻塞探测；有字节则一次性搬进自有缓冲（消息模式下一次读即一条消息，不会阻塞）
        if (!PeekNamedPipe(stream.SafePipeHandle, null, 0, out _, out uint total, out _) || total == 0)
        {
            return false;
        }

        var buffer = new byte[total];
        int read;
        try
        {
            read = stream.Read(buffer, 0, buffer.Length);
        }
        catch (IOException)
        {
            return false;
        }
        for (int i = 0; i < read; i++)
        {
            _inbox.Add(buffer[i]);
        }
        return HasCompleteLine();
    }

    /// <summary>取出缓存中的一条完整行（引擎响应以 \n 结尾）；无完整行返回 null。</summary>
    public string? ReadFrame()
    {
        int index = _inbox.IndexOf(LineFeed);
        if (index < 0)
        {
            return null;
        }

        string line = Encoding.UTF8.GetString(_inbox.ToArray(), 0, index);
        _inbox.RemoveRange(0, index + 1);
        return line.TrimEnd('\r');
    }

    private bool HasCompleteLine()
    {
        for (int i = 0; i < _inbox.Count; i++)
        {
            if (_inbox[i] == LineFeed)
            {
                return true;
            }
        }
        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PeekNamedPipe(
        Microsoft.Win32.SafeHandles.SafePipeHandle hNamedPipe,
        byte[]? lpBuffer, uint nBufferSize,
        out uint lpBytesRead, out uint lpTotalBytesAvail, out uint lpBytesLeftThisMessage);

    public void Disconnect()
    {
        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // 忽略关闭期异常
        }
        _stream = null;
        _inbox.Clear();
    }

    public void Dispose() => Disconnect();
}
