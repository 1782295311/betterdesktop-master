// BetterDesktop — 跨进程统一诊断日志（共享源文件，由各进程以 <Compile Include Link> 引入）。
//
// 为什么是共享源而不是 NuGet/项目引用：tray/updater/watchdog/recovery 有"零包引用"硬约束
// （主程序损坏时仍要能工作），而各进程又必须写同一套格式、同一套轮转策略的日志，
// 所以实现只写一份、编译进各自程序集。
//
// 设计红线（分发给他人实地测试后，日志是唯一的事后取证手段）：
//   1) 写日志绝不阻塞调用线程：入队即返回；有界队列满则丢弃并计数（丢弃量会写进日志），
//      绝不因为"写日志"卡住鼠标钩子 / UI 线程 / 剪贴板事件。
//   2) 绝不把异常抛给调用方：所有 IO 故障降级为静默计数。
//   3) 崩溃路径可同步刷盘（Flush），保证最后几秒的日志落盘。
//   4) 自治理：按天 + 单文件上限滚动，保留天数 + 目录总量上限，旧文件自动清理，
//      避免"日志把测试同学的磁盘写满"这种分发事故。
//   5) 启动横幅：版本 / OS / 进程 / 命令行 / 日志目录一次落盘，出问题先看环境。

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace BetterDesktop.Diagnostics;

/// <summary>日志级别（数值越大越严重；<see cref="Off"/> 表示不输出）。</summary>
public enum DiagnosticLevel
{
    /// <summary>最详细，仅排障时开启。</summary>
    Trace = 0,

    /// <summary>调试细节。</summary>
    Debug = 1,

    /// <summary>正常关键事件（默认级别）。</summary>
    Info = 2,

    /// <summary>可恢复的异常情况。</summary>
    Warn = 3,

    /// <summary>功能失败 / 异常。</summary>
    Error = 4,

    /// <summary>关闭日志。</summary>
    Off = 5,
}

/// <summary>
/// 异步、有界、自治理的文件日志器（每个进程一个实例，文件名 <c>&lt;组件&gt;-yyyyMMdd.log</c>）。
/// </summary>
public sealed class DiagnosticLogger : IDisposable
{
    /// <summary>单个日志文件上限，超过则滚动（默认 8MB）。</summary>
    public const int MaxFileBytes = 8 * 1024 * 1024;

    /// <summary>内存队列容量，满则丢弃（绝不阻塞调用方）。</summary>
    public const int QueueCapacity = 8192;

    private const int MaxBatchPerDrain = 4000;
    private const long DirectoryQuotaBytes = 256L * 1024 * 1024;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly string _component;
    private readonly string _directory;
    private readonly DiagnosticLevel _minLevel;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly object _writeGate = new();

    private StreamWriter? _writer;
    private string _currentPath = string.Empty;
    private long _currentBytes;
    private int _queued;
    private long _dropped;
    private long _written;
    private long _ioFailures;
    private int _disposed;

    private DiagnosticLogger(string component, string directory, DiagnosticLevel minLevel)
    {
        _component = component;
        _directory = directory;
        _minLevel = minLevel;
        _worker = Task.Run(WorkerLoopAsync);
    }

