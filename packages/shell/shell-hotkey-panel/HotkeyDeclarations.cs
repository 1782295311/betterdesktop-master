using System.Collections.Generic;
using System.Linq;
using BetterDesktop.Shell.Clipboard.Ipc;
using BetterDesktop.Shell.Hotkeys.Contracts;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.HotkeyPanel;

/// <summary>
/// 既有热键的**声明迁移**清单（热键计划 §9/§10）：全部既有热键（不分来源）声明进注册表，
/// 供侧板展示/冲突检测/隐藏/停用/改键的持久化。键位实际注册仍在各来源进程（宿主不双注册，0x581 红线）。
/// <para>接线状态：<c>Wired</c> = 侧板改键/停用**真生效**（写消费方已读的配置键）；
/// 未接线的项侧板只展示+隐藏，改键/停用入口禁用（不假装可改）。</para>
/// </summary>
internal static class HotkeyDeclarations
{
    /// <summary>面板「粘贴回原窗口」配置键（与 shell-clipboard ClipboardSection.cs:80 同源，勿漂移）。</summary>
    internal const string PasteBackHotkeyKey = "extensions.clipboard-history.paste-back-hotkey";

    /// <summary>引擎三枚全局热键配置键（Rust 引擎 settings.rs 同款解析；设置中心改键写入 + 重启引擎生效）。</summary>
    internal const string EnginePanelHotkeyKey = "extensions.clipboard-history.open-panel-hotkey";
    internal const string EngineFavoritesHotkeyKey = "extensions.clipboard-history.favorites-hotkey";
    internal const string EnginePauseHotkeyKey = "extensions.clipboard-history.toggle-pause-hotkey";

    /// <summary>引擎条目 Id → 配置键映射（供设置中心行内改键/停用）。</summary>
    internal static string? EngineHotkeyKeyFor(string id) => id switch
    {
        "engine.panel-toggle" => EnginePanelHotkeyKey,
        "engine.favorites" => EngineFavoritesHotkeyKey,
        "engine.pause" => EnginePauseHotkeyKey,
        _ => null,
    };

