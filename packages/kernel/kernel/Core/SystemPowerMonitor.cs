// BetterDesktop.Kernel — 系统电源状态监听（挂起 / 恢复）
//
// 【为什么必须有】整个产品此前**零电源事件处理**（全仓搜 PBT_APMSUSPEND / PowerModeChanged /
// SessionSwitch 均无命中），后果有两类，都会让笔记本风扇长转、发烫耗电：
//   ① 现代待机（Modern Standby / S0ix）下系统"睡着了"但后台进程仍会被调度：我们的高频活动
//      （菜单栏每帧渲染订阅、Dock 60ms 轮询、状态采集 500ms、IPC 20ms 唤醒…）继续烧 CPU；
//   ② 恢复瞬间设备 / COM / DWM 句柄往往尚未就绪，各任务以原频率猛撞失败路径（COM 激活失败重试等）。
// 本类提供**进程内唯一的电源事件源**：挂起时让高频任务停手，恢复后给一段冷却期再逐步复原。
//
// 【为什么自建窗口，不用 SystemEvents.PowerModeChanged】
//   1) kernel 是最底层库，不额外引入桌面框架包；
//   2) WM_POWERBROADCAST 是**广播消息**，只投递给顶层窗口，消息专用窗口（HWND_MESSAGE）收不到 ——
//      故必须建一个隐藏的**顶层**窗口 + 专用消息泵线程。
//      （做法与 shell-status 既有的 PowerBroadcastHook 一致，这里上移为全进程可复用的公共设施。）

using System.Runtime.InteropServices;

namespace BetterDesktop.Kernel.Core;

/// <summary>
/// 进程内系统电源状态监听：把 <c>WM_POWERBROADCAST</c> 的挂起/恢复转成可订阅事件，
/// 并对外提供「现在是否应该停手/降频」的判据。
/// </summary>
public sealed class SystemPowerMonitor : IDisposable
{
    private const uint WM_POWERBROADCAST = 0x0218;
    private const uint WM_QUIT = 0x0012;

    /// <summary>系统即将挂起（进入睡眠/现代待机）。</summary>
    private const int PBT_APMSUSPEND = 0x0004;

    /// <summary>从挂起中恢复（用户交互触发）。</summary>
    private const int PBT_APMRESUMESUSPEND = 0x0007;

    /// <summary>从危急挂起中恢复（电量耗尽保护）。</summary>
    private const int PBT_APMRESUMECRITICAL = 0x0006;

    /// <summary>自动恢复（系统自行唤醒，无人交互）。</summary>
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;

    /// <summary>GWLP_USERDATA：64 位下必须用 Get/SetWindowLongPtr。</summary>
    private const int GWLP_USERDATA = -21;

    private const string ClassName = "BetterDesktopSystemPowerWindow";

    private static readonly Lazy<SystemPowerMonitor> LazyInstance = new(() => new SystemPowerMonitor());

    /// <summary>进程内单例（每个进程一份电源监听，勿重复创建）。</summary>
    public static SystemPowerMonitor Current => LazyInstance.Value;

    /// <summary>
    /// 恢复后的冷却期。这段时间里显示设备、COM 服务（ImmersiveShell / SMTC / CoreAudio）、
    /// DWM 窗口句柄往往还没就绪，高频任务应先让路，否则会以原频率反复撞失败路径。
    /// </summary>
    public static readonly TimeSpan ResumeCooldown = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private Thread? _thread;
    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _userDataHandle = IntPtr.Zero;
    private volatile bool _suspended;
    private long _resumeTick;

    private static readonly WndProcDelegate StaticWndProc = StaticWndProcCore;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>系统即将挂起（在监听线程上下文触发）。</summary>
    public event Action? Suspended;

    /// <summary>系统已恢复（在监听线程上下文触发；此时仍处于冷却期）。</summary>
    public event Action? Resumed;

    /// <summary>当前是否处于挂起过程中。</summary>
    public bool IsSuspended => _suspended;

    /// <summary>
    /// 是否应暂停高频工作（挂起中，或处于恢复冷却期）。
    /// 高频任务（每帧渲染、≤100ms 轮询、COM/原生采集）应以此为闸门主动让路。
    /// </summary>
    public bool ShouldPauseHighFrequencyWork
    {
        get
        {
            if (_suspended)
            {
                return true;
            }

            var tick = Interlocked.Read(ref _resumeTick);
            return tick != 0 && Environment.TickCount64 - tick < (long)ResumeCooldown.TotalMilliseconds;
        }
    }

    /// <summary>启动监听（幂等；可重复调用）。失败只记日志，绝不抛出。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_thread is not null)
            {
                return;
            }

