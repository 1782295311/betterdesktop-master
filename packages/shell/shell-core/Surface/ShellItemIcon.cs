// BetterDesktop.Shell.Core — shell 命名空间项（"::{CLSID}" / `shell:xxx` / 普通路径）→ 主题图标
//
// 【为什么收口到这里 · 2026-09-14】同一段 40 行实现原先有两份拷贝：
//   · shell-dock/DockWindow.xaml.cs  `GetShellIconByClsid`（自建 SHFILEINFO + 自建 P/Invoke）
//   · shell-desktop/Services/ShellNamespaceHelper.cs `GetIcon`（自建 SHFILEINFO）
// 两者标志位、调用链、失败语义完全一致，只是前者不 Freeze、后者 Freeze。
// 本类是唯一实现：NativeMethods（同包）已提供 SHFILEINFO / SHGetFileInfo / SHParseDisplayName / DestroyIcon。

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.Core.Surface;

/// <summary>
/// shell 命名空间项的**主题图标**提取：<c>SHParseDisplayName → SHGetFileInfo(PIDL)</c>，
/// 与桌面/资源管理器中的图标样式一致（含当前主题）。
/// <para>
/// 适用：此电脑/网络/回收站/控制面板等 <c>::{CLSID}</c> 虚拟项、<c>shell:AppsFolder\…</c>、
/// exe 绝对路径（Taskmgr.exe 等）。文件系统普通文件用 <c>Win32ShellIconService</c>（app-source）即可。
/// </para>
/// <para>失败返回 null，由调用方回退 stock 图标 / glyph；PIDL 与 HICON 全部在 finally 释放。</para>
/// </summary>
public static class ShellItemIcon
{
    private const uint ShgfiIcon = 0x00000100;
    private const uint ShgfiLargeIcon = 0x00000000;
    private const uint ShgfiPidl = 0x00000008;

    /// <summary>按 shell 命名空间路径取图标；失败返回 null（不抛）。</summary>
    public static ImageSource? GetByParsingName(string? parsingName)
    {
        if (string.IsNullOrWhiteSpace(parsingName))
        {
            return null;
        }

        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (NativeMethods.SHParseDisplayName(parsingName, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
            {
                return null;
            }

            var info = new NativeMethods.SHFILEINFO();
            uint cbSize = (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>();
            if (NativeMethods.SHGetFileInfo(pidl, 0, ref info, cbSize, ShgfiIcon | ShgfiLargeIcon | ShgfiPidl) == IntPtr.Zero
                || info.hIcon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                // CreateBitmapSourceFromHIcon 复制像素，随后可安全 DestroyIcon；Freeze 后跨线程可用。
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                _ = NativeMethods.DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            // P/Invoke 失败不冒泡（M10）：图标是非关键资源，取不到由调用方兜底。
            return null;
        }
        finally
        {
            if (pidl != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
    }
}