    /// <summary>capture 热键配置路径（%LOCALAPPDATA%\BetterDesktop\capture\settings.json，
    /// HotKeyManager 启动时读取；改键后需重启 capture 生效）。</summary>
    internal static string CaptureHotkeySettingsPath =>
        System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "BetterDesktop", "capture", "settings.json");

    /// <summary>可被侧板改键/停用（已接线）的绑定 Id。</summary>
    internal static readonly HashSet<string> WiredIds = new(System.StringComparer.Ordinal)
    {
        "clipboard.paste-back",
    };

    /// <summary>系统热键 owner 标识（侧板/设置中心显示"系统"徽标；活性恒真）。</summary>
    internal const string SystemOwner = "system";

    /// <summary>是否系统热键条目（Windows 自带全局热键，仅展示/冲突提示，不可改键）。</summary>
    internal static bool IsSystem(string id) => id.StartsWith("system.", System.StringComparison.Ordinal);

    /// <summary>系统热键分组（设置中心按组渲染，便于在 50+ 条里定位）。</summary>
    internal sealed record SystemHotkeyGroup(string Title, IReadOnlyList<HotkeyBinding> Items);

    /// <summary>
    /// Windows 系统全局热键（按下即有系统功能，由 Windows 自身注册），按主题分组。
    /// <para>用途：侧板/设置中心展示"当前环境真正按得动、有功能"的热键（用户反馈：只显示自家
    /// 注册的热键实用性不高）；同时作为冲突检测的已知集合（用户把自家热键改成 Win+E 时
    /// Declare 会被"同作用域同键位"拒绝，正面提示冲突）与扫描归类依据。</para>
    /// <para>Chord 主键一律用 WPF Key 枚举名（HotkeySpec 契约）：PrintScreen = <c>Snapshot</c>、
    /// 句号 = <c>OemPeriod</c>。方向键族/数字族用一条代表（描述注明覆盖范围），避免 4 倍膨胀。</para>
    /// </summary>
    private static readonly IReadOnlyList<SystemHotkeyGroup> SystemGroups = new List<SystemHotkeyGroup>
    {
        new("窗口与桌面", new HotkeyBinding[]
        {
            Sys("win-d", "Win+D", "显示 / 隐藏桌面"),
            Sys("win-m", "Win+M", "最小化所有窗口"),
            Sys("win-shift-m", "Win+Shift+M", "还原被最小化的窗口"),
            Sys("win-home", "Win+Home", "最小化除当前外的所有窗口"),
            Sys("win-arrow", "Win+Up", "贴靠 / 最大化窗口（← → ↓ 同理）"),
            Sys("win-shift-arrow", "Win+Shift+Up", "拉伸窗口到屏幕上下沿（← → 同理）"),
            Sys("win-shift-monitor", "Win+Shift+Left", "把窗口移到另一台显示器（→ 同理）"),
            Sys("win-comma", "Win+OemComma", "临时预览桌面（按住不放）"),
            Sys("win-tab", "Win+Tab", "任务视图 / 虚拟桌面"),
            Sys("win-z", "Win+Z", "贴靠布局（Snap Layouts）"),
            Sys("win-t", "Win+T", "在任务栏程序间循环切换"),
            Sys("win-b", "Win+B", "把焦点移到通知区域（托盘）"),
            Sys("win-number", "Win+D1", "打开 / 切换任务栏第 1-9 个程序"),
            Sys("win-shift-number", "Win+Shift+D1", "以管理员身份启动任务栏第 1-9 个程序"),
            Sys("win-ctrl-number", "Win+Ctrl+D1", "切换到任务栏第 1-9 个程序的最后活动窗口"),
            Sys("alt-tab", "Alt+Tab", "切换窗口"),
            Sys("alt-shift-tab", "Alt+Shift+Tab", "反向切换窗口"),
            Sys("ctrl-alt-tab", "Ctrl+Alt+Tab", "切换窗口（常驻列表，松开仍显示）"),
            Sys("alt-esc", "Alt+Esc", "按窗口打开顺序切换"),
            Sys("alt-space", "Alt+Space", "打开窗口系统菜单"),
            Sys("alt-f4", "Alt+F4", "关闭当前窗口"),
            Sys("shift-f10", "Shift+F10", "打开右键上下文菜单（键盘）"),
        }),

        new("系统与设置", new HotkeyBinding[]
        {
            Sys("win-i", "Win+I", "打开设置"),
            Sys("win-a", "Win+A", "快速设置（Win11）/ 操作中心（Win10）"),
            Sys("win-n", "Win+N", "通知中心 / 日历（Win11）"),
            Sys("win-l", "Win+L", "锁定屏幕"),
            Sys("win-r", "Win+R", "打开「运行」对话框"),
            Sys("win-e", "Win+E", "打开文件资源管理器"),
            Sys("win-x", "Win+X", "打开快速链接菜单（等同右键开始按钮）"),
            Sys("win-s", "Win+S", "打开搜索"),
            Sys("win-q", "Win+Q", "打开搜索（旧版）"),
            Sys("win-v", "Win+V", "剪贴板历史"),
            Sys("win-w", "Win+W", "小组件 / 资讯面板"),
            Sys("win-p", "Win+P", "投影 / 显示器模式"),
            Sys("win-k", "Win+K", "连接无线显示与音频设备"),
            Sys("win-c", "Win+C", "Copilot / 旧版 Cortana"),
            Sys("win-h", "Win+H", "语音输入（听写）"),
            Sys("win-f", "Win+F", "反馈中心"),
            Sys("win-j", "Win+J", "把焦点移到「焦点提示」"),
            Sys("win-o", "Win+O", "锁定屏幕方向（平板）"),
            Sys("win-number-settings", "Win+U", "辅助功能设置"),
            Sys("win-ctrl-shift-b", "Win+Ctrl+Shift+B", "重启显卡驱动（黑屏 / 花屏急救）"),
            Sys("ctrl-shift-esc", "Ctrl+Shift+Esc", "打开任务管理器"),
            Sys("ctrl-esc", "Ctrl+Esc", "打开开始菜单（无 Win 键键盘）"),
            Sys("win-esc", "Win+Esc", "退出放大镜"),
        }),

        new("虚拟桌面", new HotkeyBinding[]
        {
            Sys("win-ctrl-d", "Win+Ctrl+D", "新建虚拟桌面"),
            Sys("win-ctrl-f4", "Win+Ctrl+F4", "关闭当前虚拟桌面"),
            Sys("win-ctrl-arrow", "Win+Ctrl+Left", "切换到上一 / 下一个虚拟桌面（→ 同理）"),
        }),

        new("输入与辅助功能", new HotkeyBinding[]
        {
            Sys("win-space", "Win+Space", "切换输入法 / 键盘布局"),
            Sys("win-ctrl-enter", "Win+Ctrl+Enter", "打开讲述人"),
            Sys("win-ctrl-o", "Win+Ctrl+O", "打开屏幕键盘"),
            Sys("win-ctrl-c", "Win+Ctrl+C", "打开颜色滤镜"),
            Sys("win-plus", "Win+OemPlus", "打开放大镜（放大）"),
            Sys("win-minus", "Win+OemMinus", "放大镜缩小"),
            Sys("win-period", "Win+OemPeriod", "表情符号 / 颜文字面板"),
        }),

        new("截图与录屏", new HotkeyBinding[]
        {
            Sys("win-shift-s", "Win+Shift+S", "区域截图（截图工具）"),
            Sys("win-snapshot", "Win+Snapshot", "全屏截图并保存到「图片 / 屏幕截图」"),
            Sys("win-g", "Win+G", "Xbox 游戏栏"),
            Sys("win-alt-snapshot", "Win+Alt+Snapshot", "游戏栏：截图"),
            Sys("win-alt-r", "Win+Alt+R", "游戏栏：开始 / 停止录制"),
            Sys("win-alt-g", "Win+Alt+G", "游戏栏：录制最近 30 秒"),
            Sys("win-alt-m", "Win+Alt+M", "游戏栏：静音 / 取消静音麦克风"),
            Sys("win-alt-b", "Win+Alt+B", "开启 / 关闭 HDR（需 HDR 显示器）"),
        }),

        new("通用编辑键（应用内生效，非全局）", new HotkeyBinding[]
        {
            Sys("edit-copy", "Ctrl+C", "复制"),
            Sys("edit-cut", "Ctrl+X", "剪切"),
            Sys("edit-paste", "Ctrl+V", "粘贴"),
            Sys("edit-undo", "Ctrl+Z", "撤销"),
            Sys("edit-redo", "Ctrl+Y", "重做"),
            Sys("edit-redo-alt", "Ctrl+Shift+Z", "重做（部分应用）"),
            Sys("edit-select-all", "Ctrl+A", "全选"),
            Sys("edit-save", "Ctrl+S", "保存"),
            Sys("edit-save-as", "Ctrl+Shift+S", "另存为"),
            Sys("edit-find", "Ctrl+F", "查找"),
            Sys("edit-replace", "Ctrl+H", "替换"),
            Sys("edit-print", "Ctrl+P", "打印"),
            Sys("edit-new", "Ctrl+N", "新建"),
            Sys("edit-open", "Ctrl+O", "打开"),
            Sys("edit-close", "Ctrl+W", "关闭当前文档 / 标签页"),
            Sys("edit-refresh", "Ctrl+R", "刷新 / 重新加载"),
            Sys("edit-word-move", "Ctrl+Left", "按词移动光标（→ 同理）"),
            Sys("edit-doc-home", "Ctrl+Home", "跳到文档开头（Ctrl+End 到末尾）"),
            Sys("edit-del-word", "Ctrl+Backspace", "删除前一个词（Ctrl+Delete 删后一个）"),
        }),

        new("通用浏览 / 导航键（应用内生效，非全局）", new HotkeyBinding[]
        {
            Sys("nav-back", "Alt+Left", "后退（Alt+Right 前进）"),
            Sys("nav-up", "Alt+Up", "上一级目录"),
            Sys("nav-properties", "Alt+Enter", "查看属性 / 全屏切换"),
            Sys("nav-permanent-delete", "Shift+Delete", "永久删除（不进回收站）"),
            Sys("nav-switch-tab", "Ctrl+Tab", "切换标签页（Ctrl+Shift+Tab 反向）"),
            Sys("nav-new-tab", "Ctrl+T", "新建标签页（浏览器）"),
            Sys("nav-reopen-tab", "Ctrl+Shift+T", "恢复刚关闭的标签页"),
            Sys("nav-bookmark", "Ctrl+D", "收藏 / 加入书签"),
            Sys("nav-address", "Ctrl+L", "定位到地址栏"),
            Sys("nav-new-folder", "Ctrl+Shift+N", "新建文件夹（资源管理器）"),
            Sys("nav-clear-data", "Ctrl+Shift+Delete", "清除浏览数据（浏览器）"),
        }),
    };

    /// <summary>系统热键分组视图（设置中心按组渲染）。</summary>
    internal static IReadOnlyList<SystemHotkeyGroup> SystemHotkeyGroups => SystemGroups;

    /// <summary>扁平化全量系统热键（冲突检测 / 扫描归类 / 侧板子集校验用）。</summary>
    internal static IReadOnlyList<HotkeyBinding> SystemHotkeys { get; } =
        SystemGroups.SelectMany(g => g.Items).ToList();

    private static HotkeyBinding Sys(string idSuffix, string chord, string description)
        => new(
            "system." + idSuffix,
            new HotkeyChord(chord),
            HotkeyScope.Global,
            description,
            HotkeySource.SystemHotkey,
            new HotkeyChord(chord),
            SystemOwner);

    /// <summary>
    /// 系统热键在**侧板只读态**常显的子集 Id（全量在可操作态与设置中心展示）。
    /// <para>只读态是点击穿透的——滚轮会落到下方应用，**看不到的部分滚不出来**，
    /// 所以只读态只放最常用的一批；想看全部 → 右 Alt + 点击进入可操作态（那里有全量 + 滚动）。</para>
    /// </summary>
    internal static readonly HashSet<string> SystemSidebarIds = new(System.StringComparer.Ordinal)
    {
        "system.win-e", "system.win-d", "system.win-i", "system.win-l", "system.win-r",
        "system.win-x", "system.win-v", "system.win-shift-s", "system.win-tab", "system.win-a",
        "system.win-p", "system.win-space",
        "system.alt-tab", "system.alt-f4", "system.ctrl-shift-esc", "system.win-snapshot",
    };

    /// <summary>按键位规范（canonical 归一）匹配系统热键；未命中返回 null（供"检测快捷键"使用）。</summary>
    internal static HotkeyBinding? FindSystemBySpec(string spec)
    {
        if (!HotkeySpec.TryParse(spec, out _, out _, out var canonical) || canonical.Length == 0)
        {
            return null;
        }

        foreach (var b in SystemHotkeys)
        {
            if (HotkeySpec.TryParse(b.Chord.Spec, out _, out _, out var c)
                && string.Equals(c, canonical, System.StringComparison.OrdinalIgnoreCase))
            {
                return b;
            }
        }
        return null;
    }

    /// <summary>声明条目列表（宿主 Load 时逐条 Declare）。paste-back 仅在**已配置**时声明
    /// （默认入口是鼠标中键，未配置不占展示位）；其余既有热键恒声明。</summary>
    internal static IReadOnlyList<HotkeyBinding> Build(ISettingsService? settings)
    {
        var list = new List<HotkeyBinding>();

        var pasteBack = ReadPasteBackChord(settings);
        if (!string.IsNullOrWhiteSpace(pasteBack))
        {
            list.Add(new HotkeyBinding(
                "clipboard.paste-back",
                new HotkeyChord(pasteBack),
                HotkeyScope.Surface("ClipboardPanel"),
                "粘贴回原窗口（面板打开时生效）",
                HotkeySource.SystemHotkey,
                new HotkeyChord(pasteBack),
                "panel"));
        }

        list.AddRange(new[]
        {
            // 截图全局热键（capture exe 自注册，读 capture/settings.json；宿主改键通道未接线）。
            new HotkeyBinding(
                "capture.toggle",
                new HotkeyChord("Win+Shift+B"),
                HotkeyScope.Global,
                "截图",
                HotkeySource.SystemHotkey,
                new HotkeyChord("Win+Shift+B"),
                "capture"),

            // 剪贴板引擎三枚全局热键（Rust 引擎注册；apply_settings 改键通道未接线）。
            new HotkeyBinding(
                "engine.panel-toggle",
                new HotkeyChord("Ctrl+Shift+V"),
                HotkeyScope.Global,
                "打开历史面板",
                HotkeySource.SystemHotkey,
                new HotkeyChord("Ctrl+Shift+V"),
                "engine"),
            new HotkeyBinding(
                "engine.favorites",
                new HotkeyChord("Ctrl+Shift+P"),
                HotkeyScope.Global,
                "收藏视图",
                HotkeySource.SystemHotkey,
                new HotkeyChord("Ctrl+Shift+P"),
                "engine"),
            new HotkeyBinding(
                "engine.pause",
                new HotkeyChord("Ctrl+Shift+Backspace"),
                HotkeyScope.Global,
                "暂停/恢复捕获",
                HotkeySource.SystemHotkey,
                new HotkeyChord("Ctrl+Shift+Backspace"),
                "engine"),

            // 开始菜单 Win 键（shell-start-menu StartKeyHook 查表；查表迁移未接线）。
            new HotkeyBinding(
                "start-menu.win-key",
                new HotkeyChord("Win"),
                HotkeyScope.Global,
                "打开开始菜单",
                HotkeySource.LowLevelHook,
                new HotkeyChord("Win"),
                "start-menu"),
        });

        // 系统全局热键（Windows 自带，按下即有功能）：登记进注册表供侧板/设置中心展示与冲突检测。
        list.AddRange(SystemHotkeys);

        return list;
    }

    /// <summary>读取面板粘贴回热键当前配置（空 = 未配置，默认鼠标中键）。</summary>
    internal static string? ReadPasteBackChord(ISettingsService? settings)
        => settings?.Get(PasteBackHotkeyKey, string.Empty);

    /// <summary>是否已接线（可改键/停用）。</summary>
    internal static bool IsWired(string id) => WiredIds.Contains(id);

    /// <summary>
    /// 写 capture 热键配置（spec 形如 "Win+Shift+K"；capture 的 HotKeyManager 只支持**单字符**主键）。
    /// <para>【2026-09-16 修复】必须**读-改-写**：capture 的 settings.json 还存有其它设置
    /// （真机实测含 <c>stickerTopmost</c>），早期实现直接 WriteAllText 一个只含 hotkey 的 JSON，
    /// 会把用户其它设置整份抹掉。</para>
    /// 返回 null = 成功；返回字符串 = 拒绝原因（UI 就地提示，不假装改成功）。
    /// </summary>
    internal static string? WriteCaptureHotkey(string spec)
    {
        if (!HotkeySpec.TryParse(spec, out var mods, out var mainKey, out _))
        {
            return "无法解析该组合键";
        }
        if (mainKey.Length != 1 || !char.IsLetterOrDigit(mainKey[0]))
        {
            return "截图热键仅支持字母 / 数字单键（如 Win+Shift+K）";
        }

        var modParts = new List<string>();
        if ((mods & 0x0008) != 0) modParts.Add("Win");
        if ((mods & 0x0004) != 0) modParts.Add("Shift");
        if ((mods & 0x0002) != 0) modParts.Add("Ctrl");
        if ((mods & 0x0001) != 0) modParts.Add("Alt");
        return PatchCaptureSettings(
            enabled: true,
            modifiers: string.Join("+", modParts),
            key: mainKey.ToUpperInvariant());
    }

    /// <summary>停用 capture 热键：保留当前键位、置 enabled=false（HotKeyManager 启动时读到即不注册）。</summary>
    internal static string? DisableCaptureHotkey()
        => PatchCaptureSettings(enabled: false, modifiers: null, key: null);

    /// <summary>
    /// capture 配置的**读-改-写**（保留文件内其它字段）。
    /// <c>null</c> = 该字段不动；返回 null 表示成功，否则为可读失败原因。
    /// </summary>
    private static string? PatchCaptureSettings(bool? enabled, string? modifiers, string? key)
    {
        try
        {
            var path = CaptureHotkeySettingsPath;
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            var existing = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null;
            System.IO.File.WriteAllText(path, PatchCaptureJson(existing, enabled, modifiers, key));
            return null;
        }
        catch (Exception e)
        {
            return "写入截图配置失败：" + e.Message;
        }
    }

    /// <summary>
    /// capture 配置 JSON 的**读-改-写**纯函数（可单测、无文件副作用）：
    /// 保留 <paramref name="existingJson"/> 中的其它字段（真机实测文件含 <c>stickerTopmost</c>），
    /// 只更新 <c>hotkey</c> 段的指定字段（传 null = 该字段不动）。
    /// </summary>
    internal static string PatchCaptureJson(string? existingJson, bool? enabled, string? modifiers, string? key)
    {
        var root = new System.Text.Json.Nodes.JsonObject();
        if (!string.IsNullOrWhiteSpace(existingJson)
            && System.Text.Json.Nodes.JsonNode.Parse(existingJson) is System.Text.Json.Nodes.JsonObject parsed)
        {
            root = parsed;
        }

        if (root["hotkey"] is not System.Text.Json.Nodes.JsonObject hotkey)
        {
            hotkey = new System.Text.Json.Nodes.JsonObject();
            root["hotkey"] = hotkey;
        }

        if (enabled is not null)
        {
            hotkey["enabled"] = enabled.Value;
        }
        if (modifiers is not null)
        {
            hotkey["modifiers"] = modifiers;
        }
        if (key is not null)
        {
            hotkey["key"] = key;
        }

        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// owner 进程活性（P1-4）：声明条目恒以 Enabled=true 出现在"此刻可用"表里，但对应进程可能根本没跑——
    /// 行上标注"未运行"。已知外部进程名映射；宿主内/未知一律视为运行中（不误报）。
    /// </summary>
    internal static bool IsOwnerAlive(string owner) => owner switch
    {
        // 【2026-09-17 内存预算】截图改为"按需拉起 + 截完即退"：热键持有者变成常驻的 Agent，
        // 截图进程只在一次截屏期间存在 → 探活目标随之改为 Agent（否则用户按完键那几秒才显示"运行中"，
        // 平时恒显示"未运行"，反而误导）。Agent 未跑时才退回看截图进程本身。
        "capture" => IsProcessRunning("BetterDesktop.Agent") || IsProcessRunning("BetterDesktop.Capture"),
        "engine" => IsProcessRunning("BetterDesktop.Clipboard.Engine"), // 实测进程名（宿主拉起 exe 名；Cargo name 与 exe 名不一致）
        _ => true, // start-menu / panel / 未知：宿主内进程或暂无法探活，不误报
    };

    private static bool IsProcessRunning(string processName)
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName(processName).Length > 0;
        }
        catch
        {
            return true; // 探活失败不误报为未运行
        }
    }
}
