// BetterDesktop.Shell.MenuBar — Wi‑Fi 独立弹出面板（零硬编码，真实系统数据）。
// UI 结构（按截图布局）：
//   - 顶部：Wi‑Fi Toggle 开关（占位）
//   - "当前连接" 块：SSID（大标题） + IP / 速度 / MAC + 网络偏好设置跳转
//   - 分隔线
//   - "其他网络" 分组：WifiEnumerator.ScanNearby() → 真实附近扫描
//     每行：信号强度图标（5 格） + SSID + 锁图标

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Services;
using Windows.Devices.Radios;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>Wi‑Fi 独立弹出面板。</summary>
internal sealed class WifiPopupWindow : MenuBarPopupWindow
{
    private const double DefaultWidth = 320;
    private readonly IVibrancyService _vibrancy;
    private readonly IAppearanceService? _appearance;

    public WifiPopupWindow(IVibrancyService vibrancy, IAppearanceService? appearance = null)
        : base(vibrancy, appearance)
    {
        Width = DefaultWidth;
        MinWidth = DefaultWidth;
        SizeToContent = SizeToContent.Height;
        _vibrancy = vibrancy;
        _appearance = appearance;
    }

    public FrameworkElement BuildPreviewContent() => BuildContent();

    protected override FrameworkElement BuildContent()
    {
        var root = new Border
        {
            // 根容器透明：面板背景/描边/圆角由基类 ApplyContent 按主题令牌统一挂载。
            Padding = new Thickness(6, 6, 6, 8),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        var column = new StackPanel { Orientation = Orientation.Vertical };

        // ========== Wi‑Fi Toggle ==========
        column.Children.Add(CreateToggleRow("Wi‑Fi", on => _ = RadioInterop.SetStateAsync(RadioKind.WiFi, on)));

        // ========== 当前连接信息 ==========
        var current = WifiEnumerator.ReadCurrentConnection();
        string connectedSsid = current.IsConnected ? current.Ssid : string.Empty;
        if (current.IsConnected)
        {
            column.Children.Add(CreateCurrentConnectionBlock(current));
        }
        else
        {
            column.Children.Add(CreateMutedText("未连接到任何 Wi‑Fi 网络", new Thickness(14, 6, 14, 6)));
        }
        column.Children.Add(CreatePrefLinkRow("网络偏好设置", "ms-settings:network-wifi"));

        column.Children.Add(CreateSeparator());

        // ========== "其他网络" 标题 ==========
        column.Children.Add(CreateMutedText("其他网络", new Thickness(14, 4, 0, 6), FontWeights.SemiBold, 10));

        // ========== 附近网络列表（异步填充，避免阻塞 UI 线程） ==========
        var nearbyPlaceholder = CreateMutedText("正在扫描附近 Wi‑Fi 网络…", new Thickness(14, 6, 14, 8), wrap: true);
        column.Children.Add(nearbyPlaceholder);

        _ = LoadNearbyAsync(column, nearbyPlaceholder, connectedSsid);

        root.Child = column;
        return root;
    }

    /// <summary>异步拉取附近网络并在就绪后替换占位文本。全程不阻塞 UI 线程；异常/超时时显示错误而非永久"搜索中"。</summary>
    private static async System.Threading.Tasks.Task LoadNearbyAsync(StackPanel column, TextBlock placeholder, string connectedSsid)
    {
        System.Collections.Generic.IReadOnlyList<WifiNearbyItem> networks;
        try
        {
            networks = await WifiEnumerator.ScanNearbyAsync();
        }
        catch
        {
            networks = Array.Empty<WifiNearbyItem>();
        }
        var ui = System.Windows.Application.Current.Dispatcher;
        await ui.InvokeAsync(() =>
        {
            column.Children.Remove(placeholder);
            if (networks.Count == 0)
            {
                column.Children.Add(CreateMutedText("未扫描到附近 Wi‑Fi 网络（适配器可能繁忙或无权限，请稍后重试）", new Thickness(14, 6, 14, 8), wrap: true));
                return;
            }
            int shown = 0;
            foreach (var net in networks)
            {
                if (shown++ >= 12) break; // 预览限制最多 12 个，避免面板太高
                bool isCurrent = string.Equals(net.Ssid, connectedSsid, StringComparison.Ordinal);
                column.Children.Add(CreateNearbyRow(net, isCurrent));
            }
        });
    }

    // ------------------- UI 工厂 -------------------

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

    /// <summary>次要提示文本：前景统一走 ThemeMutedForeground 主题令牌。</summary>
    private static TextBlock CreateMutedText(string text, Thickness margin,
        FontWeight? fontWeight = null, double fontSize = 11, bool wrap = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            Margin = margin,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
        };
        if (fontWeight.HasValue)
        {
            tb.FontWeight = fontWeight.Value;
        }
        SetThemeBinding(tb, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        return tb;
    }

    private static FrameworkElement CreateToggleRow(string label, Action<bool> onChanged)
    {
        var row = new Grid
        {
            Height = 36,
            Margin = new Thickness(8, 2, 8, 2)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        Grid.SetColumn(labelText, 0);
        row.Children.Add(labelText);
        var toggle = new ToggleSwitch
        {
            IsOn = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        toggle.Toggled += (_, args) =>
        {
            try { onChanged((bool)args); }
            catch { /* ignore */ }
        };
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        return row;
    }

    private static FrameworkElement CreatePrefLinkRow(string label, string settingsUri)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Margin = new Thickness(14, 2, 0, 2),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        // 操作/链接入口：强调色走主题令牌
        SetThemeBinding(text, TextBlock.ForegroundProperty, "AccentBrush");
        text.MouseLeftButtonUp += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(settingsUri) { UseShellExecute = true }); }
            catch { /* ignore */ }
        };
        return text;
    }

    private static FrameworkElement CreateCurrentConnectionBlock(WifiConnectedInfo info)
    {
        var outer = new Border
        {
            CornerRadius = new CornerRadius(8),
            // 已连接块：加深的蓝色（原 alpha 60 过淡，与毛玻璃背景对比弱）
            Background = new SolidColorBrush(Color.FromArgb(150, 20, 110, 255)),
            Margin = new Thickness(8, 4, 8, 4),
            Padding = new Thickness(10, 10, 10, 10)
        };

        var stack = new StackPanel();
        // SSID + Wi-Fi icon + 断开按钮
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new TextBlock
        {
            Text = "\uE814", // Segoe UI Symbol · 已连接 Wi-Fi
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        // 连接状态图标：强调色走主题令牌
        SetThemeBinding(icon, TextBlock.ForegroundProperty, "AccentBrush");
        Grid.SetColumn(icon, 0); head.Children.Add(icon);
        var title = new TextBlock
        {
            Text = info.Ssid,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(title, 1); head.Children.Add(title);
        var disconnectBtn = new Button
        {
            Content = "断开",
            FontSize = 11,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        disconnectBtn.Click += async (_, _) =>
        {
            bool ok = await System.Threading.Tasks.Task.Run(() => WifiEnumerator.Disconnect());
            if (ok)
            {
                // 等待系统更新状态后刷新面板
                await System.Threading.Tasks.Task.Delay(800);
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    var owner = Window.GetWindow(disconnectBtn);
                    if (owner is WifiPopupWindow wp) wp.RefreshContent();
                });
            }
        };
        Grid.SetColumn(disconnectBtn, 2); head.Children.Add(disconnectBtn);
        stack.Children.Add(head);

        // IP / Speed / MAC 三行细文本
        if (!string.IsNullOrEmpty(info.IpAddress))
            stack.Children.Add(CreateMetaLine(info.IpAddress, FormatSpeed(info.LinkSpeedBytesPerSec)));
        else
            stack.Children.Add(CreateMetaLine("(无 IPv4)", FormatSpeed(info.LinkSpeedBytesPerSec)));

        if (info.SignalQuality > 0)
            stack.Children.Add(CreateMetaLine($"信号强度 {info.SignalQuality}%", ""));

        if (!string.IsNullOrEmpty(info.MacAddress))
            stack.Children.Add(CreateMetaLineMac(info.MacAddress));

        outer.Child = stack;
        return outer;
    }

    /// <summary>刷新整个面板内容（连接/断开后调用）。</summary>
    internal void RefreshContent()
    {
        if (Content is Border chrome)
        {
            chrome.Child = BuildContent();
        }
    }

    /// <summary>打开自绘密码输入弹窗（不跳转系统设置）；连接请求发出后延迟刷新面板。</summary>
    internal void OpenPasswordDialog(string ssid)
    {
        var dlg = new WifiPasswordWindow(ssid, success =>
        {
            if (success)
            {
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(1500);
                    await Dispatcher.InvokeAsync(RefreshContent);
                });
            }
        }, _vibrancy, _appearance);
        // 定位在屏幕中心偏上
        double x = (SystemParameters.PrimaryScreenWidth - 300) / 2;
        double y = (SystemParameters.PrimaryScreenHeight - 220) / 2;
        dlg.ShowAt(new Point(x, y));
    }

    private static FrameworkElement CreateMetaLine(string ip, string speed)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var a = new TextBlock
        {
            Text = ip,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        // 次要元信息：次要前景走主题令牌
        SetThemeBinding(a, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        Grid.SetColumn(a, 0); row.Children.Add(a);
        var b = new TextBlock
        {
            Text = speed,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(b, 1); row.Children.Add(b);
        return row;
    }

    private static FrameworkElement CreateMetaLineMac(string mac)
    {
        var tb = new TextBlock
        {
            Text = mac,
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0)
        };
        // 次要元信息：次要前景走主题令牌
        SetThemeBinding(tb, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        return tb;
    }

    private static FrameworkElement CreateNearbyRow(WifiNearbyItem net, bool isCurrent)
    {
        var row = new Grid
        {
            Height = 38,
            Margin = new Thickness(8, 0, 8, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = isCurrent
                ? new SolidColorBrush(Color.FromArgb(110, 20, 110, 255))
                : Brushes.Transparent
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 信号图标：5 格条形（按 0-20/40/60/80/100 截断）
        var sig = SignalIcon(net.SignalQuality);
        Grid.SetColumn(sig, 0); row.Children.Add(sig);

        // SSID
        var ssid = new TextBlock
        {
            Text = string.IsNullOrEmpty(net.Ssid) ? "隐藏网络" : net.Ssid,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        // 当前连接网络：强调色走主题令牌；其余继承主题前景
        SetThemeBinding(ssid, TextBlock.ForegroundProperty, isCurrent ? "AccentBrush" : "ThemeForeground");
        Grid.SetColumn(ssid, 1); row.Children.Add(ssid);

        // 右侧：已连接标记 / 锁图标
        if (isCurrent)
        {
            var connectedMark = new TextBlock
            {
                Text = "已连接",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            // 已连接标记：强调色走主题令牌
            SetThemeBinding(connectedMark, TextBlock.ForegroundProperty, "AccentBrush");
            Grid.SetColumn(connectedMark, 2); row.Children.Add(connectedMark);
        }
        else if (net.IsEncrypted)
        {
            var lockIcon = LockIcon();
            Grid.SetColumn(lockIcon, 2); row.Children.Add(lockIcon);
        }

        // 点击连接（已连接的网络不重复连接）
        if (!isCurrent && !string.IsNullOrEmpty(net.Ssid))
        {
            row.MouseLeftButtonUp += async (_, _) =>
            {
                var owner = Window.GetWindow(row) as WifiPopupWindow;
                // 检查是否有已保存配置文件
                bool hasProfile = await System.Threading.Tasks.Task.Run(() => WifiEnumerator.HasSavedProfile(net.Ssid));
                if (!hasProfile)
                {
                    // 无保存配置：直接弹密码窗输入密码连接
                    owner?.OpenPasswordDialog(net.Ssid);
                    return;
                }
                // 有保存配置：先用保存的密码直接连接（密码正确时无需重复输入）。
                // 连接后轮询状态：成功则正常；失败/超时则删除错误 profile 并弹密码窗让用户重输。
                bool ok = await System.Threading.Tasks.Task.Run(() => WifiEnumerator.Connect(net.Ssid));
                if (!ok)
                {
                    // 连接请求都发不出去：删 profile 弹密码窗
                    await System.Threading.Tasks.Task.Run(() => WifiEnumerator.DeleteProfile(net.Ssid));
                    owner?.OpenPasswordDialog(net.Ssid);
                    return;
                }
                string targetSsid = net.Ssid;
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    for (int i = 0; i < 10; i++) // 最多轮询 10 秒（密码正确连接很快，超时即判定失败），每 1 秒查一次
                    {
                        await System.Threading.Tasks.Task.Delay(1000);
                        int state = WifiEnumerator.GetInterfaceState();
                        if (state == 1) // 已连接，成功
                        {
                            if (owner is not null) await owner.Dispatcher.InvokeAsync(owner.RefreshContent);
                            return;
                        }
                        if (state == 0 || state == 6) // 断开/AdHoc = 系统已反馈连接失败（密码错误等）
                        {
                            // 先断开释放适配器，再删除错误 profile，保证列表不崩溃、下次点击秒弹
                            WifiEnumerator.Disconnect();
                            WifiEnumerator.DeleteProfile(targetSsid);
                            if (owner is not null)
                            {
                                await owner.Dispatcher.InvokeAsync(() =>
                                {
                                    owner.RefreshContent();
                                    owner.OpenPasswordDialog(targetSsid);
                                });
                            }
                            return;
                        }
                    }
                    // 超时（30 秒系统仍未反馈）：主动断开释放适配器 + 删 profile
                    WifiEnumerator.Disconnect();
                    WifiEnumerator.DeleteProfile(targetSsid);
                    if (owner is not null)
                    {
                        await owner.Dispatcher.InvokeAsync(() =>
                        {
                            owner.RefreshContent();
                            owner.OpenPasswordDialog(targetSsid);
                        });
                    }
                });
            };
        }
        return row;
    }

    private static FrameworkElement SignalIcon(int quality)
    {
        int bars;
        if (quality >= 80) bars = 4;
        else if (quality >= 60) bars = 3;
        else if (quality >= 40) bars = 2;
        else if (quality >= 20) bars = 1;
        else bars = 0;
        var canvas = new Canvas { Width = 20, Height = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        for (int i = 0; i < 4; i++)
        {
            int h = 3 + i * 3;
            var rect = new Rectangle
            {
                Width = 3,
                Height = h,
                RadiusX = 0.5,
                RadiusY = 0.5
            };
            // 信号条：激活格=主题前景（随明暗可读），未激活格=次要前景
            SetThemeBinding(rect, Rectangle.FillProperty,
                i < bars ? "ThemeForeground" : "ThemeMutedForeground");
            Canvas.SetLeft(rect, i * 4.5);
            Canvas.SetBottom(rect, 0);
            canvas.Children.Add(rect);
        }
        return canvas;
    }

    /// <summary>绘制一把小锁（锁环 + 锁体 + 锁孔），不依赖字体字形，确保始终可见。</summary>
    private static FrameworkElement LockIcon()
    {
        const double scale = 0.72; // 缩放到信号条相近尺寸
        var canvas = new Canvas
        {
            Width = 14,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
            RenderTransform = new ScaleTransform(scale, scale, 7, 8)
        };
        // 弧形锁环（空心描边）：主题前景随明暗可读
        var shackle = new Path
        {
            Data = Geometry.Parse("M 3.5 9 V 6.6 C 3.5 3.4 4.8 1.2 7 1.2 C 9.2 1.2 10.5 3.4 10.5 6.6 V 9"),
            StrokeThickness = 1.7,
            StrokeLineJoin = PenLineJoin.Round
        };
        SetThemeBinding(shackle, Path.StrokeProperty, "ThemeForeground");
        canvas.Children.Add(shackle);
        // 锁体（圆角矩形，底角倒圆）：主题前景
        var lockBody = new Path
        {
            Data = Geometry.Parse("M 2 9 H 12 V 12.2 C 12 14.5 10.6 16 8.8 16 H 5.2 C 3.4 16 2 14.5 2 12.2 Z")
        };
        SetThemeBinding(lockBody, Path.FillProperty, "ThemeForeground");
        canvas.Children.Add(lockBody);
        // 锁孔（挖空效果：内容层背景色）
        var keyhole = new Rectangle
        {
            Width = 2,
            Height = 2.6,
            RadiusX = 1,
            RadiusY = 1
        };
        SetThemeBinding(keyhole, Rectangle.FillProperty, "ThemeContentBackground");
        Canvas.SetLeft(keyhole, 6);
        Canvas.SetTop(keyhole, 10.6);
        canvas.Children.Add(keyhole);
        return canvas;
    }

    private static string FormatSpeed(long bitsPerSec)
    {
        if (bitsPerSec <= 0) return "— Mbps";
        if (bitsPerSec >= 1000_000_000) return $"{(bitsPerSec / 1_000_000.0):0} Mbps";
        if (bitsPerSec >= 1_000_000) return $"{(bitsPerSec / 1_000_000.0):0} Mbps";
        if (bitsPerSec >= 1000) return $"{(bitsPerSec / 1000.0):0} Kbps";
        return $"{bitsPerSec} bps";
    }
}
