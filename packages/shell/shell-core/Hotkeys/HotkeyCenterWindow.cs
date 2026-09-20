using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.Core.Hotkeys;

/// <summary>
/// 热键中心窗口抽象（测试注入假实现：注册失败注入 / 调用记录）。
/// </summary>
internal interface IHotkeyWindow : IDisposable
{
    /// <summary>在中心窗口注册系统热键（组合被占用返回 false，不抛）。</summary>
    bool Register(int id, int modifiers, uint vk);

    /// <summary>注销热键（未注册过返回 true，幂等）。</summary>
    bool Unregister(int id);
}

/// <summary>
/// 热键中心窗口（HWND_MESSAGE 消息专用窗口，热键注册表唯一真相源）。
/// <para>
/// 【为什么专用线程 + 自跑消息泵】<c>RegisterHotKey</c> 要求窗口所在线程跑消息循环，否则
/// <c>WM_HOTKEY</c> 永远收不到（3101 红线）。宿主 UI 线程的 WPF 泵可以承载，但把注册表绑定到
/// UI 线程会让"改键/注册"必须编队回 UI，且与插件加载线程解耦麻烦；故自建专用泵线程
/// （7435 泵线程纪律：注册线程必须跑消息循环），<c>Register/Unregister</c> 经命令队列编队执行。
/// </para>
/// <para>
/// 触发回调（<c>dispatch</c>）在**泵线程**执行——涉及 UI 的操作须自行编队回 UI 线程。
/// </para>
/// </summary>
internal sealed class HotkeyCenterWindow : IHotkeyWindow
{
    private readonly Func<int, bool> _dispatch; // WM_HOTKEY id → 是否消费
    private readonly NativeMethods.WndProcDelegate _wndProc; // 字段强引用防 GC（7435/7437 纪律）
    private readonly BlockingCollection<Action> _commands = new();
    private readonly ManualResetEventSlim _ready = new(initialState: false);
    private readonly string _className;
    private Thread? _thread;
    private IntPtr _hwnd;
    private int _pumpThreadId;
    private bool _failed;
    private int _disposed;

