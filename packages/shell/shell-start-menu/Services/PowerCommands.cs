using System;
using System.Runtime.InteropServices;

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

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges, ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private static readonly object _sync = new();
    private static bool _privilegeReady;

    /// <summary>启动时提权 SeShutdownPrivilege（线程安全，仅一次）。</summary>
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
                if (OpenProcessToken(current, TokenAdjustPrivileges | TokenQuery, out var token))
                {
                    try
                    {
                        if (LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid))
                        {
                            var privileges = new TOKEN_PRIVILEGES
                            {
                                PrivilegeCount = 1,
                                Privileges = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SePrivilegeEnabled }
                            };
                            AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
                        }
                    }
                    finally
                    {
                        CloseHandle(token);
                    }
                }
            }
            catch
            {
                // 提权失败不影响后续尝试
            }
            finally
            {
                _privilegeReady = true;
            }
        }
    }

    public static bool Shutdown()
    {
        EnsureShutdownPrivilege();
        try
        {
            return ExitWindowsEx(EWX_SHUTDOWN | EWX_POWEROFF, 0);
        }
        catch
        {
            return false;
        }
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
