// BetterDesktop.Shell.ContextMenus — 创建快捷方式（WScript.Shell 动态 COM，零 NuGet）
// OQ4：动态 COM 可行性需真机验证；失败则该菜单项隐藏（调用方按返回值处理），
//      不阻塞本批次；备选方案（纯二进制 lnk 写入）另立，不混进本批。

using System;
using System.IO;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>快捷方式创建（动态 COM：WScript.Shell）。</summary>
public static class ShellShortcut
{
    /// <summary>WScript.Shell COM 可用（隐藏优先：不可用时贡献项不生成——OQ4 降级路径）。</summary>
    public static bool IsAvailable => Type.GetTypeFromProgID("WScript.Shell", throwOnError: false) is not null;

    /// <summary>
    /// 为目标文件/文件夹创建快捷方式。
    /// linkDirectory 为空 → 与目标同目录；返回生成的 lnk 路径，失败返回 null。
    /// </summary>
    public static string? Create(string targetPath, string? linkDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(targetPath)
            || (!File.Exists(targetPath) && !Directory.Exists(targetPath)))
        {
            return null;
        }

        try
        {
            var dir = string.IsNullOrWhiteSpace(linkDirectory)
                ? Path.GetDirectoryName(targetPath)
                : linkDirectory;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return null;
            }

            var linkPath = ZipOps.UniquePath(
                Path.Combine(dir, Path.GetFileNameWithoutExtension(targetPath) + " - 快捷方式.lnk"));

            var type = Type.GetTypeFromProgID("WScript.Shell", throwOnError: false);
            if (type is null)
            {
                return null;
            }

            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(linkPath);
                shortcut.TargetPath = targetPath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath) ?? string.Empty;
                shortcut.Save();
                return File.Exists(linkPath) ? linkPath : null;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
        catch
        {
            return null; // 动态 COM 不可用 → 调用方隐藏该项（M10）
        }
    }
}
