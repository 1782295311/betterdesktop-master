// BetterDesktop.Kernel.Hmr — ExternalPluginAdapter 跨语言外部进程插件（B 层骨架）
// 进程级隔离：把内核依赖/服务映射为外部进程协议（Phase 1 协议后续接）。
// 本文件只落地「进程生命周期 + 看门狗退避重启 + 精确内存治理」——即用户要的"杀掉自重启"。
// 完整 JSON-RPC 协议按跨语言计划 Phase 1 后续接入，不影响本骨架的崩溃自愈能力。

using System.Diagnostics;
using System.Threading;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Kernel.Hmr;

/// <summary>
/// 外部进程插件适配器：宿主侧唯一入口（其余内核代码零改动）。
/// 实现 IResourceSubject —— 精确按 Process.WorkingSet64 采样内存，被 ResourceGovernor 接管杀掉自重启。
/// </summary>
public sealed class ExternalPluginAdapter : IPlugin, IResourceSubject, IDisposable
{
    private readonly PluginManifest _manifest;
    private readonly IContext _context;
    private readonly string _executable;
    private readonly string[] _arguments;
    private Process? _process;
    private readonly object _gate = new();
    private bool _quarantined;
    private int _consecutiveFails;
    private DateTimeOffset _lastFailAt;
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;

    // 退避序列：0.5s → 1s → 2s → 4s → 8s → 16s → 30s（封顶）
    private static readonly int[] Backoff = { 500, 1000, 2000, 4000, 8000, 16000, 30_000 };

    /// <summary>构造：executable 为外部进程路径，arguments 为启动参数（禁 shell 拼接，数组直传）。</summary>
    public ExternalPluginAdapter(PluginManifest manifest, IContext context, string executable, string[] arguments)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _executable = executable ?? throw new ArgumentNullException(nameof(executable));
        _arguments = arguments ?? Array.Empty<string>();
    }

    public string Name => _manifest.Name ?? _manifest.Id;

    public IReadOnlyList<Type> Inject { get; set; } = Array.Empty<Type>();

    // IResourceSubject
    string IResourceSubject.Id => _manifest.Id;
    string? IResourceSubject.DisplayName => Name;
    // 外部进程有精确 WorkingSet64，可被治理器直接隔离/重启（自愈）。
    bool IResourceSubject.IsSelfHealable => true;
    bool IResourceSubject.IsQuarantined => _quarantined;

    long IResourceSubject.SampleMemoryBytes()
    {
        lock (_gate)
        {
            if (_process is not { HasExited: false })
            {
                return 0;
            }
            try
            {
                _process.Refresh();
                return _process.WorkingSet64;
            }
            catch
            {
                return 0;
            }
        }
    }

    Task IResourceSubject.IsolateAsync(CancellationToken cancellationToken)
    {
        KillProcess();
        return Task.CompletedTask;
    }

    Task IResourceSubject.KillAndRestartAsync(CancellationToken cancellationToken)
    {
        KillProcess();
        StartProcess();
        return Task.CompletedTask;
    }

    void IResourceSubject.Quarantine()
    {
        _quarantined = true;
        KillProcess();
        _context.Logger.Error($"[Governor] 外部进程插件 {_manifest.Id} 已进入熔断隔离，停止自动重启");
    }

    void IResourceSubject.RecordPressure() { /* 计数由 Governor 维护，外部进程无需自记 */ }

    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        _cts = new CancellationTokenSource();
        StartProcess();
        // 启动看门狗：进程退出 → 退避重启（连续失败达上限转 Quarantine）
        _monitorTask = Task.Run(() => MonitorLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        _cts?.Cancel();
        KillProcess();
        try
        {
            // 等监控任务自然结束（Cancel 使其 Task.Delay 取消，忽略取消异常）
            if (_monitorTask is not null && !_monitorTask.Wait(2000))
            {
                _context.Logger.Warn($"外部进程插件 {_manifest.Id} 监控任务未在 2s 内退出");
            }
        }
        catch (AggregateException)
        {
            // 监控任务因取消令牌终止，属正常卸载路径
        }
        return Task.CompletedTask;
    }

    private void StartProcess()
    {
        if (_quarantined)
        {
            return;
        }
        lock (_gate)
        {
            if (_process is { HasExited: false })
            {
                return;
            }
            var psi = new ProcessStartInfo(_executable, string.Join(" ", _arguments))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = false
            };
            try
            {
                _process = Process.Start(psi);
                if (_process is null)
                {
                    _context.Logger.Error($"外部进程插件 {_manifest.Id} 启动失败（Process.Start 返回 null）");
                    return;
                }
                // stderr 聚合进内核日志（跨语言计划 §3 #6）
                _process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _context.Logger.Warn($"[{_manifest.Id}] {e.Data}");
                    }
                };
                _process.BeginErrorReadLine();
                _context.Logger.Info($"外部进程插件 {_manifest.Id} 已启动 (pid={_process.Id})");
            }
            catch (Exception ex)
            {
                _context.Logger.Error($"外部进程插件 {_manifest.Id} 启动异常：{ex.Message}");
            }
        }
    }

    private void KillProcess()
    {
        lock (_gate)
        {
            if (_process is null)
            {
                return;
            }
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(2000);
                }
            }
            catch (Exception ex)
            {
                _context.Logger.Warn($"外部进程插件 {_manifest.Id} 终止异常（已忽略）：{ex.Message}");
            }
            finally
            {
                try { _process.Dispose(); } catch { }
                _process = null;
            }
        }
    }

    private async Task MonitorLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Process? p;
            lock (_gate)
            {
                p = _process;
            }
            if (p is { HasExited: true })
            {
                if (_quarantined)
                {
                    return;
                }
                var now = DateTimeOffset.UtcNow;
                if ((now - _lastFailAt).TotalMilliseconds > 60_000)
                {
                    _consecutiveFails = 0;
                }
                _consecutiveFails++;
                _lastFailAt = now;
                _context.Logger.Error($"外部进程插件 {_manifest.Id} 崩溃（连续 {_consecutiveFails} 次）");

                if (_consecutiveFails >= 3)
                {
                    ((IResourceSubject)this).Quarantine();
                    return;
                }

                var delay = Backoff[Math.Min(_consecutiveFails - 1, Backoff.Length - 1)];
                await Task.Delay(delay, token).ConfigureAwait(false);
                StartProcess();
            }
            await Task.Delay(1000, token).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        KillProcess();
        try
        {
            _monitorTask?.Wait(2000);
        }
        catch (AggregateException)
        {
            // 监控任务因取消令牌终止，属正常释放路径
        }
        _cts?.Dispose();
    }
}
