// BetterDesktop.Host — 文件日志 sink（M10 单一管道的宿主端实现）
// 写入 %LocalAppData%\BetterDesktop\logs\host-yyyyMMdd.log，按日期滚动。
// 异步非阻塞：调用方仅入队，后台 Task 批量 flush；线程安全（多插件并发写日志）。
// 限定 catch(IOException)：磁盘满/文件锁定等 IO 故障降级静默，其他异常不吞。

using System.Collections.Concurrent;
using System.IO;
using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Host;

/// <summary>
/// 文件日志 sink：实现 <see cref="Action{LogLevel, string}"/> 签名，供 CordisContext logSink 注入。
/// 后台线程批量 flush，调用方不阻塞；按日期滚动文件；目录不存在自动创建。
/// </summary>
public sealed class FileLogSink : IDisposable
{
    private readonly string _directory;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flushTask;
    private readonly object _writeGate = new();
    private StreamWriter? _writer;
    private string _currentDate = "";
    private int _disposed;

    /// <summary>构造：创建日志目录，启动后台 flush Task。</summary>
    public FileLogSink()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BetterDesktop", "logs");
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (IOException)
        {
            // 目录创建失败（权限/磁盘满）：后续写入也会失败，降级为静默丢弃。
        }
        _flushTask = Task.Run(FlushLoop);
    }

    /// <summary>日志入队（非阻塞）。供 CordisContext logSink: logSink.Invoke 调用。</summary>
    public void Invoke(LogLevel level, string message)
    {
        if (_disposed != 0) return;
        _queue.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}");
    }

    /// <summary>后台 flush 循环：批量出队写入，空队列时 50ms 轮询。</summary>
    private async Task FlushLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (_queue.IsEmpty)
                {
                    await Task.Delay(50, _cts.Token).ConfigureAwait(false);
                    continue;
                }

                EnsureWriter();
                while (_queue.TryDequeue(out var line))
                {
                    _writer?.WriteLine(line);
                }
                _writer?.Flush();
            }
            catch (OperationCanceledException)
            {
                // 正常取消，退出循环
            }
            catch (IOException)
            {
                // 磁盘满/文件锁定/目录消失：跳过本批，下轮重试（日志可丢失但不阻断主流程）
            }
            // 非 IOException 不吞：让 Task 故障并由 Dispose 时的 Wait 观察到（M10 禁止裸 catch）
        }
    }

    /// <summary>确保 StreamWriter 指向当前日期文件；日期变化时滚动。</summary>
    private void EnsureWriter()
    {
        var date = DateTime.Now.ToString("yyyyMMdd");
        if (_writer is not null && _currentDate == date) return;
        lock (_writeGate)
        {
            if (_writer is not null && _currentDate == date) return;
            _writer?.Dispose();
            var path = Path.Combine(_directory, $"host-{date}.log");
            _writer = new StreamWriter(path, append: true) { AutoFlush = false };
            _currentDate = date;
        }
    }

    /// <summary>停止后台 flush，排空队列剩余日志，释放文件句柄。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        try
        {
            _flushTask.Wait(3000);
        }
        catch (AggregateException)
        {
            // 后台 Task 故障（非 IOException）：已观察到，不阻断 Dispose
        }
        // 排空剩余队列（进程退出前最后一次 flush）
        try
        {
            EnsureWriter();
            while (_queue.TryDequeue(out var line))
            {
                _writer?.WriteLine(line);
            }
            _writer?.Flush();
        }
        catch (IOException)
        {
            // 退出期 IO 失败：静默降级
        }
        lock (_writeGate)
        {
            _writer?.Dispose();
            _writer = null;
        }
        _cts.Dispose();
    }
}
