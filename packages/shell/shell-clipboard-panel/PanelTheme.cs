using System;
using System.Text.Json;
using BetterDesktop.Shell.Core.Surface;

namespace BetterDesktop.Clipboard.Panel;

/// <summary>
/// 剪贴板面板的**扩展配置读取**（本类只负责"面板自己那几个键"）。
/// <para>
/// 【2026-09-15 职责收敛】主题令牌的读取与推导（<c>appearance.*</c> → App.Resources、
/// 设计刻度 <c>Scale</c>、<c>Blend</c> 等）已上提到 <see cref="EntryTheme"/>（shell-core），
/// 因为截图入口面（BetterDesktop.Capture）要用**同一份**推导与同一套刻度 ——
/// 两边各写一遍必然漂移。本类现在只剩：剪贴板扩展键（enabled / entry-style / capacity /
/// dismiss-mode / 热键 / 敏感遮罩）+ 对 <see cref="EntryTheme"/> 的薄转发。
/// </para>
/// <para>
/// 面板不监听宿主运行时的设置变更（v1 启动时读取一次；宿主改主题后面板重启跟随——与扩展中心开关同语义）。
/// </para>
/// </summary>
public static class PanelTheme
{
    /// <summary>
    /// 读到的剪贴板扩展配置（enabled / entry-style / capacity / dismiss-mode）。
    /// <para>
    /// <c>DismissMode</c>：<c>auto</c>（默认）= 点面板外/失焦自动收起；<c>manual</c> = 只能点「✕」或 Esc 收起。
    /// 面板进程启动时读取一次（与其它扩展项同语义：改完重启面板生效）。
    /// </para>
    /// </summary>
    public sealed record ClipboardExtConfig(bool Enabled, string EntryStyle, int Capacity, string DismissMode)
    {
        // 默认入口 = 侧边栏（2026-09-12 用户实测否决悬浮球：'悬浮球并没有用，还是用侧边栏好一点'）
        public static readonly ClipboardExtConfig Default = new(true, "sidebar", 10000, "auto");
    }

    /// <summary>设置文件路径（可注入，测试用；转发到 <see cref="EntryTheme"/>）。</summary>
    public static string SettingsPath
    {
        get => EntryTheme.SettingsPath;
        set => EntryTheme.SettingsPath = value;
    }

    /// <summary>解析结果是否来自真实文件（false = 文件缺失，全默认）。</summary>
    public static bool Loaded => EntryTheme.Loaded;

    /// <summary>读同一份 settings.json（诊断转给 PanelLog，避免读失败静默降级）。</summary>
    public static void Load()
    {
        EntryTheme.OnDiagnostic ??= PanelLog.Trace;
        EntryTheme.Load();
    }

    public static ClipboardExtConfig ExtensionConfig()
    {
        // 引擎/宿主写 extensions.clipboard-history 嵌套节（引擎 settings.rs 同款解析）
        var enabled = true;
        var style = ClipboardExtConfig.Default.EntryStyle;
        var capacity = ClipboardExtConfig.Default.Capacity;
        var dismiss = ClipboardExtConfig.Default.DismissMode;

        // ① 嵌套节形态：`{"extensions":{"clipboard-history":{...}}}`（引擎首次写入/手工编辑的形态）
        if (EntryTheme.Raw("extensions") is { ValueKind: JsonValueKind.Object } ext
            && ext.TryGetProperty("clipboard-history", out var ch) && ch.ValueKind == JsonValueKind.Object)
        {
            if (ch.TryGetProperty("enabled", out var en)) enabled = en.ValueKind != JsonValueKind.False;
            if (ch.TryGetProperty("entry-style", out var es) && es.ValueKind == JsonValueKind.String)
                style = es.GetString() ?? "sidebar"; // 默认侧边栏（悬浮球已于 2026-09-12 移除）
            if (ch.TryGetProperty("capacity", out var cap) && cap.ValueKind == JsonValueKind.Number)
                _ = cap.TryGetInt32(out capacity);
            if (ch.TryGetProperty("dismiss-mode", out var dm) && dm.ValueKind == JsonValueKind.String)
                dismiss = dm.GetString() ?? "auto";
        }

        // ② 扁平键**覆盖**：设置界面（ISettingsService）把 `extensions.clipboard-history.entry-style`
        //    当作**整串键名**存成平铺键，而不是嵌套对象。
        // 【2026-09-12 修复】此前只读嵌套节 → 用户在设置里改 entry-style / dismiss-mode 时，
        // 宿主侧（读扁平键）照常生效，**面板却读不到**（症状：改了设置只有面板相关行为没反应）。
        // 故此处按扁平键再覆盖一次（与宿主同源，优先级更高）。
        if (EntryTheme.Raw("extensions.clipboard-history.enabled") is { } en2)
        {
            enabled = en2.ValueKind != JsonValueKind.False;
        }
        if (EntryTheme.Raw("extensions.clipboard-history.entry-style") is { ValueKind: JsonValueKind.String } es2)
        {
            style = es2.GetString() ?? style;
        }
        if (EntryTheme.Raw("extensions.clipboard-history.capacity") is { ValueKind: JsonValueKind.Number } cap2)
        {
            _ = cap2.TryGetInt32(out capacity);
        }
        if (EntryTheme.Raw("extensions.clipboard-history.dismiss-mode") is { ValueKind: JsonValueKind.String } dm2)
        {
            dismiss = dm2.GetString() ?? dismiss;
        }

        return new ClipboardExtConfig(enabled, style, capacity, dismiss);
    }

