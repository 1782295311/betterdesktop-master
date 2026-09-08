// BetterDesktop.Shell.ContextMenus — 菜单管理列表图标（2026-09-05 小白化：行首图标让用户一眼认出是哪个软件）
//
// 来源分发：静态项 → 命令行解析出 exe/dll 提图标；ShellEx 项 → CLSID\DefaultIcon（回退 InprocServer32）；
// 提取失败 = 不显示图标（绝不占位破行高）。进程级缓存（右键列表高频重渲染，注册表+提取不能每次跑）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterDesktop.Shell.Core.Native;
using Microsoft.Win32;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>菜单项/分组图标提取（进程级缓存；SHGetFileInfo/ExtractIconEx 双通道）。</summary>
public static class MenuItemIconCache
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>取分组行首图标（按组内首项分发来源）。</summary>
    public static ImageSource? GetForGroup(MenuExtensionGroup group)
        => GetForItem(group.Items.Count > 0 ? group.Items[0] : null);

    /// <summary>取单项图标。失败返回 null（调用方不占位）。</summary>
    public static ImageSource? GetForItem(MenuItemInfo? item)
    {
        if (item is null)
        {
            return null;
        }
        var key = item.Kind == "Shellex" ? "clsid:" + item.Clsid : "cmd:" + item.Command;
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
            var source = item.Kind == "Shellex" ? FromClsid(item.Clsid) : FromCommand(item.Command);
            Cache[key] = source;
            return source;
        }
    }

    /// <summary>清缓存（重渲染时机自定；当前会话内图标不常变，无需过期）。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Cache.Clear();
        }
    }

    private static ImageSource? FromCommand(string command)
        => Extract(ParseExeFromCommand(command));

    private static ImageSource? FromClsid(string clsid)
    {
        try
        {
            // DefaultIcon 优先："C:\...\x.dll,-1" / "x.exe,0"
            var di = Registry.GetValue(@"HKEY_CLASSES_ROOT\CLSID\" + clsid + @"\DefaultIcon", null, null) as string;
            if (!string.IsNullOrWhiteSpace(di))
            {
                var (path, index) = SplitDefaultIcon(di);
                var icon = Extract(path, index);
                if (icon is not null)
                {
                    return icon;
                }
            }

            // 回退：InprocServer32 的 dll 首图标
            var dll = Registry.GetValue(@"HKEY_CLASSES_ROOT\CLSID\" + clsid + @"\InprocServer32", null, null) as string;
            return Extract(dll);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>静态 verb 命令行 → 可执行文件路径（带引号取引号内；否则空格分词取首个存在者）。</summary>
    internal static string? ParseExeFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }
        var cmd = Environment.ExpandEnvironmentVariables(command.Trim());
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd[1..end] : null;
        }
        // 未加引号且路径含空格：从最长候选往回找第一个真实存在的文件
        var space = cmd.IndexOf(' ');
        if (space < 0)
        {
            return File.Exists(cmd) ? cmd : null;
        }
        var candidate = cmd;
        while (candidate.Length > 0)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
            var lastSpace = candidate.LastIndexOf(' ');
            if (lastSpace <= 0)
            {
                return null;
            }
            candidate = candidate[..lastSpace];
        }
        return null;
    }

    private static (string Path, int Index) SplitDefaultIcon(string value)
    {
        var v = Environment.ExpandEnvironmentVariables(value.Trim());
        var comma = v.LastIndexOf(',');
        if (comma > 0 && int.TryParse(v[(comma + 1)..].Trim(), out var idx))
        {
            return (v[..comma].Trim('"', ' '), idx);
        }
        return (v.Trim('"', ' '), 0);
    }

    private static ImageSource? Extract(string? path, int index = 0)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }
        try
        {
            // ExtractIconEx 负数 idx = 按资源 id 取；非负 = 按序号。小图标优先（行高 16px）。
            var icons = new IntPtr[1];
            var smalls = new IntPtr[1];
            var wantById = index < 0;
            var n = NativeMethods.ExtractIconEx(path, wantById ? index : index, icons, smalls, 1);
            var hIcon = n > 0 && smalls[0] != IntPtr.Zero ? smalls[0] : (n > 0 ? icons[0] : IntPtr.Zero);
            if (hIcon == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                _ = NativeMethods.DestroyIcon(hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

}
