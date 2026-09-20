// BetterDesktop.Shell.ContextMenus — 第三方工具目录（检测共享层）
// 排除法模型（用户 2026-09-03 定版）：检测到的第三方工具**默认全开**进右键菜单；
// 用户在设置里关闭 → 记入排除名单（context-menu.tools.disabled），不再渲染。
// 检测结果进程级缓存 60s（右键高频触发，注册表枚举+File.Exists 不能每次跑）。
// 消费方：DesktopIconsControl（桌面图标右键：Detect 进程级预热 + MatchOpenWith「打开方式」匹配）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

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
        ["winword"] = "Word",
        ["excel"] = "Excel",
        ["powerpnt"] = "PowerPoint",
        ["onenote"] = "OneNote",
        ["msaccess"] = "Access",
        ["outlook"] = "Outlook",
        ["photoshop"] = "Photoshop",
        ["illustrator"] = "Illustrator",
        ["premiere"] = "Premiere",
        ["afterfx"] = "After Effects",
        ["audition"] = "Audition",
        ["code"] = "VS Code",
        ["subl"] = "Sublime Text",
        ["notepad3"] = "Notepad3",
        ["devenv"] = "Visual Studio",
        ["idea64"] = "IntelliJ IDEA",
        ["pycharm64"] = "PyCharm",
        ["webstorm64"] = "WebStorm",
        ["goland64"] = "GoLand",
        ["clion64"] = "CLion",
        ["rider64"] = "Rider",
        ["datagrip64"] = "DataGrip",
        ["py64"] = "PyCharm",
        ["typora"] = "Typora",
        ["gvim"] = "gVim",
        ["wps"] = "WPS 文字",
        ["et"] = "WPS 表格",
        ["wpp"] = "WPS 演示",
        ["wpspdf"] = "WPS PDF",
        ["potplayer"] = "PotPlayer",
        ["vlc"] = "VLC",
        ["mpv"] = "mpv",
        ["wmplayer"] = "Windows Media Player",
        ["quicktime"] = "QuickTime",
        ["7zfm"] = "7-Zip",
        ["winrar"] = "WinRAR",
        ["bandizip"] = "Bandizip",
        ["360zip"] = "360 压缩",
        ["thunder"] = "迅雷",
        ["idman"] = "IDM",
        ["baidunetdisk"] = "百度网盘",
        ["wt"] = "Windows Terminal",
        ["pwsh"] = "PowerShell 7",
        ["git-bash"] = "Git Bash",
        ["kugou"] = "酷狗音乐",
        ["cloudmusic"] = "网易云音乐",
        ["qqmusic"] = "QQ音乐",
        ["snipaste"] = "Snipaste",
        ["pixpin"] = "PixPin",
        ["everything"] = "Everything",
        ["xnview"] = "XnView",
        ["xshell"] = "Xshell",
        ["finalshell"] = "FinalShell",
        ["navicat"] = "Navicat",
        ["postman"] = "Postman",
        ["docker"] = "Docker Desktop",
        ["vmware"] = "VMware",
        ["virtualbox"] = "VirtualBox",
    };

    // ===== 打开方式候选（2026-09-07 自绘菜单「用 xx 打开」） =====
    // 不触碰系统 Open With 对话框与第三方扩展聚合：候选 = 本机检测到的常用软件按文件类别
    // 过滤（Detect 60s 缓存）+ 系统兜底（画图/记事本/WMP）。消费方：桌面/文件管理器自绘菜单。

    private static readonly Dictionary<string, string> OpenWithExtMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image",
        [".jpg"] = "image",
        [".jpeg"] = "image",
        [".bmp"] = "image",
        [".gif"] = "image",
        [".webp"] = "image",
        [".ico"] = "image",
        [".tif"] = "image",
        [".tiff"] = "image",
        [".mp4"] = "video",
        [".mkv"] = "video",
        [".avi"] = "video",
        [".mov"] = "video",
        [".wmv"] = "video",
        [".flv"] = "video",
        [".webm"] = "video",
        [".mp3"] = "audio",
        [".wav"] = "audio",
        [".flac"] = "audio",
        [".m4a"] = "audio",
        [".ogg"] = "audio",
        [".ape"] = "audio",
        [".zip"] = "archive",
        [".rar"] = "archive",
        [".7z"] = "archive",
        [".tar"] = "archive",
        [".gz"] = "archive",
        [".cab"] = "archive",
        [".txt"] = "text",
        [".md"] = "text",
        [".log"] = "text",
        [".ini"] = "text",
        [".json"] = "text",
        [".xml"] = "text",
        [".yaml"] = "text",
        [".yml"] = "text",
        [".toml"] = "text",
        [".config"] = "text",
        [".cfg"] = "text",
        [".cs"] = "code",
        [".js"] = "code",
        [".ts"] = "code",
        [".py"] = "code",
        [".java"] = "code",
        [".cpp"] = "code",
        [".c"] = "code",
        [".h"] = "code",
        [".go"] = "code",
        [".rs"] = "code",
        [".html"] = "code",
        [".css"] = "code",
        [".sql"] = "code",
        [".doc"] = "office",
        [".docx"] = "office",
        [".docm"] = "office",
        [".rtf"] = "office",
        [".odt"] = "office",
        [".wps"] = "office",
        [".xls"] = "office",
        [".xlsx"] = "office",
        [".xlsm"] = "office",
        [".csv"] = "office",
        [".et"] = "office",
        [".ppt"] = "office",
        [".pptx"] = "office",
        [".pps"] = "office",
        [".dps"] = "office",
    };

    private static readonly Dictionary<string, string[]> OpenWithCatTools = new()
    {
        ["image"] = new[] { "图像" },
        ["video"] = new[] { "播放器" },
        ["audio"] = new[] { "播放器" },
        ["archive"] = new[] { "压缩" },
        ["text"] = new[] { "编辑器" },
        ["code"] = new[] { "编辑器", "开发" },
        ["office"] = new[] { "办公" },
    };

    private static readonly Dictionary<string, string[]> OpenWithSystemFallback = new()
    {
        ["image"] = new[] { "%windir%\\System32\\mspaint.exe" },
        ["video"] = new[] { "%windir%\\System32\\wmplayer.exe" },
        ["audio"] = new[] { "%windir%\\System32\\wmplayer.exe" },
        ["text"] = new[] { "%windir%\\System32\\notepad.exe" },
        ["code"] = new[] { "%windir%\\System32\\notepad.exe" },
    };

    /// <summary>文件路径 → 打开方式类别（无扩展名/未命中 → text 保守给编辑器）。</summary>
    private static string OpenWithCategoryFor(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return ext.Length > 0 && OpenWithExtMap.TryGetValue(ext, out var cat) ? cat : "text";
    }

    // ===== 打开方式 · 系统真实关联匹配（2026-09-07 用户拍板"显示电脑内最匹配的打开软件"） =====
    // 匹配源按可信度排序：① UserChoice（用户为 .ext 明确选定的默认 app）
    // ② OpenWithList（该扩展历史打开过的 app，MRU 顺序）③ OpenWithProgids（系统登记的候选 ProgId）
    // ④ Applications\SupportedTypes（显式声明支持该扩展的已安装应用）。
    // 解析出的命令 exe 存在性校验；按源顺序排序（UserChoice 永远最前）。进程级 60s 缓存防右键高频。

    /// <summary>扩展名 → 关联候选缓存（60s；右键菜单每次构建都会调用，注册表枚举不能每次跑）。</summary>
    private static readonly Dictionary<string, (DateTime At, List<(string Name, string Path, string Args, int Rank)> List)>
        AssocCache = new(StringComparer.OrdinalIgnoreCase);

    private const int OpenWithLimit = 8;

    /// <summary>
    /// 打开方式候选（上限 8）：系统真实关联匹配优先 + 类别匹配兜底。
    /// <para>
    /// <b>Args = 参数模板，占位符由唤起方替换</b>：来自注册表 shell\open\command 的候选保留**原始命令行尾部**
    /// （如 Edge/Chrome 的 <c>--single-argument %1</c>、WPS 的 <c>/prometheus /pdf "%1"</c>、夸克的
    /// <c>--brand-clouddrive "%1"</c>）；App Paths / 类别兜底候选为 <c>"%1"</c>。
    /// </para>
    /// <para>
    /// 【2026-09-17 修复】此前一律写死 <c>"%file%"</c>（丢弃注册表里的真实参数）——用户实测点「Microsoft Edge」
    /// 无反应：Edge 的注册命令是 <c>msedge.exe --single-argument %1</c>，丢掉 <c>--single-argument</c> 后
    /// Windows 传给 Edge 的命令行不是它认得的形态，Edge 起来也是空窗口/直接退出。改成按注册表原样唤起。
    /// </para>
    /// </summary>
    public static IReadOnlyList<(string Name, string Path, string Args)> MatchOpenWith(string filePath)
    {
        var result = new List<(string Name, string Path, string Args)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ① 系统真实关联（UserChoice → OpenWithList MRU → OpenWithProgids → Applications SupportedTypes）
        foreach (var (name, path, args, _) in MatchByAssociation(filePath))
        {
            if (result.Count >= OpenWithLimit)
            {
                break;
            }
            if (seen.Add(NormalizePath(path)))
            {
                result.Add((name, path, args));
            }
        }

        // ② 类别匹配兜底（数量不足时；检测工具 60s 缓存 + 系统内置兜底）
        if (result.Count < OpenWithLimit)
        {
            var cat = OpenWithCategoryFor(filePath);
            var want = new HashSet<string>(
                OpenWithCatTools.TryGetValue(cat, out var c) ? c : new[] { "编辑器" },
                StringComparer.Ordinal);

            foreach (var (name, path, args, toolCat) in Detect())
            {
                if (result.Count >= OpenWithLimit)
                {
                    break;
                }
                if (want.Contains(toolCat) && seen.Add(NormalizePath(path)))
                {
                    result.Add((name, path, args));
                }
            }

            if (OpenWithSystemFallback.TryGetValue(cat, out var fallbacks))
            {
                foreach (var raw in fallbacks)
                {
                    if (result.Count >= OpenWithLimit)
                    {
                        break;
                    }
                    var resolved = Environment.ExpandEnvironmentVariables(raw);
                    if (File.Exists(resolved) && seen.Add(NormalizePath(resolved)))
                    {
                        result.Add((Path.GetFileNameWithoutExtension(resolved), resolved, "\"%1\""));
                    }
                }
            }
        }
        return result;
    }

    /// <summary>系统真实关联候选（按源可信度排序；60s 缓存）。</summary>
    private static IReadOnlyList<(string Name, string Path, string Args, int Rank)> MatchByAssociation(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (ext.Length == 0)
        {
            return [];
        }

        if (AssocCache.TryGetValue(ext, out var cached)
            && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(60))
        {
            return cached.List;
        }

        var candidates = new List<(string Name, string Path, string Args, int Rank)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rank = 0;

        // ① UserChoice：用户为 .ext 明确选定的默认 app（ProgId）
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ext + @"\UserChoice");
            if (k?.GetValue("ProgId") is string progId && progId.Length > 0)
            {
                AddFromProgId(progId, rank++);
            }
        }
        catch
        {
            // 单项失败跳过（M10）
        }

        // ② OpenWithList：该扩展历史打开过的 app（MRUList 顺序 = 使用时间倒序）
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ext + @"\OpenWithList");
            if (k is not null)
            {
                var mru = k.GetValue("MRUList") as string ?? string.Empty;
                foreach (var ch in mru)
                {
                    if (k.GetValue(ch.ToString()) is string app && app.Length > 0)
                    {
                        AddFromAppId(app, rank++);
                    }
                }
                // MRUList 未覆盖的值兜底（异常注册数据）
                foreach (var name in k.GetValueNames())
                {
                    if (name.Length == 1 && !mru.Contains(name)
                        && k.GetValue(name) is string app2 && app2.Length > 0)
                    {
                        AddFromAppId(app2, rank++);
                    }
                }
            }
        }
        catch
        {
            // M10
        }

        // ③ OpenWithProgids：系统登记的候选 ProgId（HKCR 合并视图）
        try
        {
            using var k = Registry.ClassesRoot.OpenSubKey(ext + @"\OpenWithProgids");
            if (k is not null)
            {
                foreach (var progId in k.GetValueNames())
                {
                    AddFromProgId(progId, 100 + rank++);
                }
            }
        }
        catch
        {
            // M10
        }

        // ④ Applications\SupportedTypes：显式声明支持该扩展的已安装应用（HKLM/HKCU）
        try
        {
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using var apps = root.OpenSubKey(@"Software\Classes\Applications");
                if (apps is null)
                {
                    continue;
                }
                foreach (var appName in apps.GetSubKeyNames())
                {
                    if (!appName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    using var st = apps.OpenSubKey(appName + @"\SupportedTypes");
                    if (st?.GetValue(ext) is not null)
                    {
                        AddFromAppId(appName, 200 + rank++);
                    }
                }
            }
        }
        catch
        {
            // M10
        }

        var ordered = candidates.OrderBy(c => c.Rank).ToList();
        AssocCache[ext] = (DateTime.UtcNow, ordered);
        return ordered;

        void AddFromProgId(string progId, int r)
        {
            // Applications\xxx.exe 或普通 ProgId；HKCR 合并视图一次覆盖
            try
            {
                using var key = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command");
                if (key?.GetValue(null) is not string cmd || cmd.Length == 0)
                {
                    return;
                }
                var split = SplitCommandLine(cmd);
                if (!split.HasValue)
                {
                    return;
                }
                var (exe, args) = split.Value;
                if (!File.Exists(exe) || !seen.Add(Path.GetFileName(exe).ToLowerInvariant()))
                {
                    return;
                }
                candidates.Add((DisplayNameFor(exe, progId), exe, args, r));
            }
            catch
            {
                // M10
            }
        }

        void AddFromAppId(string appId, int r)
        {
            try
            {
                var path = appId;
                if (!Path.IsPathRooted(appId))
                {
                    path = ResolveAppPath(appId);
                    if (path is null)
                    {
                        return;
                    }
                }
                var expanded = Environment.ExpandEnvironmentVariables(path);
                if (!File.Exists(expanded) || !seen.Add(Path.GetFileName(expanded).ToLowerInvariant()))
                {
                    return;
                }
                // MRUList / SupportedTypes 只登记了 exe 名，没有命令行 → 用 shell 默认形态（文件作唯一实参）
                candidates.Add((DisplayNameFor(expanded, appId), expanded, "\"%1\"", r));
            }
            catch
            {
                // M10
            }
        }
    }

    /// <summary>命令模板 → exe 路径（带引号/空格分隔均支持；环境变量展开）。</summary>
    /// <summary>
    /// 参数模板 → 实际命令行（占位符替换）。**与 <see cref="SplitCommandLine"/> 成对**：
    /// 前者产出模板、本方法消费模板，故同处一个类（模板语法只有一处权威定义）。
    /// <para>
    /// 支持 shell 占位符与本仓库占位符两套：<c>%1 / %L / %V / %* / %file%</c> → 文件全路径（带引号）；
    /// <c>%w / %dir%</c> → 文件所在目录。
    /// </para>
    /// <para>
    /// 【去重引号】注册表里普遍写成 <c>"%1"</c>（占位符自带引号）。替换时若再包一层引号会得到
    /// <c>""C:\x.pdf""</c>——CommandLineToArgvW 虽能解析回原值，但脏命令行在部分应用的自研解析器里
    /// 会多出一个空实参。故先把 <c>"%X"</c> 归一成 <c>%X</c> 再统一加引号。
    /// </para>
    /// </summary>
    public static string BuildLaunchArgs(string? argsTemplate, string filePath)
    {
        var template = argsTemplate ?? string.Empty;
        var quoted = "\"" + filePath + "\"";
        var dir = Path.GetDirectoryName(filePath) ?? string.Empty;

        // ① 去重引号："%X" → %X（注册表普遍写成带引号形态，再包一层会得到 ""path""）。
        //    含 %dir%：目录实参同样要带引号，但绝不能被包两次。
        //    不含 %w：shell 的 %w 本身不带引号，命令里若显式写了引号要保留。
        foreach (var placeholder in QuotedPlaceholders)
        {
            template = template.Replace("\"" + placeholder + "\"", placeholder, StringComparison.OrdinalIgnoreCase);
        }

        // ② 目录类占位符（%w 保持原样替换；它按 shell 语义是无引号工作目录）
        template = template
            .Replace("%dir%", "\"" + dir + "\"", StringComparison.OrdinalIgnoreCase)
            .Replace("%w", dir, StringComparison.Ordinal);

        // ③ 文件实参替换（%file% 是本仓库既有写法，大小写不敏感；%1/%L/%V/%* 按 shell 原样）
        foreach (var placeholder in FilePlaceholders)
        {
            template = string.Equals(placeholder, "%file%", StringComparison.OrdinalIgnoreCase)
                ? template.Replace(placeholder, quoted, StringComparison.OrdinalIgnoreCase)
                : template.Replace(placeholder, quoted, StringComparison.Ordinal);
        }

        return template.Trim();
    }

    /// <summary>文件实参占位符全集（shell 的 %1/%L/%V/%* + 本仓库既有写法 %file%）。</summary>
    private static readonly string[] FilePlaceholders = ["%1", "%L", "%l", "%V", "%v", "%*", "%file%"];

    /// <summary>需要"去重引号"的占位符 = 文件实参 + 目录实参（%dir%）；%w 不在内（shell 语义无引号）。</summary>
    private static readonly string[] QuotedPlaceholders = ["%1", "%L", "%l", "%V", "%v", "%*", "%file%", "%dir%"];

    /// <summary>
    /// 注册表命令行 → (exe 路径, 参数模板)。
    /// <para>
    /// 【为什么要拆而不是只取 exe】shell\open\command 的真实形态常常**必须带参数**才认文件：
    /// Edge/Chrome <c>--single-argument %1</c>、WPS <c>/prometheus /pdf "%1"</c>、
    /// 夸克 <c>--brand-clouddrive "%1"</c>、Okular <c>-- "%1"</c>。只取 exe、自己拼一个裸文件路径，
    /// 就会出现"点了没反应/起来是空窗口"（2026-09-17 用户实测）。
    /// </para>
    /// <para>
    /// 模板里没有占位符时按 shell 语义补 <c>%1</c>（explorer 对无占位符的命令会把文件追加为末位实参）。
    /// 解析失败返回 null（该项跳过）。
    /// </para>
    /// </summary>
    internal static (string Exe, string Args)? SplitCommandLine(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.Length == 0)
        {
            return null;
        }

        string exe;
        string rest;
        if (cmd.StartsWith("\"", StringComparison.Ordinal))
        {
            var end = cmd.IndexOf('"', 1);
            if (end <= 1)
            {
                return null;
            }
            exe = cmd[1..end];
            rest = cmd[(end + 1)..].Trim();
        }
        else
        {
            var space = cmd.IndexOf(' ');
            exe = space < 0 ? cmd : cmd[..space];
            rest = space < 0 ? string.Empty : cmd[(space + 1)..].Trim();
        }

        exe = Environment.ExpandEnvironmentVariables(exe.Trim('"'));
        if (exe.Length == 0)
        {
            return null;
        }

        if (!HasFilePlaceholder(rest))
        {
            rest = rest.Length == 0 ? "\"%1\"" : rest + " \"%1\"";
        }
        return (exe, rest);
    }

    /// <summary>命令行模板是否已含文件占位符（shell 的 %1/%L/%V/%* 与本仓库的 %file%）。</summary>
    private static bool HasFilePlaceholder(string template)
        => template.Contains("%1", StringComparison.Ordinal)
        || template.Contains("%L", StringComparison.Ordinal)
        || template.Contains("%l", StringComparison.Ordinal)
        || template.Contains("%V", StringComparison.Ordinal)
        || template.Contains("%v", StringComparison.Ordinal)
        || template.Contains("%*", StringComparison.Ordinal)
        || template.Contains("%file%", StringComparison.OrdinalIgnoreCase);

    /// <summary>exe 名 → App Paths 注册路径（HKLM 优先，HKCU 兜底）。</summary>
    private static string? ResolveAppPath(string exeName)
    {
        if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            exeName += ".exe";
        }
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName);
                if (key?.GetValue(null) is string v && v.Length > 0)
                {
                    var p = Environment.ExpandEnvironmentVariables(v.Trim('"'));
                    if (File.Exists(p))
                    {
                        return p;
                    }
                }
            }
            catch
            {
                // M10
            }
        }
        return null;
    }

    /// <summary>显示名：exe 版本信息（FileDescription/ProductName）→ FriendlyNames 映射 → exe 名。</summary>
    private static string DisplayNameFor(string exePath, string fallbackId)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            var desc = info.FileDescription?.Trim();
            if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 40)
            {
                return desc;
            }
            var prod = info.ProductName?.Trim();
            if (!string.IsNullOrWhiteSpace(prod) && prod.Length <= 40)
            {
                return prod;
            }
        }
        catch
        {
            // 读不到版本信息走映射
        }
        var exe = Path.GetFileNameWithoutExtension(exePath);
        return FriendlyNames.TryGetValue(exe, out var friendly) ? friendly : exe;
    }


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
