using System;
using System.IO;
using System.Runtime.InteropServices;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Taskbar.Contracts;

namespace BetterDesktop.Shell.Taskbar.Native;

/// <summary>
/// Win11 XAML 任务栏外观桥。
/// 原样搬运 TranslucentTB 的 ExplorerTAP.dll 注入方案：Win11 任务栏是 XAML，
/// 必须向 explorer.exe 注入 DLL 才能改外观。注入后通过 COM 拿到
/// <c>ITaskbarAppearanceService</c>（GUID 5bcf9150-c28a-4ef2-913c-4c3ea2f5ead0）。
///
/// 本类负责：LoadLibrary(ExplorerTAP.dll) → 取其导出 InjectExplorerTAP →
/// 对主任务栏窗口调用 → 拿到 ITaskbarAppearanceService 实例。
/// ExplorerTAP.dll 作为外部编译产物随包分发（仅需此一个 DLL：其 InjectExplorerTAP
/// 通过 Detours 内存 payload 机制把代码注入 explorer 并自注册 COM 类，
/// 不依赖 ExplorerHooks.dll——后者是 TTB 主程序自身 hook explorer 用的，本桥不调用）。
/// 获取方式：编译 TTB 源码（见仓库 参考/build_explorertap_locally.cmd），或取 TTB 官方
/// Release 包内的 amd64/ExplorerTAP.dll。
/// </summary>
public sealed class ExplorerTapBridge : IDisposable
{
    private const string TapDllName = "ExplorerTAP.dll";

    // IID_ITaskbarAppearanceService = {5bcf9150-c28a-4ef2-913c-4c3ea2f5ead0}
    private static readonly Guid IID_TaskbarAppearanceService =
        new("5bcf9150-c28a-4ef2-913c-4c3ea2f5ead0");

    private readonly IntPtr _module;
    private readonly InjectExplorerTapDelegate? _inject;
    private ITaskbarAppearanceServiceCom? _service;
    private bool _disposed;

    /// <summary>桥是否成功初始化（DLL 加载 + 导出解析成功）。</summary>
    public bool Available { get; }

    public ExplorerTapBridge(string? dllDirectory = null)
    {
        _module = IntPtr.Zero;
        Available = false;

        var dllPath = ResolveDllPath(dllDirectory);
        if (dllPath is null || !File.Exists(dllPath))
        {
            return;
        }

        try
        {
            _module = LoadLibrary(dllPath);
            if (_module == IntPtr.Zero) return;

            var proc = NativeMethods.GetProcAddress(_module, "InjectExplorerTAP");
            if (proc == IntPtr.Zero) return;

            _inject = Marshal.GetDelegateForFunctionPointer<InjectExplorerTapDelegate>(proc);
            Available = true;
        }
        catch (Exception)
        {
            Available = false;
        }
    }

    /// <summary>向 explorer 注入并获取 ITaskbarAppearanceService（针对指定任务栏窗口）。</summary>
    public bool Connect(IntPtr taskbarHwnd)
    {
        if (!Available || _inject is null || taskbarHwnd == IntPtr.Zero) return false;
        try
        {
            int hr = _inject(taskbarHwnd, IID_TaskbarAppearanceService, out var ppv);
            if (hr < 0 || ppv is null) return false;
            _service = (ITaskbarAppearanceServiceCom)ppv;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>设置任务栏外观（Win11 路径）。brush: 0=Acrylic, 1=SolidColor。</summary>
    public bool SetAppearance(IntPtr taskbarHwnd, byte brush, uint color)
    {
        if (_service is null) return false;
        try { return _service.SetTaskbarAppearance(taskbarHwnd, brush, color) >= 0; }
        catch (Exception) { return false; }
    }

    /// <summary>设置任务栏模糊（Win11 路径，blurAmount 通常 = radius/3）。</summary>
    public bool SetBlur(IntPtr taskbarHwnd, uint color, float blurAmount)
    {
        if (_service is null) return false;
        try { return _service.SetTaskbarBlur(taskbarHwnd, color, blurAmount) >= 0; }
        catch (Exception) { return false; }
    }

    /// <summary>还原单个任务栏到默认。</summary>
    public bool ReturnToDefault(IntPtr taskbarHwnd)
    {
        if (_service is null) return false;
        try { return _service.ReturnTaskbarToDefaultAppearance(taskbarHwnd) >= 0; }
        catch (Exception) { return false; }
    }

    /// <summary>还原所有任务栏到默认（进程退出时调用）。</summary>
    public bool RestoreAll()
    {
        if (_service is null) return false;
        try { return _service.RestoreAllTaskbarsToDefault() >= 0; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// 进程死亡还原契约（F1/V1，7436 红线 2）：告诉 explorer「本进程死亡时还原所有任务栏为默认」。
    /// TTB taskbarattributeworker.cpp:1430 启动必调——声明了就必须调用，否则进程崩溃/强杀后
    /// 任务栏外观永久残留。传 Environment.ProcessId（对齐 TTB GetCurrentProcessId()）。
    /// </summary>
    public bool RestoreAllWhenProcessDies()
    {
        if (_service is null) return false;
        try { return _service.RestoreAllTaskbarsToDefaultWhenProcessDies((uint)Environment.ProcessId) >= 0; }
        catch (Exception) { return false; }
    }

    private static string? ResolveDllPath(string? dllDirectory)
    {
        if (dllDirectory is not null)
        {
            var p = Path.Combine(dllDirectory, TapDllName);
            if (File.Exists(p)) return p;
        }
        // 回退：程序基目录 / 程序基目录下的 native 子目录
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, TapDllName),
            Path.Combine(baseDir, "native", TapDllName),
            Path.Combine(baseDir, "x64", TapDllName),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // V2（审计 2026-09-04）：COM RCW 必须显式释放——原实现只置 null，RCW 随 GC 延迟终结，
        // explorer 常驻进程侧的 COM 引用全程泄漏（对照 TsfInputProcessor 双释放纪律）。
        if (_service is not null)
        {
            try { _ = Marshal.FinalReleaseComObject(_service); } catch { /* 已释放 */ }
            _service = null;
        }
        if (_module != IntPtr.Zero)
        {
            try { FreeLibrary(_module); } catch { /* ignore */ }
        }
        GC.SuppressFinalize(this);
    }

    // 注意：无终结器——终结器线程不碰 COM（审计建议"终结器不碰 COM"；FreeLibrary/ReleaseComObject
    // 在终结器线程执行有竞态风险，资源确定性释放由 Dispose 契约承担）。

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InjectExplorerTapDelegate(
        IntPtr window, // HWND
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);

    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("5bcf9150-c28a-4ef2-913c-4c3ea2f5ead0")]
    private interface ITaskbarAppearanceServiceCom
    {
        [PreserveSig] int SetTaskbarAppearance(IntPtr taskbar, byte brush, uint color);
        [PreserveSig] int SetTaskbarBlur(IntPtr taskbar, uint color, float blurAmount);
        [PreserveSig] int ReturnTaskbarToDefaultAppearance(IntPtr taskbar);
        [PreserveSig] int SetTaskbarBorderVisibility(IntPtr taskbar, bool visible);
        [PreserveSig] int RestoreAllTaskbarsToDefault();
        [PreserveSig] int RestoreAllTaskbarsToDefaultWhenProcessDies(uint pid);
        [PreserveSig] int KillExplorerWhenPackageUninstalls([MarshalAs(UnmanagedType.LPWStr)] string packageFullName);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);
}
