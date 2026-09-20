// BetterDesktop.Shell.ContextMenus — 原生右键扩展的配置快照写入端（宿主侧）
//
// 【职责边界】本文件只负责「菜单树模型 → shellmenu.json」这一段的序列化与原子落盘。
//   · 谁决定"菜单里有什么" → 调用方（DesktopPlugin：桌面控制来自 settings，格式转换来自 IConvertMenuService）
//   · 谁决定"这项此刻显不显示" → 原生侧按通用谓词判定（见 native/include/MenuModel.h）
//   · 谁决定"点了做什么" → CLI（--menu-batch → HeadlessExecutor）
//
// 【为什么是原子替换】原生侧用 (mtime, size) 做解析缓存键；若直接覆盖写，写到一半被外壳读到
// 半截 JSON 会拿不到配置。写临时文件再 Move(overwrite) 保证读方永远看到完整版本。
//
// 【为什么单独一个文件，不复用 settings.json】两者的消费者与变更频率不同：
// settings.json 是宿主内部状态；shellmenu.json 是"已渲染好的菜单树"（含引擎探测结果），
// 由宿主在设置/引擎状态变化时重算。混在一起会让原生侧必须理解 settings 的全部语义。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>菜单项出现的位置（映射到原生侧的 scenes 谓词）。</summary>
public enum ShellMenuScene
{
    /// <summary>选中了文件（至少一个非目录）。</summary>
    Files,

    /// <summary>选中的全是目录。</summary>
    Directory,

    /// <summary>目录/桌面空白（无选中对象）。</summary>
    Background,
}

/// <summary>菜单项种类（映射到原生侧 kind）。</summary>
public enum ShellMenuKind
{
    /// <summary>普通命令。</summary>
    Command,

    /// <summary>二级子菜单。</summary>
    Submenu,

    /// <summary>开关项（带勾选态）。</summary>
    Toggle,
}

/// <summary>
/// 一个原生菜单项（递归）。全部字段都是可选/有默认值 —— 未设置的字段不会写进 JSON，
/// 让"默认行为"只有一处定义（原生侧）。
/// </summary>
public sealed record ShellMenuItem
{
    /// <summary>稳定标识（同层唯一；也用于诊断日志）。</summary>
    public required string Id { get; init; }

    /// <summary>显示文本（原生侧红线：≤80 字符，超长被系统静默隐藏）。</summary>
    public required string Title { get; init; }

    public ShellMenuKind Kind { get; init; } = ShellMenuKind.Command;

    /// <summary>出现场景白名单；空 = 所有场景。</summary>
    public IReadOnlyList<ShellMenuScene> Scenes { get; init; } = [];

    /// <summary>勾选态（Kind=Toggle 时有意义）。</summary>
    public bool IsChecked { get; init; }

    /// <summary>加粗高亮（无损转换项；用户拍板"可无损才高亮"）。</summary>
    public bool Highlight { get; init; }

    /// <summary>默认动作加粗。</summary>
    public bool IsDefault { get; init; }

    /// <summary>CLI 动作标识（--menu-batch 的 action；与自绘菜单 MenuItemDef.Action 同源）。</summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>动作参数。</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>可见性谓词：选中项扩展名必须全在此列表内（空 = 不过滤）。</summary>
    public IReadOnlyList<string> FilterExtensions { get; init; } = [];

    /// <summary>可见性谓词：多选时要求扩展名一致。</summary>
    public bool FilterSameExtension { get; init; }

    /// <summary>可见性谓词：选中项数量下限（0 = 不限）。</summary>
    public int RequiresCountMin { get; init; }

    /// <summary>可见性谓词：选中项扩展名必须全在此列表内（空 = 不约束）。</summary>
    public IReadOnlyList<string> RequiresAllExtIn { get; init; } = [];

