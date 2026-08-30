// BetterDesktop.Shell.MenuBar — IME 输入法独立弹出面板（继承 MenuBarPopupWindow：失焦关闭）
// UI 内容完全来自系统，零硬编码：
//   - 列表项：ImeLayoutEnumerator.Enumerate() → 真实布局名（"美式键盘" / "微软拼音" / "搜狗拼音输入法"）
//   - 方块图标：CompactLabel（"美" / "中" / "拼" / "搜"，从真实 LayoutName 提取）
//   - 激活态：IsActive（KeyboardLayoutInterop 底层 GetKeyboardLayout 真值）+ 勾选
//   - Shift 键秒切中/英文（弹窗打开时按 Shift 即切换）
//   - 三条设置行：Toggle 开关，尽力读写 HKCU\Software\Microsoft\InputMethod\Settings（微软 IME）
//   - 键盘偏好设置：ShellExecute 打开 ms-settings:regionlanguage（系统自带）

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Services;
using BetterDesktop.Shell.Status.Contracts;
using BetterDesktop.Shell.Status.Native;
using Microsoft.Win32;
using BetterDesktop.Shell.MenuBar.Contracts;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>输入法独立弹出面板。</summary>
internal sealed class ImePopupWindow : MenuBarPopupWindow
{
    private readonly IImeMonitor _ime;
    private const double DefaultWidth = 290;
    private bool _managerMode; // true = 显示"输入法管理"子视图；false = 快速切换视图
    private bool _addMode;     // 管理视图内：true = 显示"添加输入法"列表；false = 已启用输入法列表

