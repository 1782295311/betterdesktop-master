using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterDesktop.Shell.Clipboard.Ipc;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Hotkeys.Contracts;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.Settings.Surface;

namespace BetterDesktop.Shell.HotkeyPanel.Sections;

/// <summary>改键/停用的应用结果（true=成功；Message 给用户看的状态，不假装生效）。</summary>
internal readonly record struct HotkeyApplyResult(bool Ok, string Message);

/// <summary>
/// 热键设置分区（设置中心"热键"分节）——**正常窗口改键**（用户裁定"侧板改键反人类"：
/// 侧板是常驻透明浮层，行内录键受 WS_EX_NOACTIVATE/焦点限制；改键集中到常规可激活窗口，
/// 键盘输入可达）。
/// <para>【2026-09-16 用户三轮反馈后定稿】</para>
/// <para>① 取消"只读"概念：capture / 引擎条目与 paste-back 一样**行内直改**（点键位直接录键）；
/// ② 每行有管理操作（改键 = 点键位；停用 = Backspace 或按钮；启用 = 恢复默认键）；
/// ③ 生效链路真实接通并**如实报告**（重启成功 / 未运行下次生效 / 重启失败三态）；
/// ④ 新增「第三方热键」卡（扫描占用 + 常见软件默认键参考）；
/// ⑤ 系统热键卡默认折叠，避免 20+ 行淹没真正可改的项。</para>
/// </summary>
public sealed class HotkeySettingsSection : ISettingsSection
{
    /// <summary>扫描互斥：两个扫描同时跑会互把对方的"试注册"当成"被占用"→ 必须串行。</summary>
    private static readonly object ScanGate = new();

    /// <summary>扫描结果缓存（分区来回切换不重复扫描；点"重新扫描"强制刷新）。</summary>
    private static HotkeyScanResult? _cachedScan;

    /// <summary>上一次扫描（用于"对比差异"定位未知归属：退出可疑程序 → 重扫 → 看哪些键被释放）。</summary>
    private static HotkeyScanResult? _previousScan;

    /// <summary>最近一次扫描的差异文案（懒显示，不随重渲染丢失）。</summary>
    private static string? _lastDeltaText;

    private readonly IHotkeyRegistryService _registry;

    public HotkeySettingsSection(IHotkeyRegistryService registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public string Title => "热键";

    public string? IconKey => null;

    // ── 主题令牌取色（界面颜色一律走 Application.Resources 令牌，禁止硬编码；见 ThemeBrushes） ──

    private static Brush ThemeMuted => ThemeBrushes.Get("ThemeMutedForeground");
    private static Brush ThemeAccent => ThemeBrushes.Get("SkinAccentFromSkin");
    private static Brush ThemeDanger => new SolidColorBrush(ThemeBrushes.DangerColor);
    private static Brush ThemeWarning => new SolidColorBrush(ThemeBrushes.WarningColor);

    /// <summary>强调色底上的前景（如录键态按钮文字）。</summary>
    private static Brush ThemeOnAccent => ThemeBrushes.Get("ControlForeground");

    public UIElement Build(ISettingsService settings, IThemeTokens tokens)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };

        // 子树内所有按钮统一为胶囊 + hover/pressed 反馈（隐式样式，见 HotkeyPanelVisuals）
        panel.Resources.Add(typeof(Button), HotkeyPanelVisuals.PillButtonStyle);

        panel.Children.Add(BuildPanelCard(settings, tokens));
        panel.Children.Add(BuildCustomizableCard(settings, tokens));
        panel.Children.Add(BuildExternalCard(settings, tokens));
        panel.Children.Add(BuildThirdPartyCard(settings, tokens));
        panel.Children.Add(BuildAppHotkeysCard(tokens));
        panel.Children.Add(BuildSystemCard(tokens));
        panel.Children.Add(BuildDetectorCard(tokens));

