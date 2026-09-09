using System;
using System.Runtime.InteropServices;
using System.Threading;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.Taskbar.Native;

/// <summary>
/// 开始菜单（Launcher）可见性监视，原样搬运 TranslucentTB 的 <c>IAppVisibility</c> 用法。
/// 通过 CLSID_AppVisibility 创建实例并 Advise 一个事件 sink，开始菜单开/关时回调。
/// </summary>
public sealed class AppVisibilityWatcher : IDisposable
{
    // CLSID_AppVisibility = {7E5FE3D9-985F-4908-91F9-C4D346E54BCE}
    private static readonly Guid CLSID_AppVisibility = new("7E5FE3D9-985F-4908-91F9-C4D346E54BCE");
    // IID_IAppVisibility = {2246EA2D-48B5-4B71-A8E6-BC12E4712AB5}
    private static readonly Guid IID_IAppVisibility = new("2246EA2D-48B5-4B71-A8E6-BC12E4712AB5");

    private IAppVisibility? _avi;
    // F7/V8：COM sink 必须字段持有——局部变量在某些 COM 实现下 CCW 被回收导致回调失效（7438 同源纪律）。
    private LauncherSink? _sink;
    private uint _cookie;
    private readonly object _lock = new();
    private bool _disposed;

    public event EventHandler<bool>? LauncherVisibilityChanged;

    public bool IsLauncherVisible()
    {
        lock (_lock)
        {
            if (_avi is null) return false;
            try
            {
                return _avi.IsLauncherVisible(out var visible) == 0 && visible;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed || _avi is not null) return;
            try
            {
                var clsid = CLSID_AppVisibility;
                var iid = IID_IAppVisibility;
                var hr = NativeMethods.CoCreateInstance(ref clsid, IntPtr.Zero, 1 /*CLSCTX_LOCAL_SERVER*/, ref iid, out var ppv);
                if (hr != 0 || ppv == IntPtr.Zero)
                {
                    DiagnosticLog.Trace("TaskbarAccent", $"CoCreateInstance(AppVisibility) 失败 hr=0x{hr:X8}（开始菜单联动不生效）");
                    return;
                }
                _avi = (IAppVisibility)Marshal.GetObjectForIUnknown(ppv);
                Marshal.Release(ppv);
                _sink = new LauncherSink(this);
                // F6/V7：Advise 返回值必须检查（cookie 有效性未知则 Unadvise 静默失败，7437 纪律 4）。
                var adviseHr = _avi.Advise(_sink, out _cookie);
                if (adviseHr != 0 || _cookie == 0)
                {
                    DiagnosticLog.Trace("TaskbarAccent", $"IAppVisibility.Advise 失败 hr=0x{adviseHr:X8} cookie={_cookie}（开始菜单联动不生效）");
                    _cookie = 0;
                }
            }
            catch (Exception)
            {
                _avi = null;
                _sink = null;
                _cookie = 0;
            }
        }
    }

    internal void OnVisibility(bool visible)
    {
        LauncherVisibilityChanged?.Invoke(this, visible);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_avi is not null && _cookie != 0)
            {
                try { _avi.Unadvise(_cookie); } catch { /* ignore */ }
                _cookie = 0;
            }
            // F6/V7：COM 对象必须显式释放（RCW 延迟 GC = COM 引用泄漏，7437 纪律 3）。
            if (_avi is not null)
            {
                try { _ = Marshal.ReleaseComObject(_avi); } catch { /* 已释放 */ }
                _avi = null;
            }
            _sink = null;
        }
    }

    // ---- COM 接口（严格按 Windows SDK 签名，不得改） ----
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("2246EA2D-48B5-4B71-A8E6-BC12E4712AB5")]
    private interface IAppVisibility
    {
        [PreserveSig]
        int IsLauncherVisible(out bool pIsVisible);
        [PreserveSig]
        int Advise(IAppVisibilitySink pSink, out uint pdwCookie);
        [PreserveSig]
        int Unadvise(uint dwCookie);
    }

    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("6584CE6B-7D82-49C2-9B2C-3A8921A8DA11")]
    private interface IAppVisibilitySink
    {
        [PreserveSig]
        int LauncherVisibilityChange(bool currentVisibleState);
    }

    private sealed class LauncherSink : IAppVisibilitySink
    {
        private readonly AppVisibilityWatcher _owner;
        public LauncherSink(AppVisibilityWatcher owner) => _owner = owner;
        public int LauncherVisibilityChange(bool currentVisibleState)
        {
            _owner.OnVisibility(currentVisibleState);
            return 0;
        }
    }
}

