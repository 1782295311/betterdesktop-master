// BetterDesktop.Shell.Clipboard — 剪贴板设置分区
// （Phase B 6.5 G6/K5 配置化 + S8 引擎化增量 + 2026-09-12 控制项补齐与可编辑化）
// 键（均写入 settings.json，宿主 ClipboardPlugin 经 apply_settings 推给引擎，引擎也自行读同一份）：
//   extensions.clipboard-history.{enabled,backend,capacity,pinned-limit,retention-days,storage-mode,
//                                max-image-mb,max-image-pixels,thumb-width,max-html-image-mb,
//                                max-total-mb,file-copy-max-mb,max-text-bytes,max-content-total-mb,
//                                entry-style,dismiss-mode,push-events}
// 热键：引擎内置的 3 个为只读展示（组合键由引擎常量锁定）；
//   「粘贴回原窗口」例外 —— 由用户在设置里**自配**（2026-09-13 用户裁定：
//   "鼠标中键就可以了，如果用户想要热键，让用户自己设置就行了，我们提供入口在设置中"），
//   存 extensions.clipboard-history.paste-back-hotkey，由面板在**可见期间**注册（收起即注销）。
//
// 【2026-09-12 两轮用户反馈】
//   ① "控制太糙" → 引擎 Settings 有 14 项而界面只暴露 7 项，已按用途分卡片补齐；
//   ② "太率了…也要提供给用户修改的机会" + "存储总上限才 4 个 G，面对上万的条目上限也太不合理"
//      → 两处修正：滑条范围按真实用量放宽（总预算 4GB → 200GB、容量 1 万 → 10 万条等），
//        且数值改为**可直接输入的编辑框**（滑条大跨度下拖不准，只能靠输入精调）。

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterDesktop.Shell.Clipboard.Ipc;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.Settings.Surface;

namespace BetterDesktop.Shell.Clipboard.Sections;

/// <summary>剪贴板历史设置分区（捕获与容量 / 存储与空间 / 面板与入口 / 引擎与诊断；热键与隐私只读）。</summary>
internal sealed class ClipboardSection : ISettingsSection
{
    private const string Prefix = "extensions.clipboard-history.";

    // 【2026-09-13 TieZ 对标 P1-4/P1-5/P2】新增设置键（必须与引擎 settings.rs 及
    // ClipboardPlugin 的 WatchKeys / apply_settings patch 四处同步，否则"改了不生效"）。
    private const string AppRulesKey = Prefix + "app-rules";
    private const string NamedFormatPassthroughKey = Prefix + "named-format-passthrough";
    private const string NamedFormatMaxCountKey = Prefix + "named-format-max-count";
    private const string NamedFormatMaxKbKey = Prefix + "named-format-max-kb";
    private const string NamedFormatTotalKbKey = Prefix + "named-format-total-kb";
    private const string SensitiveDetectionKey = Prefix + "sensitive-detection";
    private const string SensitiveMaskLeadingKey = Prefix + "sensitive-mask-leading";
    private const string SensitiveMaskTrailingKey = Prefix + "sensitive-mask-trailing";

    /// <summary>
    /// 【按格粘 · 2026-09-13】"粘完一格后自动按什么键"的**默认值**（面板启动按格粘时会读它，
    /// 并允许用户当场改）。
    /// <para>
    /// ⚠️ 本键**只由面板消费**（面板直读扁平键），**不进引擎 Settings / WatchKeys / apply_settings** ——
    /// 与先例 <c>paste-back-hotkey</c> 同模式：引擎不消费的键无需"四处同步"。
    /// </para>
    /// </summary>
    private const string CellPasteAutoKeyKey = Prefix + "cell-paste-auto-key";
    private const string EnabledKey = Prefix + "enabled";
    private const string CapacityKey = Prefix + "capacity";
    private const string PinnedLimitKey = Prefix + "pinned-limit";
    private const string RetentionDaysKey = Prefix + "retention-days";
    private const string StorageModeKey = Prefix + "storage-mode";
    private const string MaxImageMbKey = Prefix + "max-image-mb";
    private const string MaxImagePixelsKey = Prefix + "max-image-pixels";
    private const string ThumbWidthKey = Prefix + "thumb-width";
    private const string MaxHtmlImageMbKey = Prefix + "max-html-image-mb";
    private const string MaxTotalMbKey = Prefix + "max-total-mb";
    private const string FileCopyMaxMbKey = Prefix + "file-copy-max-mb";
    private const string MaxTextBytesKey = Prefix + "max-text-bytes";
    private const string MaxContentTotalMbKey = Prefix + "max-content-total-mb";
    private const string EntryStyleKey = Prefix + "entry-style";
    private const string DismissModeKey = Prefix + "dismiss-mode";
    private const string PushEventsKey = Prefix + "push-events";

    /// <summary>
    /// 「复制并粘贴回原窗口」的用户自定义热键（2026-09-13）。
    /// 空 = 不启用（默认；默认入口是**面板里的鼠标中键**）。面板每次打开时读取并自行注册
    ///（仅面板可见期间生效，收起即注销）——见 shell-clipboard-panel 的 `ApplyPasteBackHotkey`。
    /// </summary>
    private const string PasteBackHotkeyKey = Prefix + "paste-back-hotkey";

