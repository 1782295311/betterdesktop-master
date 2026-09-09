using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using BetterDesktop.Shell.AppSource.Models;

namespace BetterDesktop.Shell.AppSource.Services;

/// <summary>
/// 快捷方式解析工具（LNK/URL/EXE/AppRef）。
/// 仅做最小解析，不引入第三方依赖。
/// </summary>
public static class ShellLinkResolver
{
    // 可执行文件扩展名白名单
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",
        ".bat",
        ".cmd",
        ".com",
        ".msc",
        ".appref-ms",
        ".url",
        ".lnk"
    };

    /// <summary>
    /// 解析快捷方式/可执行文件，返回 (显示名, 目标路径, 来源类型)。
    /// 解析失败时回退到原始文件信息。
    /// </summary>
    public static (string DisplayName, string TargetPath, BetterDesktop.Shell.AppSource.Models.AppSource Source) Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ("未知应用", path ?? string.Empty, BetterDesktop.Shell.AppSource.Models.AppSource.StartMenu);
        }

        var extension = Path.GetExtension(path);

        if (string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveLnk(path);
        }

        if (string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveUrl(path);
        }

        if (string.Equals(extension, ".appref-ms", StringComparison.OrdinalIgnoreCase))
        {
            return (Path.GetFileNameWithoutExtension(path), path, BetterDesktop.Shell.AppSource.Models.AppSource.Store);
        }

        // EXE/BAT/CMD/COM/MSC 或其它可执行类型
        var displayName = GetFileDisplayName(path);
        return (displayName, path, BetterDesktop.Shell.AppSource.Models.AppSource.Installed);
    }

    /// <summary>
    /// 判断是否为支持的可执行/快捷方式文件。
    /// </summary>
    public static bool IsSupportedFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return !string.IsNullOrEmpty(extension) && ExecutableExtensions.Contains(extension);
    }

    private static (string DisplayName, string TargetPath, BetterDesktop.Shell.AppSource.Models.AppSource Source) ResolveLnk(string path)
    {
        var link = (IShellLink)new CShellLink();
        try
        {
            try
            {
                ((IPersistFile)link).Load(path, 0);

                var buffer = new char[1024];
                var targetBuilder = new System.Text.StringBuilder(1024);

                // 取目标路径
                link.GetPath(targetBuilder, buffer.Length, IntPtr.Zero, 0);
                var targetPath = targetBuilder.ToString();

                // 取显示名（优先用友好名称）
                link.GetDescription(targetBuilder, buffer.Length);
                var description = targetBuilder.ToString();

                var displayName = string.IsNullOrWhiteSpace(description)
                    ? Path.GetFileNameWithoutExtension(path)
                    : description;

                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    targetPath = path;
                }

                return (displayName, targetPath, BetterDesktop.Shell.AppSource.Models.AppSource.StartMenu);
            }
            catch
            {
                // LNK 解析失败时回退到文件名
                return (Path.GetFileNameWithoutExtension(path), path, BetterDesktop.Shell.AppSource.Models.AppSource.StartMenu);
            }
        }
        finally
        {
            // F9/O1（7437 纪律 3）：CShellLink RCW 确定性释放，不依赖 GC。
            _ = Marshal.ReleaseComObject(link);
        }
    }

    private static (string DisplayName, string TargetPath, BetterDesktop.Shell.AppSource.Models.AppSource Source) ResolveUrl(string path)
    {
        try
        {
            // .url 文件是 INI 格式，URL= 行为目标
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    var url = line[4..].Trim();
                    var displayName = Path.GetFileNameWithoutExtension(path);
                    return (displayName, url, BetterDesktop.Shell.AppSource.Models.AppSource.StartMenu);
                }
            }
        }
        catch
        {
            // 读取失败时回退
        }

        return (Path.GetFileNameWithoutExtension(path), path, BetterDesktop.Shell.AppSource.Models.AppSource.StartMenu);
    }

    private static string GetFileDisplayName(string path)
    {
        try
        {
            var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(versionInfo.FileDescription))
            {
                return versionInfo.FileDescription;
            }
        }
        catch
        {
            // 取版本信息失败时回退到文件名
        }

        return Path.GetFileNameWithoutExtension(path);
    }

    // COM 接口声明（最小集合）
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
