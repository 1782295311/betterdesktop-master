// BetterDesktop.Shell.ContextMenus — 第三方工具目录（检测共享层）
// 排除法模型（用户 2026-09-03 定版）：检测到的第三方工具**默认全开**进右键菜单；
// 用户在设置里关闭 → 记入排除名单（context-menu.tools.disabled），不再渲染。
// 检测结果进程级缓存 60s（右键高频触发，注册表枚举+File.Exists 不能每次跑）。
// 消费方：UserMenuContributor（菜单渲染）+ ContextMenuSection（设置开关面板）。

using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>第三方工具目录（检测 + 分类；排除名单由消费方按 settings 裁决）。</summary>
public static class ToolCatalog
{
    private const int TtlMs = 60_000;

    private static List<(string Name, string Path, string Args, string Cat)>? _cache;
    private static DateTime _cachedAt;

    /// <summary>排除名单设置键（List&lt;string&gt; = 规范化路径）。</summary>
    public const string DisabledKey = "context-menu.tools.disabled";

    /// <summary>检测本机第三方工具（带 60s 缓存）。全部带功能分类，按路径去重，上限 60。</summary>
    public static IReadOnlyList<(string Name, string Path, string Args, string Cat)> Detect()
    {
        if (_cache is not null && (DateTime.UtcNow - _cachedAt).TotalMilliseconds < TtlMs)
        {
            return _cache;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(string Name, string Path, string Args, string Cat)>();

        // ① 精选候选（路径直查）
        (string Name, string[] Paths, string Args, string Cat)[] candidates =
        [
            ("VS Code", new[] { "%LOCALAPPDATA%\\Programs\\Microsoft VS Code\\Code.exe", "C:\\Program Files\\Microsoft VS Code\\Code.exe" }, "\"%file%\"", "编辑器"),
            ("7-Zip", new[] { "C:\\Program Files\\7-Zip\\7zFM.exe", "C:\\Program Files (x86)\\7-Zip\\7zFM.exe" }, "\"%file%\"", "压缩"),
            ("WinRAR", new[] { "C:\\Program Files\\WinRAR\\WinRAR.exe", "C:\\Program Files (x86)\\WinRAR\\WinRAR.exe" }, "\"%file%\"", "压缩"),
            ("Bandizip", new[] { "C:\\Program Files\\Bandizip\\Bandizip.exe", "C:\\Program Files (x86)\\Bandizip\\Bandizip.exe" }, "\"%file%\"", "压缩"),
            ("360 压缩", new[] { "C:\\Program Files\\360\\360zip\\360zip.exe", "C:\\Program Files (x86)\\360\\360zip\\360zip.exe" }, "\"%file%\"", "压缩"),
            ("2345 好压", new[] { "C:\\Program Files\\2345Soft\\HaoZip\\HaoZip.exe", "C:\\Program Files (x86)\\2345Soft\\HaoZip\\HaoZip.exe" }, "\"%file%\"", "压缩"),
            ("PeaZip", new[] { "C:\\Program Files\\PeaZip\\peazip.exe" }, "\"%file%\"", "压缩"),
            ("Notepad++", new[] { "C:\\Program Files\\Notepad++\\notepad++.exe", "C:\\Program Files (x86)\\Notepad++\\notepad++.exe" }, "\"%file%\"", "编辑器"),
            ("Windows Terminal", new[] { "%LOCALAPPDATA%\\Microsoft\\WindowsApps\\wt.exe" }, "\"%dir%\"", "终端"),
            ("PowerShell 7", new[] { "%ProgramFiles%\\PowerShell\\7\\pwsh.exe" }, "\"%dir%\"", "终端"),
            ("Git Bash", new[] { "%ProgramFiles%\\Git\\git-bash.exe" }, "\"%dir%\"", "终端"),
            ("PotPlayer", new[] { "C:\\Program Files\\PotPlayer\\PotPlayerMini64.exe", "C:\\Program Files (x86)\\PotPlayer\\PotPlayerMini64.exe" }, "\"%file%\"", "播放器"),
            ("VLC", new[] { "C:\\Program Files\\VideoLAN\\VLC\\vlc.exe" }, "\"%file%\"", "播放器"),
            ("IrfanView", new[] { "C:\\Program Files\\IrfanView\\i_view64.exe", "C:\\Program Files (x86)\\IrfanView\\i_view64.exe" }, "\"%file%\"", "图像"),
            ("Honeyview", new[] { "C:\\Program Files\\Bandizip\\Honeyview.exe", "C:\\Program Files (x86)\\Honeyview\\Honeyview.exe" }, "\"%file%\"", "图像"),
        ];

        foreach (var (name, paths, args, cat) in candidates)
        {
            foreach (var raw in paths)
            {
                var resolved = Environment.ExpandEnvironmentVariables(raw);
                if (File.Exists(resolved) && seen.Add(NormalizePath(resolved)))
                {
                    list.Add((name, resolved, args, cat));
                    break;
                }
            }
        }

        // ② App Paths 全量枚举（覆盖所有正规安装的软件）
        foreach (var (name, path, exe) in EnumerateAppPaths())
        {
            if (seen.Add(NormalizePath(path)))
            {
                list.Add((name, path, "\"%file%\"", CategoryFor(exe)));
                if (list.Count >= 60)
                {
                    break; // 防爆表
                }
            }
        }

        _cache = list;
        _cachedAt = DateTime.UtcNow;
        return list;
    }

    public static string NormalizePath(string path)
        => path.Replace("/", "\\").Trim().TrimEnd('\\').ToLowerInvariant();

    /// <summary>常见 exe → 功能分类（App Paths 枚举项用；未命中归"工具"）。</summary>
    private static string CategoryFor(string exe) => exe.ToLowerInvariant() switch
    {
        "winword" or "excel" or "powerpnt" or "wps" or "et" or "wpp" or "wpspdf" or "onenote" => "办公",
        "code" or "subl" or "notepad++" or "notepad3" or "gvim" or "typora" => "编辑器",
        "7zfm" or "winrar" or "bandizip" or "360zip" or "peazip" => "压缩",
        "potplayer" or "vlc" or "mpv" or "wmplayer" or "quicktime" => "播放器",
        "photoshop" or "illustrator" or "xnview" => "图像",
        "wt" or "pwsh" or "git-bash" => "终端",
        "devenv" or "idea64" or "pycharm64" or "webstorm64" or "goland64" or "clion64"
            or "rider64" or "datagrip64" or "navicat" or "postman" or "docker" => "开发",
        _ => "工具",
    };

    /// <summary>常见 exe → 友好名（App Paths 枚举的显示名映射；未命中回退 exe 名）。</summary>
    private static readonly Dictionary<string, string> FriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["winword"] = "Word", ["excel"] = "Excel", ["powerpnt"] = "PowerPoint",
        ["onenote"] = "OneNote", ["msaccess"] = "Access", ["outlook"] = "Outlook",
        ["photoshop"] = "Photoshop", ["illustrator"] = "Illustrator", ["premiere"] = "Premiere",
        ["afterfx"] = "After Effects", ["audition"] = "Audition",
        ["code"] = "VS Code", ["subl"] = "Sublime Text", ["notepad3"] = "Notepad3",
        ["devenv"] = "Visual Studio", ["idea64"] = "IntelliJ IDEA", ["pycharm64"] = "PyCharm",
        ["webstorm64"] = "WebStorm", ["goland64"] = "GoLand", ["clion64"] = "CLion",
        ["rider64"] = "Rider", ["datagrip64"] = "DataGrip",
        ["py64"] = "PyCharm", ["typora"] = "Typora", ["gvim"] = "gVim",
        ["wps"] = "WPS 文字", ["et"] = "WPS 表格", ["wpp"] = "WPS 演示", ["wpspdf"] = "WPS PDF",
        ["potplayer"] = "PotPlayer", ["vlc"] = "VLC", ["mpv"] = "mpv",
        ["wmplayer"] = "Windows Media Player", ["quicktime"] = "QuickTime",
        ["7zfm"] = "7-Zip", ["winrar"] = "WinRAR", ["bandizip"] = "Bandizip", ["360zip"] = "360 压缩",
        ["thunder"] = "迅雷", ["idman"] = "IDM", ["baidunetdisk"] = "百度网盘",
        ["wt"] = "Windows Terminal", ["pwsh"] = "PowerShell 7", ["git-bash"] = "Git Bash",
        ["kugou"] = "酷狗音乐", ["cloudmusic"] = "网易云音乐", ["qqmusic"] = "QQ音乐",
        ["snipaste"] = "Snipaste", ["pixpin"] = "PixPin", ["everything"] = "Everything",
        ["xnview"] = "XnView", ["xshell"] = "Xshell", ["finalshell"] = "FinalShell",
        ["navicat"] = "Navicat", ["postman"] = "Postman", ["docker"] = "Docker Desktop",
        ["vmware"] = "VMware", ["virtualbox"] = "VirtualBox",
    };