    /// <summary>
    /// 菜单项图标（可选），资源定位串格式：<c>"&lt;exe 或 dll 路径&gt;, &lt;索引&gt;"</c>
    /// （例如 <c>"C:\...\BetterDesktop.Host.exe, 0"</c>）。
    /// <para>
    /// 【原生侧已完整支持，**无需重编原生 DLL**】B 路经 <c>SetMenuItemBitmaps</c>
    /// （native/src/ShellMenuHandler.cpp 的 ApplyItemIcon）、A 路经 <c>IExplorerCommand::GetIcon</c>
    /// 消费；原生解析侧 native/src/MenuModel.cpp 早已读取 <c>icon</c> 字段，只是此前没有任何写入方。
    /// 留空则原生回退到宿主 exe 图标（ShellMenuHandler.h 的 ResolveEffectiveIcon）。
    /// </para>
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>子项（Kind=Submenu 时有意义）。</summary>
    public IReadOnlyList<ShellMenuItem> Children { get; init; } = [];
}

/// <summary>
/// shellmenu.json 写入端（宿主 → 原生扩展的单向契约）。
/// 路径与原生侧 ConfigFilePath() 必须一致：%APPDATA%\BetterDesktop\shellmenu.json。
/// </summary>
public static class ShellMenuConfigWriter
{
    /// <summary>配置文件名（原生侧硬编码同名；改动需两端同步）。</summary>
    public const string FileName = "shellmenu.json";

    /// <summary>schema 版本（原生侧 version 字段；不匹配时原生侧按缺失处理）。</summary>
    public const int SchemaVersion = 1;

    /// <summary>宿主设定目录（%APPDATA%\BetterDesktop）。</summary>
    public static string GetDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BetterDesktop");

    /// <summary>配置快照绝对路径。</summary>
    public static string GetConfigPath() => Path.Combine(GetDirectory(), FileName);

    /// <summary>序列化（纯函数，可单测）。未设置的字段不写出，默认语义由原生侧单点定义。</summary>
    public static string BuildJson(IReadOnlyList<ShellMenuItem> items, bool extensionEnabled)
    {
        ArgumentNullException.ThrowIfNull(items);

        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add(BuildItem(item));
        }

