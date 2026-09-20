using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterDesktop.Shell.Clipboard.Ipc;

/// <summary>
/// 传输抽象（内存 fake 供单测；NamedPipe 真机）。
/// 帧语义：一次 WriteFrame = 一条完整消息；ReadFrame 取出缓存中的一条完整行（无完整行返回 null）。
/// </summary>
public interface IClipboardTransport : IDisposable
{
    bool IsConnected { get; }
    void Connect();
    /// <summary>写一条消息帧（消息模式管道，一次写入 = 一帧）。</summary>
    void WriteFrame(string json);
    /// <summary>非阻塞：是否已有一条完整消息可读（供单线程轮询；断开时返回 false，由 ReadFrame=null 判定）。</summary>
    bool IsDataAvailable();
    /// <summary>取出一条完整消息帧；缓存中无完整行时返回 null（对端断开亦为 null）。</summary>
    string? ReadFrame();
    void Disconnect();
}

/// <summary>
/// NamedPipe 实现：**字节模式写 + 自有行缓冲读**。
///
/// 【为什么不用 StreamReader（2026-09-12 真机血案）】
/// 早期实现用 `StreamReader.ReadLine()` 读帧，同时用 `PeekNamedPipe` 判断"管道里有没有数据"。
/// 两者会**打架**：StreamReader 内部有 8KB 预读缓冲，一次 `ReadLine` 会把管道里**所有**可用字节
/// 连同后续响应一起读进自己的缓冲，管道随即变空 → `IsDataAvailable()` 误判"无数据" →
/// 后续响应**永远闷在客户端缓冲里取不出来** → 该请求 5s 超时。
/// 症状极具迷惑性：心跳 ping 超时 → 连接被反复掐断重连（宿主/面板"时好时坏"、
/// 引擎侧记 "WriteFile failed: 管道正在被关闭"），而单独用外部脚本探测管道却完全正常。
/// 故改为**自有行缓冲**：数据只有在被显式搬进 <see cref="_inbox"/> 后才算"已读"，
/// 判断与读取共用同一事实来源，再无预读打架。
///
/// 写端**一次性写入完整字节**（见 WriteFrame 注释：StreamWriter 会把 &gt;8KB 的帧拆散）；消息模式管道下一次写入 = 一帧，引擎按消息读。
/// </summary>
public sealed class NamedPipeTransport : IClipboardTransport
{
    private const byte LineFeed = (byte)'\n';

    /// <summary>单帧写的等待上限（毫秒）。超时 = 对端不读（引擎半死），按连接已死处理。</summary>
    private const int WriteTimeoutMs = 3000;

    private readonly string _pipeName;
    private readonly List<byte> _inbox = new();
    private NamedPipeClientStream? _stream;

    public NamedPipeTransport(string pipeName = IpcProtocol.PipeName)
    {
        _pipeName = pipeName;
    }

    public bool IsConnected => _stream is { IsConnected: true };

    public void Connect()
    {
        // 【生死线 · 2026-09-12 真机实测】重复 Connect 必须先释放旧流：
        // 重试路径（Connect 成功但后续写订阅帧失败）不会走 Disconnect，旧管道实例在客户端侧
        // 被直接覆盖丢引用、在**引擎侧永不释放** → 每次重试泄漏一个连接槽，
        // 4 槽耗尽后所有客户端（面板/宿主）都连不上（现象：engine not connected / 请求超时）。
        Disconnect();

        // 【2026-09-18】**必须带 PipeOptions.Asynchronous**：写端要有超时（见 WriteFrame 注释），
        // 而同步管道句柄不支持真正的异步写（WriteAsync 会抛 NotSupportedException）。
        // 读端仍用同步 Read，由 PeekNamedPipe 保证"有数据才读"，行为不变。
        var stream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        stream.Connect(TimeSpan.FromSeconds(3));
        // 字节模式（默认）+ 行协议；UTF-8 无 BOM
        _stream = stream;
    }

    /// <summary>
    /// 写一帧（行协议：JSON + '\n'）。
    /// <para>
    /// 【生死线 · 2026-09-12 真机复现】**必须一次性 `Write` 完整字节，绝不能用 `StreamWriter`**：
    /// 消息模式管道「一次写入 = 一帧」，而 `StreamWriter` 自带 8192 缓冲，会把 &gt;8KB 的帧
    /// 拆成多次 `WriteFile` → 引擎只读到半截 JSON → `-32700 Parse error`，后续碎片甚至被当成新请求。
    /// 引擎日志实证：批量 `import` 的帧在 **column 1034（≈8192 字节边界）** 被切断，
    /// 紧接着的残片被报 `expected value at line 1 column 1`。
    /// </para>
    /// </summary>
    public void WriteFrame(string json)
    {
        var stream = _stream ?? throw new InvalidOperationException("pipe not connected");
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        try
        {
            // 【生死线 · 2026-09-18 审计】**写必须有上限**。
            // 消息模式管道的 WriteFile 在**对端不读**（引擎卡死/半死/被挂起）时会一直阻塞，
            // 直到管道缓冲写满 —— 于是：
            //   ① 客户端唯一的工作线程被卡在写里 → 心跳停发、所有在途请求超时；
            //   ② 连接状态仍显示"已连接"，**永远不会触发重连** → 用户只能重启面板/宿主
            //      （症状："引擎明明在跑，剪贴板却一直转圈/超时"）。
            // 改为异步写 + 有界等待：超时即视为连接已死，主动拆掉句柄（解除底层挂起）并抛出，
            // 由客户端的工作线程走既有的 OnDisconnected → 重连路径。
            // 注意：WriteAsync(Memory) 返回 ValueTask，没有带超时的 Wait —— 先 AsTask 才能有界等待。
            var write = stream.WriteAsync(bytes.AsMemory(0, bytes.Length)).AsTask();
            if (!write.Wait(WriteTimeoutMs))
            {
                Disconnect();
                throw new IOException($"pipe write timeout ({WriteTimeoutMs}ms)");
            }

            stream.Flush();
        }
        catch (AggregateException ae) when (ae.InnerException is not null)
        {
            // 写失败（管道关闭/超时）：立刻断开，避免"假连接"继续承接请求。
            Disconnect();
            throw ae.InnerException;
        }
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
