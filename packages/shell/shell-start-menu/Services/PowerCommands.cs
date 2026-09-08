using System;
using System.Runtime.InteropServices;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace BetterDesktop.Shell.StartMenu.Services;

/// <summary>
/// 电源操作（关机 / 重启 / 睡眠 / 锁定）P/Invoke。
/// 关机/重启/睡眠需要 SeShutdownPrivilege（启动时提权一次，随进程生命周期持有）。
/// 所有操作失败静默返回 false，不抛异常（M10）。
/// </summary>
internal static class PowerCommands
{
    private const uint EWX_SHUTDOWN = 0x1;
    private const uint EWX_REBOOT = 0x2;
    private const uint EWX_POWEROFF = 0x8;
    private const uint EWX_FORCE = 0x4;

    private const int SePrivilegeEnabled = 0x2;
    private const int TokenAdjustPrivileges = 0x20;
    private const int TokenQuery = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }




    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    private static readonly object _sync = new();
    private static bool _privilegeReady;

    /// <summary>
    /// 提权 SeShutdownPrivilege（线程安全）。【F12/O4 修复（7439）】_privilegeReady 语义 =
    /// "确认已提权成功"而非"尝试过一次"——仅在 AdjustTokenPrivileges 成功路径置 true；
    /// 原 finally 无条件置 true 使首次提权失败后关机/重启/注销永不重试、永远静默失败。
    /// </summary>
    private static void EnsureShutdownPrivilege()
    {
        lock (_sync)
        {
            if (_privilegeReady)
            {
                return;
            }

            try
            {
                var current = System.Diagnostics.Process.GetCurrentProcess().Handle;
                if (NativeMethods.OpenProcessToken(current, TokenAdjustPrivileges | TokenQuery, out var token))
                {
                    try
                    {
                        if (NativeMethods.LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid))
                        {
                            var privileges = new NativeMethods.TokenPrivileges
                            {
                                PrivilegeCount = 1,
                                Privileges = new NativeMethods.LuidAndAttributes { Luid = luid, Attributes = (uint)SePrivilegeEnabled }
                            };
                            if (NativeMethods.AdjustTokenPrivileges(token.DangerousGetHandle(), false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
                            {
                                // TRUE 但 GetLastError=ERROR_NOT_ALL_ASSIGNED(1300) 表示权限未授予——仍视为未就绪。
                                _privilegeReady = Marshal.GetLastWin32Error() == 0;
                            }
                            if (!_privilegeReady)
                            {
                                DiagnosticLog.Trace("PowerCommands",
                                    $"SeShutdownPrivilege 提权失败 err={Marshal.GetLastWin32Error()}（关机/重启可能失败；保持未就绪，下次可重试）");
                            }
                        }
                    }
                    finally
                    {
                        NativeMethods.CloseHandle(token.DangerousGetHandle());
                        token.Dispose();
                    }
                }
            }
            catch
            {
                // 提权异常不影响后续重试（M10）；失败路径不置 ready（7439 红线 2）。
            }
        }
    }

    public static bool Shutdown()
    {
        EnsureShutdownPrivilege();
        try
        {
            // P2b（7403）：对齐 Windows 开始菜单语义——系统有待装更新时走"更新关机"
            //（shutdownux!UsoCommitHelper::SetAndModifyShutdownFlags 同路径）。
            // 前置 EnhancedShutdownEnabled 检查（红线 3：无更新不拉服务）；任一步失败降级直接关机（不阻塞）。
            var flags = WindowsUpdateAdjustShutdownFlags(EWX_SHUTDOWN | EWX_POWEROFF);
            return ExitWindowsEx(flags, 0);
        }
        catch
        {
            return false;
        }
    }

    // ---------------- P2b：更新关机编排（7403 变体 A，Open-Shell MenuCommands.cpp L825-873 移植） ----------------

    private static readonly Guid ClsidUpdateSessionOrchestrator = new("B91D5831-B1BD-4608-8198-D72E155020F7");
    private static readonly Guid IidIUnknown = new("00000000-0000-0000-0000-000000000000");
    private static readonly Guid IidIUpdateSessionOrchestrator = new("07F3AFAC-7C8A-4CE7-A5E0-3D24EE8A77E0");
    private static readonly Guid IidIUxUpdateManagerWin10 = new("833EE9A0-2999-432C-8EF2-87A8EC2D748D");
    private static readonly Guid IidIUxUpdateManagerWin11 = new("B96BA95F-9479-4656-B7A1-6F3A69091910");

    private const uint ClsctxLocalServer = 4;

    /// <summary>
    /// 对齐 Open-Shell WindowsUpdateAdjustShutdownFlags：有更新准备时经 UpdateSessionOrchestrator
    /// 调 IUxUpdateManager::SetAndModifyShutdownFlags（Win10/Win11 双接口降级链），返回调整后的关机 flags；
    /// 任一步失败/无更新 → 原样返回（直接关机，不阻塞）。
    /// </summary>
    private static uint WindowsUpdateAdjustShutdownFlags(uint flags)
    {
        object? orchestratorRcw = null;
        IntPtr orchestratorUnknown = IntPtr.Zero;
        try
        {
            // 前置（7403 红线 3）："EnhancedShutdownEnabled" 值必须存在才处理，否则不拉 COM 服务。
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Default);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\WindowsUpdate\Orchestrator");
            if (key is null || key.GetValue("EnhancedShutdownEnabled") is null)
            {
                return flags;
            }

            // NativeMethods.CoCreateInstance(CLSID_UpdateSessionOrchestrator, CLSCTX_LOCAL_SERVER)（红线 4：必须 LOCAL_SERVER）。
            var clsid = ClsidUpdateSessionOrchestrator;
            var iidUnknown = IidIUnknown;
            var hr = NativeMethods.CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxLocalServer, ref iidUnknown, out orchestratorUnknown);
            if (hr != 0 || orchestratorUnknown == IntPtr.Zero)
            {
                return flags;
            }

            orchestratorRcw = Marshal.GetObjectForIUnknown(orchestratorUnknown);
            var orchestrator = (IUpdateSessionOrchestrator)orchestratorRcw;
            if (orchestrator.CreateUxUpdateManager(out var mgrObj) != 0 || mgrObj is null)
            {
                return flags;
            }

            try
            {
                // Win10 → Win11 降级链（方法序经 Open-Shell MenuCommands.cpp L723-814 逐项核对）。
                if (mgrObj is IUxUpdateManagerWin10 mgr10 && mgr10.SetAndModifyShutdownFlags(flags, out var newFlags) == 0)
                {
                    return newFlags;
                }
                if (mgrObj is IUxUpdateManagerWin11 mgr11 && mgr11.SetAndModifyShutdownFlags(flags, out var newFlags11) == 0)
                {
                    return newFlags11;
                }
                return flags;
            }
            finally
            {
                _ = Marshal.ReleaseComObject(mgrObj);
            }
        }
        catch
        {
            return flags; // 7403：更新编排失败降级直接关机，不阻塞关机
        }
        finally
        {
            if (orchestratorRcw is not null)
            {
                try { _ = Marshal.FinalReleaseComObject(orchestratorRcw); } catch { /* 已释放 */ }
            }
            if (orchestratorUnknown != IntPtr.Zero)
            {
                _ = Marshal.Release(orchestratorUnknown);
            }
        }
    }

    /// <summary>IUpdateSessionOrchestrator（07F3AFAC-...）方法序对齐 Open-Shell MenuCommands.cpp L816-823。</summary>
    [ComImport, Guid("07F3AFAC-7C8A-4CE7-A5E0-3D24EE8A77E0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUpdateSessionOrchestrator
    {
        [PreserveSig] int CreateUpdateSession(int sessionType, ref Guid riid, out IntPtr ppSession);
        [PreserveSig] int GetCurrentActiveUpdateSessions(out IntPtr ppCollection);
        [PreserveSig] int LogTaskRunning([MarshalAs(UnmanagedType.LPWStr)] string taskName);
        [PreserveSig] int CreateUxUpdateManager([MarshalAs(UnmanagedType.IUnknown)] out object ppManager);
    }

    /// <summary>
    /// IUxUpdateManager_Win10（833EE9A0-...）——34 个方法序逐项对照 Open-Shell MenuCommands.cpp L723-760。
    /// 非目标方法用等宽槽位占位（x64 下每参数一个 8 字节槽，封送安全）；红线：禁止臆造方法序。
    /// </summary>
    [ComImport, Guid("833EE9A0-2999-432C-8EF2-87A8EC2D748D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUxUpdateManagerWin10
    {
        [PreserveSig] int GetUxStateVariableBOOL(int variable, out int pValue, out int pResult);
        [PreserveSig] int GetUxStateVariableDWORD(int variable, out uint pValue, out int pResult);
        [PreserveSig] int GetUxStateVariableSYSTEMTIME(int variable, IntPtr pSystemTime, out int pResult);
        [PreserveSig] int SetUxStateVariableBOOL(int variable, int value);
        [PreserveSig] int SetUxStateVariableDWORD(int variable, uint value);
        [PreserveSig] int SetUxStateVariableSYSTEMTIME(int variable, IntPtr value);
        [PreserveSig] int DeleteUxStateVariable(int variable);
        [PreserveSig] int GetNextRebootTaskRunTime(out int pFlag, IntPtr pSystemTime);
        [PreserveSig] int CreateRebootTasks([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr systemTime);
        [PreserveSig] int CreateUpdateResultsTaskSchedule();
        [PreserveSig] int CreateMigrationResultsTaskSchedule();
        [PreserveSig] int CreateUpdateLogonNotificationTaskSchedule();
        [PreserveSig] int CreateUpdateNotificationTaskSchedule(IntPtr systemTime);
        [PreserveSig] int CreateLogonRebootTaskSchedule();
        [PreserveSig] int DidUXRebootTaskWakeUpDevice(out int pValue);
        [PreserveSig] int RemoveUpdateResultsTaskSchedule();
        [PreserveSig] int RemoveMigrationResultsTaskSchedule();
        [PreserveSig] int EnableRebootTasks();
        [PreserveSig] int DisableRebootTasks();
        [PreserveSig] int ValidateAndRecoverRebootTasks();
        [PreserveSig] int RebootToCompleteInstall(uint flags, int a2, out uint a3, short a4, short a5, uint a6);
        [PreserveSig] int IsRestartAllowed(uint flags, int a2, uint a3, out int pResult);
        [PreserveSig] int GetIsWaaSOutOfDate(uint a1, int a2, int a3, out int pResult, out uint a5);
        [PreserveSig] int GetWaaSHoursOutOfDate(int a1, int a2, out uint pResult);
        [PreserveSig] int GetCachedPolicy(uint a1, IntPtr pVariant, out uint a3, out uint a4);
        [PreserveSig] int GetEnterpriseCachedPolicy(uint a1, IntPtr pVariant, out uint a3, out uint a4);
        [PreserveSig] int GetCachedSettingValue(uint a1, short a2, IntPtr pVariant);
        [PreserveSig] int GetOptInToMU(out int pValue);
        [PreserveSig] int SetOptInToMU(int value);
        [PreserveSig] int SetAndModifyShutdownFlags(uint flags, out uint newFlags);
        [PreserveSig] int GetIsFlightingEnabled(out int pValue);
        [PreserveSig] int GetIsCTA(out int pValue);
        [PreserveSig] int NotifyStateVariableChange();
        [PreserveSig] int GetAlwaysAllowMeteredNetwork(out int pValue);
    }

    /// <summary>
    /// IUxUpdateManager_Win11（B96BA95F-...）——46 个方法序逐项对照 Open-Shell MenuCommands.cpp L764-813。
    /// </summary>
    [ComImport, Guid("B96BA95F-9479-4656-B7A1-6F3A69091910"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUxUpdateManagerWin11
    {
        [PreserveSig] int GetUxStateVariableBOOL(int variable, out int pValue, out int pResult);
        [PreserveSig] int GetUxStateVariableDWORD(int variable, out uint pValue, out int pResult);
        [PreserveSig] int GetUxStateVariableSYSTEMTIME(int variable, IntPtr pSystemTime, out int pResult);
        [PreserveSig] int SetUxStateVariableBOOL(int variable, int value);
        [PreserveSig] int SetUxStateVariableDWORD(int variable, uint value);
        [PreserveSig] int SetUxStateVariableSYSTEMTIME(int variable, IntPtr value);
        [PreserveSig] int DeleteUxStateVariable(int variable);
        [PreserveSig] int GetNextScheduledRebootTaskRunTime(IntPtr pSystemTime);
        [PreserveSig] int GetIsRebootTaskScheduledToRun(out int pValue);
        [PreserveSig] int CreateRebootTasks([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr systemTime);
        [PreserveSig] int CreateUpdateResultsTaskSchedule();
        [PreserveSig] int CreateMigrationResultsTaskSchedule();
        [PreserveSig] int CreateUpdateLogonNotificationTaskSchedule();
        [PreserveSig] int CreateUpdateNotificationTaskSchedule(IntPtr systemTime);
        [PreserveSig] int CreateLogonRebootTaskSchedule();
        [PreserveSig] int DidUXRebootTaskWakeUpDevice(out int pValue);
        [PreserveSig] int RemoveUpdateResultsTaskSchedule();
        [PreserveSig] int RemoveMigrationResultsTaskSchedule();
        [PreserveSig] int EnableRebootTasks();
        [PreserveSig] int DisableRebootTasks();
        [PreserveSig] int ValidateAndRecoverRebootTasks();
        [PreserveSig] int RebootToCompleteInstall(uint flags, int a2, out uint a3, int a4, int a5, double a6);
        [PreserveSig] int IsRestartAllowed(uint flags, double a2, int a3, out int pResult);
        [PreserveSig] int GetIsWaaSOutOfDate(uint a1, int a2, int a3, out int pResult, out uint a5);
        [PreserveSig] int GetWaaSHoursOutOfDate(int a1, int a2, out uint pResult);
        [PreserveSig] int GetDeviceEndOfServiceDate(int a1, out int pResult, IntPtr pFileTime);
        [PreserveSig] int GetCachedPolicy(uint a1, IntPtr pVariant, out uint a3, out uint a4);
        [PreserveSig] int GetEnterpriseCachedPolicy(uint a1, IntPtr pVariant, out uint a3, out uint a4);
        [PreserveSig] int GetOptInToMU(out int pValue);
        [PreserveSig] int SetOptInToMU(int value);
        [PreserveSig] int SetAndModifyShutdownFlags(uint flags, out uint newFlags);
        [PreserveSig] int GetIsFlightingEnabled(out int pValue);
        [PreserveSig] int GetIsCTA(out int pValue);
        [PreserveSig] int NotifyStateVariableChange();
        [PreserveSig] int GetAlwaysAllowMeteredNetwork(out int pValue);
        [PreserveSig] int SetInstallAtShutdown(int value);
        [PreserveSig] int GetUxStateVariableValueOrDefaultBOOL(int variable, int def, out int pValue);
        [PreserveSig] int GetUxStateVariableValueOrDefaultDWORD(int variable, uint def, out uint pValue);
        [PreserveSig] int GetUxStateVariableValueOrDefaultSYSTEMTIME(int variable, IntPtr def, IntPtr pSystemTime);
        [PreserveSig] int GetSuggestedRebootTime(int a1, IntPtr systemTime, IntPtr pResult, out int a4);
        [PreserveSig] int GetSuggestedActiveHours(uint a1, out uint a2, out uint a3, out int a4);
        [PreserveSig] int GetIsIntervalAcceptableForActiveHours(IntPtr start, IntPtr end, out int pResult);
        [PreserveSig] int GetSmartScheduledPredictionsAccurate(out int pValue);
        [PreserveSig] int EvaluateAndStoreRebootDowntimePrediction();
        [PreserveSig] int GetCachedRebootDowntimePrediction(out uint pValue);
        [PreserveSig] int GetAlwaysAllowCTADownload(out int pValue);
    }

    public static bool Restart()
    {
        EnsureShutdownPrivilege();
        try
        {
            return ExitWindowsEx(EWX_REBOOT, 0);
        }
        catch
        {
            return false;
        }
    }

    public static bool Sleep()
    {
        try
        {
            return SetSuspendState(false, false, false);
        }
        catch
        {
            return false;
        }
    }

    public static bool Lock()
    {
        try
        {
            return LockWorkStation();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>注销当前用户（回到登录界面）。EWX_LOGOFF = 0。</summary>
    public static bool LogOff()
    {
        EnsureShutdownPrivilege();
        try
        {
            return ExitWindowsEx(0x0, 0);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSDisconnectSession(IntPtr serverHandle, int sessionId, bool wait);

    /// <summary>切换用户：断开当前会话返回登录/账户选择界面（WTS 当前会话）。</summary>
    public static bool SwitchUser()
    {
        try
        {
            return WTSDisconnectSession(IntPtr.Zero, -1, false);
        }
        catch
        {
            return false;
        }
    }
}
