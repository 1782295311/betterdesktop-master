// BetterDesktop.Shell.Status — 电源状态广播的 Win32 消息窗口钩子（电池事件驱动）
// 背景：WM_POWERBROADCAST（0x0218）是广播消息，只会投递到"顶层窗口"，消息专用窗口（HWND_MESSAGE）收不到，
// 因此复用 WinEventForegroundPump 的"仅消息泵线程"方案行不通，必须建一个隐藏的顶层窗口并挂 WndProc。
// 职责：电源状态变化（插拔电源/电池电量越过阈值）时触发回调，让电池监控立即刷新，替代"至少等 3s 轮询"。
// 该钩子只做一件事：收到 PBT_APMPOWERSTATUSCHANGE 时调用回调；不承载任何业务逻辑。

using System;
using System.Runtime.InteropServices;
using System.Threading;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.Status.Services;

/// <summary>
/// 一条专用后台线程 + 隐藏顶层窗口 + 消息泵，监听 <see cref="WM_POWERBROADCAST"/>。
/// 电源状态每次变化时调用 <paramref name="onPowerChanged"/>（在钩子线程上下文）。
/// </summary>
internal sealed class PowerBroadcastHook : IDisposable
{
    private const uint WM_POWERBROADCAST = 0x0218;
    private const int PBT_APMPOWERSTATUSCHANGE = 0x000A;
    private const int WM_QUIT = 0x0012;

    private const string ClassName = "BetterDesktopPowerHookWindow";
    private const string WindowName = "BetterDesktopPowerHookWindow";

    // GWLP_USERDATA 在 64 位下必须用 Get/SetWindowLongPtr（IntPtr 版本）。
    private const int GWLP_USERDATA = -21;

    private readonly Action _onPowerChanged;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly object _gate = new();

    private Thread? _thread;
    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _userDataHandle = IntPtr.Zero; // 持有 GCHandle（防 self 被 GC）

    // 类窗口过程必须保持委托存活；窗口销毁由消息泵退出路径统一释放。
    private static readonly WndProcDelegate StaticWndProc = StaticWndProcCore;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public PowerBroadcastHook(Action onPowerChanged)
    {
        _onPowerChanged = onPowerChanged;
    }

    /// <summary>启动后台钩子线程并等待窗口创建完成（带超时，失败不阻塞）。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_thread is not null) return;
            _ready.Reset();
            _thread = new Thread(ThreadProc) { IsBackground = true, Name = "PowerBroadcastHook" };
            _thread.Start();
        }
        _ready.Wait(TimeSpan.FromSeconds(1));
    }

    /// <summary>通过向钩子线程投递 WM_QUIT 优雅退出，窗口在其线程上下文内销毁并释放资源。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_thread is null) return;
            if (_hwnd != IntPtr.Zero)
            {
                _ = NativeMethods.PostMessage(_hwnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
            _thread.Join(2000);
            _thread = null;
            _hwnd = IntPtr.Zero;
        }
    }

    public void Dispose() => Stop();

    // ---------------- 窗口线程 ----------------

    private void ThreadProc()
    {
        WNDCLASSEX wc = default;
        wc.cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>();
        wc.lpfnWndProc = StaticWndProc;
        wc.hInstance = GetModuleHandleW(null);
        wc.lpszClassName = ClassName;
        // 无光标/背景/图标，纯消息载体，不显示。
        if (RegisterClassEx(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410 /*ERROR_CLASS_ALREADY_EXISTS*/)
        {
            _ready.Set();
            return;
        }

        try
        {
            _hwnd = NativeMethods.CreateWindowExW(
                0, ClassName, WindowName,
                0,                     // 无 WS_* 样式：不可见、无边框、无任务栏，仅作广播接收载体
                0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                _ready.Set();
                return;
            }

            // 把实例压进窗口的 GWLP_USERDATA，静态 WndProc 据此取回收发广播的宿主。
            var gch = GCHandle.Alloc(this);
            _userDataHandle = GCHandle.ToIntPtr(gch);
            NativeMethods.SetWindowLongPtr(_hwnd, GWLP_USERDATA, _userDataHandle);

            _ready.Set();

            // 消息泵：广播（含 WM_POWERBROADCAST）依赖本线程派发消息。
            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_QUIT) break;
                _ = NativeMethods.TranslateMessage(ref msg);
                _ = NativeMethods.DispatchMessage(ref msg);
            }
        }
        finally
        {
            if (_hwnd != IntPtr.Zero)
            {
                _ = NativeMethods.DestroyWindow(_hwnd);
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
        if (msg == WM_POWERBROADCAST && wParam.ToInt32() == PBT_APMPOWERSTATUSCHANGE)
        {
            try
            {
                _onPowerChanged?.Invoke();
            }
            catch
            {
                // 回调里拉取失败不影响钩子继续工作
            }
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // ---------------- 原生窗口过程 ----------------

    /// <summary>窗口类过程：从 GWLP_USERDATA 取回实例分发；不存在则走默认过程（WM_NCCREATE 等）。</summary>
    private static IntPtr StaticWndProcCore(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        IntPtr ptr = NativeMethods.GetWindowLongPtr(hWnd, GWLP_USERDATA);
        if (ptr == IntPtr.Zero)
        {
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }
        try
        {
            var self = (PowerBroadcastHook)GCHandle.FromIntPtr(ptr).Target!;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpWndClass);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

}
