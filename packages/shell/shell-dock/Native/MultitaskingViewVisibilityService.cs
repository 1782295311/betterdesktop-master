using System;
using System.Runtime.InteropServices;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.Dock.Native;

/// <summary>
/// 多任务视图（AltTab / 任务视图 / SnapAssist）可见性检测。
/// 契约完全对齐 TTB <c>Common/undoc/explorer.hpp</c> [verified]：
///   CLSID_ImmersiveShell = C2F03A33-21F5-47FA-B4BB-156362A2F239（L9）
///   SID_MultitaskingViewVisibilityService = 785702DD-B8EF-469F-8C19-E91B5F4CA564（L10）
///   IID_IMultitaskingViewVisibilityService = AC11CDA3-1601-4AD7-A40E-FE2CED187307（L30）
///   方法序（L31-36）：IsViewVisible → Register → Unregister（错序 = 调用错乱）
///   MULTITASKING_VIEW_TYPES：ALT_TAB=1 / ALL_UP=2 / SNAP_ASSIST=4 / PPI_ALL_UP=8(Team) / ANY=0xF
/// P2c 拍板：只轮询查询、不 Register 回调（AltTab 瞬态轮询足够，避免 7438 配对复杂度）。
/// QueryService/调用失败（Win10 低版本/无服务）→ 降级 false（7404 红线 5），不崩溃。
/// </summary>
internal static class MultitaskingViewVisibilityService
{
    private static readonly Guid ClsidImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid SidMultitaskingViewVisibilityService = new("785702DD-B8EF-469F-8C19-E91B5F4CA564");
    private static readonly Guid IidMultitaskingViewVisibilityService = new("AC11CDA3-1601-4AD7-A40E-FE2CED187307");
    private static readonly Guid IidIServiceProvider = new("6D5140C1-7436-11CE-8034-00AA006009FA");

    private const int MvtAny = 0xF;

    /// <summary>多任务视图任一视图（ANY 位）是否可见。服务不可用/失败返回 false。</summary>
    public static bool IsAnyViewVisible()
    {
        object? shellRcw = null;
        object? svcObj = null;
        IntPtr shellUnknown = IntPtr.Zero;
        try
        {
            // CoCreateInstance(CLSID_ImmersiveShell, IID_IServiceProvider)——与 AppVisibilityWatcher 同模式
            //（ole32 直取 IUnknown + GetObjectForIUnknown，ComImport 类直 cast 在部分 TFM 下编译受限）。
            var clsid = ClsidImmersiveShell;
            var iidSp = IidIServiceProvider;
            if (NativeMethods.CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxLocalServer | ClsctxInprocServer, ref iidSp, out shellUnknown) != 0
                || shellUnknown == IntPtr.Zero)
            {
                return false;
            }

            shellRcw = Marshal.GetObjectForIUnknown(shellUnknown);
            if (shellRcw is not IServiceProviderShell shell)
            {
                return false;
            }

            var sid = SidMultitaskingViewVisibilityService;
            var iid = IidMultitaskingViewVisibilityService;
            var qsHr = shell.QueryService(ref sid, ref iid, out svcObj);
            if (qsHr != 0 || svcObj is null)
            {
                return false; // 版本不支持/服务不在 → 降级
            }

            var service = (IMultitaskingViewVisibilityService)svcObj;
            var hr = service.IsViewVisible(MvtAny, out var retFlags);
            if (hr != 0)
            {
                return false;
            }

            return (retFlags & MvtAny) != 0;
        }
        catch
        {
            return false; // 7404 红线 5：QueryService/调用失败降级，不崩溃
        }
        finally
        {
            if (svcObj is not null)
            {
                try { _ = Marshal.ReleaseComObject(svcObj); } catch { /* 已释放 */ }
            }
            if (shellRcw is not null)
            {
                try { _ = Marshal.FinalReleaseComObject(shellRcw); } catch { /* 已释放 */ }
            }
            if (shellUnknown != IntPtr.Zero)
            {
                _ = Marshal.Release(shellUnknown);
            }
        }
    }

    private const uint ClsctxInprocServer = 0x1;
    private const uint ClsctxLocalServer = 0x4;

    /// <summary>IServiceProvider（6D5140C1-7436-11CE-8034-00AA006009FA），QueryService 标准入口。</summary>
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProviderShell
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
    }


    /// <summary>方法序对齐 TTB explorer.hpp L31-36（IsViewVisible → Register → Unregister）。</summary>
    [ComImport, Guid("AC11CDA3-1601-4AD7-A40E-FE2CED187307"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMultitaskingViewVisibilityService
    {
        [PreserveSig]
        int IsViewVisible(int flags, out int retFlags);

        [PreserveSig]
        int Register(IntPtr pVisibilityNotification, out uint pdwCookie);

        [PreserveSig]
        int Unregister(uint dwCookie);
    }
}
