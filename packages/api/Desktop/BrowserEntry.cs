// BetterDesktop.Shell.Desktop — 浏览器条目模型（契约层，供 UI 网格与浏览器共用）。

using System;

namespace BetterDesktop.Shell.Desktop.Contracts;

/// <summary>浏览器条目（目录或文件）。</summary>
public sealed record BrowserEntry(
    string Name,
    string Path,
    bool IsDirectory,
    long Size = 0,
    DateTime Modified = default,
    string Kind = "")
{
    /// <summary>
    /// 显示名：对快捷方式（.lnk）剥去扩展名，对齐 explorer 桌面惯例（如 "Chrome" 而非 "Chrome.lnk"）。
    /// 目录与非 .lnk 文件返回原 Name。仅影响显示，<see cref="Path"/> 始终保留完整路径供文件操作。
    /// </summary>
    public string DisplayName =>
        !IsDirectory && Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            ? Name[..^4]
            : Name;

    /// <summary>
    /// shell 命名空间虚拟项（"::{CLSID}" 路径，如此电脑/回收站）：非文件系统对象，
    /// 图标须走 shell PIDL 提取、启动走 explorer.exe ::{CLSID}、文件操作（剪切/删除等）一律不适用。
    /// </summary>
    public bool IsShellNamespace => Path.StartsWith("::", StringComparison.Ordinal);
}