    /// <summary>
    /// 粘贴按键注入方式（2026-09-13）：`auto`（默认）/ `ctrl-v` / `shift-insert`。
    /// 终端类窗口里 `Ctrl+V` 是**字面输入**而非粘贴 —— 面板据此决定注入哪种按键。
    /// </summary>
    private const string PasteInjectModeKey = Prefix + "paste-inject-mode";

    /// <summary>MB 与字节换算（<c>max-text-bytes</c> 引擎按字节存，UI 按 MB 展示与输入）。</summary>
    private const int BytesPerMb = 1024 * 1024;

    /// <summary>像素与 MP 换算（<c>max-image-pixels</c> 引擎按像素存，UI 按 MP 展示与输入）。</summary>
    private const int PixelsPerMp = 1_000_000;

    public string Title => "剪贴板";

    public string? IconKey => null;

    public UIElement Build(ISettingsService settings, IThemeTokens tokens)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };

        panel.Children.Add(BuildCaptureCard(settings, tokens));
        panel.Children.Add(BuildStorageCard(settings, tokens));
        panel.Children.Add(BuildPanelCard(settings, tokens));
        panel.Children.Add(BuildNamedFormatCard(settings, tokens));
        panel.Children.Add(BuildAppRulesCard(settings, tokens));
        panel.Children.Add(BuildEngineCard(tokens));
        panel.Children.Add(BuildHotkeyCard(settings, tokens));
        panel.Children.Add(BuildPrivacyCard(settings, tokens));

        return panel;
    }

    /// <summary>捕获与容量：开关 + 三类驱逐上限（条数/收藏/天数）。</summary>
    private static UIElement BuildCaptureCard(ISettingsService settings, IThemeTokens tokens)
    {
        var card = SettingsUi.CreateCard();
        var body = SettingsUi.CardBody(card);
        body.Children.Add(TitleBlock("捕获与容量", tokens));

        // 监听开关：此前只有扩展中心的总开关，设置分区里没有——这里给一个同键（enabled）的显式开关
        body.Children.Add(ChoiceRow(
            "剪贴板监听",
            new[] { "开启（记录复制内容）", "关闭（停止捕获）" },
            settings.Get(EnabledKey, true) ? 0 : 1,
            i => settings.Set(EnabledKey, i == 0),
            tokens));

        body.Children.Add(SliderRow(
            "历史容量上限", 100, 100_000, settings.Get(CapacityKey, 10000), "条",
            v => settings.Set(CapacityKey, v), tokens));
        body.Children.Add(SliderRow(
            "收藏上限", 10, 5_000, settings.Get(PinnedLimitKey, 200), "条",
            v => settings.Set(PinnedLimitKey, v), tokens));
        body.Children.Add(SliderRow(
            "保留天数", 7, 3_650, settings.Get(RetentionDaysKey, 90), "天",
            v => settings.Set(RetentionDaysKey, v), tokens));
        body.Children.Add(HintBlock(
            "超限时按「最旧且未收藏」顺序驱逐；收藏条目不参与驱逐（受收藏上限单独保护）。\n" +
            "数值可直接点进输入框键入（回车或点别处提交），滑条用于快速粗调。", tokens));
        return card;
    }

    /// <summary>存储与空间：存储模式 + 各类体积/尺寸预算。</summary>
    private static UIElement BuildStorageCard(ISettingsService settings, IThemeTokens tokens)
    {
        var card = SettingsUi.CreateCard();
        var body = SettingsUi.CardBody(card);
        body.Children.Add(TitleBlock("存储与空间", tokens));

        // 存储模式：完整保存 vs 仅路径（引擎 StorageMode，kebab-case 枚举值 full / paths-only）。
        body.Children.Add(ChoiceRow(
            "存储模式",
            new[] { "完整保存（图片原图 + 文件副本）", "仅路径（省空间；图片只留缩略图、文件只记路径）" },
            string.Equals(settings.Get(StorageModeKey, "full"), "paths-only", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            i => settings.Set(StorageModeKey, i == 1 ? "paths-only" : "full"),
            tokens));

        // 【范围口径】总预算必须与"容量上限"匹配：10000 条里只要混进截图/大文件，
        // 4GB 的天花板远远不够（用户 2026-09-12："存储总上限才 4 个 G…太不合理"）→ 放到 200GB。
        body.Children.Add(SliderRow(
            "总存储预算", 200, 204_800, settings.Get(MaxTotalMbKey, 1024), "MB",
            v => settings.Set(MaxTotalMbKey, v), tokens));
        body.Children.Add(SliderRow(
            "单张图片上限", 5, 2_048, settings.Get(MaxImageMbKey, 64), "MB",
            v => settings.Set(MaxImageMbKey, v), tokens));
        body.Children.Add(SliderRow(
            "图像素上限", 10, 500, settings.Get(MaxImagePixelsKey, 100_000_000), "MP",
            v => settings.Set(MaxImagePixelsKey, v * PixelsPerMp), tokens, PixelsPerMp));
        body.Children.Add(SliderRow(
            "缩略图宽度", 160, 2_048, settings.Get(ThumbWidthKey, 480), "px",
            v => settings.Set(ThumbWidthKey, v), tokens));
        body.Children.Add(SliderRow(
            "文件副本单文件上限", 8, 4_096, settings.Get(FileCopyMaxMbKey, 64), "MB",
            v => settings.Set(FileCopyMaxMbKey, v), tokens));
        body.Children.Add(SliderRow(
            "HTML 内嵌图单张上限", 1, 256, settings.Get(MaxHtmlImageMbKey, 2), "MB",
            v => settings.Set(MaxHtmlImageMbKey, v), tokens));
        body.Children.Add(SliderRow(
            "单条文本上限", 1, 512, Math.Max(1, settings.Get(MaxTextBytesKey, 10 * BytesPerMb) / BytesPerMb), "MB",
            v => settings.Set(MaxTextBytesKey, v * BytesPerMb), tokens));
        body.Children.Add(SliderRow(
            "文本总量上限", 10, 20_480, settings.Get(MaxContentTotalMbKey, 100), "MB",
            v => settings.Set(MaxContentTotalMbKey, v), tokens));
        body.Children.Add(HintBlock(
            "超限内容按规则降级：图片只留缩略图、文件只记路径、超长文本拒绝捕获（不会把单条撑爆整个预算）。\n" +
            "「总存储预算」是所有占用之和（图片原图 + 文件副本 + HTML 提取图 + 文本压缩），超限驱逐最旧未收藏条目。", tokens));
        return card;
    }

    /// <summary>面板与入口：入口形态 + 收起方式 + 事件推送。</summary>
    private static UIElement BuildPanelCard(ISettingsService settings, IThemeTokens tokens)
    {
        var card = SettingsUi.CreateCard();
        var body = SettingsUi.CardBody(card);
        body.Children.Add(TitleBlock("面板与入口", tokens));

        body.Children.Add(ChoiceRow(
            "快捷入口形态",
            new[] { "侧边栏（屏幕右缘「›」手柄）", "关闭（仅热键 / 菜单入口）" },
            // 【2026-09-12】移除「侧边栏 + 悬浮球」「悬浮球」两项 —— 悬浮球已被用户否决，
            // 保留会让用户选到一个不会生效的形态（设置 UI 与实际行为不符）；历史值 orb/both 显示为侧边栏。
            settings.Get(EntryStyleKey, "sidebar") switch
            {
                "off" => 1,
                _ => 0,
            },
            i => settings.Set(EntryStyleKey, i == 1 ? "off" : "sidebar"),
            tokens));

        body.Children.Add(ChoiceRow(
            "收起方式",
            new[] { "点面板外自动收起", "手动收起（点「✕」或 Esc）" },
            string.Equals(settings.Get(DismissModeKey, "auto"), "manual", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            i => settings.Set(DismissModeKey, i == 1 ? "manual" : "auto"),
            tokens));
        body.Children.Add(HintBlock(
            "「手动收起」下点面板外不会消失，适合边看历史边在其他窗口操作；面板底部会提示当前方式。\n" +
            "入口形态与收起方式由面板进程读取（改动后重启面板生效：结束 BetterDesktop.Clipboard.Panel 进程后重新触发入口）。", tokens));

        // 【2026-09-13 · 对标 TieZ 的"三档注入"，先落两档】少数终端（mintty / PuTTY）不认 Ctrl+V，
        // 靠 Shift+Insert 粘贴。**默认仍是 Ctrl+V** —— 用户实测 cmd / PowerShell / Windows Terminal
        // 都支持 Ctrl+V，不能拿一个过时印象去改本来能用的行为（详见 PasteInjectMode 类注释）。
        body.Children.Add(ChoiceRow(
            "粘贴按键方式",
            new[]
            {
                "总是 Ctrl+V（默认；cmd / PowerShell / Windows Terminal 均适用）",
                "自动（仅 mintty / PuTTY 这类终端换 Shift+Insert）",
                "总是 Shift+Insert",
            },
            settings.Get(PasteInjectModeKey, "ctrl-v")?.Trim().ToLowerInvariant() switch
            {
                "auto" => 1,
                "shift-insert" or "shiftinsert" => 2,
                _ => 0,
            },
            i => settings.Set(PasteInjectModeKey, i switch
            {
                1 => "auto",
                2 => "shift-insert",
                _ => "ctrl-v",
            }),
            tokens));
        body.Children.Add(HintBlock(
            "cmd / PowerShell / Windows Terminal **都支持 Ctrl+V**，默认保持原行为。\n" +
            "只有在 Git Bash（mintty）、PuTTY 这类终端里粘不上时，才需要切到「自动」或「总是 Shift+Insert」。", tokens));

        body.Children.Add(ChoiceRow(
            "剪贴板事件推送",
            new[] { "开启（供灵动岛等订阅方）", "关闭" },
            settings.Get(PushEventsKey, true) ? 0 : 1,
            i => settings.Set(PushEventsKey, i == 0),
            tokens));

        // 【按格粘 · 2026-09-13】粘完一格后的自动按键**默认值**。
        // 用户口径："表格内容的形式多种多样，我们固定的形式无法应对" → 给选择而不是写死；
        // 启动「按格粘」时还能当场改，这里只定默认。
        body.Children.Add(ChoiceRow(
            "按格粘：粘完自动",
            new[] { "Tab（跳到下一格）", "Enter（换行 / 确认）", "不自动（只粘贴）" },
            (settings.Get(CellPasteAutoKeyKey, "tab") ?? "tab").Trim().ToLowerInvariant() switch
            {
                "none" => 2,
                "enter" => 1,
                _ => 0,
            },
            i => settings.Set(CellPasteAutoKeyKey, i switch
            {
                1 => "enter",
                2 => "none",
                _ => "tab",
            }),
            tokens));
        body.Children.Add(HintBlock(
            "「按格粘」= 把 Excel/WPS 表格条目**逐格**粘到业务系统：每按一次 Ctrl+V 粘一个格子（顺序＝行优先）。\n" +
            "表格与目标系统千差万别，故自动按键只在这里定默认，启动「按格粘」时还可以临时改。", tokens));
        return card;
    }

    /// <summary>引擎与诊断：运行状态快照。</summary>
    private static UIElement BuildEngineCard(IThemeTokens tokens)
    {
        var card = SettingsUi.CreateCard();
        var body = SettingsUi.CardBody(card);
        body.Children.Add(TitleBlock("引擎与诊断", tokens));
        body.Children.Add(HintBlock(
            "数据面（监听/捕获/去重/存储/查询/热键）全部在 Rust 引擎进程，宿主与面板只经 IPC 消费。", tokens));

        body.Children.Add(EngineStatusBlock(tokens));
        return card;
    }

    private static UIElement BuildHotkeyCard(ISettingsService settings, IThemeTokens tokens)
    {
        var card = SettingsUi.CreateCard();
        var body = SettingsUi.CardBody(card);
        body.Children.Add(TitleBlock("快捷键", tokens));
        body.Children.Add(HintBlock(
            "全局热键（由剪贴板引擎注册，全局唯一，不支持改键）：\n" +
            "Ctrl+Shift+V 打开历史面板（按序粘贴激活时改为粘贴下一条）\n" +
            "Ctrl+Shift+P 收藏视图\n" +
            "Ctrl+Shift+Backspace 暂停/恢复捕获", tokens));

        // 【2026-09-13】"粘贴回原窗口"从只读改为**用户自配** —— 该动作的默认入口是面板里的
        // **鼠标中键**（不占键、不用记）；键盘等价入口交给用户按自己的手型去配
        //（我们猜组合键猜了四轮都对不上，见 1301 技术库"高频动作必须一次动作完成"）。
        body.Children.Add(HotkeyRow("粘贴回原窗口", PasteBackHotkeyKey, settings, tokens));
        body.Children.Add(HintBlock(
            "在面板里按此键 = 把当前选中条目**直接粘贴回**打开面板前的那个窗口（等同鼠标中键单击）。\n" +
            "点输入框后按下想用的组合键即可录制；按 Backspace 或点「清除」停用。\n" +
            "需含至少一个修饰键（Ctrl / Alt / Shift / Win），且不能与上面的全局热键重复。\n" +
            "仅面板打开期间有效（收起即释放，不长期占用系统热键）。", tokens));
        return card;
    }

    /// <summary>
    /// 「热键录制」一行：只读输入框显示当前组合，点它后按下按键即录制。
    /// <para>
    /// 用只读 TextBox 而非自绘按钮 —— 它天然可聚焦、语义就是"点一下然后按键"，
    /// 且我们本来就 `Handled = true` 全拦按键，不存在被当成文本框输入的问题。
    /// </para>
    /// </summary>
    private static UIElement HotkeyRow(
        string label,
        string key,
        ISettingsService settings,
        IThemeTokens tokens)
    {
        var current = settings.Get(key, string.Empty) ?? string.Empty;

        var box = new TextBox
        {
            Width = 168,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            Text = DescribeHotkey(current),
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            Foreground = tokens.Foreground,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(2, 4, 2, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "点这里，然后按下想用的组合键（需含 Ctrl / Alt / Shift / Win）\n按 Backspace 清除",
        };

        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true; // 录制框：所有按键都由我们处理，不当作文本输入

            // Alt 组合时真实按键在 SystemKey（e.Key 只会是 Key.System）
            var pressed = e.Key == Key.System ? e.SystemKey : e.Key;

            if (pressed is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
            {
                settings.Set(key, string.Empty);
                box.Text = DescribeHotkey(string.Empty);
                return;
            }

            if (!TryBuildHotkey(Keyboard.Modifiers, pressed, out var spec, out var reason))
            {
                box.Text = reason; // 就地说明为什么没录上，别让用户以为"点了没反应"
                return;
            }

            settings.Set(key, spec);
            box.Text = HotkeySpec.Pretty(spec);
        };

        var clear = new Button
        {
            Content = "清除",
            FontSize = 11,
            Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        clear.Click += (_, _) =>
        {
            settings.Set(key, string.Empty);
            box.Text = DescribeHotkey(string.Empty);
        };

        var host = new StackPanel { Orientation = Orientation.Horizontal };
        host.Children.Add(box);
        host.Children.Add(clear);
        return Labeled(label, host, tokens);
    }

    private static string DescribeHotkey(string spec)
        => string.IsNullOrWhiteSpace(spec) ? "（未设置，点此录制）" : HotkeySpec.Pretty(spec);

    /// <summary>
    /// 录制结果校验：主键不能还是修饰键、必须至少一个修饰键、不能与引擎内置全局热键冲突。
    /// 主键名统一用 WPF <c>Key</c> 枚举名（面板侧据此换虚拟键码），见 <c>HotkeySpec</c> 类注释。
    /// </summary>
    private static bool TryBuildHotkey(ModifierKeys modifiers, Key key, out string spec, out string reason)
    {
        spec = string.Empty;
        reason = string.Empty;

        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            reason = "再按一个主键"; // 只按住修饰键 —— 组合还不完整
            return false;
        }

        if (modifiers == ModifierKeys.None)
        {
            reason = "需加 Ctrl/Alt/Shift"; // 裸键太易与系统/其它程序冲突
            return false;
        }

        var candidate = HotkeySpec.Build(
            modifiers.HasFlag(ModifierKeys.Control),
            modifiers.HasFlag(ModifierKeys.Shift),
            modifiers.HasFlag(ModifierKeys.Alt),
            modifiers.HasFlag(ModifierKeys.Windows),
            key.ToString());

        if (HotkeySpec.ConflictsWithBuiltIn(candidate))
        {
            reason = "与全局热键冲突";
            return false;
        }

        spec = candidate;
        return true;
    }

    /// <summary>【P1-4】格式透传：保留 Excel/WPS 表格等自定义命名格式 + 三重体积上限。</summary>
    private static UIElement BuildNamedFormatCard(ISettingsService settings, IThemeTokens tokens)
    {
        var card = SettingsUi.CreateCard();
        var body = SettingsUi.CardBody(card);
        body.Children.Add(TitleBlock("格式透传", tokens));

        body.Children.Add(ChoiceRow(
            "命名格式透传",
            new[] { "开启（保留 Excel / WPS 表格等可编辑数据）", "关闭（只留文字，更省空间）" },
            settings.Get(NamedFormatPassthroughKey, true) ? 0 : 1,
            i => settings.Set(NamedFormatPassthroughKey, i == 0),
            tokens));
        body.Children.Add(HintBlock(
            "从 Excel / WPS 表格复制的内容，除文字外还带一份**应用自定义数据**；保留它，粘贴回表格仍是可编辑表格。\n" +
            "关闭后这类内容只能粘成纯文本（会丢表格结构），但占用更小。", tokens));

        body.Children.Add(SliderRow(
            "单条最多格式数", 1, 32, settings.Get(NamedFormatMaxCountKey, 8), "个",
            v => settings.Set(NamedFormatMaxCountKey, v), tokens));
        body.Children.Add(SliderRow(
            "单个格式上限", 64, 8_192, settings.Get(NamedFormatMaxKbKey, 1024), "KB",
            v => settings.Set(NamedFormatMaxKbKey, v), tokens));
        body.Children.Add(SliderRow(
            "单条格式总量", 256, 32_768, settings.Get(NamedFormatTotalKbKey, 4096), "KB",
            v => settings.Set(NamedFormatTotalKbKey, v), tokens));
        body.Children.Add(HintBlock("超限的格式会被**跳过**（不截断，截断后的二进制不可用）。默认值适配单个表格区域。", tokens));
        return card;
    }

    /// <summary>
    /// 【P1-5】按应用清洗规则：结构化编辑器（应用 + 动作 + 正则 + 替换），存 JSON 数组字符串。
    /// <para>
    /// 规则在引擎侧编译执行；非法正则**不会崩溃**（引擎跳过该条并记日志），本界面只做即时红字提示。
    /// </para>
    /// </summary>
    private static UIElement BuildAppRulesCard(ISettingsService settings, IThemeTokens tokens)
    {
        var card = SettingsUi.CreateCard();
        var body = SettingsUi.CardBody(card);
        body.Children.Add(TitleBlock("按应用清洗规则", tokens));
        body.Children.Add(HintBlock(
            "某个应用总复制垃圾？**自己**设规则治它，不必等我们猜：\n" +
            "· 忽略该应用 —— 它复制的内容一律不记录\n" +
            "· 正则丢弃 —— 命中正则的内容不记录（如纯链接、验证码）\n" +
            "· 正则替换 —— 命中的部分替换后再记录（如把手机号打码）\n" +
            "「应用」填进程名或窗口标题的**子串**（如 wechat / 记事本）；留空 = 对所有应用生效。", tokens));

        var rows = new StackPanel { Orientation = Orientation.Vertical };
        body.Children.Add(rows);

        // 保存：把当前所有行收集为规则数组并写回设置（JSON 字符串）。
        void SaveRules()
        {
            var list = new List<ClipboardAppRule>();
            foreach (var child in rows.Children)
            {
                if (child is FrameworkElement { Tag: RuleRowState state })
                {
                    list.Add(state.ToRule());
                }
            }

            settings.Set(AppRulesKey, JsonSerializer.Serialize(list));
        }

        void AddRow(ClipboardAppRule rule) =>
            rows.Children.Add(BuildRuleRow(rule, rows, SaveRules, tokens));

        foreach (var rule in ParseAppRules(settings.Get(AppRulesKey, string.Empty)))
        {
            AddRow(rule);
        }

        var add = new Button
        {
            Content = "＋ 添加规则",
            FontSize = 12,
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.Hand,
        };
        add.Click += (_, _) => AddRow(new ClipboardAppRule());
        body.Children.Add(add);
        return card;
    }

    /// <summary>单条规则的编辑行（两行布局：应用/动作/删除 + 正则/替换）。</summary>
    private static FrameworkElement BuildRuleRow(
        ClipboardAppRule rule,
        Panel host,
        Action save,
        IThemeTokens tokens)
    {
        var state = new RuleRowState
        {
            App = new ComboBox
            {
                IsEditable = true,
                Width = 150,
                Text = rule.App,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Action = new ComboBox
            {
                Width = 120,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            },
        };
        state.Action.Items.Add("忽略该应用");
        state.Action.Items.Add("正则丢弃");
        state.Action.Items.Add("正则替换");
        state.Action.SelectedIndex = rule.Action switch
        {
            "drop" => 1,
            "replace" => 2,
            _ => 0,
        };

        state.Pattern = MakeRuleTextBox(rule.Pattern, 12);
        state.Pattern.MinWidth = 180;
        state.Replacement = MakeRuleTextBox(rule.Replacement, 12);
        state.Replacement.Width = 140;

        var remove = new Button
        {
            Content = "删除",
            FontSize = 11,
            Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var border = new Border
        {
            Tag = state,
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(8, 6, 8, 6),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
        };
        border.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");

        // 第一行：应用 / 动作 / 删除
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(state.App);
        top.Children.Add(state.Action);
        top.Children.Add(remove);

        // 第二行：正则 / 替换为
        var bottom = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 0),
        };
        bottom.Children.Add(new TextBlock
        {
            Text = "正则",
            FontSize = 11,
            Width = 32,
            Foreground = tokens.MutedForeground,
            VerticalAlignment = VerticalAlignment.Center,
        });
        bottom.Children.Add(state.Pattern);
        bottom.Children.Add(new TextBlock
        {
            Text = "替换为",
            FontSize = 11,
            Width = 46,
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = tokens.MutedForeground,
            VerticalAlignment = VerticalAlignment.Center,
        });
        bottom.Children.Add(state.Replacement);

        var content = new StackPanel { Orientation = Orientation.Vertical };
        content.Children.Add(top);
        content.Children.Add(bottom);
        border.Child = content;

        // 动作切换：ignore 时无需正则/替换 → 置灰但仍保留文本（避免切换动作丢失用户输入）。
        void SyncActionEnabled()
        {
            bool needsRegex = state.Action.SelectedIndex != 0;
            state.Pattern.IsEnabled = needsRegex;
            state.Replacement.IsEnabled = needsRegex;
            state.Pattern.Opacity = needsRegex ? 1.0 : 0.5;
            state.Replacement.Opacity = needsRegex ? 1.0 : 0.5;
        }

        // 正则即时校验：非法只做红框 + 提示（引擎侧会安全跳过该条，不会崩）。
        void ValidatePattern()
        {
            var pattern = state.Pattern.Text ?? string.Empty;
            if (string.IsNullOrEmpty(pattern) || state.Action.SelectedIndex == 0)
            {
                state.Pattern.ClearValue(Border.BorderBrushProperty);
                state.Pattern.ToolTip = "输入正则；如 \\d{4}-\\d{4} 匹配分组数字";
                return;
            }

            try
            {
                _ = new Regex(pattern);
                state.Pattern.ClearValue(Border.BorderBrushProperty);
                state.Pattern.ToolTip = "正则有效";
            }
            catch (ArgumentException ex)
            {
                state.Pattern.BorderBrush = Brushes.Red;
                state.Pattern.ToolTip = $"正则非法（引擎将跳过本条）：{ex.Message}";
            }
        }

        state.Action.SelectionChanged += (_, _) =>
        {
            SyncActionEnabled();
            ValidatePattern();
            save();
        };
        state.App.SelectionChanged += (_, _) => save();
        state.App.LostFocus += (_, _) => save();
        state.Pattern.TextChanged += (_, _) => ValidatePattern();
        state.Pattern.LostFocus += (_, _) => save();
        state.Replacement.LostFocus += (_, _) => save();
        state.Pattern.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                save();
                e.Handled = true;
            }
        };
        remove.Click += (_, _) =>
        {
            host.Children.Remove(border);
            save();
        };

        SyncActionEnabled();
        ValidatePattern();
        return border;
    }

    private static TextBox MakeRuleTextBox(string text, double fontSize)
    {
        var box = new TextBox
        {
            Text = text ?? string.Empty,
            FontSize = fontSize,
            Padding = new Thickness(2, 2, 2, 2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 0, 1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.SetResourceReference(Control.ForegroundProperty, "ThemeForeground");
        box.SetResourceReference(Control.BorderBrushProperty, "CardBorderBrush");
        return box;
    }

    /// <summary>解析设置里的规则 JSON；损坏时返回空（不阻断分区构建）。</summary>
    private static List<ClipboardAppRule> ParseAppRules(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<ClipboardAppRule>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<ClipboardAppRule>>(json) ?? new List<ClipboardAppRule>();
        }
        catch (JsonException)
        {
            return new List<ClipboardAppRule>();
        }
    }

    /// <summary>规则行 → 控件的引用集合（保存时收集为 <see cref="ClipboardAppRule"/>）。</summary>
    private sealed class RuleRowState
    {
        public ComboBox App = null!;

        public ComboBox Action = null!;

        public TextBox Pattern = null!;

        public TextBox Replacement = null!;

        public ClipboardAppRule ToRule() => new()
        {
            App = (App.Text ?? string.Empty).Trim(),
            Action = Action.SelectedIndex switch
            {
                1 => "drop",
                2 => "replace",
                _ => "ignore",
            },
            Pattern = Pattern.Text ?? string.Empty,
            Replacement = Replacement.Text ?? string.Empty,
        };
    }

    /// <summary>隐私：仅说明 + 敏感信息识别（识别只影响预览显示，不改内容）。</summary>
    private static UIElement BuildPrivacyCard(ISettingsService settings, IThemeTokens tokens)
    {
        var card = SettingsUi.CreateCard();
        var body = SettingsUi.CardBody(card);
        body.Children.Add(TitleBlock("隐私", tokens));
        body.Children.Add(HintBlock(
            "密码管理器/网银/验证码等敏感窗口中的复制内容默认不记录；\n" +
            "暂停期间不捕获，历史可正常查询与粘贴。", tokens));

        body.Children.Add(ChoiceRow(
            "敏感信息识别",
            new[] { "开启（列表预览里自动遮罩手机号 / 身份证 / 邮箱等）", "关闭" },
            settings.Get(SensitiveDetectionKey, true) ? 0 : 1,
            i => settings.Set(SensitiveDetectionKey, i == 0),
            tokens));
        body.Children.Add(SliderRow(
            "遮罩：前可见位数", 0, 12, settings.Get(SensitiveMaskLeadingKey, 3), "字",
            v => settings.Set(SensitiveMaskLeadingKey, v), tokens));
        body.Children.Add(SliderRow(
            "遮罩：后可见位数", 0, 12, settings.Get(SensitiveMaskTrailingKey, 2), "字",
            v => settings.Set(SensitiveMaskTrailingKey, v), tokens));
        body.Children.Add(HintBlock(
            "识别**只影响列表预览的显示** —— 复制/粘贴出去的仍是完整内容（不改变数据）。\n" +
            "命中的条目会带上类别标签（手机号/身份证/邮箱/银行卡/密钥），可用 `tag:手机号` 等搜索。", tokens));
        return card;
    }

    /// <summary>引擎/面板运行状态快照（设置在打开时构建，属快照；重开设置刷新）。</summary>
    /// <remarks>
    /// 【2026-09-20 收敛】原先这里显示"引擎路径/面板路径"——那是本进程自己解析出来的候选路径，
    /// 而**组件 exe 从哪来是 core 的职责**（core/src/process.rs::resolve_exe）。设置里再显示一份
    /// 自己算的路径，只会在两边不一致时把人引向错误方向。现在只报"core 判定的运行状态"。
    /// </remarks>
    private static UIElement EngineStatusBlock(IThemeTokens tokens)
    {
        bool engineRunning = ClipboardEngineLauncher.IsEngineRunning();
        bool panelRunning = ClipboardEngineLauncher.IsPanelRunning();

        string text =
            $"引擎状态：{(engineRunning ? "运行中" : "未运行（首次触发入口/宿主启动会自动拉起）")}\n" +
            $"面板入口：{(panelRunning ? "运行中" : "未运行（右键/热键首次触发时装配）")}\n" +
            "（运行状态由 core 按组件表判定；本页不展示 exe 路径 —— 那是 core 的职责）";
        return HintBlock(text, tokens);
    }

    private static TextBlock TitleBlock(string text, IThemeTokens tokens) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Foreground = tokens.Foreground,
        Margin = new Thickness(0, 0, 0, 6),
    };

    private static TextBlock HintBlock(string text, IThemeTokens tokens) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = tokens.MutedForeground,
        Margin = new Thickness(0, 6, 0, 2),
        TextWrapping = TextWrapping.Wrap,
    };

    private static ComboBox ComboRow(IThemeTokens tokens) => WithStyle(new ComboBox
    {
        Width = 260,
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, 6, 0, 0),
    }, "MacCombo", tokens);

    /// <summary>
    /// 多选一行（下拉）。收起方式/存储模式/入口形态/后端模式等"枚举型"设置统一走这里，
    /// 避免每处重复写 SelectedIndex 映射。
    /// </summary>
    private static UIElement ChoiceRow(
        string label,
        string[] items,
        int selectedIndex,
        Action<int> onChange,
        IThemeTokens tokens)
    {
        var combo = ComboRow(tokens);
        foreach (var item in items)
        {
            combo.Items.Add(item);
        }
        combo.SelectedIndex = Math.Clamp(selectedIndex, 0, items.Length - 1);
        combo.SelectionChanged += (_, _) => onChange(combo.SelectedIndex);
        return Labeled(label, combo, tokens);
    }

    private static UIElement Labeled(string label, FrameworkElement control, IThemeTokens tokens)
    {
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = tokens.Foreground,
            Width = 110,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(labelText, System.Windows.Controls.Dock.Left);
        row.Children.Add(labelText);
        row.Children.Add(control);
        return row;
    }

    private static T WithStyle<T>(T element, string key, IThemeTokens tokens) where T : FrameworkElement
    {
        element.Style = (Style)element.FindResource(key);
        return element;
    }

    /// <summary>
    /// 一行「标签 + 滑条 + 可输入数值」。
    /// <para>
    /// <b>DockPanel 铁律</b>：填充剩余空间的必须是**最后一个 Add** 的子元素。若把标签放最后，
    /// 标签会占满剩余宽度、滑条被压成 0 宽（只剩一个 Thumb 圆点，用户以为"只给看默认值、不让改"）。
    /// 故顺序固定为 label(Left) → value(Right) → slider(最后=填充)。
    /// </para>
    /// <para>
    /// <b>为什么数值要可输入</b>（2026-09-12 用户反馈）：滑条范围按真实用量放宽后（如总存储预算
    /// 200MB–200GB），纯拖动精度不够；且"只能拖、不能填"的观感就是"不给改"。故数值改为编辑框，
    /// 回车/失焦提交，非法输入回退。滑条不吸附刻度（连续可拖），精确值靠输入。
    /// </para>
    /// <para>
    /// <paramref name="displayDivisor"/>：内部实际值 → 显示值的折算（像素上限内部存
    /// <c>100_000_000</c> 而显示 <c>100 MP</c>；其余为 1）。
    /// </para>
    /// </summary>
    private static UIElement SliderRow(
        string label,
        int min,
        int max,
        int value,
        string unit,
        Action<int> onChange,
        IThemeTokens tokens,
        int displayDivisor = 1)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };

        var slider = WithStyle(new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value / displayDivisor, min, max),
            IsSnapToTickEnabled = false, // 大跨度下不吸附，连续可拖（精确值用输入框）
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 160,
        }, "MacSlider", tokens);

        var valueBox = new TextBox
        {
            Text = (value / displayDivisor).ToString(),
            Width = 68,
            FontSize = 11,
            TextAlignment = TextAlignment.Right,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 2, 1),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "可直接输入数值（回车或点别处提交）",
        };
        valueBox.SetResourceReference(Control.ForegroundProperty, "ThemeForeground");
        valueBox.SetResourceReference(Control.BorderBrushProperty, "CardBorderBrush");

        var unitText = new TextBlock
        {
            Text = " " + unit,
            FontSize = 11,
            Foreground = tokens.MutedForeground,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        right.Children.Add(valueBox);
        right.Children.Add(unitText);
        DockPanel.SetDock(right, System.Windows.Controls.Dock.Right);

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = tokens.Foreground,
            Width = 110,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(labelText, System.Windows.Controls.Dock.Left);

        // 输入提交：clamp 到范围后写回（滑条同步 + 落盘）
        void Commit()
        {
            if (int.TryParse(valueBox.Text.Trim(), out var typed))
            {
                var actual = Math.Clamp(typed, min, max) * displayDivisor;
                valueBox.Text = (actual / displayDivisor).ToString();
                slider.Value = actual; // 值有变则触发 ValueChanged → onChange
                onChange(actual);      // 值相同（滑条未变）时也保证落盘
            }
            else
            {
                // 非法输入回退为当前滑条值（注意折算 divisor，否则 MP 类字段会显示成原始像素数）
                valueBox.Text = ((int)slider.Value / displayDivisor).ToString();
            }
        }

        valueBox.LostFocus += (_, _) => Commit();
        valueBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };

        slider.ValueChanged += (_, _) =>
        {
            var v = (int)slider.Value;
            // 正在输入时不要覆盖用户键的字
            if (!valueBox.IsFocused)
            {
                valueBox.Text = (v / displayDivisor).ToString();
            }
            onChange(v);
        };

        dock.Children.Add(labelText);
        dock.Children.Add(right);
        dock.Children.Add(slider); // 最后 Add = 填充剩余宽度
        return dock;
    }
}
