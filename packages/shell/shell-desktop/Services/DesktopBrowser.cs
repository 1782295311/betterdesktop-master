// BetterDesktop.Shell.Desktop — 桌面/文件夹浏览器实现
// 职责：Location 导航（历史栈）+ 条目枚举（后台）+ 选中集 + 文件操作（剪贴板剪切/复制/粘贴、
//       回收站删除 SHFileOperation、重命名）。
// 线程：枚举在后台 Task；所有事件经 SynchronizationContext（UI）回抛——订阅方安全改 WPF 控件。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BetterDesktop.Shell.ContextMenus.Services;
using BetterDesktop.Shell.Desktop.Contracts;

namespace BetterDesktop.Shell.Desktop.Services;

/// <summary>桌面/文件夹浏览器（IDesktopBrowser 实现）。</summary>
public sealed class DesktopBrowser : IDesktopBrowser
{
    /// <summary>枚举结果上限（桌面图标过多时不至于卡死；超出按名称排序截断）。</summary>
    private const int MaxEntries = 500;

    private readonly List<string> _back = new();
    private readonly List<string> _forward = new();
    private readonly List<BrowserEntry> _items = new();
    private readonly HashSet<string> _selection = new(StringComparer.OrdinalIgnoreCase);
    private readonly SynchronizationContext? _sync;

    private string _location;
    // 剪贴板已迁至 FileClipboard（CF_HDROP 系统剪贴板，与资源管理器双向互通）：
    // 不再持有进程内 _clipboardPaths/_clipboardCut 内存态。
    private int _generation;               // 丢弃过期枚举结果
    private bool _busy;

    public DesktopBrowser()
    {
        _sync = SynchronizationContext.Current;
        _location = DesktopPath;
    }

    public string DesktopPath => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public string Location => _location;

    public IReadOnlyList<string> SelectedPaths => _selection.ToList();

    public bool CanPaste => FileClipboard.HasFiles;

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    /// <summary>当前条目快照（UI 网格在 ItemsChanged 后读取）。</summary>
    public IReadOnlyList<BrowserEntry> Items => _items;

    /// <summary>当前选中集快照。</summary>
    public IReadOnlyCollection<string> Selection => _selection;

    public event EventHandler<string>? LocationChanged;

    public event EventHandler? SelectionChanged;

    public event EventHandler? ItemsChanged;

    // ======== 导航 ========

