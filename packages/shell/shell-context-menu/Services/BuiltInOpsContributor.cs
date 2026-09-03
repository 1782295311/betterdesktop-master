// BetterDesktop.Shell.ContextMenus — 内置文件操作贡献者（计划 C7）
// 消费空转能力位：Edit/Print/Preview/Extract/SetAsWallpaper/Restore/Browse/PasteInto
//（+ OQ2 扩展项：永久删除 / 以其他用户身份运行 / 复制到文件夹）。Share 仍 deferred（OQ5）。
// 纪律：§5-2 秒开（Build 只做内存判定 + $I 小文件读取）；§5-4 隐藏优先（能力不满足不生成）；
//       M10 错误静默（DiagnosticLog.Trace）；反假提示（贡献项一律不标 GestureText——键盘未覆盖）。

using System.Diagnostics;
using System.IO;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using Microsoft.Win32;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>内置文件操作贡献项（Scope=DesktopIcon/ShellFile；由 ContextMenuPlugin 注册）。</summary>
public sealed class BuiltInOpsContributor : IContextMenuContributor
{
    /// <summary>ConvertToPdf(-200) 之后、用户自定义项之前。</summary>
    public int Priority => -150;

    public MenuScope Scope { get; }

    public BuiltInOpsContributor(MenuScope scope) => Scope = scope;

    public IReadOnlyList<MenuItemDef> Build(MenuRequest request)
    {
        if (request.File is not { } id)
        {
            return [];
        }

        var paths = request.SelectedPaths is { Count: > 0 } selected ? selected : [id.Path];
        var items = new List<MenuItemDef>();

        // 回收站场景：只提供 还原 + 在资源管理器中显示（explorer 同款轻量集）
        if (id.Kind == FileKind.InRecycleBin)
        {
            // $I 元数据解析成功才显示（格式验证失败的整项降级，不拖累批次——计划 §9）
            if (id.Caps.HasFlag(FileCapabilities.Restore)
                && RecycleRestore.ParseOriginalPath(id.Path) is not null)
            {
                items.Add(new MenuItemDef
                {
                    Id = "builtin.restore", Text = "还原", Group = MenuGroup.Contribution, IconKey = "sys:E894",
                    Command = () => TraceFail("还原", RecycleRestore.Restore(id.Path) is null, id.Path),
                });
            }
            if (id.Caps.HasFlag(FileCapabilities.Browse))
            {
                items.Add(BrowseItem(paths));
            }
            return items;
        }

        var isContainer = id.Kind is FileKind.Folder or FileKind.Drive;

        // 用记事本打开（文件；目录不显示）
        if (!isContainer && File.Exists(id.Path))
        {
            items.Add(new MenuItemDef
            {
                Id = "builtin.notepad", Text = "用记事本打开", Group = MenuGroup.Contribution, IconKey = "sys:E8A5",
                Command = () => RunFile(paths, verb: null, notepad: true),
            });
        }

        // ShellExecute verb 组（编辑/打印/预览）——按能力位自动显隐
        if (id.Caps.HasFlag(FileCapabilities.Edit))
        {
            items.Add(VerbItem("builtin.edit", "编辑", "edit", paths, "sys:E70F"));
        }
        if (id.Caps.HasFlag(FileCapabilities.Print))
        {
            items.Add(VerbItem("builtin.print", "打印", "print", paths, "sys:E7A7"));
        }
        if (id.Caps.HasFlag(FileCapabilities.Preview))
        {
            items.Add(VerbItem("builtin.preview", "预览", "preview", paths, "sys:E8B9"));
        }

        // ZIP 组
        if (id.Caps.HasFlag(FileCapabilities.Extract) && paths.Count == 1 && File.Exists(id.Path))
        {
            var zip = id.Path;
            var dest = Path.Combine(
                Path.GetDirectoryName(zip) ?? ".", Path.GetFileNameWithoutExtension(zip));
            items.Add(new MenuItemDef
            {
                Id = "builtin.extract", Text = "解压到 \"" + Path.GetFileNameWithoutExtension(zip) + "\\\"",
                Group = MenuGroup.Contribution, IconKey = "sys:E8B2",
                Command = () => TraceFail("解压", !ZipOps.Extract(zip, dest), zip),
            });
        }
        if (isContainer || paths.Any(File.Exists))
        {
            // 压缩为「首名.zip」（文件/文件夹/多选打包；explorer 同款命名）
            var first = paths[0];
            var dir = Path.GetDirectoryName(first);
            if (!string.IsNullOrEmpty(dir))
            {
                var zipPath = ZipOps.UniquePath(Path.Combine(
                    dir, Path.GetFileName(first.TrimEnd(Path.DirectorySeparatorChar)) + ".zip"));
                items.Add(new MenuItemDef
                {
                    Id = "builtin.zip",
                    Text = paths.Count > 1
                        ? $"压缩为 {paths.Count} 个项目"
                        : "压缩为 \"" + Path.GetFileName(zipPath) + "\"",
                    Group = MenuGroup.Contribution, IconKey = "sys:E8B2",
                    Command = () => TraceFail("压缩", !ZipOps.Compress(paths, zipPath), first),
                });
            }
        }

        // 设为桌面背景（图片）
        if (id.Caps.HasFlag(FileCapabilities.SetAsWallpaper) && paths.Count == 1)
        {
            items.Add(new MenuItemDef
            {
                Id = "builtin.wallpaper", Text = "设为桌面背景", Group = MenuGroup.Contribution, IconKey = "sys:E730",
                Command = () => TraceFail("设壁纸", !WallpaperOps.Set(id.Path), id.Path),
            });
        }

        // 创建快捷方式（WScript.Shell 动态 COM，失败 Trace 静默并隐藏——OQ4）
        if (ShellShortcut.IsAvailable && paths.Count == 1)
        {
            items.Add(new MenuItemDef
            {
                Id = "builtin.shortcut", Text = "创建快捷方式", Group = MenuGroup.Contribution,
                Command = () => TraceFail("创建快捷方式", ShellShortcut.Create(id.Path) is null, id.Path),
            });
        }

        // 在资源管理器中显示（Browse 位：文件位置定位）
        if (id.Caps.HasFlag(FileCapabilities.Browse) && id.Kind != FileKind.InRecycleBin)
        {
            items.Add(BrowseItem(paths));
        }

        // 粘贴到文件夹（PasteInto 位；剪贴板无文件时隐藏——弹出前的动态判定）
        if (id.Caps.HasFlag(FileCapabilities.PasteInto) && id.Kind == FileKind.Folder && FileClipboard.HasFiles)
        {
            var dest = id.Path;
            items.Add(new MenuItemDef
            {
                Id = "builtin.pasteinto", Text = "粘贴到文件夹", Group = MenuGroup.Contribution,
                Command = () =>
                {
                    if (!FileClipboard.TryGetFiles(out var sources, out var cut))
                    {
                        return;
                    }
                    TraceFail("粘贴", !(cut ? FileClipboard.Move(sources, dest) : FileClipboard.Copy(sources, dest)), dest);
                    if (cut)
                    {
                        FileClipboard.Clear(); // 剪切是一次性语义
                    }
                },
            });
        }

        // ===== Shift 扩展项（OQ2 首批 3 项；Extended 过滤由 MenuService 按 ShiftPressed/设置键执行） =====

        // 永久删除（不带 FOF_ALLOWUNDO，保留系统确认框；多选整集）
        if (id.Caps.HasFlag(FileCapabilities.Delete))
        {
            items.Add(new MenuItemDef
            {
                Id = "builtin.delete-permanent", Text = "永久删除", Group = MenuGroup.Contribution,
                Extended = true, IconKey = "sys:E74D",
                Command = () => TraceFail("永久删除", !FileClipboard.DeletePermanent(paths), id.Path),
            });
        }

        // 以其他用户身份运行（runasuser verb；真机不可用即静默失败——OQ3）
        if (id.Kind is FileKind.Executable or FileKind.Shortcut)
        {
            items.Add(new MenuItemDef
            {
                Id = "builtin.runasuser", Text = "以其他用户身份运行", Group = MenuGroup.Contribution,
                Extended = true, IconKey = "sys:E7EF",
                Command = () => RunFile(paths, verb: "runasuser", notepad: false),
            });
        }

        // 复制到文件夹…（FolderBrowserDialog 选目标 → SHFileOperation 复制，多选整集）
        if (id.Caps.HasFlag(FileCapabilities.Copy))
        {
            items.Add(new MenuItemDef
            {
                Id = "builtin.copyto", Text = "复制到文件夹…", Group = MenuGroup.Contribution,
                Extended = true, IconKey = "sys:E8C8",
                Command = () =>
                {
                    // .NET 8 WPF 新增 OpenFolderDialog（选目录；FolderBrowserDialog 为 WinForms 专属）
                    var dialog = new OpenFolderDialog
                    {
                        Title = "选择复制目标文件夹",
                    };
                    if (dialog.ShowDialog() != true)
                    {
                        return;
                    }
                    TraceFail("复制到文件夹", !FileClipboard.Copy(paths, dialog.FolderName), id.Path);
                },
            });
        }

        return items;
    }

