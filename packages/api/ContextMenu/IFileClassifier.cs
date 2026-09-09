namespace BetterDesktop.Shell.ContextMenus.Contracts;

/// <summary>文件类别（FileClassifier 输出；驱动菜单项能力过滤）。</summary>
public enum FileKind
{
    /// <summary>未分类（非文件目标）。</summary>
    None,

    Folder,
    Drive,
    File,
    Shortcut,

    Executable,
    WordDocument,
    ExcelWorkbook,
    Presentation,
    Archive,
    Image,
    Video,
    Audio,
    Document,
    SourceCode,
    Config,
    SystemFile,

    /// <summary>回收站内对象。</summary>
    InRecycleBin,

    /// <summary>Shell 命名空间虚拟项（::{CLSID}），不进分类器。</summary>
    ShellNamespace,

    /// <summary>未知/无关联（定版菜单集：打开方式…/复制/发送到/属性）。</summary>
    Unknown,
}

/// <summary>文件能力位（菜单项 RequiredCapability 的判定依据；能力不满足 → 隐藏）。</summary>
[Flags]
public enum FileCapabilities
{
    None = 0,
    Open = 1 << 0,
    OpenInNewWindow = 1 << 1,
    RunAsAdmin = 1 << 2,
    OpenFileLocation = 1 << 3,
    Edit = 1 << 4,
    Print = 1 << 5,
    Preview = 1 << 6,
    Copy = 1 << 7,
    Cut = 1 << 8,
    PasteInto = 1 << 9,
    Delete = 1 << 10,
    Rename = 1 << 11,
    Properties = 1 << 12,
    Share = 1 << 13,
    Extract = 1 << 14,
    SetAsWallpaper = 1 << 15,
    OpenWith = 1 << 16,
    Restore = 1 << 17,
    Browse = 1 << 18,
    PinToDock = 1 << 19,

    /// <summary>可转 PDF（分类命中文档类 且 转换引擎可用；shell-convert 贡献项判定）。</summary>
    ConvertToPdf = 1 << 20,
}

/// <summary>文件身份（一次右键分类结果）。</summary>
/// <param name="Kind">文件类别。</param>
/// <param name="Caps">能力位。</param>
/// <param name="IsReadOnly">只读（删除/重命名置灰并带说明）。</param>
/// <param name="IsHidden">隐藏属性。</param>
/// <param name="Path">目标路径（虚拟项为 ::{CLSID} 串）。</param>
public sealed record FileIdentity(
    FileKind Kind,
    FileCapabilities Caps,
    bool IsReadOnly,
    bool IsHidden,
    string Path);

/// <summary>文件属性精准识别（右键菜单能力过滤的唯一判定源）。</summary>
public interface IFileClassifier
{
    /// <summary>分类单个目标（UI 线程外调用安全；内部只做注册表只读 + 文件属性读取）。</summary>
    FileIdentity Classify(string path);

    /// <summary>分类多选集合：返回交集能力（单文件专属项自动隐藏）。</summary>
    FileIdentity ClassifyMany(IReadOnlyList<string> paths);
}