    public HotkeyCenterWindow(Func<int, bool> dispatch)
    {
        _dispatch = dispatch;
        _wndProc = WndProc;
        // 类名必须每次唯一：窗口类注册在【进程】范围且不随窗口销毁释放，同名类二次 RegisterClassEx
        // 会失败（ERROR_CLASS_ALREADY_EXISTS）。插件 HMR / 重建注册表时会让所有热键注册失败，
        // 并被上层误报成「热键被其他程序占用」（用户怎么改键都没用）。
        _className = $"BetterDesktop.HotkeyCenter.{Environment.ProcessId}.{Guid.NewGuid():N}";
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "BetterDesktop.HotkeyCenter",
        };
        _thread.SetApartmentState(ApartmentState.STA); // Win32 窗口线程须 STA
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)) || _failed || _hwnd == IntPtr.Zero)
        {
            _failed = true;
            DiagnosticLog.Trace(
                "hotkeys",
                $"热键中心窗口不可用，系统热键将全部注册失败：class={_className} lastError={Marshal.GetLastWin32Error()}");
        }
    }

    /// <inheritdoc />
    public bool Register(int id, int modifiers, uint vk)
    {
        if (_failed || _hwnd == IntPtr.Zero)
        {
            return false;
        }

        return RunOnThread(() => NativeMethods.RegisterHotKey(_hwnd, id, modifiers, vk));
    }

    /// <inheritdoc />
    public bool Unregister(int id)
    {
        if (_failed || _hwnd == IntPtr.Zero)
        {
            return true; // 窗口未就绪/已失效：视为已释放，幂等
        }

        return RunOnThread(() => NativeMethods.UnregisterHotKey(_hwnd, id));
    }

    private bool RunOnThread(Func<bool> action)
    {
        // 【自等死锁防护】热键触发回调就跑在泵线程上（见类注释）；回调里若再注册/注销/改键，
        // 入队后同步等待会变成「自己等自己」，该线程永久卡死；而调用方多数持有注册表的 _gate，
        // 会连带把宿主的设置/热键操作一起拖死。故识别到「当前即泵线程」时直接执行。
        if (_pumpThreadId != 0 && Environment.CurrentManagedThreadId == _pumpThreadId)
        {
            try
            {
                return action();
            }
            catch
            {
                return false;
            }
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _commands.Add(() =>
            {
                try
                {
                    tcs.TrySetResult(action());
                }
                catch (Exception e)
                {
                    tcs.TrySetException(e);
                }
            });

            // 兜底超时：泵线程异常退出或命令被卡住时不无限期阻塞调用方（本地窗口操作 5s 足够）。
            return tcs.Task.Wait(TimeSpan.FromSeconds(5)) && tcs.Task.Result;
        }
        catch
        {
            return false; // 线程已退出等：按失败处理，不冒泡
        }
    }

    private void ThreadMain()
    {
        _pumpThreadId = Environment.CurrentManagedThreadId;
        try
        {
            var wc = new NativeMethods.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = NativeMethods.GetModuleHandle(null),
                lpszClassName = _className,
                style = 0,
            };
            if (NativeMethods.RegisterClassEx(ref wc) == 0)
            {
                return; // _ready 未置位 → 构造器判 _failed
            }

            _hwnd = NativeMethods.CreateWindowExW(
                0, _className, string.Empty, 0, 0, 0, 0, 0,
                new IntPtr(NativeMethods.HWND_MESSAGE), IntPtr.Zero,
                NativeMethods.GetModuleHandle(null), IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            _ready.Set();

            // 【为什么必须有消息泵】RegisterHotKey 命中时，系统是把 WM_HOTKEY **投递**到注册线程的
            // 消息队列，只有该线程跑 PeekMessage/GetMessage + DispatchMessage 才会进 WndProc。
            // 历史实现只消费 _commands 队列、从不抽消息 → WndProc 永不被调用 → 所有系统热键
            // 「注册成功但按键无反应」，且因为注册没报错，界面还显示可用，极难排查。
            // 这里用「先抽干消息、再带超时取命令」的混合循环，两类工作都由本线程驱动。
            var msg = new NativeMethods.MSG();
            while (true)
            {
                while (NativeMethods.PeekMessage(out msg, IntPtr.Zero, 0, 0, NativeMethods.PM_REMOVE))
                {
                    if (msg.message == NativeMethods.WM_QUIT)
                    {
                        return;
                    }

                    _ = NativeMethods.TranslateMessage(ref msg);
                    _ = NativeMethods.DispatchMessage(ref msg);
                }

                if (_commands.IsAddingCompleted && _commands.Count == 0)
                {
                    return; // Dispose 已收尾：退出泵
                }

                if (_commands.TryTake(out var command, 50))
                {
                    command();
                }
            }
        }
        catch (Exception e)
        {
            _failed = true;
            DiagnosticLog.Trace("hotkeys", $"热键中心线程异常退出：{e}");
        }
        finally
        {
            _ready.Set(); // 兜底：即使失败也放行构造器（_failed 由 _hwnd 判定）
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WmHotKey)
        {
            _ = _dispatch(wParam.ToInt32());
            return IntPtr.Zero; // 已消费
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _commands.CompleteAdding();
        if (_thread is not null && _thread.IsAlive)
        {
            if (_hwnd != IntPtr.Zero)
            {
                _ = NativeMethods.DestroyWindow(_hwnd);
            }

            // 泵线程在 GetConsumingEnumerable 上等待：向它投递 WM_QUIT 不适用（非 GetMessage 泵），
            // 依赖 CompleteAdding 让 foreach 自然退出。
            if (!_thread.Join(TimeSpan.FromSeconds(2)))
            {
                // 超时兜底：后台线程随进程退出，不阻塞 Dispose
            }
        }

        _commands.Dispose();
        _ready.Dispose();
        _thread = null;
        _hwnd = IntPtr.Zero;
    }
}