    /// <summary>枚举 HKLM+HKCU 的 App Paths（已安装软件的正规注册入口）。收集后整体返回（迭代器禁 try-catch 内 yield）。</summary>
    private static List<(string Name, string Path, string Exe)> EnumerateAppPaths()
    {
        var result = new List<(string Name, string Path, string Exe)>();
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "betterdesktop.host", "setup", "uninstall", "uninst",
        };

        foreach (var root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            try
            {
                using var key = root.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths");
                if (key?.GetSubKeyNames() is not { Length: > 0 } names)
                {
                    continue;
                }

                foreach (var subName in names)
                {
                    if (!subName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var exe = Path.GetFileNameWithoutExtension(subName);
                    if (skip.Any(s => exe.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    try
                    {
                        using var sub = key.OpenSubKey(subName);
                        if (sub?.GetValue(null) is string value && value.Length > 0)
                        {
                            var resolved = Environment.ExpandEnvironmentVariables(value.Trim('"'));
                            if (File.Exists(resolved))
                            {
                                var name = FriendlyNames.TryGetValue(exe, out var friendly) ? friendly : exe;
                                result.Add((name, resolved, exe));
                            }
                        }
                    }
                    catch
                    {
                        // 单项失败跳过（M10）
                    }
                }
            }
            catch
            {
                // 枚举失败按无该根处理（M10）
            }
        }

        return result;
    }
}