    /// <summary>统一日志目录：<c>%LocalAppData%\BetterDesktop\logs</c>。</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BetterDesktop",
        "logs");

    /// <summary>已写入的行数（诊断用）。</summary>
    public long WrittenCount => Interlocked.Read(ref _written);

    /// <summary>因拥塞被丢弃的行数（诊断用）。</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>创建日志器：建目录、清理过期与超额日志、启动后台写线程。</summary>
    public static DiagnosticLogger Start(
        string component,
        DiagnosticLevel minLevel = DiagnosticLevel.Info,
        string? directory = null)
    {
        var logger = new DiagnosticLogger(component, directory ?? DefaultDirectory, minLevel);
        logger.Housekeeping();
        return logger;
    }

    /// <summary>按环境变量 <c>BETTERDESKTOP_LOG_LEVEL</c> 解析级别（Trace/Debug/Info/Warn/Error/Off）。</summary>
    public static DiagnosticLevel ResolveLevel(DiagnosticLevel fallback = DiagnosticLevel.Info)
    {
        var raw = Environment.GetEnvironmentVariable("BETTERDESKTOP_LOG_LEVEL");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return Enum.TryParse<DiagnosticLevel>(raw.Trim(), ignoreCase: true, out var parsed) ? parsed : fallback;
    }

    /// <summary>入队一条日志（非阻塞；队列满则丢弃并计数）。</summary>
    public void Write(DiagnosticLevel level, string message)
    {
        if (level < _minLevel || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _queued) >= QueueCapacity)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        Interlocked.Increment(ref _queued);
        _queue.Enqueue(Format(level, message));

        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // 信号量已满：后台线程下一轮会把队列抽干，忽略。
        }
    }

    public void Trace(string message) => Write(DiagnosticLevel.Trace, message);

    public void Debug(string message) => Write(DiagnosticLevel.Debug, message);

    public void Info(string message) => Write(DiagnosticLevel.Info, message);

    public void Warn(string message) => Write(DiagnosticLevel.Warn, message);

    public void Error(string message) => Write(DiagnosticLevel.Error, message);

    /// <summary>记录异常（带类型、消息与堆栈）。</summary>
    public void Error(string what, Exception ex) => Write(
        DiagnosticLevel.Error,
        $"{what}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}    {ex.StackTrace}");

    /// <summary>写一段分节的启动横幅。</summary>
    public void Banner(string title, params string[] lines)
    {
        Write(DiagnosticLevel.Info, $"------ {title} ------");
        foreach (var line in lines)
        {
            Write(DiagnosticLevel.Info, "  " + line);
        }

        Write(DiagnosticLevel.Info, "--------------------");
    }

    /// <summary>同步刷盘（崩溃路径 / 导出诊断包前必须调用）。</summary>
    public void Flush()
    {
        Drain();

        lock (_writeGate)
        {
            try
            {
                _writer?.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                _ioFailures++;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放：忽略
        }

        try
        {
            _worker.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 后台线程故障已在循环内处理；这里只保证不阻断退出
        }

        Drain();

        lock (_writeGate)
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                _ioFailures++;
            }

            _writer = null;
        }

        _cts.Dispose();
        _signal.Dispose();
    }

    /// <summary>收集标准环境信息（版本 / 进程 / OS / 目录 / 命令行），供启动横幅与诊断包使用。</summary>
    public static string[] EnvironmentInfo()
    {
        var lines = new List<string>();
        try
        {
            var asm = System.Reflection.Assembly.GetEntryAssembly();
            var informational = asm?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
            var version = informational is { Length: > 0 }
                ? ((System.Reflection.AssemblyInformationalVersionAttribute)informational[0]).InformationalVersion
                : asm?.GetName().Version?.ToString();
            lines.Add($"版本   : {version ?? "未知"}");
            lines.Add($"进程   : {Path.GetFileName(Environment.ProcessPath ?? "?")} (pid={Environment.ProcessId})");
            lines.Add($"系统   : {Environment.OSVersion.VersionString} / {(Environment.Is64BitProcess ? "x64" : "x86")} / CLR {Environment.Version}");
            lines.Add($"目录   : {AppContext.BaseDirectory}");
            lines.Add($"日志   : {DefaultDirectory}");
            using (var process = Process.GetCurrentProcess())
            {
                lines.Add($"启动于 : {process.StartTime:yyyy-MM-dd HH:mm:ss}");
            }

            lines.Add($"命令行 : {Environment.CommandLine}");
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.Security.SecurityException)
        {
            lines.Add($"环境信息收集失败: {e.Message}");
        }

        return lines.ToArray();
    }

    private static string Format(DiagnosticLevel level, string message)
        => $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Abbrev(level)}] [t{Environment.CurrentManagedThreadId:D2}] {message}";

    private static string Abbrev(DiagnosticLevel level) => level switch
    {
        DiagnosticLevel.Trace => "TRC",
        DiagnosticLevel.Debug => "DBG",
        DiagnosticLevel.Info => "INF",
        DiagnosticLevel.Warn => "WRN",
        DiagnosticLevel.Error => "ERR",
        _ => "???",
    };

    private async Task WorkerLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(250, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            Drain();
        }

        Drain();
    }

    /// <summary>把队列抽干并写盘（后台线程与崩溃/退出路径共用）。</summary>
    private void Drain()
    {
        var batch = 0;
        while (batch < MaxBatchPerDrain && _queue.TryDequeue(out var line))
        {
            Interlocked.Decrement(ref _queued);
            Append(line);
            batch++;
        }

        var dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0)
        {
            Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [WRN] 日志队列拥塞，已丢弃 {dropped} 条（可设置 BETTERDESKTOP_LOG_LEVEL=Warn 减少输出）");
        }
    }

    private void Append(string line)
    {
        lock (_writeGate)
        {
            try
            {
                if (_writer is null)
                {
                    OpenWriter();
                }

                var writer = _writer;
                if (writer is null)
                {
                    return; // 目录不可用：静默丢弃
                }

                writer.WriteLine(line);
                _currentBytes += Encoding.UTF8.GetByteCount(line) + 2;
                _written++;

                if (_currentBytes >= MaxFileBytes)
                {
                    writer.Flush();
                    writer.Dispose();
                    _writer = null;
                    RollCurrentFile();
                    OpenWriter();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                _ioFailures++;

                try
                {
                    _writer?.Dispose();
                }
                catch (Exception inner) when (inner is IOException or ObjectDisposedException)
                {
                    // 关闭也失败：放弃该文件句柄，下一轮重开
                }

                _writer = null;
            }
        }
    }

    private void OpenWriter()
    {
        var path = Path.Combine(_directory, $"{_component}-{DateTime.Now:yyyyMMdd}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        try
        {
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = false,
            };
            _currentPath = path;
            _currentBytes = stream.Length;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>把已写满的当日文件改名为带序号的历史文件，让下一次写入开新文件。</summary>
    private void RollCurrentFile()
    {
        if (string.IsNullOrEmpty(_currentPath) || !File.Exists(_currentPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(_currentPath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        var baseName = Path.GetFileNameWithoutExtension(_currentPath);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(directory, $"{baseName}-{i}.log");
            if (File.Exists(candidate))
            {
                continue;
            }

            try
            {
                File.Move(_currentPath, candidate);
            }
            catch (IOException)
            {
                // 改名失败：继续往原文件追加（只是不滚动，不影响功能）
            }

            return;
        }
    }

    /// <summary>启动时治理：建目录、删过期日志、按目录总量上限清理最旧的日志。</summary>
    private void Housekeeping()
    {
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return; // 目录不可用：后续写入静默丢弃
        }

        try
        {
            var info = new DirectoryInfo(_directory);
            var cutoff = DateTime.Now - Retention;

            foreach (var file in info.GetFiles("*.log"))
            {
                if (file.LastWriteTime < cutoff)
                {
                    TryDelete(file);
                }
            }

            var remaining = info.GetFiles("*.log").OrderBy(f => f.LastWriteTime).ToList();
            var total = remaining.Sum(f => f.Length);
            foreach (var file in remaining)
            {
                if (total <= DirectoryQuotaBytes)
                {
                    break;
                }

                total -= file.Length;
                TryDelete(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 治理失败不影响写入
        }
    }

    private static void TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 被其他进程占用（例如同名组件的另一实例）：跳过
        }
    }
}

/// <summary>
/// 全局异常兜底：把未处理异常写进日志并同步刷盘。
/// WPF 的 <c>DispatcherUnhandledException</c> / WinForms 的 <c>ThreadException</c> 由各进程自行注册后
/// 调用 <see cref="Report"/>（共享文件不能引用 WPF/WinForms 类型）。
/// </summary>
public static class CrashGuard
{
    /// <summary>注册与 UI 框架无关的两类兜底（AppDomain / 未观察 Task）。</summary>
    public static void Attach(DiagnosticLogger logger, string component)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report(logger, component, "AppDomain.UnhandledException", e.ExceptionObject as Exception, e.IsTerminating);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Report(logger, component, "UnobservedTaskException", e.Exception, terminating: false);
            e.SetObserved();
        };
    }

    /// <summary>记录一次致命/未处理异常（含环境快照），并同步刷盘。</summary>
    public static void Report(
        DiagnosticLogger? logger,
        string component,
        string source,
        Exception? exception,
        bool terminating)
    {
        if (logger is null)
        {
            return;
        }

        try
        {
            logger.Error($"[崩溃] 来源={source} 组件={component} 进程终止={terminating}");
            if (exception is not null)
            {
                logger.Error($"  类型: {exception.GetType().FullName}");
                logger.Error($"  消息: {exception.Message}");
                logger.Error($"  堆栈: {exception.StackTrace}");

                var inner = exception.InnerException;
                var depth = 0;
                while (inner is not null && depth++ < 5)
                {
                    logger.Error($"  内层[{depth}]: {inner.GetType().FullName}: {inner.Message}");
                    inner = inner.InnerException;
                }
            }

            foreach (var line in DiagnosticLogger.EnvironmentInfo())
            {
                logger.Info("  " + line);
            }

            // 崩溃路径的异步队列可能来不及刷：这里同步落盘，保证最后几秒的日志不丢。
            logger.Flush();
        }
        catch
        {
            // 崩溃处理器自身绝不允许再抛
        }
    }
}
