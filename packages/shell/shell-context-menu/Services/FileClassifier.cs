using Microsoft.Win32;
using System.Collections.Concurrent;
using System.IO;
using BetterDesktop.Shell.AppSource.Services;
using BetterDesktop.Shell.ContextMenus.Contracts;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>
/// 文件属性精准识别（能力过滤唯一判定源）。
/// 判定链（计划 §3 定版）：虚拟项 → 回收站 → 目录/驱动器 → lnk 目标继承 → 扩展名表 →
/// HKCR 关联只读探测 → Unknown。
/// 【红线】对注册表只读，永不写 HKCR；关联探测结果缓存（进程级），HKCU UserChoice 精细判定 M2 换 Assoc API。
/// </summary>
public sealed class FileClassifier : IFileClassifier
{
    /// <summary>已知扩展名 → 类别（命中率高的常用表；未命中走关联探测）。</summary>
    private static readonly Dictionary<string, FileKind> KnownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".exe"] = FileKind.Executable, [".bat"] = FileKind.Executable, [".cmd"] = FileKind.Executable,
        [".com"] = FileKind.Executable, [".msc"] = FileKind.Executable, [".appref-ms"] = FileKind.Executable,

        [".doc"] = FileKind.WordDocument, [".docx"] = FileKind.WordDocument, [".docm"] = FileKind.WordDocument,
        [".rtf"] = FileKind.WordDocument, [".odt"] = FileKind.WordDocument, [".wps"] = FileKind.WordDocument,

        [".xls"] = FileKind.ExcelWorkbook, [".xlsx"] = FileKind.ExcelWorkbook,
        [".xlsm"] = FileKind.ExcelWorkbook, [".csv"] = FileKind.ExcelWorkbook, [".et"] = FileKind.ExcelWorkbook,

        [".ppt"] = FileKind.Presentation, [".pptx"] = FileKind.Presentation,
        [".pps"] = FileKind.Presentation, [".dps"] = FileKind.Presentation,

        [".zip"] = FileKind.Archive, [".rar"] = FileKind.Archive, [".7z"] = FileKind.Archive,
        [".tar"] = FileKind.Archive, [".gz"] = FileKind.Archive, [".cab"] = FileKind.Archive,

        [".png"] = FileKind.Image, [".jpg"] = FileKind.Image, [".jpeg"] = FileKind.Image,
        [".bmp"] = FileKind.Image, [".gif"] = FileKind.Image, [".webp"] = FileKind.Image,
        [".ico"] = FileKind.Image, [".tif"] = FileKind.Image, [".tiff"] = FileKind.Image,

        [".mp4"] = FileKind.Video, [".mkv"] = FileKind.Video, [".avi"] = FileKind.Video,
        [".mov"] = FileKind.Video, [".wmv"] = FileKind.Video, [".flv"] = FileKind.Video, [".webm"] = FileKind.Video,

        [".mp3"] = FileKind.Audio, [".wav"] = FileKind.Audio, [".flac"] = FileKind.Audio,
        [".m4a"] = FileKind.Audio, [".ogg"] = FileKind.Audio, [".ape"] = FileKind.Audio,

        [".cs"] = FileKind.SourceCode, [".js"] = FileKind.SourceCode, [".ts"] = FileKind.SourceCode,
        [".py"] = FileKind.SourceCode, [".java"] = FileKind.SourceCode, [".cpp"] = FileKind.SourceCode,
        [".c"] = FileKind.SourceCode, [".h"] = FileKind.SourceCode, [".go"] = FileKind.SourceCode,
        [".rs"] = FileKind.SourceCode, [".html"] = FileKind.SourceCode, [".css"] = FileKind.SourceCode,
        [".sql"] = FileKind.SourceCode,

        [".ini"] = FileKind.Config, [".json"] = FileKind.Config, [".xml"] = FileKind.Config,
        [".yaml"] = FileKind.Config, [".yml"] = FileKind.Config, [".toml"] = FileKind.Config,
        [".config"] = FileKind.Config, [".cfg"] = FileKind.Config,

        [".txt"] = FileKind.Document, [".log"] = FileKind.Document, [".md"] = FileKind.Document,

        [".dll"] = FileKind.SystemFile, [".sys"] = FileKind.SystemFile, [".msi"] = FileKind.SystemFile,
    };

    private const string ShortcutExt = ".lnk";
    private const string UrlShortcutExt = ".url";

    private const string RecycleBinMarker = "\\$Recycle.Bin\\";

    // 关联探测缓存：ext → HKCR\<ext> 是否存在（进程级；HKCU UserChoice M2 换 Assoc API）
    private readonly ConcurrentDictionary<string, bool> _associationCache = new(StringComparer.OrdinalIgnoreCase);

    public FileIdentity Classify(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new FileIdentity(FileKind.Unknown, FileCapabilities.None, false, false, path ?? string.Empty);

        // 1) Shell 命名空间虚拟项（::{CLSID}）——不进文件分类
        if (path.StartsWith("::", StringComparison.Ordinal))
            return new FileIdentity(FileKind.ShellNamespace, FileCapabilities.Open, false, false, path);

        // 2) 回收站内对象（还原/删除/属性/定位）。不给 Open：对象已删除，"打开"必然失败（审查 P2-3）。
        //    Browse 供 C7「在资源管理器中显示」消费（计划 A4/C7）。
        if (path.Contains(RecycleBinMarker, StringComparison.OrdinalIgnoreCase))
            return new FileIdentity(FileKind.InRecycleBin, FileCapabilities.Restore | FileCapabilities.Delete | FileCapabilities.Properties | FileCapabilities.Browse, false, false, path);

        // 3) 目录 / 驱动器
        if (Directory.Exists(path))
        {
            var (ro, hidden) = ReadAttributes(path);
            var caps = FileCapabilities.Open | FileCapabilities.OpenInNewWindow | FileCapabilities.Copy
                     | FileCapabilities.Cut | FileCapabilities.PasteInto | FileCapabilities.Delete
                     | FileCapabilities.Rename | FileCapabilities.Properties | FileCapabilities.Browse | FileCapabilities.PinToDock;
            var kind = IsDriveRoot(path) ? FileKind.Drive : FileKind.Folder;
            return new FileIdentity(kind, caps, ro, hidden, path);
        }

        // 4) 快捷方式：解析目标并继承能力（目标失效 → 降级 Unknown，计划定版）
        var ext = Path.GetExtension(path);
        if (ext is ShortcutExt or UrlShortcutExt)
            return ClassifyShortcut(path);

        // 5) 文件：只读/隐藏属性
        var (readOnly, isHidden) = ReadAttributes(path);
        var baseCaps = FileCapabilities.Open | FileCapabilities.Copy | FileCapabilities.Cut
                     | FileCapabilities.Delete | FileCapabilities.Rename | FileCapabilities.Properties;

        // 6) 扩展名表命中
        if (ext.Length > 0 && KnownExtensions.TryGetValue(ext, out var kind2))
        {
            var caps = kind2 switch
            {
                FileKind.Executable => baseCaps | FileCapabilities.RunAsAdmin | FileCapabilities.OpenFileLocation,
                FileKind.WordDocument or FileKind.ExcelWorkbook or FileKind.Presentation
                    => baseCaps | FileCapabilities.Edit | FileCapabilities.Print | FileCapabilities.OpenWith,
                FileKind.Archive => baseCaps | FileCapabilities.Extract,
                FileKind.Image => baseCaps | FileCapabilities.Edit | FileCapabilities.Print | FileCapabilities.SetAsWallpaper | FileCapabilities.OpenWith,
                FileKind.Video or FileKind.Audio => baseCaps | FileCapabilities.OpenWith,
                FileKind.Document or FileKind.SourceCode or FileKind.Config
                    => baseCaps | FileCapabilities.Edit | FileCapabilities.Print | FileCapabilities.OpenWith,
                FileKind.SystemFile => baseCaps & ~FileCapabilities.Open, // 系统文件默认不可直接执行
                _ => baseCaps,
            };
            return new FileIdentity(kind2, caps, readOnly, isHidden, path);
        }

        // 7) 无扩展名或未命中表 → HKCR 关联探测（只读）
        if (ext.Length > 0 && HasAssociation(ext))
            return new FileIdentity(FileKind.File, baseCaps | FileCapabilities.OpenWith, readOnly, isHidden, path);

        // 8) 未知格式（定版菜单集：打开方式…/复制/剪切/删除/重命名/发送到/属性）
        var unknownCaps = FileCapabilities.OpenWith | baseCaps;
        return new FileIdentity(FileKind.Unknown, unknownCaps, readOnly, isHidden, path);
    }

    public FileIdentity ClassifyMany(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return new FileIdentity(FileKind.None, FileCapabilities.None, false, false, string.Empty);
        if (paths.Count == 1)
            return Classify(paths[0]);

        var identities = paths.Select(Classify).ToList();
        var caps = identities.Aggregate(identities[0].Caps, (acc, i) => acc & i.Caps);
        var kind = identities.All(i => i.Kind == identities[0].Kind) ? identities[0].Kind : FileKind.File;
        return new FileIdentity(
            kind,
            caps,
            identities.Any(i => i.IsReadOnly),
            identities.Any(i => i.IsHidden),
            paths[0]);
    }

    /// <summary>快捷方式：解析目标 → 目标身份能力 + OpenFileLocation（exe 目标追加 RunAsAdmin）。</summary>
    private FileIdentity ClassifyShortcut(string path)
    {
        var (readOnly, isHidden) = ReadAttributes(path);
        try
        {
            var (_, targetPath, _) = ShellLinkResolver.Resolve(path);
            if (string.IsNullOrEmpty(targetPath))
                return UnknownShortcut(path, readOnly, isHidden);

            var target = File.Exists(targetPath)
                ? Classify(targetPath)
                : new FileIdentity(FileKind.Unknown, FileCapabilities.None, false, false, targetPath);

            if (target.Kind == FileKind.Unknown)
                return UnknownShortcut(path, readOnly, isHidden); // 目标失效降级

            var caps = target.Caps | FileCapabilities.OpenFileLocation;
            if (target.Kind == FileKind.Executable)
                caps |= FileCapabilities.RunAsAdmin;
            // 快捷方式本身可删除/重命名，目标只读不传导
            caps |= FileCapabilities.Copy | FileCapabilities.Cut | FileCapabilities.Delete | FileCapabilities.Rename | FileCapabilities.Properties;
            return new FileIdentity(FileKind.Shortcut, caps, readOnly, isHidden, path);
        }
        catch
        {
            // 解析失败（COM 异常/格式损坏）：按可操作文件降级，不白屏
            return new FileIdentity(FileKind.File,
                FileCapabilities.Open | FileCapabilities.Copy | FileCapabilities.Cut
                | FileCapabilities.Delete | FileCapabilities.Rename | FileCapabilities.Properties,
                readOnly, isHidden, path);
        }
    }

    private static FileIdentity UnknownShortcut(string path, bool readOnly, bool isHidden) =>
        new(FileKind.Unknown,
            FileCapabilities.OpenWith | FileCapabilities.Copy | FileCapabilities.Cut
            | FileCapabilities.Delete | FileCapabilities.Rename | FileCapabilities.Properties,
            readOnly, isHidden, path);

    private (bool ReadOnly, bool Hidden) ReadAttributes(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            return (attr.HasFlag(FileAttributes.ReadOnly), attr.HasFlag(FileAttributes.Hidden));
        }
        catch
        {
            return (false, false);
        }
    }

    private static bool IsDriveRoot(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Length <= 3 && full.EndsWith(Path.DirectorySeparatorChar) && Directory.Exists(full);
    }

    /// <summary>HKCR\&lt;ext&gt; 是否存在（进程级缓存）。只读，异常按无关联处理。</summary>
    private bool HasAssociation(string ext)
    {
        try
        {
            return _associationCache.GetOrAdd(ext, e =>
            {
                using var key = Registry.ClassesRoot.OpenSubKey(e);
                return key is not null;
            });
        }
        catch
        {
            return false;
        }
    }
}