        return panel;
    }

    // ---------------- 热键侧板（显隐开关 + 关闭后的"回家路"，2026-09-17） ----------------

    /// <summary>
    /// 侧板显隐卡。勾选框与侧板右键菜单 / 切换热键**同写一个设置键**（<see cref="HotkeyPanelSettings.EnabledKey"/>），
    /// 落地由 <c>HotkeyPanelPlugin</c> 统一执行 —— 不会出现"菜单关了、设置里还显示开"的漂移。
    /// <para>为什么这张卡必须在最上面：侧板一旦关掉，右键菜单随之消失 —— 本页与切换热键是仅剩的两条回家路。</para>
    /// </summary>
    private static UIElement BuildPanelCard(ISettingsService settings, IThemeTokens tokens)
    {
        var card = SettingsUi.CreateCard();
        var body = SettingsUi.CardBody(card);
        body.Children.Add(TitleBlock("热键侧板", tokens));
        body.Children.Add(HintBlock(
            "侧板常驻屏幕右侧（可拖动，位置会记住）：只读态**完全点击穿透**，不影响任何点击；"
            + "按住右 Alt 指向侧板即进入可操作态（行内「隐藏」/「已忽略 N 项 · 管理」/ 右键菜单）。\n"
            + "关闭：可操作态下**右键侧板 → 取消「显示热键侧板」**，或点底部「关闭侧板」。\n"
            + "关掉后按 " + HotkeyPanelSettings.ToggleHotkeyDefault + " 或勾选下面的开关随时再打开"
            + "（键位在本页「全局热键」区可改、可停用；被别的程序占用时会如实提示冲突）。",
            tokens));

        var box = new CheckBox
        {
            Content = "在屏幕右侧常驻显示热键侧板",
            Margin = new Thickness(0, 10, 0, 0),
            FontSize = 13,
            IsChecked = HotkeyPanelSettings.IsEnabled(settings),
        };
        if (Application.Current?.Resources["MacToggle"] is Style toggleStyle)
        {
            box.Style = toggleStyle;
        }
        box.Checked += (_, _) => HotkeyPanelSettings.SetEnabled(settings, true);
        box.Unchecked += (_, _) => HotkeyPanelSettings.SetEnabled(settings, false);
        body.Children.Add(box);

        return card;
    }

    // ---------------- 可自定义（paste-back，宿主/面板读配置生效） ----------------

    private UIElement BuildCustomizableCard(ISettingsService settings, IThemeTokens tokens)
    {
        var card = SettingsUi.CreateCard();
        var body = SettingsUi.CardBody(card);
        body.Children.Add(TitleBlock("可自定义热键", tokens));
        body.Children.Add(HintBlock(
            "点键位直接录新组合键（需含 Ctrl / Alt / Shift / Win），Backspace 或「清除」停用（回默认入口）。",
            tokens));

        var wired = _registry.GetAll().Where(v => HotkeyDeclarations.IsWired(v.Binding.Id)).ToList();
        if (wired.Count > 0)
        {
            foreach (var view in wired)
            {
                body.Children.Add(PasteBackRow(view, settings, tokens));
            }
        }
        else
        {
            // 首次设置入口：paste-back 尚未配置/声明时卡片为空，用户无从录制第一个键。
            body.Children.Add(PasteBackRowForDescription("粘贴回原窗口（面板打开时生效）", settings, tokens));
        }

        return card;
    }

    private UIElement PasteBackRow(HotkeyView view, ISettingsService settings, IThemeTokens tokens)
        => PasteBackRowForDescription(view.Binding.Description, settings, tokens);

    private UIElement PasteBackRowForDescription(string description, ISettingsService settings, IThemeTokens tokens)
    {
        var key = HotkeyDeclarations.PasteBackHotkeyKey;
        string current = settings.Get(key, string.Empty) ?? string.Empty;

        return ManageableHotkeyRow(
            id: "clipboard.paste-back",
            description: description,
            currentSpec: current,
            defaultSpec: null, // paste-back 无"恢复默认键"（清除 = 回鼠标中键入口）
            ownerAlive: true,
            applySpec: spec =>
            {
                ApplyPasteBack(settings, spec);
                return new HotkeyApplyResult(
                    true,
                    ClipboardEngineLauncher.IsPanelRunning()
                        ? "已保存（面板下次打开生效）"
                        : "已保存");
            },
            disable: () =>
            {
                ClearPasteBack(settings);
            },
            tokens: tokens);
    }

    // ---------------- 全局热键（capture / 引擎，外部进程注册，可改键/停用） ----------------

    private UIElement BuildExternalCard(ISettingsService settings, IThemeTokens tokens)
    {
        var card = SettingsUi.CreateCard();
        var body = SettingsUi.CardBody(card);
        body.Children.Add(TitleBlock("全局热键（截图 / 剪贴板引擎）", tokens));
        body.Children.Add(HintBlock(
            "点键位直接改键；Backspace 或「停用」禁用；「启用」恢复默认键。\n" +
            "改完只在对应程序**正在运行**时自动重启使其立即生效；未运行则保存配置，下次启动生效（不会无故把程序拉起来）。\n" +
            "截图热键仅支持字母 / 数字单键；引擎热键支持常见组合（含功能键）。", tokens));

        var external = _registry.GetAll()
            .Where(v => !HotkeyDeclarations.IsWired(v.Binding.Id) && !HotkeyDeclarations.IsSystem(v.Binding.Id))
            .ToList();
        if (external.Count == 0)
        {
            body.Children.Add(HintBlock("（暂无）", tokens));
        }
        else
        {
            foreach (var view in external.OrderBy(v => v.Binding.Description, StringComparer.Ordinal))
            {
                body.Children.Add(ExternalRow(view, settings, tokens));
            }
        }

        return card;
    }

    private UIElement ExternalRow(HotkeyView view, ISettingsService settings, IThemeTokens tokens)
    {
        var id = view.Binding.Id;
        var defaultSpec = view.Binding.DefaultChord.Spec;
        var engineKey = HotkeyDeclarations.EngineHotkeyKeyFor(id);

        // 引擎条目：当前键从配置键读（P1-2 配置键唯一真相源；改键后声明已同步，双读一致）。
        string current;
        if (engineKey is not null)
        {
            current = settings.Get(engineKey, view.Binding.Chord.Spec) ?? view.Binding.Chord.Spec;
        }
        else
        {
            current = view.Binding.Chord.Spec;
        }

        HotkeyApplyResult applySpec(string spec)
        {
            if (engineKey is not null)
            {
                settings.Set(engineKey, spec);
                RebindOrDeclare(id, spec);
                return DescribeRestart(ClipboardEngineLauncher.RestartEngineIfRunning(), "引擎");
            }

            // capture：独立配置文件（仅单字符主键；读-改-写，保留文件内其它设置）
            var err = HotkeyDeclarations.WriteCaptureHotkey(spec);
            if (err is not null)
            {
                return new HotkeyApplyResult(false, err);
            }
            RebindOrDeclare(id, spec);
            return DescribeRestart(ClipboardEngineLauncher.RestartCaptureIfRunning(), "截图进程");
        }

        void disable()
        {
            if (engineKey is not null)
            {
                settings.Set(engineKey, string.Empty);
                SetEnabled(id, false);
                ClipboardEngineLauncher.RestartEngineIfRunning();
            }
            else
            {
                HotkeyDeclarations.DisableCaptureHotkey();
                SetEnabled(id, false);
                ClipboardEngineLauncher.RestartCaptureIfRunning();
            }
        }

        return ManageableHotkeyRow(
            id: id,
            description: view.Binding.Description,
            currentSpec: current,
            defaultSpec: defaultSpec,
            ownerAlive: HotkeyDeclarations.IsOwnerAlive(view.Binding.Owner),
            applySpec: applySpec,
            disable: disable,
            tokens: tokens);
    }

    /// <summary>把"按需重启"的三态翻译成用户看得懂且不骗人的状态文案。</summary>
    internal static HotkeyApplyResult DescribeRestart(
        ClipboardEngineLauncher.ExternalProcessRestart restart, string who)
        => restart switch
        {
            ClipboardEngineLauncher.ExternalProcessRestart.Restarted
                => new HotkeyApplyResult(true, $"已重启{who}，立即生效"),
            ClipboardEngineLauncher.ExternalProcessRestart.NotRunning
                => new HotkeyApplyResult(true, $"已保存（{who}未运行，下次启动生效）"),
            _ => new HotkeyApplyResult(false, $"配置已写入，但重启{who}失败（请手动重启{who}）"),
        };

    // ---------------- 第三方热键（扫描占用 + 常见软件默认键参考） ----------------

    private UIElement BuildThirdPartyCard(ISettingsService settings, IThemeTokens tokens)
    {
        var card = SettingsUi.CreateCard();
        var body = SettingsUi.CardBody(card);
        body.Children.Add(TitleBlock("第三方热键（其他程序 / 常见软件）", tokens));
        body.Children.Add(HintBlock(
            "Windows 不公开「谁占用了全局热键」，这里用三条路拼出全貌：\n" +
            "① 扫描：逐个试注册组合键，注册失败（0x581）即已被某程序占用——能发现用 RegisterHotKey 的程序；\n" +
            "② 归属推断：把占用的键位与「常见软件已知键位」比对，命中即给出疑似来源与功能，并按该软件是否在运行标注可信度；\n" +
            "③ 常见软件默认键：输入法、截图工具、聊天软件多用键盘钩子实现全局热键，系统层探测不到，只能用已知默认值兜底。\n" +
            "仍然查不出归属时，可退出可疑程序（远控 / 安全 / 录屏 / AI 助手类）再点「重新扫描」对比差异。\n" +
            "关于修改：**其他软件的热键只能由它们自己去改**（我们无权也不应改别人的配置）；这里能做的是一键看清单、" +
            "标记忽略、以及在你要给自家功能绑键时提示冲突。",
            tokens));

        var status = new TextBlock
        {
            FontSize = 11,
            Foreground = tokens.MutedForeground,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var rescan = SmallButton("重新扫描", tokens);
        var host = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

        rescan.Click += (_, _) =>
        {
            _cachedScan = null; // 用户主动重扫：丢弃缓存（外部程序可能刚开关过）
            RunScan(settings, status, host, rescan, tokens);
        };
        body.Children.Add(rescan);
        body.Children.Add(status);
        body.Children.Add(host);

        // 常见软件默认键（进程活性标注；运行中的排前面）
        body.Children.Add(SubTitleBlock("常见软件已知键位（参考）", tokens));
        var running = ThirdPartyHotkeyCatalog.Running();
        var apps = ThirdPartyHotkeyCatalog.All
            .OrderByDescending(a => running.Contains(a))
            .ThenBy(a => a.App, StringComparer.Ordinal)
            .ToList();
        foreach (var app in apps)
        {
            body.Children.Add(ThirdPartyAppRow(app, running.Contains(app), tokens));
        }

        // 进入分节自动扫描一次（后台，不阻塞设置窗口打开）；已有缓存则直接渲染，避免来回切分节重复扫描。
        RunScan(settings, status, host, rescan, tokens);
        return card;
    }

    private void RunScan(
        ISettingsService settings, TextBlock status, StackPanel host, Button rescan, IThemeTokens tokens)
    {
        if (_cachedScan is not null)
        {
            RenderScanResult(_cachedScan, settings, status, host, tokens);
            return;
        }

        rescan.IsEnabled = false;
        status.Text = "正在扫描全局热键占用……（约 1 秒，期间不会抢占任何键位）";

        var registry = _registry;
        _ = Task.Run(() =>
        {
            lock (ScanGate)
            {
                _cachedScan ??= HotkeyScanner.Scan(registry);
                return _cachedScan!;
            }
        }).ContinueWith(
            task =>
            {
                // 回填必须回 UI 线程：用元素自己的 Dispatcher（比 SynchronizationContext 更可靠——
                // 后者在极端情况下为 null 会导致跨线程操作 UI 抛异常）。
                host.Dispatcher.InvokeAsync(() =>
                {
                    rescan.IsEnabled = true;
                    if (task.IsFaulted)
                    {
                        status.Text = "扫描失败：" + (task.Exception?.GetBaseException().Message ?? "未知错误");
                        return;
                    }

                    _lastDeltaText = ComputeDelta(_previousScan, task.Result);
                    _previousScan = task.Result;
                    RenderScanResult(task.Result, settings, status, host, tokens);
                });
            },
            TaskScheduler.Default);
    }

    private static void RenderScanResult(
        HotkeyScanResult result, ISettingsService settings, TextBlock status, StackPanel host, IThemeTokens tokens)
    {
        host.Children.Clear();
        var ignored = ReadIgnoredThirdParty(settings);
        var allThird = result.ThirdParty.ToList();
        var third = allThird.Where(e => !ignored.Contains(e.Spec)).ToList();
        int ignoredCount = allThird.Count - third.Count;

        int systemCount = result.Entries.Count(e => e.Kind == HotkeyScanKind.System);
        int oursCount = result.Entries.Count(e => e.Kind == HotkeyScanKind.Ours);
        status.Text = $"扫描完成：探测 {result.ProbedCount} 组组合键，被占用 {result.Entries.Count} 项"
            + $"（系统 / 系统保留 {systemCount}、BetterDesktop {oursCount}、其他程序 {allThird.Count}；已忽略 {ignoredCount}），"
            + $"用时 {result.ElapsedMs} ms。";

        if (!string.IsNullOrEmpty(_lastDeltaText))
        {
            host.Children.Add(HintBlock(_lastDeltaText, tokens));
        }

        if (third.Count == 0)
        {
            host.Children.Add(HintBlock(
                allThird.Count > 0
                    ? "其他程序占用的热键都已标记忽略。点下方「恢复全部」可以重新显示。"
                    : "未发现「其他程序」占用的全局热键。注意：用键盘钩子实现的热键（很多输入法 / 截图工具）本方法探测不到。",
                tokens));
        }
        else
        {
            const int collapsedLimit = 15;
            var expanded = false;
            var list = new StackPanel();
            var toggle = SmallButton($"展开全部 {third.Count} 项", tokens);
            toggle.Visibility = third.Count > collapsedLimit ? Visibility.Visible : Visibility.Collapsed;

            void Render(bool all)
            {
                list.Children.Clear();
                var rows = all ? third : third.Take(collapsedLimit).ToList();
                foreach (var entry in rows)
                {
                    list.Children.Add(ThirdPartyScanRow(entry, settings, tokens));
                }
                if (third.Count > collapsedLimit)
                {
                    toggle.Content = all ? "收起" : $"展开全部 {third.Count} 项（其余 {third.Count - collapsedLimit} 项）";
                }
            }

            toggle.Click += (_, _) =>
            {
                expanded = !expanded;
                Render(expanded);
            };
            Render(false);

            host.Children.Add(list);
            host.Children.Add(toggle);
        }

        if (ignoredCount > 0)
        {
            var restore = SmallButton($"恢复已忽略的 {ignoredCount} 项", tokens);
            restore.Margin = new Thickness(0, 6, 0, 0);
            restore.Click += (_, _) =>
            {
                WriteIgnoredThirdParty(settings, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                RenderScanResult(result, settings, status, host, tokens);
            };
            host.Children.Add(restore);
        }
    }

    /// <summary>
    /// 单条「其他程序占用」行：键位 + 疑似来源/功能（知识库反查 + 运行中加权）+ 忽略按钮。
    /// <para>反查不中时如实说"归属未知"并给出排查办法，绝不编造归属。</para>
    /// </summary>
    private static UIElement ThirdPartyScanRow(
        HotkeyScanEntry entry, ISettingsService settings, IThemeTokens tokens)
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };

        var ignore = new Button
        {
            Content = "忽略",
            FontSize = 10,
            Padding = new Thickness(10, 3, 10, 3),
            Cursor = Cursors.Hand,
            ToolTip = "从列表收起（不改动该热键，只是不再显示）",
        };
        DockPanel.SetDock(ignore, System.Windows.Controls.Dock.Right);
        ignore.Click += (_, _) =>
        {
            var set = ReadIgnoredThirdParty(settings);
            set.Add(entry.Spec);
            WriteIgnoredThirdParty(settings, set);
            if (row.Parent is StackPanel parent)
            {
                parent.Children.Remove(row); // 就地收起，不整表重建（避免打断浏览）
            }
        };

        var match = ThirdPartyHotkeyCatalog.MatchByChord(entry.Spec);
        var description = new TextBlock
        {
            FontSize = 11,
            Foreground = tokens.MutedForeground,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        string text;
        Brush color;
        if (match is { } hit)
        {
            bool running = ThirdPartyHotkeyCatalog.IsRunning(hit.App);
            text = $"疑似 {hit.App.App}：{hit.Binding.Description}"
                + (running ? "（该软件正在运行，可能性高）" : "（该软件当前未运行，可能被你改过键）");
            if (!string.IsNullOrEmpty(hit.App.SettingsPath))
            {
                text += "\n改键位置：" + hit.App.SettingsPath;
            }
            color = running
                ? ThemeWarning
                : ThemeMuted;

            if (running)
            {
                // "一键打开该软件"：把目标程序调到前台，用户去它自己的设置里改键。
                // 【能力边界】我们**不代改**别人的配置：微信/QQ 加密存储、NVIDIA 走专有面板，
                // 写坏对方配置的代价远大于省下的几次点击。能做的就是把路铺到门口。
                var open = new Button
                {
                    Content = "打开该软件",
                    FontSize = 10,
                    Padding = new Thickness(10, 3, 10, 3),
                    Cursor = Cursors.Hand,
                    ToolTip = "把该软件调到前台，去它的设置里改键（我们不代改他人配置）",
                };
                DockPanel.SetDock(open, System.Windows.Controls.Dock.Right);
                open.Click += (_, _) =>
                {
                    if (!TryActivateApp(hit.App))
                    {
                        description.Text = "未找到该软件窗口（可能最小化到托盘）——请从托盘图标打开后再改键。";
                    }
                };
                row.Children.Add(ignore);
                row.Children.Add(open);
            }
            else
            {
                row.Children.Add(ignore);
            }
        }
        else
        {
            text = "归属未知：多为输入法 / 远控 / 安全 / 录屏 / AI 助手类常驻程序。"
                + "退出某个可疑程序后点「重新扫描」，对比差异即可锁定归属。";
            color = ThemeDanger;
            row.Children.Add(ignore);
        }

        description.Text = text;

        var chord = new TextBlock
        {
            Text = HotkeySpec.Pretty(entry.Spec),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = color,
            Width = 168,
        };
        DockPanel.SetDock(chord, System.Windows.Controls.Dock.Left);

        row.Children.Add(chord);
        row.Children.Add(description);
        return row;
    }

    /// <summary>把归属推断出的软件调到前台（让用户去它自己的设置里改键）。尽力而为，失败返回 false。</summary>
    private static bool TryActivateApp(ThirdPartyHotkeyApp app)
    {
        foreach (var name in app.ProcessNames)
        {
            try
            {
                foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
                {
                    using (process)
                    {
                        var hwnd = process.MainWindowHandle;
                        if (hwnd == IntPtr.Zero)
                        {
                            continue;
                        }
                        _ = NativeMethods.ShowWindow(hwnd, 9); // SW_RESTORE
                        _ = NativeMethods.SetForegroundWindow(hwnd);
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                // 尽力而为：拿不到窗口就返回 false（UI 提示用户从托盘打开）
            }
        }
        return false;
    }

    /// <summary>
    /// 与上次扫描对比（定位"归属未知"热键的实战手段）：退出某个可疑程序后重扫，
    /// 看哪些键被释放 —— 被释放的那批就属于刚退出的程序。
    /// </summary>
    private static string? ComputeDelta(HotkeyScanResult? previous, HotkeyScanResult current)
    {
        if (previous is null)
        {
            return null;
        }

        var prev = previous.ThirdParty.Select(e => e.Spec).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = current.ThirdParty.Select(e => e.Spec).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = now.Except(prev).Select(HotkeySpec.Pretty).ToList();
        var released = prev.Except(now).Select(HotkeySpec.Pretty).ToList();

        if (added.Count == 0 && released.Count == 0)
        {
            return "与上次扫描一致：其他程序占用的热键集合没有变化。";
        }

        var parts = new List<string>();
        if (added.Count > 0)
        {
            parts.Add($"新增占用 {added.Count} 项（{string.Join("、", added.Take(10))}）");
        }
        if (released.Count > 0)
        {
            parts.Add($"已释放 {released.Count} 项（{string.Join("、", released.Take(10))}）");
        }
        return "与上次扫描对比：" + string.Join("；", parts) + " —— 释放的这批即属于你刚退出/关闭的程序。";
    }

    private const string ThirdPartyIgnoredKey = "hotkeys-panel.thirdparty-ignored";

    private static HashSet<string> ReadIgnoredThirdParty(ISettingsService settings)
    {
        var raw = settings.Get(ThirdPartyIgnoredKey, string.Empty) ?? string.Empty;
        return new HashSet<string>(
            raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void WriteIgnoredThirdParty(ISettingsService settings, IEnumerable<string> specs)
        => settings.Set(ThirdPartyIgnoredKey, string.Join(";", specs));

    private static UIElement ThirdPartyAppRow(ThirdPartyHotkeyApp app, bool running, IThemeTokens tokens)
    {
        var host = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock
        {
            Text = app.App,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = running ? tokens.Foreground : tokens.MutedForeground,
            Width = 150,
            TextWrapping = TextWrapping.Wrap,
        });
        line.Children.Add(new TextBlock
        {
            Text = running ? "运行中" : "未运行",
            FontSize = 10,
            Foreground = running
                ? new SolidColorBrush(ThemeBrushes.SuccessColor)
                : tokens.MutedForeground,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        host.Children.Add(line);

        foreach (var b in app.Bindings)
        {
            host.Children.Add(new TextBlock
            {
                Text = b.Chord + "    " + b.Description,
                FontSize = 11,
                Foreground = running ? tokens.Foreground : tokens.MutedForeground,
                Margin = new Thickness(150, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        foreach (var note in app.Notes)
        {
            host.Children.Add(new TextBlock
            {
                Text = note,
                FontSize = 11,
                Foreground = tokens.MutedForeground,
                Margin = new Thickness(150, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        if (!string.IsNullOrEmpty(app.SettingsPath))
        {
            host.Children.Add(new TextBlock
            {
                Text = "改键位置：" + app.SettingsPath,
                FontSize = 11,
                Foreground = tokens.MutedForeground,
                Margin = new Thickness(150, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        return host;
    }

    // ---------------- 应用热键（场景置顶显示；应用内键位，不进注册表） ----------------

    private UIElement BuildAppHotkeysCard(IThemeTokens tokens)
    {
        var card = SettingsUi.CreateCard();
        var body = SettingsUi.CardBody(card);
        body.Children.Add(TitleBlock("应用热键（按场景置顶显示）", tokens));

        var foreground = AppHotkeyCatalog.Match(ForegroundAppWatcher.ProbeForegroundProcessName());
        body.Children.Add(HintBlock(
            "切到某个应用时，热键侧板会把该应用的**功能键排在最前面**（例如打开 Blender 就先列 G / R / S / Tab 这些）。\n"
            + "这里是完整清单：收录各应用「默认键位」里的高频核心项，不是全量手册；键位以各自软件的设置为准。\n"
            + "这些是**应用内**键位（裸键也在此列），不参与全局冲突检测，也不进热键注册表。\n"
            + "当前前台：" + (foreground?.App ?? "（未收录该应用，或前台不是普通应用窗口）"),
            tokens));

        foreach (var profile in AppHotkeyCatalog.All)
        {
            body.Children.Add(SubTitleBlock($"{profile.App}（{profile.Count} 项）", tokens));
            foreach (var group in profile.Groups)
            {
                body.Children.Add(new TextBlock
                {
                    Text = group.Title,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = tokens.MutedForeground,
                    Margin = new Thickness(0, 8, 0, 1),
                });
                foreach (var item in group.Items)
                {
                    body.Children.Add(ChordRow(item.Chord, item.Description, tokens));
                }
            }
        }

        return card;
    }

    private static UIElement ChordRow(string chord, string description, IThemeTokens tokens)
    {
        var line = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        var chordBlock = new TextBlock
        {
            Text = chord,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = tokens.Foreground,
            Width = 168,
            TextAlignment = TextAlignment.Center,
        };
        DockPanel.SetDock(chordBlock, System.Windows.Controls.Dock.Left);
        line.Children.Add(chordBlock);
        line.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 11,
            Foreground = tokens.MutedForeground,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return line;
    }

    // ---------------- 系统热键（Windows 自带，不可改，纯参考；默认折叠） ----------------

    private UIElement BuildSystemCard(IThemeTokens tokens)
    {
        var card = SettingsUi.CreateCard();
        var body = SettingsUi.CardBody(card);
        body.Children.Add(TitleBlock("系统热键（Windows 全局，按下即生效）", tokens));
        body.Children.Add(HintBlock(
            "以下由 Windows 自身注册，任何界面下按下都有对应系统功能（键位归系统所有，不可修改，仅参考）。"
            + "共 " + HotkeyDeclarations.SystemHotkeys.Count + " 条，按主题分组；方向键 / 数字键族只列一条代表，"
            + "描述里注明了覆盖范围。「通用编辑 / 浏览键」组只在应用窗口内生效，不是全局热键。\n"
            + "每行可「隐藏」——隐藏 = 不在热键侧板里显示，键位本身不受影响（隐藏 ≠ 停用）。",
            tokens));

        var list = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var group in HotkeyDeclarations.SystemHotkeyGroups)
        {
            list.Children.Add(SubTitleBlock($"{group.Title}（{group.Items.Count}）", tokens));
            foreach (var b in group.Items)
            {
                list.Children.Add(SystemRow(b, tokens));
            }
        }

        var toggle = SmallButton($"展开全部 {HotkeyDeclarations.SystemHotkeys.Count} 项", tokens);
        toggle.Margin = new Thickness(0, 8, 0, 0);
        toggle.Click += (_, _) =>
        {
            var show = list.Visibility != Visibility.Visible;
            list.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = show ? "收起" : $"展开全部 {HotkeyDeclarations.SystemHotkeys.Count} 项";
        };

        body.Children.Add(toggle);
        body.Children.Add(list);
        return card;
    }

    // ---------------- 检测快捷键（系统 / 其他程序占用） ----------------

    private UIElement BuildDetectorCard(IThemeTokens tokens)
    {
        var card = SettingsUi.CreateCard();
        var body = SettingsUi.CardBody(card);
        body.Children.Add(TitleBlock("检测快捷键（系统 / 其他程序占用）", tokens));
        body.Children.Add(HintBlock(
            "点输入框后按下组合键：识别它是 Windows 系统热键、已被 BetterDesktop 使用，还是被其他程序占用。\n" +
            "边界：只能检测经 RegisterHotKey 注册的全局热键；用键盘钩子实现的热键（浏览器 Ctrl+T、多数输入法与截图工具）无法检测，\n" +
            "且检测到的「占用」无法识别归属（系统不公开）。", tokens));

        var box = new TextBox
        {
            Width = 168,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            Text = "（点此录制）",
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            Foreground = tokens.Foreground,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(2, 4, 2, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "点这里，然后按下想检测的组合键（需含 Ctrl / Alt / Shift / Win）",
        };
        var result = new TextBlock
        {
            FontSize = 11,
            Foreground = tokens.MutedForeground,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };

        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true; // 检测框：所有按键由我们处理，不当作文本输入

            var pressed = e.Key == Key.System ? e.SystemKey : e.Key;

            if (pressed is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
            {
                box.Text = "（点此录制）";
                result.Text = string.Empty;
                return;
            }

            if (!HotkeyCaptureKeys.TryBuild(Keyboard.Modifiers, pressed, out var spec, out var reason))
            {
                box.Text = reason; // 就地说明为什么没录上
                result.Text = string.Empty;
                return;
            }

            box.Text = HotkeySpec.Pretty(spec);
            result.Text = AnalyzeHotkey(_registry, spec);
        };

        body.Children.Add(box);
        body.Children.Add(result);
        return card;
    }

    /// <summary>
    /// 检测逻辑（纯函数，可单测）：系统库 → 自家注册表 → RegisterHotKey 试注册。
    /// <para>【2026-09-16 修正】自家命中不再一律说"已被注册"——**声明条目不等于真的注册了**：
    /// 外部进程（引擎/截图/面板）没运行时键根本不会响应，必须如实说明；
    /// 停用项、非活跃作用域项同理。否则用户按着提示去按却毫无反应（本批要修的偏差之一）。</para>
    /// </summary>
    internal static string AnalyzeHotkey(IHotkeyRegistryService registry, string spec)
    {
        if (!HotkeySpec.TryParse(spec, out _, out _, out var canonical) || canonical.Length == 0)
        {
            return "无法解析该组合键（需至少一个 Ctrl / Alt / Shift / Win 修饰键 + 一个主键）";
        }

        var system = HotkeyDeclarations.FindSystemBySpec(canonical);
        if (system is not null)
        {
            return "✓ 这是 Windows 系统热键：" + system.Description + "（按下即生效）";
        }

        var ours = registry.GetAll().FirstOrDefault(v =>
            HotkeySpec.TryParse(v.Binding.Chord.Spec, out _, out _, out var oc)
            && string.Equals(oc, canonical, StringComparison.OrdinalIgnoreCase));
        if (ours is not null)
        {
            var binding = ours.Binding;
            var details = new List<string>();

            if (!ours.Enabled)
            {
                details.Add("该条目已停用");
            }
            if (!HotkeyDeclarations.IsOwnerAlive(binding.Owner))
            {
                details.Add("对应程序当前未运行");
            }

            var scopeId = binding.Scope.Id;
            if (scopeId.StartsWith("Surface.", StringComparison.Ordinal))
            {
                details.Add($"仅在「{scopeId["Surface.".Length..]}」界面打开时生效");
            }

            if (details.Count == 0)
            {
                details.Add("按下应当生效");
            }

            return "已被 BetterDesktop 声明：" + binding.Description + "（" + string.Join("；", details) + "）";
        }

        if (!HotkeyScanner.IsGloballyTaken(canonical))
        {
            return "✓ 未被任何程序注册为全局热键（可自行注册使用）";
        }

        return "✗ 已被某个程序注册为全局热键（系统不公开归属；也可能是被键盘钩子程序占用但此处无法确认）";
    }

    // ---------------- 行内直改行组件 ----------------

    /// <summary>
    /// 一行可管理热键：键位按钮（点击 → 录键态，按组合键即改；Backspace 停用；Esc 取消）+
    /// 功能（未运行标注）+ 停用/启用操作 + 结果状态行。
    /// </summary>
    private UIElement ManageableHotkeyRow(
        string id,
        string description,
        string currentSpec,
        string? defaultSpec,
        bool ownerAlive,
        Func<string, HotkeyApplyResult> applySpec,
        Action disable,
        IThemeTokens tokens)
    {
        var host = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

        var head = new DockPanel();
        var descBlock = new TextBlock
        {
            Text = description,
            FontSize = 12,
            Foreground = tokens.Foreground,
            Width = 150,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        if (!ownerAlive)
        {
            descBlock.Text += " · 未运行";
            descBlock.Foreground = ThemeWarning;
            descBlock.ToolTip = "该热键由外部进程注册，当前进程未运行——键此刻不会生效";
        }
        DockPanel.SetDock(descBlock, System.Windows.Controls.Dock.Left);
        head.Children.Add(descBlock);

        var keyBtn = new Button
        {
            Content = DescribeSpec(currentSpec),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(6, 0, 0, 0),
            MinWidth = 120,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "点这里直接录新组合键；Backspace 停用；Esc 取消",
        };
        DockPanel.SetDock(keyBtn, System.Windows.Controls.Dock.Right);

        var status = new TextBlock
        {
            FontSize = 10,
            Foreground = tokens.MutedForeground,
            Margin = new Thickness(150, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        var recording = false;
        var normalBackground = keyBtn.Background;

        void SetStatus(HotkeyApplyResult r)
        {
            status.Text = r.Message;
            status.Foreground = r.Ok ? tokens.MutedForeground : ThemeDanger;
        }

        void StopRecording(string content)
        {
            recording = false;
            keyBtn.Content = content;
            keyBtn.ClearValue(Button.BackgroundProperty);
            keyBtn.ClearValue(Button.ForegroundProperty);
            if (normalBackground is not null)
            {
                keyBtn.Background = normalBackground;
            }
        }

        keyBtn.Click += (_, _) =>
        {
            recording = true;
            keyBtn.Content = "请按新组合键…（Esc 取消）";
            keyBtn.Background = tokens.Accent;
            keyBtn.Foreground = ThemeOnAccent;
            keyBtn.Focus();
            status.Text = "录键中：按下组合键即保存；Backspace 停用；Esc 取消";
            status.Foreground = tokens.MutedForeground;
        };
        keyBtn.PreviewKeyDown += (_, e) =>
        {
            if (!recording)
            {
                return;
            }
            e.Handled = true; // 录键态：所有按键由我们处理

            var pressed = e.Key == Key.System ? e.SystemKey : e.Key;

            if (pressed == Key.Escape)
            {
                StopRecording(DescribeSpec(currentSpec));
                status.Text = "已取消";
                return;
            }
            if (pressed is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
            {
                recording = false;
                disable();
                keyBtn.Content = defaultSpec is null ? "（未设置，点此设置）" : "（已停用）";
                keyBtn.ClearValue(Button.BackgroundProperty);
                keyBtn.ClearValue(Button.ForegroundProperty);
                status.Text = defaultSpec is null ? "已清除" : "已停用";
                return;
            }
            if (!HotkeyCaptureKeys.TryBuild(Keyboard.Modifiers, pressed, out var spec, out var reason))
            {
                keyBtn.Content = reason; // 就地说明为什么没录上，保持录键态继续按
                return;
            }

            var applied = applySpec(spec);
            StopRecording(applied.Ok ? HotkeySpec.Pretty(spec) : DescribeSpec(currentSpec));
            SetStatus(applied);
            if (applied.Ok)
            {
                currentSpec = spec;
            }
        };

        head.Children.Add(keyBtn);

        // 停用/启用操作（paste-back 无默认恢复时只显示"清除"；其余行：停用 ↔ 启用（恢复默认键））
        var action = "disable";
        var actionBtn = new Button
        {
            Content = defaultSpec is null ? "清除" : "停用",
            FontSize = 11,
            Padding = new Thickness(12, 3, 12, 3),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = defaultSpec is null ? "清除配置，回默认入口" : "禁用该热键（不注册）",
        };
        actionBtn.Click += (_, _) =>
        {
            if (action == "disable")
            {
                disable();
                if (defaultSpec is null)
                {
                    keyBtn.Content = "（未设置，点此设置）";
                    status.Text = "已清除";
                }
                else
                {
                    keyBtn.Content = "（已停用）";
                    actionBtn.Content = "启用";
                    actionBtn.ToolTip = "恢复默认键 " + HotkeySpec.Pretty(defaultSpec);
                    action = "enable";
                    status.Text = "已停用";
                }
            }
            else
            {
                var applied = applySpec(defaultSpec!);
                if (applied.Ok)
                {
                    keyBtn.Content = HotkeySpec.Pretty(defaultSpec!);
                    actionBtn.Content = "停用";
                    actionBtn.ToolTip = "禁用该热键（不注册）";
                    action = "disable";
                }
                SetStatus(applied);
            }
        };
        // 隐藏 = **不在热键侧板里显示**（功能照常生效），设置中心里始终可见、可一键恢复。
        // 这就是"隐藏 ≠ 静音"红线：只动 Visible，不碰 Enabled / 键位注册状态。
        var snapshot = _registry.GetAll().FirstOrDefault(v => v.Binding.Id == id);
        var hideBtn = new Button
        {
            Content = snapshot is null || snapshot.Visible ? "隐藏" : "显示",
            FontSize = 11,
            Padding = new Thickness(12, 3, 12, 3),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            IsEnabled = snapshot is not null,
            ToolTip = snapshot is null
                ? "该热键尚未声明，暂不可隐藏"
                : "隐藏 = 不在热键侧板中显示（功能照常）；设置里始终可见，可随时恢复",
        };
        hideBtn.Click += (_, _) =>
        {
            var current = _registry.GetAll().FirstOrDefault(v => v.Binding.Id == id);
            if (current is null)
            {
                return;
            }

            _registry.SetVisible(id, !current.Visible);
            hideBtn.Content = current.Visible ? "显示" : "隐藏";
            status.Text = current.Visible ? "已在侧板隐藏（功能照常）" : "已在侧板恢复显示";
            status.Foreground = tokens.MutedForeground;
        };
        head.Children.Add(hideBtn); // 位于键位按钮左侧

        DockPanel.SetDock(actionBtn, System.Windows.Controls.Dock.Right);
        head.Children.Add(actionBtn);

        host.Children.Add(head);
        host.Children.Add(status);
        return host;
    }

    // ---------------- 写入动作 ----------------

    private void RebindOrDeclare(string id, string spec)
    {
        if (_registry.GetAll().Any(v => v.Binding.Id == id))
        {
            _ = _registry.Rebind(id, new HotkeyChord(spec));
        }
    }

    private void SetEnabled(string id, bool enabled)
    {
        if (_registry.GetAll().Any(v => v.Binding.Id == id))
        {
            _registry.SetEnabled(id, enabled);
        }
    }

    /// <summary>写入面板消费的配置键（真生效）+ 同步注册表声明（P1-2：配置键唯一真相源，声明只做展示/冲突）。</summary>
    private void ApplyPasteBack(ISettingsService settings, string spec)
    {
        settings.Set(HotkeyDeclarations.PasteBackHotkeyKey, spec);
        RebindOrDeclare("clipboard.paste-back", spec);
    }

    /// <summary>清除粘贴回热键（回默认鼠标中键入口）：配置键清空 + 注销注册表声明。</summary>
    private void ClearPasteBack(ISettingsService settings)
    {
        settings.Set(HotkeyDeclarations.PasteBackHotkeyKey, string.Empty);
        _registry.Unregister("clipboard.paste-back");
    }

    // ---------------- 展示辅助 ----------------

    private static string DescribeSpec(string spec)
        => string.IsNullOrWhiteSpace(spec) ? "（未设置，点此设置）" : HotkeySpec.Pretty(spec);

    /// <summary>
    /// 系统热键只读行 + 「隐藏」按钮：隐藏只影响**是否出现在热键侧板**，
    /// Windows 自身的键位当然改不动（标注"系统"）。
    /// </summary>
    private UIElement SystemRow(HotkeyBinding b, IThemeTokens tokens)
    {
        var host = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var line = new DockPanel();

        var chord = new TextBlock
        {
            Text = HotkeySpec.Pretty(b.Chord.Spec),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = tokens.Foreground,
            Width = 168,
            TextAlignment = TextAlignment.Center,
        };
        DockPanel.SetDock(chord, System.Windows.Controls.Dock.Left);
        line.Children.Add(chord);

        var badge = new TextBlock
        {
            Text = " 系统",
            FontSize = 11,
            Foreground = ThemeAccent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(badge, System.Windows.Controls.Dock.Left);
        line.Children.Add(badge);

        var snapshot = _registry.GetAll().FirstOrDefault(v => v.Binding.Id == b.Id);
        var hideBtn = new Button
        {
            Content = snapshot is null || snapshot.Visible ? "隐藏" : "显示",
            FontSize = 10,
            Padding = new Thickness(10, 3, 10, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            IsEnabled = snapshot is not null,
            ToolTip = "隐藏 = 不在热键侧板里显示；Windows 自身的键位不受影响（我们改不动它）",
        };
        DockPanel.SetDock(hideBtn, System.Windows.Controls.Dock.Right);
        hideBtn.Click += (_, _) =>
        {
            var current = _registry.GetAll().FirstOrDefault(v => v.Binding.Id == b.Id);
            if (current is null)
            {
                return;
            }

            _registry.SetVisible(b.Id, !current.Visible);
            hideBtn.Content = current.Visible ? "显示" : "隐藏";
        };
        line.Children.Add(hideBtn);

        line.Children.Add(new TextBlock
        {
            Text = b.Description,
            FontSize = 12,
            Foreground = tokens.MutedForeground,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        host.Children.Add(line);
        return host;
    }

    private static Button SmallButton(string text, IThemeTokens tokens)
    {
        return new Button
        {
            Content = text,
            FontSize = 11,
            Padding = new Thickness(12, 3, 12, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.Hand,
            Foreground = tokens.Foreground,
        };
    }

    private static TextBlock TitleBlock(string text, IThemeTokens tokens) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Foreground = tokens.Foreground,
        Margin = new Thickness(0, 0, 0, 6),
    };

    private static TextBlock SubTitleBlock(string text, IThemeTokens tokens) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = tokens.Foreground,
        Margin = new Thickness(0, 14, 0, 0),
    };

    private static TextBlock HintBlock(string text, IThemeTokens tokens) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = tokens.MutedForeground,
        Margin = new Thickness(0, 6, 0, 2),
        TextWrapping = TextWrapping.Wrap,
    };
}