            _thread = new Thread(ThreadProc)
            {
                IsBackground = true,
                Name = "SystemPowerMonitor",
            };
            _thread.Start();
        }
    }

    /// <summary>停止监听（幂等）。</summary>
    public void Dispose()
    {
        Thread? thread;
        IntPtr hwnd;
        lock (_gate)
        {
            thread = _thread;
            hwnd = _hwnd;
            _thread = null;
            _hwnd = IntPtr.Zero;
        }

        if (thread is null)
        {
            return;
        }

        if (hwnd != IntPtr.Zero)
        {
            _ = PostMessage(hwnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        thread.Join(TimeSpan.FromSeconds(2));
    }

    private void Raise(Action? handler)
    {
        try
        {
            handler?.Invoke();
        }
        catch (Exception e)
        {
            // 订阅者异常不影响监听本身继续工作
            DiagnosticLog.Trace("power", $"电源事件订阅者异常：{e.GetType().Name}: {e.Message}");
        }
    }

    private void ThreadProc()
    {
        var wc = default(WNDCLASSEX);
        wc.cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>();
        wc.lpfnWndProc = StaticWndProc;
        wc.hInstance = GetModuleHandleW(null);
        wc.lpszClassName = ClassName;
        wc.lpszMenuName = string.Empty;

        var registered = RegisterClassEx(ref wc);
        if (registered == 0 && Marshal.GetLastWin32Error() != 1410 /*ERROR_CLASS_ALREADY_EXISTS*/)
        {
            DiagnosticLog.Trace("power", $"电源监听窗口类注册失败（错误 {Marshal.GetLastWin32Error()}），挂起/恢复通知不可用");
            return;
        }

        try
        {
            // 无 WS_* 样式：不可见、无边框、不占任务栏，仅作电源广播接收载体。
            // 必须是**顶层窗口**（父窗口为 0），否则收不到 WM_POWERBROADCAST。
            var hwnd = CreateWindowExW(0, ClassName, ClassName, 0, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                DiagnosticLog.Trace("power", "电源监听窗口创建失败，挂起/恢复通知不可用");
                return;
            }

            // 在锁内发布句柄：Dispose() 也在锁内读取它以投递 WM_QUIT。
            lock (_gate)
            {
                _hwnd = hwnd;
            }

            var handle = GCHandle.Alloc(this);
            _userDataHandle = GCHandle.ToIntPtr(handle);
            SetWindowLongPtr(hwnd, GWLP_USERDATA, _userDataHandle);

            DiagnosticLog.Trace("power", "电源监听已启动（挂起 / 恢复）");

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_QUIT)
                {
                    break;
                }

                _ = TranslateMessage(ref msg);
                _ = DispatchMessage(ref msg);
            }
        }
        finally
        {
            if (_hwnd != IntPtr.Zero)
            {
                _ = DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            if (_userDataHandle != IntPtr.Zero)
            {
                GCHandle.FromIntPtr(_userDataHandle).Free();
                _userDataHandle = IntPtr.Zero;
            }
        }
    }

    private IntPtr InstanceWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_POWERBROADCAST)
        {
            switch (wParam.ToInt32())
            {
                case PBT_APMSUSPEND:
                    _suspended = true;
                    DiagnosticLog.Trace("power", "系统即将挂起：高频任务应停手（渲染订阅 / 轮询 / 采集）");
                    Raise(Suspended);
                    break;

                case PBT_APMRESUMESUSPEND:
                case PBT_APMRESUMECRITICAL:
                case PBT_APMRESUMEAUTOMATIC:
                    Interlocked.Exchange(ref _resumeTick, Environment.TickCount64);
                    _suspended = false;
                    DiagnosticLog.Trace(
                        "power",
                        $"系统已恢复：进入 {ResumeCooldown.TotalSeconds:0} 秒冷却期（设备/COM/DWM 通常在此时段内才就绪）");
                    Raise(Resumed);
                    break;
            }

            // 返回 1 = 已处理该广播（注意：-1 / BROADCAST_QUERY_DENY 是"拒绝挂起"，绝不能返回）
            return new IntPtr(1);
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static IntPtr StaticWndProcCore(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var ptr = GetWindowLongPtr(hWnd, GWLP_USERDATA);
        if (ptr == IntPtr.Zero)
        {
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        try
        {
            var self = (SystemPowerMonitor)GCHandle.FromIntPtr(ptr).Target!;
            return self.InstanceWndProc(hWnd, msg, wParam, lParam);
        }
        catch
        {
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    // ---------------- P/Invoke ----------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
