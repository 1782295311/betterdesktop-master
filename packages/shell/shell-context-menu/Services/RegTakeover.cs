// BetterDesktop.Shell.ContextMenus — TrustedInstaller 夺权写助手（M1 管理器前置）
// 技术库：72-右键菜单/reg-take-ownership-write（L2，ContextMenuManager 变体 A）。
//
// 【红线遵守】
// 1. 特权顺序：先 SeTakeOwnershipPrivilege，再 SeRestorePrivilege，两者同时持有才能改所有者；
// 2. 所有者 → 当前用户 SID + 自身 FullControl(ContainerInherit)，否则只能改键写不了子树；
// 3. 夺权单向（无法还原 TrustedInstaller 所有者）——仅对确需写的具体分支夺权，禁盲目全树；
// 4. 失败必须可被上层感知：返回 bool + DiagnosticLog（禁止文档记录的空 catch 吞掉）。
// 5. 前置：进程以管理员运行（宿主由用户以管理员启动；非提权时 AdjustTokenPrivileges 失败 → false）。

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Core.Native;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>受保护注册表键的夺权写（SeTakeOwnership + SeRestore；夺权单向，仅限确需写的分支）。</summary>
public static class RegTakeover
{
    /// <summary>开启进程特权（SeTakeOwnership → SeRestore，顺序红线）。返回 false = 非管理员/失败。</summary>
    public static bool EnablePrivileges()
    {
        if (!OpenProcessToken())
        {
            return false;
        }
        return TrySetPrivilege("SeTakeOwnershipPrivilege") && TrySetPrivilege("SeRestorePrivilege");
    }

    /// <summary>夺单键所有权 + 自身 FullControl。regPath 形如 "HKLM\SOFTWARE\Classes\..."。</summary>
    public static bool TakeKeyOwnership(string regPath)
    {
        if (string.IsNullOrWhiteSpace(regPath))
        {
            return false;
        }
        try
        {
            using var key = OpenForOwnership(regPath);
            if (key is null)
            {
                DiagnosticLog.Trace("menu-manager", $"夺权失败（打不开键）: {regPath}");
                return false;
            }

            var user = WindowsIdentity.GetCurrent().User;
            if (user is null)
            {
                return false;
            }

            var security = key.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group);
            security.SetOwner(user);
            key.SetAccessControl(security);

            var dacl = key.GetAccessControl(AccessControlSections.Access);
            dacl.AddAccessRule(new RegistryAccessRule(
                user, RegistryRights.FullControl,
                InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
            key.SetAccessControl(dacl);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("menu-manager", $"夺权失败: {regPath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>递归夺键树（深度上限 8 防误夺整个 Classes 根）。返回成功夺权的键数；-1 = 完全失败。</summary>
    public static int TakeTreeOwnership(string regPath, int maxDepth = 8)
    {
        if (!TakeKeyOwnership(regPath))
        {
            return -1;
        }
        var count = 1;
        try
        {
            var (root, subPath) = SplitPath(regPath);
            using var baseKey = RegistryKey.OpenBaseKey(root, RegistryView.Default);
            count += TakeChildren(baseKey.OpenSubKey(subPath, RegistryKeyPermissionCheck.ReadSubTree), subPath, 1, maxDepth);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("menu-manager", $"树夺权部分失败（已夺 {count}）: {regPath}: {ex.Message}");
        }
        return count;
    }

    private static int TakeChildren(RegistryKey? key, string path, int depth, int maxDepth)
    {
        if (key is null || depth > maxDepth)
        {
            return 0;
        }
        var count = 0;
        foreach (var name in key.GetSubKeyNames())
        {
            var childPath = $"{path}\\{name}";
            if (TakeKeyOwnership(childPath))
            {
                count++;
            }
            try
            {
                using var child = key.OpenSubKey(name, RegistryKeyPermissionCheck.ReadSubTree);
                count += TakeChildren(child, childPath, depth + 1, maxDepth);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Trace("menu-manager", $"子树遍历跳过: {childPath}: {ex.Message}");
            }
        }
        return count;
    }

    /// <summary>解析 "HKLM\SOFTWARE\..." → (Hive, 子路径)。</summary>
    internal static (RegistryHive Root, string SubPath) SplitPath(string regPath)
    {
        var idx = regPath.IndexOf('\\');
        var rootName = idx < 0 ? regPath : regPath[..idx];
        var sub = idx < 0 ? string.Empty : regPath[(idx + 1)..];
        var root = rootName.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKCR" or "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
            "HKU" or "HKEY_USERS" => RegistryHive.Users,
            _ => RegistryHive.CurrentUser,
        };
        return (root, sub);
    }

    private static RegistryKey? OpenForOwnership(string regPath)
    {
        var (root, sub) = SplitPath(regPath);
        using var baseKey = RegistryKey.OpenBaseKey(root, RegistryView.Default);
        return baseKey.OpenSubKey(sub, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.TakeOwnership);
    }

    // ===== advapi32 特权 P/Invoke =====

    private static bool _tokenOpened;
    private static SafeProcessHandle? _tokenHandle;

    private static bool OpenProcessToken()
    {
        if (_tokenOpened)
        {
            return true;
        }
        try
        {
            if (!NativeMethods.OpenProcessToken(
                    NativeMethods.GetCurrentProcess(), 0x0028 /* TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY */, out var handle))
            {
                return false;
            }
            _tokenHandle = handle;
            _tokenOpened = true;
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("menu-manager", $"OpenProcessToken 失败: {ex.Message}");
            return false;
        }
    }

    private static bool TrySetPrivilege(string name)
    {
        if (!OpenProcessToken() || _tokenHandle is null)
        {
            return false;
        }
        try
        {
            var tp = new NativeMethods.TokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges = new NativeMethods.LuidAndAttributes
                {
                    Luid = default,
                    Attributes = 0x00000002 /* SE_PRIVILEGE_ENABLED */
                },
            };
            if (!NativeMethods.LookupPrivilegeValue(null, name, out tp.Privileges.Luid))
            {
                DiagnosticLog.Trace("menu-manager", $"LookupPrivilegeValue 失败: {name}");
                return false;
            }
            if (!NativeMethods.AdjustTokenPrivileges(_tokenHandle.DangerousGetHandle(), false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
            {
                DiagnosticLog.Trace("menu-manager", $"AdjustTokenPrivileges 失败: {name}: {Marshal.GetLastWin32Error()}");
                return false;
            }
            return Marshal.GetLastWin32Error() != 1300 /* ERROR_NOT_ALL_ASSIGNED */;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("menu-manager", $"特权开启异常 {name}: {ex.Message}");
            return false;
        }
    }

}
