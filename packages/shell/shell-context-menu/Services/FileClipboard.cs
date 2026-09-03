// BetterDesktop.Shell.ContextMenus — 文件剪贴板（CF_HDROP）+ 系统文件操作
// 目标：把浏览器（DesktopBrowser/FolderBrowser）的"内存态剪贴板"升级为 **Windows 标准
// CF_HDROP**，从而与资源管理器双向互通（我们复制 → 资源管理器可粘贴；反之亦然）。
// 实现：WPF 的 DataFormats.FileDrop 即 CF_HDROP 的托管封装（拖拽已在用同一通道，实证互通）；
//       剪切/复制语义用 CF_PREFERREDDROPEFFECT（"Preferred DropEffect"，2=移动 / 5=复制）。
// 文件操作统一走 SHFileOperation：系统进度框 + 可撤销（FOF_ALLOWUNDO）+ 长路径友好。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>文件剪贴板 + SHFileOperation 收口（全场景共享）。</summary>
public static class FileClipboard
{
    private const string DropEffectFormat = "Preferred DropEffect";
    private const int DropEffectCopy = 5;
    private const int DropEffectMove = 2;

    /// <summary>把文件放到系统剪贴板（cut=true → 移动语义，粘贴后源被移走）。</summary>
    public static void SetFiles(IReadOnlyList<string> paths, bool cut)
    {
        if (paths.Count == 0)
        {
            return;
        }

        try
        {
            var data = new System.Windows.DataObject();
            data.SetData(DataFormats.FileDrop, paths.ToArray());
            var effect = cut ? DropEffectMove : DropEffectCopy;
            data.SetData(DropEffectFormat, new MemoryStream(BitConverter.GetBytes(effect)));
            Clipboard.SetDataObject(data, copy: true);
        }
        catch
        {
            // 剪贴板被占用等场景静默（M10）
        }
    }

    /// <summary>读取系统剪贴板中的文件（含剪切语义）。无文件返回 false。</summary>
    public static bool TryGetFiles(out List<string> paths, out bool cut)
    {
        paths = new List<string>();
        cut = false;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null || !data.GetDataPresent(DataFormats.FileDrop))
            {
                return false;
            }

            if (data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            {
                return false;
            }

            paths.AddRange(files.Where(f => !string.IsNullOrWhiteSpace(f)));
            cut = ReadDropEffect(data) == DropEffectMove;
            return paths.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>剪贴板中是否有可粘贴的文件。</summary>
    public static bool HasFiles => TryGetFiles(out _, out _);

    public static void Clear()
    {
        try
        {
            Clipboard.Clear();
        }
        catch
        {
            // 静默（M10）
        }
    }

    private static int ReadDropEffect(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DropEffectFormat) && data.GetData(DropEffectFormat) is Stream stream)
            {
                var buffer = new byte[4];
                if (stream.Read(buffer, 0, 4) == 4)
                {
                    return BitConverter.ToInt32(buffer, 0);
                }
            }
        }
        catch
        {
            // 静默（M10）
        }
        return DropEffectCopy;
    }

    // ======== 系统文件操作（SHFileOperation） ========

    /// <summary>复制到目标目录。</summary>
    public static bool Copy(IReadOnlyList<string> sources, string destination)
        => Run(FoCopy, sources, destination, allowUndo: false);

    /// <summary>移动到目标目录。</summary>
    public static bool Move(IReadOnlyList<string> sources, string destination)
        => Run(FoMove, sources, destination, allowUndo: false);

    /// <summary>删除到回收站（可撤销、带系统确认框）。</summary>
    public static bool DeleteToRecycleBin(IReadOnlyList<string> paths)
        => Run(FoDelete, paths, null, allowUndo: true);

    /// <summary>永久删除（不可恢复，用于 Shift 扩展项）。</summary>
    public static bool DeletePermanent(IReadOnlyList<string> paths)
        => Run(FoDelete, paths, null, allowUndo: false);

    private const uint FoMove = 0x0001;
    private const uint FoCopy = 0x0002;
    private const uint FoDelete = 0x0003;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoConfirmMkDir = 0x0200;

    private static bool Run(uint func, IReadOnlyList<string> sources, string? destination, bool allowUndo)
    {
        if (sources.Count == 0)
        {
            return false;
        }

        try
        {
            var from = string.Join("\0", sources) + "\0\0"; // 双 NUL 结尾
            var to = string.IsNullOrEmpty(destination) ? null : destination + "\0\0";
            var op = new ShFileOpStruct
            {
                wFunc = func,
                pFrom = from,
                pTo = to,
                fFlags = (ushort)((allowUndo ? FofAllowUndo : 0) | FofNoConfirmMkDir),
            };
            var result = SHFileOperation(ref op);
            return result == 0 && !op.fAnyOperationsAborted;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct lpFileOp);
}