    public ImePopupWindow(IImeMonitor ime, IVibrancyService vibrancy, IAppearanceService? appearance = null)
        : base(vibrancy, appearance)
    {
        _ime = ime;
        Width = DefaultWidth;
        MinWidth = DefaultWidth;
        SizeToContent = SizeToContent.Height;
        // Shift 键秒切中/英文（弹窗打开时按 Shift 即切换，对齐原生体验）
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
            {
                try { ImeLayoutEnumerator.ToggleChineseEnglish(); } catch { /* 切换失败静默 */ }
                RebuildContent();
                e.Handled = true;
            }
        };
    }

    public FrameworkElement BuildPreviewContent() => BuildContent();

    protected override FrameworkElement BuildContent()
    {
        if (_managerMode)
        {
            return BuildManagerContent();
        }
        var root = new Border
        {
            // 根容器透明：面板背景/描边/圆角由基类 ApplyContent 按主题令牌统一挂载（对齐 ShellWindow 窗口属性）。
            Padding = new Thickness(6, 6, 6, 8),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        var column = new StackPanel { Orientation = Orientation.Vertical };

        // ========== 切换按钮（点击直接切到下一个输入法，不弹系统选择器 UI） ==========
        var switchBtn = new Button
        {
            Content = "切换输入法",
            Height = 32,
            Margin = new Thickness(2, 2, 2, 4),
            Padding = new Thickness(0),
            Foreground = MenuBarTheme.Foreground,
            BorderBrush = Brushes.Transparent,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand
        };
        // 强调按钮背景走主题 AccentBrush
        SetThemeBinding(switchBtn, Control.BackgroundProperty, "AccentBrush");
        switchBtn.Click += async (_, _) =>
        {
            try
            {
                // CycleOnce 内部枚举布局 → 直接激活下一项（TSF 走 ActivateProfile / IMM 走窗口消息），
                // 不模拟按键、不弹系统输入法选择器；异步等待避免阻塞 UI，并给系统留 200ms 完成切换后再刷新列表。
                ImeLayoutEnumerator.CycleOnce();
                await Task.Delay(200).ConfigureAwait(false);
            }
            catch { /* 失败静默 */ }
            await Dispatcher.InvokeAsync(RebuildContent);
        };
        column.Children.Add(switchBtn);

        // ========== 布局列表（来自系统真实枚举，IMM + TSF 全量；点击行 = 直接切换） ==========
        var layouts = ImeLayoutEnumerator.Enumerate();
        foreach (var item in layouts)
        {
            column.Children.Add(CreateLayoutRow(item));
        }

        // 如果系统没枚举到任何布局（极端罕见），放一个占位，避免面板空白
        if (layouts.Count == 0)
        {
            column.Children.Add(new Border
            {
                Height = 36,
                Margin = new Thickness(8, 6, 8, 6),
                Child = CreatePlaceholder("未检测到已安装的输入法 / 键盘布局", new Thickness(0))
            });
        }

        // ========== 分隔线 ==========
        column.Children.Add(CreateSeparator());

        // ========== 三条设置行（Toggle 开关，尽力读写 HKCU） ==========
        column.Children.Add(CreateToggleRow("显示表情和符号", "EnableEmoji", defaultValue: true));
        column.Children.Add(CreateToggleRow("显示虚拟键盘", "ShowOnScreenKeyboard", defaultValue: false));
        column.Children.Add(CreateToggleRow("显示输入法名称", "ShowImeName", defaultValue: true));

        // ========== 分隔线 ==========
        column.Children.Add(CreateSeparator());

        // ========== 键盘偏好设置（跳转系统设置，零自造） ==========
        column.Children.Add(CreateLinkRow("键盘偏好设置", OpenKeyboardSettings));
        // 管理入口：切换进"输入法管理"子视图（对应系统设置里替代的输入法/键盘管理）
        column.Children.Add(CreateLinkRow("管理输入法…", EnterManager));

        root.Child = column;
        return root;
    }

    /// <summary>输入法管理子视图入口：切模式并重建弹窗内容。</summary>
    private void EnterManager()
    {
        _managerMode = true;
        _addMode = false;
        RebuildContent();
    }

    /// <summary>退出管理视图，回到快速切换视图。</summary>
    private void ExitManager()
    {
        _managerMode = false;
        _addMode = false;
        RebuildContent();
    }

    /// <summary>按当前模式重建弹窗内容并应用统一主题外观（背景/描边/前景走基类）。</summary>
    private void RebuildContent()
    {
        ApplyContent(BuildContent());
    }

    /// <summary>
    /// 输入法管理子视图（对应系统设置里替代的"输入法/键盘管理"）：
    /// 已启用列表 = HKCU\Keyboard Layout\Preload 真实顺序，支持上下移、设为默认、删除；并可进入"添加输入法"。全部改动写注册表后立即套用。
    /// </summary>
    private FrameworkElement BuildManagerContent()
    {
        var root = new Border
        {
            // 根容器透明：面板背景/描边/圆角由基类 ApplyContent 按主题令牌统一挂载（对齐 ShellWindow 窗口属性）。
            Padding = new Thickness(6, 6, 6, 8),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        var column = new StackPanel { Orientation = Orientation.Vertical };

        // 顶栏：返回（退出管理）+ 标题
        var header = new Grid { Height = 34, Margin = new Thickness(2, 0, 2, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // 返回动作：添加子列表返回已启用列表；已启用列表返回快速切换视图
        var back = CreateIconButton("‹", 24, () =>
        {
            if (_addMode) { _addMode = false; RebuildContent(); }
            else ExitManager();
        });
        Grid.SetColumn(back, 0); header.Children.Add(back);
        var title = new TextBlock
        {
            Text = _addMode ? "添加输入法" : "输入法管理",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        Grid.SetColumn(title, 1); header.Children.Add(title);
        column.Children.Add(header);

        column.Children.Add(CreateSeparator());

        if (_addMode)
        {
            FillAddLayoutsContent(column);
        }
        else
        {
            FillEnabledLayoutsContent(column);
        }

        root.Child = column;
        return root;
    }

    /// <summary>已启用输入法列表：真实 Preload 顺序排列，支持上移/下移/设为默认/删除。</summary>
    private void FillEnabledLayoutsContent(StackPanel column)
    {
        var layouts = ImeLayoutEnumerator.Enumerate();
        if (layouts.Count == 0)
        {
            column.Children.Add(CreatePlaceholder("未检测到已启用的输入法 / 键盘布局", new Thickness(10, 6, 0, 6)));
        }
        else
        {
            for (var i = 0; i < layouts.Count; i++)
            {
                var item = layouts[i];
                column.Children.Add(CreateManagerLayoutRow(item.KlidHex, item.DisplayName, item.CompactLabel, i, layouts.Count));
            }
        }

        column.Children.Add(CreateSeparator());

        // 进入"添加输入法"子列表
        var add = new Grid { Height = 32, Margin = new Thickness(2, 0, 2, 0), Cursor = System.Windows.Input.Cursors.Hand };
        add.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        add.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var addText = new TextBlock
        {
            Text = "添加输入法…",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        // 操作/链接入口：强调色走主题令牌
        SetThemeBinding(addText, TextBlock.ForegroundProperty, "AccentBrush");
        Grid.SetColumn(addText, 0); add.Children.Add(addText);
        var chevron = new TextBlock
        {
            Text = "\uE0E3",
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 4, 0)
        };
        // 次要图标：次要前景走主题令牌
        SetThemeBinding(chevron, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        Grid.SetColumn(chevron, 1); add.Children.Add(chevron);
        add.MouseLeftButtonUp += (_, _) =>
        {
            _addMode = true;
            RebuildContent();
        };
        column.Children.Add(add);
    }

    /// <summary>
    /// 一条已启用输入法管理行：序号 + 图标 + 名称 + 操作区（上移/下移 或 "默认"标记 / 删除）。
    /// 首个即默认输入法；仅剩一个时禁用删除。
    /// </summary>
    private static FrameworkElement CreateManagerLayoutRow(string klid, string displayName, string compactLabel, int index, int count)
    {
        var row = new Grid { Height = 34, Margin = new Thickness(2, 1, 2, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 序号
        var idx = new TextBlock
        {
            Text = (index + 1).ToString(),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        // 次要序号：次要前景走主题令牌
        SetThemeBinding(idx, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        Grid.SetColumn(idx, 0); row.Children.Add(idx);

        // 方块图标：背景随主题内容层，文字继承主题前景
        var icon = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(4),
            Child = new TextBlock
            {
                Text = compactLabel,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            },
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        SetThemeBinding(icon, Border.BackgroundProperty, "ThemeContentBackground");
        Grid.SetColumn(icon, 1); row.Children.Add(icon);

        // 名称
        var name = new TextBlock
        {
            Text = displayName,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = "第一个为默认输入法"
        };
        Grid.SetColumn(name, 2); row.Children.Add(name);

        // 操作区（右）：默认标记 / 上移 / 下移 / 删除
        var ops = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (index == 0)
        {
            var defMark = new TextBlock
            {
                Text = "默认",
                FontSize = 11,
                Margin = new Thickness(0, 0, 6, 0)
            };
            // 默认标记：强调色走主题令牌
            SetThemeBinding(defMark, TextBlock.ForegroundProperty, "AccentBrush");
            ops.Children.Add(defMark);
            // 非唯一时允许调整顺序
            if (count > 1)
            {
                ops.Children.Add(CreateIconButton("↓", 20, () => ImeLayoutEnumerator.Move(klid, index + 1)));
            }
        }
        else
        {
            // 上移（放到前一位即更靠近默认）
            ops.Children.Add(CreateIconButton("↑", 20, () => ImeLayoutEnumerator.Move(klid, index - 1)));
            // 下移（放到后一位）
            if (index < count - 1)
            {
                ops.Children.Add(CreateIconButton("↓", 20, () => ImeLayoutEnumerator.Move(klid, index + 1)));
            }
            // 设为默认
            ops.Children.Add(CreateTextButton("设默认", () => ImeLayoutEnumerator.Move(klid, 0)));
        }
        // 删除（仅剩一个时禁用）
        var del = CreateIconButton("🗑", 20, () =>
        {
            // 由调用方判定
        });
        if (count <= 1)
        {
            del.Opacity = 0.35;
            del.IsEnabled = false;
        }
        else
        {
            del = CreateIconButton("🗑", 20, () => ImeLayoutEnumerator.Remove(klid));
        }
        del.Margin = new Thickness(4, 0, 2, 0);
        ops.Children.Add(del);

        Grid.SetColumn(ops, 3); row.Children.Add(ops);
        return row;
    }

    /// <summary>"添加输入法"子列表：列出系统已注册但尚未启用的布局，点击即加入（默认加到末尾）。</summary>
    private void FillAddLayoutsContent(StackPanel column)
    {
        var enabled = KeyboardLayoutInterop.GetActivePreloadOrder();
        var registered = KeyboardLayoutInterop.GetRegisteredLayouts();
        var available = registered
            .Where(r => !enabled.Contains(r.KlidHex, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (available.Count == 0)
        {
            column.Children.Add(CreatePlaceholder("没有更多可添加的输入法 / 键盘布局", new Thickness(10, 8, 0, 8)));
            return;
        }

        foreach (var item in available)
        {
            var row = new Grid
            {
                Height = 32,
                Margin = new Thickness(2, 1, 2, 1),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(4),
                Child = new TextBlock
                {
                    Text = ImeNaming.ToLetter(item.LayoutName, item.IsIme),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            SetThemeBinding(icon, Border.BackgroundProperty, "ThemeContentBackground");
            Grid.SetColumn(icon, 0); row.Children.Add(icon);
            var name = new TextBlock
            {
                Text = item.LayoutName,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(name, 1); row.Children.Add(name);
            var klid = item.KlidHex;
            row.MouseLeftButtonUp += (_, _) =>
            {
                ImeLayoutEnumerator.Add(klid);
                _addMode = false;
                RebuildContent();
            };
            column.Children.Add(row);
        }
    }

    /// <summary>统一构建图标小按钮（Segoe UI Symbol 字形）。</summary>
    private static Button CreateIconButton(string glyph, double size, Action onClick)
    {
        var b = new Button
        {
            Width = size,
            Height = size,
            Padding = new Thickness(0),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            FontSize = 11,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(2, 0, 2, 0)
        };
        var tb = new TextBlock
        {
            Text = glyph,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        if (glyph == "🗑" || glyph == "↑" || glyph == "↓" || glyph == "‹")
        {
            tb.FontFamily = new FontFamily("Segoe UI Symbol");
        }
        b.Content = tb;
        b.Click += (_, _) =>
        {
            try { onClick(); }
            catch { /* 管理操作失败静默，UI 保持原状 */ }
        };
        return b;
    }

    /// <summary>统一构建文字小按钮。</summary>
    private static Button CreateTextButton(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            FontSize = 11,
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(2, 0, 2, 0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        b.Click += (_, _) =>
        {
            try { onClick(); }
            catch { /* 管理操作失败静默 */ }
        };
        return b;
    }

    /// <summary>加载指定输入法的真实图标（ImageSource），失败返回 null。</summary>
    private static ImageSource? LoadLayoutIcon(string klidHex, bool isTs)
    {
        try
        {
            var hIcon = KeyboardLayoutInterop.GetLayoutIconHandle(klidHex, isTs);
            if (hIcon == IntPtr.Zero) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            KeyboardLayoutInterop.ReleaseIcon(hIcon);
            return source;
        }
        catch
        {
            return null;
        }
    }

    private static Border CreateSeparator()
    {
        // 分隔线走主题令牌，随亮/暗/无色模式自动切换。
        var sep = new Border
        {
            Height = 1,
            Margin = new Thickness(8, 6, 8, 6)
        };
        SetThemeBinding(sep, Border.BackgroundProperty, "ThemeSeparator");
        return sep;
    }

    /// <summary>空状态占位文字：次要色走主题令牌（ThemeMutedForeground）。</summary>
    private static FrameworkElement CreatePlaceholder(string text, Thickness margin)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = margin
        };
        SetThemeBinding(tb, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        return tb;
    }

    // ========================= 行构造器 =========================

    private FrameworkElement CreateLayoutRow(ImeLayoutItem item)
    {
        var row = new Grid
        {
            Margin = new Thickness(2, 1, 2, 1),
            Height = 32
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 真实输入法图标（纯键盘布局无图标时显示语言代码，如 "ENG"）
        var iconSource = LoadLayoutIcon(item.KlidHex, item.IsTs);
        FrameworkElement iconElem;
        if (iconSource is not null)
        {
            iconElem = new Image
            {
                Width = 22,
                Height = 22,
                Source = iconSource,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode((Image)iconElem, BitmapScalingMode.HighQuality);
        }
        else
        {
            iconElem = new Border
            {
                Width = 28,
                Height = 22,
                CornerRadius = new CornerRadius(4),
                Child = new TextBlock
                {
                    Text = KeyboardLayoutInterop.GetLanguageCode(item.KlidHex),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            // 激活=强调色背景，非激活=内容层背景，均走主题令牌
            SetThemeBinding(iconElem, Border.BackgroundProperty, item.IsActive ? "AccentBrush" : "ThemeContentBackground");
        }
        Grid.SetColumn(iconElem, 0);
        row.Children.Add(iconElem);

        // 真实布局名（继承主题前景色）
        var name = new TextBlock
        {
            Text = item.DisplayName,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        // 勾选 ✓（强调色走主题令牌）
        if (item.IsActive)
        {
            var mark = new TextBlock
            {
                Text = "✓",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            SetThemeBinding(mark, TextBlock.ForegroundProperty, "AccentBrush");
            Grid.SetColumn(mark, 2);
            row.Children.Add(mark);
        }

        // 列表项点击 = 直接切换到该输入法（v5 直切：TSF 走 ActivateProfile / IMM 走窗口消息，无 UI）。
        // 补齐历史反馈"点击无法切换输入法"——此前 Activate 对 TSF 走 KlidToHkl 解析出无效 HKL，点击无效。
        row.Background = Brushes.Transparent;
        row.Cursor = Cursors.Hand;
        row.MouseEnter += (_, _) => row.Background = MenuBarTheme.Hover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) =>
        {
            try
            {
                ImeLayoutEnumerator.Activate(item.KlidHex, item.IsTs);
                RebuildContent(); // 重建面板，让激活项勾选/高亮跟随新状态
            }
            catch
            {
                // 切换失败静默，不打断用户
            }
        };

        return row;
    }

    private static FrameworkElement CreateToggleRow(string label, string registryValueName, bool defaultValue)
    {
        var grid = new Grid
        {
            Height = 30,
            Margin = new Thickness(2, 0, 2, 0)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock
        {
            Text = label,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var toggle = new ToggleSwitch
        {
            Width = 36,
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
            IsOn = ReadRegistryBool(registryValueName, defaultValue)
        };
        toggle.Toggled += (_, args) =>
        {
            try { WriteRegistryBool(registryValueName, (bool)args); }
            catch { /* 静默 */ }
        };
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);

        return grid;
    }

    private static FrameworkElement CreateLinkRow(string label, Action onClick)
    {
        var row = new Grid
        {
            Height = 32,
            Margin = new Thickness(2, 0, 2, 0),
            Cursor = Cursors.Hand
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock
        {
            Text = label,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(text, 0);
        row.Children.Add(text);

        var chevron = new TextBlock
        {
            Text = "\uE0E3", // Segoe Fluent 右箭头
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 4, 0)
        };
        // 次要箭头：次要前景走主题令牌
        SetThemeBinding(chevron, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        Grid.SetColumn(chevron, 1);
        row.Children.Add(chevron);

        row.MouseLeftButtonUp += (_, _) =>
        {
            try { onClick(); }
            catch { /* 静默 */ }
        };
        return row;
    }

    // ========================= 读写 HKCU（尽力而为） =========================
    private const string ImeSettingsKey = @"Software\Microsoft\InputMethod\Settings";

    private static bool ReadRegistryBool(string valueName, bool defaultValue)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ImeSettingsKey, writable: false);
            if (key is null) return defaultValue;
            var v = key.GetValue(valueName);
            if (v is int i) return i != 0;
            if (v is long l) return l != 0;
            if (v is string s && int.TryParse(s, out var si)) return si != 0;
            return defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    private static void WriteRegistryBool(string valueName, bool value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ImeSettingsKey, writable: true);
            key?.SetValue(valueName, value ? 1 : 0, RegistryValueKind.DWord);
        }
        catch
        {
            // 静默
        }
    }

    // ========================= 系统入口 =========================
    private static void OpenKeyboardSettings()
    {
        Process.Start(new ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true });
    }
}