    public void Navigate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(full, _location, StringComparison.OrdinalIgnoreCase)) { Refresh(); return; }

        _back.Add(_location);
        _forward.Clear();
        LoadLocation(full);
    }

    public bool Back()
    {
        if (_back.Count == 0) return false;
        _forward.Add(_location);
        LoadLocation(_back[^1]);
        _back.RemoveAt(_back.Count - 1);
        return true;
    }

    public bool Forward()
    {
        if (_forward.Count == 0) return false;
        _back.Add(_location);
        LoadLocation(_forward[^1]);
        _forward.RemoveAt(_forward.Count - 1);
        return true;
    }

    public bool Up()
    {
        var parent = Directory.GetParent(_location)?.FullName;
        if (parent is null || string.Equals(parent, _location, StringComparison.OrdinalIgnoreCase)) return false;
        Navigate(parent);
        return true;
    }

    public void Refresh() => LoadLocation(_location);

    // ======== shell 命名空间虚拟项（桌面图标） ========

    // explorer 桌面同款虚拟项 CLSID（"::{CLSID}" 解析路径）
    private const string ThisPcClsid = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
    private const string RecycleBinClsid = "::{645FF040-5081-101B-9F08-00AA002F954E}";
    private const string ControlPanelClsid = "::{26EE0668-A00A-44D7-9371-BEB064C98683}";
    private const string NetworkClsid = "::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}";

    private const uint ShgfiPidl = 0x0008;
    private const uint ShgfiDisplayname = 0x0200;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(IntPtr pidl, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    /// <summary>解析 shell 项的本地化显示名（随系统语言，如"此电脑"）；失败用回落名。</summary>
    private static string ResolveShellName(string clsidPath, string fallback)
    {
        try
        {
            if (SHParseDisplayName(clsidPath, IntPtr.Zero, out var pidl, 0, out _) == 0 && pidl != IntPtr.Zero)
            {
                try
                {
                    var info = new SHFILEINFO();
                    if (SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), ShgfiDisplayname | ShgfiPidl) != IntPtr.Zero
                        && !string.IsNullOrWhiteSpace(info.szDisplayName))
                    {
                        return info.szDisplayName;
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pidl);
                }
            }
        }
        catch
        {
            // shell 解析失败（极端环境）用回落名
        }
        return fallback;
    }

    private void LoadLocation(string path)
    {
        if (_busy) return; // 防重入：枚举中忽略新导航（桌面场景足够）
        _busy = true;
        _location = path;
        _selection.Clear();
        var gen = ++_generation;

        LocationChanged?.Invoke(this, path);

        Task.Run(() =>
        {
            var entries = new List<BrowserEntry>();
            try
            {
                // 同名去重（用户桌面优先于公共桌面，与 explorer 桌面合并语义一致）
                var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

                void Collect(string dir)
                {
                    foreach (var d in Directory.EnumerateDirectories(dir))
                    {
                        if (seen.Add(Path.GetFileName(d)))
                        {
                            var info = new FileInfo(d);
                            entries.Add(new BrowserEntry(Path.GetFileName(d), d, true,
                                0, info.LastWriteTime, "文件夹"));
                        }
                    }

                    foreach (var f in Directory.EnumerateFiles(dir))
                    {
                        var name = Path.GetFileName(f);
                        if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        if (seen.Add(name))
                        {
                            var info = new FileInfo(f);
                            var ext = Path.GetExtension(f);
                            var kind = ext.Length > 0 ? ext[1..].ToUpperInvariant() + " 文件" : "文件";
                            entries.Add(new BrowserEntry(name, f, false,
                                info.Length, info.LastWriteTime, kind));
                        }
                    }
                }

                Collect(path);

                // 桌面语义对齐 explorer：桌面 = 用户桌面 ∪ 公共桌面
                //（安装程序创建的快捷方式大多落在公共桌面，只枚举用户桌面会"缺图标"）
                if (string.Equals(path, DesktopPath, StringComparison.OrdinalIgnoreCase))
                {
                    var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                    if (!string.IsNullOrEmpty(common) && !string.Equals(common, path, StringComparison.OrdinalIgnoreCase))
                    {
                        Collect(common);
                    }

                    // shell 命名空间虚拟项（此电脑/回收站等）：文件系统枚举天然拿不到（它们不是文件），
                    // explorer 桌面靠 shell 命名空间层显示——这里显式注入，缺了就是"桌面缺少虚拟程序图标"。
                    // 显示名经 shell 解析（随系统语言），解析失败用中文回落名。
                    entries.Add(new BrowserEntry(ResolveShellName(ThisPcClsid, "此电脑"), ThisPcClsid, true));
                    entries.Add(new BrowserEntry(ResolveShellName(RecycleBinClsid, "回收站"), RecycleBinClsid, true));
                    entries.Add(new BrowserEntry(ResolveShellName(ControlPanelClsid, "控制面板"), ControlPanelClsid, true));
                    entries.Add(new BrowserEntry(ResolveShellName(NetworkClsid, "网络"), NetworkClsid, true));
                }

                entries = ApplySort(entries);
            }
            catch
            {
                // 目录不可读（权限/已删除）：返回空列表（M10）
            }

            Post(() =>
            {
                if (gen != _generation) return; // 过期结果丢弃
                _items.Clear();
                _items.AddRange(entries);
                _busy = false;
                ItemsChanged?.Invoke(this, EventArgs.Empty);
            });
        });
    }

    // ======== 选中 ========

    public void SetSelection(IEnumerable<string> paths)
    {
        _selection.Clear();
        foreach (var p in paths)
        {
            _selection.Add(p);
        }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleSelection(string path)
    {
        if (!_selection.Remove(path))
        {
            _selection.Add(path);
        }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ======== 剪贴板 / 文件操作 ========

    public void Cut()
    {
        if (_selection.Count == 0) return;
        FileClipboard.SetFiles(_selection.ToList(), cut: true);
    }

    public void Copy()
    {
        if (_selection.Count == 0) return;
        FileClipboard.SetFiles(_selection.ToList(), cut: false);
    }

    public void Paste()
    {
        // 系统剪贴板（CF_HDROP）：资源管理器复制的文件同样可粘贴进来，反之亦然。
        if (!FileClipboard.TryGetFiles(out var sources, out var cut)) return;
        if (!Directory.Exists(_location)) return;

        Task.Run(() =>
        {
            // 走 SHFileOperation：系统进度框 + 长路径 + 可撤销（FOF_ALLOWUNDO 视操作而定）
            var ok = cut
                ? FileClipboard.Move(sources, _location)
                : FileClipboard.Copy(sources, _location);
            if (ok && cut)
            {
                FileClipboard.Clear(); // 剪切是一次性语义
            }
            Post(Refresh);
        });
    }

    public void Rename(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || _selection.Count != 1) return;
        var src = _selection.First();
        try
        {
            var dest = Path.Combine(Path.GetDirectoryName(src)!, newName);
            if (!UniquePath(dest, out _)) return;
            if (Directory.Exists(src)) Directory.Move(src, dest);
            else File.Move(src, dest);
            Post(Refresh);
        }
        catch
        {
            // 重命名失败静默（M10）
        }
    }

    public void Delete()
    {
        if (_selection.Count == 0) return;
        var targets = _selection.ToList();
        _selection.Clear();
        Post(() => SelectionChanged?.Invoke(this, EventArgs.Empty));

        Task.Run(() =>
        {
            FileClipboard.DeleteToRecycleBin(targets);
            Post(Refresh);
        });
    }

    /// <summary>永久删除（Shift 扩展项；不可恢复，走 SHFileOperation 不带 FOF_ALLOWUNDO）。</summary>
    public void DeletePermanent()
    {
        if (_selection.Count == 0) return;
        var targets = _selection.ToList();
        _selection.Clear();
        Post(() => SelectionChanged?.Invoke(this, EventArgs.Empty));

        Task.Run(() =>
        {
            FileClipboard.DeletePermanent(targets);
            Post(Refresh);
        });
    }

    // ======== 排序（desktop.sortKey：null=智能默认 / name / size / type / modified） ========

    /// <summary>当前排序键（持久化由设置层负责，Browser 只执行）。</summary>
    public string? SortKey { get; private set; }

    /// <summary>设置排序键并重载（null 恢复智能默认：虚拟项→文件夹→文件按名）。</summary>
    public void SetSort(string? key)
    {
        var normalized = key?.ToLowerInvariant() switch
        {
            "name" or "size" or "type" or "modified" => key.ToLowerInvariant(),
            _ => null,
        };
        if (string.Equals(SortKey, normalized, StringComparison.Ordinal)) return;
        SortKey = normalized;
        Refresh();
    }

    private List<BrowserEntry> ApplySort(List<BrowserEntry> entries)
    {
        // 虚拟项恒排最前（explorer 桌面惯例，任何排序键下不变）
        IOrderedEnumerable<BrowserEntry> ordered = SortKey switch
        {
            "size" => entries.OrderBy(e => !e.IsDirectory) // 文件夹在前，再按大小降序
                             .ThenByDescending(e => e.Size)
                             .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
            "type" => entries.OrderBy(e => !e.IsDirectory)
                             .ThenBy(e => e.Kind, StringComparer.CurrentCulture)
                             .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
            "modified" => entries.OrderBy(e => !e.IsDirectory)
                                 .ThenByDescending(e => e.Modified)
                                 .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => entries.OrderBy(e => !e.IsShellNamespace)  // 默认：虚拟项→文件夹→文件按名
                        .ThenBy(e => !e.IsDirectory)
                        .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        return ordered
            .ThenBy(e => !e.IsShellNamespace) // 非默认排序时虚拟项仍置顶
            .Take(MaxEntries)
            .ToList();
    }

    public void NewFolder()
    {
        Task.Run(() =>
        {
            try
            {
                var name = "新建文件夹";
                var dir = Path.Combine(_location, name);
                for (var i = 2; Directory.Exists(dir); i++)
                {
                    dir = Path.Combine(_location, $"{name} ({i})");
                }

                Directory.CreateDirectory(dir);
            }
            catch
            {
                // 新建失败静默（M10）
            }

            Post(Refresh);
        });
    }

    /// <summary>新建文本文档（explorer 同款重名自增："新建文本文档.txt"、"新建文本文档 (2).txt"）。</summary>
    public void CreateTextFile()
    {
        Task.Run(() =>
        {
            try
            {
                var name = "新建文本文档.txt";
                var file = Path.Combine(_location, name);
                var stem = Path.GetFileNameWithoutExtension(name);
                for (var i = 2; File.Exists(file); i++)
                {
                    file = Path.Combine(_location, $"{stem} ({i}).txt");
                }

                File.WriteAllText(file, string.Empty);
            }
            catch
            {
                // 新建失败静默（M10）
            }

            Post(Refresh);
        });
    }

    public void ImportFiles(IEnumerable<string> paths, bool move)
    {
        var sources = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (sources.Count == 0) return;

        Task.Run(() =>
        {
            foreach (var src in sources)
            {
                try
                {
                    var dest = Path.Combine(_location, Path.GetFileName(src));
                    if (!UniquePath(dest, out var unique)) continue;
                    dest = unique;
                    if (move)
                    {
                        if (Directory.Exists(src)) Directory.Move(src, dest);
                        else File.Move(src, dest);
                    }
                    else
                    {
                        if (Directory.Exists(src)) CopyDirectory(src, dest);
                        else File.Copy(src, dest, overwrite: false);
                    }
                }
                catch
                {
                    // 单项失败不影响其余（M10）
                }
            }

            Post(Refresh);
        });
    }

    /// <summary>目标是否可写入（不存在才可）；存在时生成 "name - 副本(2)" 类唯一名。</summary>
    private static bool UniquePath(string dest, out string unique)
    {
        unique = dest;
        if (!File.Exists(dest) && !Directory.Exists(dest)) return true;
        var dir = Path.GetDirectoryName(dest) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(dest);
        var ext = Path.GetExtension(dest);
        for (var i = 2; i < 100; i++)
        {
            var candidate = Path.Combine(dir, $"{name} - 副本({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                unique = candidate;
                return true;
            }
        }
        return false;
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(src))
        {
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: false);
        }
        foreach (var d in Directory.EnumerateDirectories(src))
        {
            CopyDirectory(d, Path.Combine(dest, Path.GetFileName(d)));
        }
    }

    private void Post(Action action)
    {
        if (_sync is not null) _sync.Post(_ => action(), null);
        else action();
    }
}

/// <summary>文件操作 P/Invoke 收口（回收站删除）。</summary>
internal static class FileOps
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_SILENT = 0x0004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct lpFileOp);

    /// <summary>删除文件/目录到回收站（带系统确认对话框；失败静默 M10）。</summary>
    public static void DeleteToRecycleBin(string path)
    {
        try
        {
            var op = new ShFileOpStruct
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0", // 双 NUL 结尾（多路径分隔）
                fFlags = FOF_ALLOWUNDO | FOF_SILENT
            };
            _ = SHFileOperation(ref op);
        }
        catch
        {
            // 删除失败静默（M10）
        }
    }
}