    /// <summary>
    /// 「复制并粘贴回原窗口」的**用户自定义热键**（空 = 未启用，默认）。
    /// <para>
    /// 键：<c>extensions.clipboard-history.paste-back-hotkey</c>（设置界面写的**扁平键**；
    /// 兼容手工编辑 settings.json 的嵌套节形态）。
    /// </para>
    /// <para>
    /// 面板在**每次打开时**重读（与 <c>dismiss-mode</c> 同款）—— 用户改完设置无需重启面板。
    /// 解析与冲突判定统一走 <c>HotkeySpec</c>（单一真相源，设置界面与面板共用一份）。
    /// </para>
    /// </summary>
    public static string PasteBackHotkey()
    {
        const string flatKey = "extensions.clipboard-history.paste-back-hotkey";

        // ① 扁平键（设置界面写的就是它）
        if (EntryTheme.Raw(flatKey) is { ValueKind: JsonValueKind.String } v)
        {
            return v.GetString() ?? string.Empty;
        }

        // ② 嵌套节（手工编辑 settings.json 的形态）
        if (EntryTheme.Raw("extensions") is { ValueKind: JsonValueKind.Object } ext
            && ext.TryGetProperty("clipboard-history", out var ch)
            && ch.ValueKind == JsonValueKind.Object
            && ch.TryGetProperty("paste-back-hotkey", out var p)
            && p.ValueKind == JsonValueKind.String)
        {
            return p.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    /// <summary>
    /// 粘贴按键注入方式（2026-09-13）：<c>auto</c>（默认）/ <c>ctrl-v</c> / <c>shift-insert</c>。
    /// <para>
    /// 键：<c>extensions.clipboard-history.paste-inject-mode</c>（兼容手工编辑的嵌套节形态）。
    /// 面板每次打开时重读 —— 改完设置无需重启面板。返回**原始串**，由面板映射成枚举
    ///（本类不依赖 IPC 包，保持"只读配置"的单一职责）。
    /// </para>
    /// </summary>
    public static string PasteInjectModeSetting()
    {
        var raw = EntryTheme.Str("extensions.clipboard-history.paste-inject-mode", string.Empty);
        if (string.IsNullOrWhiteSpace(raw)
            && EntryTheme.Raw("extensions") is { ValueKind: JsonValueKind.Object } ext
            && ext.TryGetProperty("clipboard-history", out var ch)
            && ch.ValueKind == JsonValueKind.Object
            && ch.TryGetProperty("paste-inject-mode", out var p)
            && p.ValueKind == JsonValueKind.String)
        {
            raw = p.GetString() ?? string.Empty;
        }
        return string.IsNullOrWhiteSpace(raw) ? "ctrl-v" : raw;
    }

    /// <summary>
    /// 【按格粘 · 2026-09-13】"粘完一格后自动按什么键"的默认值：<c>tab</c>（默认）/ <c>enter</c> / <c>none</c>。
    /// <para>
    /// 键：<c>extensions.clipboard-history.cell-paste-auto-key</c>（设置界面写的扁平键）。
    /// 与 <see cref="PasteInjectModeSetting"/> 同规：返回**原始串**，由面板映射成枚举 ——
    /// 本类不依赖 IPC 包，保持"只读配置"的单一职责。
    /// </para>
    /// <para>
    /// 面板**不写**该键（独立 exe 无 SettingsService）：浮层里的选择只对本次会话生效，
    /// 想改长期默认请到「设置 → 剪贴板 → 面板与入口」。
    /// </para>
    /// </summary>
    public static string CellPasteAutoKeySetting()
    {
        var raw = EntryTheme.Str("extensions.clipboard-history.cell-paste-auto-key", string.Empty);
        return string.IsNullOrWhiteSpace(raw) ? "tab" : raw.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// 【P2-2 敏感遮罩】预览遮罩保留的**前缀可见字符数**（默认 3）。
    /// 键：<c>extensions.clipboard-history.sensitive-mask-leading</c>（设置界面写入的扁平键）。
    /// </summary>
    public static int SensitiveMaskLeading =>
        Math.Clamp(EntryTheme.Int("extensions.clipboard-history.sensitive-mask-leading", 3), 0, 32);

    /// <summary>【P2-2 敏感遮罩】预览遮罩保留的**后缀可见字符数**（默认 2）。</summary>
    public static int SensitiveMaskTrailing =>
        Math.Clamp(EntryTheme.Int("extensions.clipboard-history.sensitive-mask-trailing", 2), 0, 32);
}