        var root = new JsonObject
        {
            ["version"] = SchemaVersion,
            ["extensionEnabled"] = extensionEnabled,
            ["items"] = array,
        };

        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            // 中文标题必须原样写成 UTF-8（默认编码器会转成 \uXXXX；原生解析器能读但不可读、不便排查）
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            // ⚠️ 必须显式给 TypeInfoResolver（2026-09-11 单测抓出的真实缺陷）：
            // JsonNode.ToJsonString(自定义 options) 在 .NET 8 下，写 JsonValue 标量（int/bool/string，
            // 由隐式转换生成的 JsonValueCustomized<T>）会走 options.GetTypeInfo()，而此时 options 已被
            // 标记只读且无 resolver → 抛 InvalidOperationException。Write() 的 catch 会把它吞成
            // "写入失败"，表现为**配置永远写不出去、右键菜单永不出现**（无崩溃、无显式报错，极难排查）。
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        });
    }

    /// <summary>
    /// 原子写入配置快照。返回 false 时 error 带原因；调用方只记日志，不得因此抛出。
    /// </summary>
    public static bool Write(IReadOnlyList<ShellMenuItem> items, bool extensionEnabled, out string? error)
    {
        error = null;
        var path = GetConfigPath();
        var tempPath = path + ".tmp";

        try
        {
            var directory = GetDirectory();
            Directory.CreateDirectory(directory);

            // UTF-8 无 BOM：原生解析器虽容忍 BOM，但无 BOM 让 JSON 工具/文本编辑器读起来更干净。
            File.WriteAllText(tempPath, BuildJson(items, extensionEnabled), new UTF8Encoding(false));

            // 原子替换（同卷 Move；.NET Core 3.0+ 支持 overwrite）
            File.Move(tempPath, path, overwrite: true);
            DiagnosticLog.Trace("shell.context-menu", $"shellmenu.json 已写入: 顶级项={items.Count} 扩展启用={extensionEnabled}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            DiagnosticLog.Trace("shell.context-menu", $"shellmenu.json 写入失败: {ex.Message}");
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // 清理失败不影响结果
            }
            return false;
        }
    }

    /// <summary>删除配置快照（扩展整体关闭时调用；原生侧随即不再显示任何项）。</summary>
    public static void Remove()
    {
        try
        {
            var path = GetConfigPath();
            if (File.Exists(path))
            {
                File.Delete(path);
                DiagnosticLog.Trace("shell.context-menu", "shellmenu.json 已删除（扩展关闭）");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.context-menu", $"shellmenu.json 删除失败: {ex.Message}");
        }
    }

    // ===================== 序列化细节 =====================

    private static JsonObject BuildItem(ShellMenuItem item)
    {
        var node = new JsonObject
        {
            ["id"] = item.Id,
            ["title"] = item.Title,
        };

        if (item.Kind != ShellMenuKind.Command)
        {
            node["kind"] = KindText(item.Kind);
        }
        if (item.Scenes.Count > 0)
        {
            var scenes = new JsonArray();
            foreach (var scene in item.Scenes)
            {
                scenes.Add(SceneText(scene));
            }
            node["scenes"] = scenes;
        }
        if (item.IsChecked)
        {
            node["checked"] = true;
        }
        if (item.Highlight)
        {
            node["highlight"] = true;
        }
        if (item.IsDefault)
        {
            node["default"] = true;
        }
        // 图标：原生侧 MenuModel.cpp 早已解析 "icon"（B 路 SetMenuItemBitmaps / A 路 GetIcon），
        // 此前无写入方 → 所有项都回退到宿主图标。写上即可让各项有自己的图标。
        if (!string.IsNullOrEmpty(item.Icon))
        {
            node["icon"] = item.Icon;
        }
        if (!string.IsNullOrEmpty(item.Action))
        {
            node["action"] = item.Action;
        }
        if (item.Args.Count > 0)
        {
            node["args"] = ToArray(item.Args);
        }
        // ⚠️ 契约形状：原生侧读的是 filter 对象（native/src/MenuModel.cpp ParseItem）：
        //     "filter": { "extensions": [...], "selection": "sameExtension" }
        // 不是扁平的 filterExtensions/filterSameExtension —— 写成扁平字段会被原生侧静默忽略，
        // 表现为"所有转换目标对所有文件都出现"（不报错、难排查）。
        if (item.FilterExtensions.Count > 0 || item.FilterSameExtension)
        {
            var filter = new JsonObject();
            if (item.FilterExtensions.Count > 0)
            {
                filter["extensions"] = ToArray(item.FilterExtensions);
            }
            if (item.FilterSameExtension)
            {
                filter["selection"] = "sameExtension";
            }
            node["filter"] = filter;
        }
        if (item.RequiresCountMin > 0)
        {
            node["requiresCountMin"] = item.RequiresCountMin;
        }
        if (item.RequiresAllExtIn.Count > 0)
        {
            node["requiresAllExtIn"] = ToArray(item.RequiresAllExtIn);
        }
        if (item.Children.Count > 0)
        {
            var children = new JsonArray();
            foreach (var child in item.Children)
            {
                children.Add(BuildItem(child));
            }
            node["children"] = children;
        }

        return node;
    }

    private static JsonArray ToArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }
        return array;
    }

    private static string KindText(ShellMenuKind kind) => kind switch
    {
        ShellMenuKind.Submenu => "submenu",
        ShellMenuKind.Toggle => "toggle",
        _ => "command",
    };

    private static string SceneText(ShellMenuScene scene) => scene switch
    {
        ShellMenuScene.Files => "files",
        ShellMenuScene.Directory => "directory",
        _ => "background",
    };
}