    private static MenuItemDef BrowseItem(IReadOnlyList<string> paths)
    {
        var path = paths[0];
        return new MenuItemDef
        {
            Id = "builtin.browse", Text = "在资源管理器中显示", Group = MenuGroup.Contribution,
            Command = () =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Trace("shell.contextmenu", $"定位失败 {path}: {ex.Message}");
                }
            },
        };
    }

    private static MenuItemDef VerbItem(string id, string text, string verb, IReadOnlyList<string> paths, string icon)
    {
        return new MenuItemDef
        {
            Id = id, Text = text, Group = MenuGroup.Contribution, IconKey = icon,
            Command = () => RunFile(paths, verb, notepad: false),
        };
    }

    /// <summary>ShellExecute 批量执行（verb=null 且 notepad=true → notepad.exe 直开；失败逐项 Trace 静默）。</summary>
    private static void RunFile(IReadOnlyList<string> paths, string? verb, bool notepad)
    {
        foreach (var path in paths)
        {
            try
            {
                var psi = notepad
                    ? new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true }
                    : new ProcessStartInfo(path) { UseShellExecute = true, Verb = verb };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Trace("shell.contextmenu", $"{(verb ?? "notepad")} 失败 {path}: {ex.Message}");
            }
        }
    }

    private static void TraceFail(string op, bool failed, string path)
    {
        if (failed)
        {
            DiagnosticLog.Trace("shell.contextmenu", $"{op} 失败 {path}");
        }
    }
}
