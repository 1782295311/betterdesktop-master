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
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Services;
using Windows.Devices.Radios;

namespace BetterDesktop.Shell.MenuBar.Windows;

// ── 本文件方法级白话索引（WiFi 列表面板，白话 → 方法）──
//   "面板整体"                       → BuildContent / RefreshContent
//   "当前已连网络信息块（IP/速率/MAC）" → LoadCurrentAsync / CreateCurrentConnectionBlock / CreateMetaLine / CreateMetaLineMac
//   "WiFi 总开关（无线电）"          → LoadRadioStateAsync
//   "定时扫描 + 附近网络列表"         → ScanTickAsync / RenderNearby / CreateNearbyRow；信号/锁图标 SignalIcon/LockIcon
//   "输密码连网 / 断开"              → OpenPasswordDialog（连接逻辑在 WifiEnumerator）
//   "跳系统设置项"                   → CreatePrefLinkRow；速率格式化 FormatSpeed
//   原生枚举与连接能力在 Services/WifiEnumerator.cs；面板基类 MenuBarPopupWindow。
// ────────────────────────────────────

/// <summary>Wi‑Fi 独立弹出面板。</summary>
internal sealed class WifiPopupWindow : MenuBarPopupWindow
{
    private const double DefaultWidth = 320;
    private readonly IVibrancyService _vibrancy;
    private readonly IAppearanceService? _appearance;

    // 2026-09-04 用户拍板：WiFi 面板要像蓝牙面板一样持续扫描更新——
    // 面板可见期间周期扫描刷新列表（隐藏即停，防后台空转）；防重入避免扫描耗时 > 间隔时重叠。
    private System.Windows.Threading.DispatcherTimer? _scanTimer;
    private StackPanel? _nearbyHost;
    private int _scanBusy;

    public WifiPopupWindow(IVibrancyService vibrancy, IAppearanceService? appearance = null)
        : base(vibrancy, appearance)
    {
        Width = DefaultWidth;
        MinWidth = DefaultWidth;
        SizeToContent = SizeToContent.Height;
        _vibrancy = vibrancy;
        _appearance = appearance;

        _scanTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _scanTimer.Tick += async (_, _) => await ScanTickAsync();
        // 面板显示 → 启动周期扫描并立即来一轮；隐藏 → 停（7438 配对纪律：启停严格成对）。
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && _scanTimer is not null)
            {
                _scanTimer.Start();
                _ = ScanTickAsync();
            }
            else
            {
                _scanTimer?.Stop();
            }
        };
    }

    // G7：设计预览路径门控——预览时跳过真实 radio/wlan 后台读取，只渲染占位。
    // 基类 BuildContent() 为无参抽象方法（MenuBarPopupWindow.cs L131），故用字段而非参数传递。
    private bool _previewMode;

    public FrameworkElement BuildPreviewContent()
    {
        _previewMode = true;
        try { return BuildContent(); }
        finally { _previewMode = false; }
    }

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
        // G3：初始态不再硬编码 ON——BuildContent 后异步读真实 radio 状态回填。
        // ToggleSwitch 程序化赋值 IsOn 只走 OnIsOnChanged 更新视觉、不触发 Toggled（无回环风险）。
        // 切换失败（SetStateAsync=false）时记日志并回弹开关；读取失败保守保持占位值。
        ToggleSwitch? wifiToggle = null;
        column.Children.Add(CreateToggleRow("Wi‑Fi", on =>
        {
            var toggle = wifiToggle;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                bool ok = await RadioInterop.SetStateAsync(RadioKind.WiFi, on).ConfigureAwait(false);
                if (!ok && toggle is not null)
                {
                    DiagnosticLog.Trace("menu-bar.wifi", $"WiFi radio 切换失败（目标={(on ? "开" : "关")}），回弹开关");
                    await toggle.Dispatcher.InvokeAsync(() => toggle.IsOn = !on);
                }
            });
        }, t => wifiToggle = t));
        if (!_previewMode && wifiToggle is not null)
        {
            _ = LoadRadioStateAsync(wifiToggle);
        }

        // ========== 当前连接信息（2026-09-04 回归修复：改后台加载——切换网络瞬间
        // ReadCurrentConnection 的 W4 托管降级会跑 GetAllNetworkInterfaces（可达秒级），
        // 此前在 UI 线程同步调用 = 每次刷新卡死 STA 线程（右键菜单唤不出/程序卡死的根因）。 ==========
        var currentPlaceholder = CreateMutedText("正在读取连接状态…", new Thickness(14, 6, 14, 6));
        column.Children.Add(currentPlaceholder);
        column.Children.Add(CreatePrefLinkRow("网络偏好设置", "ms-settings:network-wifi"));

        column.Children.Add(CreateSeparator());

        // ========== "其他网络" 标题 ==========
        column.Children.Add(CreateMutedText("其他网络", new Thickness(14, 4, 0, 6), FontWeights.SemiBold, 10));

        // ========== 附近网络列表（持续扫描：面板可见期间每 4s 刷新，见 ScanTickAsync） ==========
        var nearbyHost = new StackPanel();
        _nearbyHost = nearbyHost;
        nearbyHost.Children.Add(CreateMutedText("正在扫描附近 Wi‑Fi 网络…", new Thickness(14, 6, 14, 8), wrap: true));
        column.Children.Add(nearbyHost);

        if (!_previewMode)
        {
            _ = LoadCurrentAsync(column, currentPlaceholder);
        }

        root.Child = column;
        return root;
    }

    /// <summary>后台读取当前连接并回填（UI 线程零 wlanapi/GetAllNetworkInterfaces 调用）。
    /// S3：整体 try-catch + Dispatcher 判空——fire-and-forget 链上的异常不得直达进程级处理器。</summary>
    private static async System.Threading.Tasks.Task LoadCurrentAsync(StackPanel column, FrameworkElement placeholder)
    {
        try
        {
            var current = await System.Threading.Tasks.Task.Run(() => WifiEnumerator.ReadCurrentConnection()).ConfigureAwait(false);
            var ui = System.Windows.Application.Current?.Dispatcher;
            if (ui is null)
            {
                return; // 宿主已退出/预览环境：放弃回填
            }
            await ui.InvokeAsync(() =>
            {
                // 面板可能在等待期间被 RefreshContent 重建：只替换仍挂在本列上的占位元素。
                if (!column.Children.Contains(placeholder))
                {
                    return;
                }

                var index = column.Children.IndexOf(placeholder);
                column.Children.RemoveAt(index);
                if (current.IsConnected)
                {
                    column.Children.Insert(index, CreateCurrentConnectionBlock(current));
                }
                else
                {
                    column.Children.Insert(index, CreateMutedText("未连接到任何 Wi‑Fi 网络", new Thickness(14, 6, 14, 6)));
                }
            });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("menu-bar.wifi", "当前连接回填失败: " + ex.Message);
        }
    }

    /// <summary>
    /// G3：异步读取真实 WiFi radio 状态并回填开关。ToggleSwitch 程序化赋值 IsOn 只走
    /// OnIsOnChanged 更新视觉、不触发 Toggled（ToggleSwitch.cs L70-81），无回环风险。
    /// 读取失败/不可用：保守保持占位值，不打断 UI。
    /// </summary>
    private static async System.Threading.Tasks.Task LoadRadioStateAsync(ToggleSwitch toggle)
    {
        try
        {
            var state = await RadioInterop.GetStateAsync(RadioKind.WiFi).ConfigureAwait(false);
            if (state is null)
            {
                return; // 读不到 radio（无权限/无设备）：保守保持当前值
            }
            bool on = state.Value == RadioState.On;
            await toggle.Dispatcher.InvokeAsync(() => toggle.IsOn = on);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("menu-bar.wifi", "读取 WiFi radio 状态失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 周期扫描一轮（面板可见期间每 4s；防重入：上一轮未完成时跳过本轮）。
    /// 全程后台执行，UI 线程零 wlanapi 调用。空列表不再定论——下一轮继续（对齐蓝牙面板持续更新语义）。
    /// </summary>
    private async System.Threading.Tasks.Task ScanTickAsync()
    {
        if (!IsVisible || _nearbyHost is null)
        {
            return;
        }
        if (System.Threading.Interlocked.CompareExchange(ref _scanBusy, 1, 0) != 0)
        {
            return; // 上一轮还在跑
        }
        try
        {
            // 当前连接 SSID：走 ReadCurrentConnection 的 1s 缓存，与 LoadCurrentAsync 共享底层读取。
            var current = await System.Threading.Tasks.Task.Run(() => WifiEnumerator.ReadCurrentConnection()).ConfigureAwait(false);
            var networks = await System.Threading.Tasks.Task.Run(() => WifiEnumerator.ScanNearbyAsync()).ConfigureAwait(false);
            var connectedSsid = current.IsConnected ? current.Ssid : string.Empty;
            await Dispatcher.InvokeAsync(() => RenderNearby(networks, connectedSsid));
        }
        catch (Exception ex)
        {
            // S3：Tick 的 async void 链上不允许异常逃逸（否则直达进程级处理器，表现为闪退）。
            DiagnosticLog.Trace("menu-bar.wifi", "扫描周期异常: " + ex.Message);
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _scanBusy, 0);
        }
    }

    /// <summary>把扫描结果渲染到附近网络区块（仅当该区块仍属于当前面板内容时）。</summary>
    private void RenderNearby(System.Collections.Generic.IReadOnlyList<WifiNearbyItem> networks, string connectedSsid)
    {
        var host = _nearbyHost;
        if (host is null || !IsVisible)
        {
            return; // 面板已被 RefreshContent 重建/关闭：放弃本轮渲染
        }

        host.Children.Clear();
        if (networks.Count == 0)
        {
            host.Children.Add(CreateMutedText(
                "未扫描到附近 Wi‑Fi 网络。适配器可能正忙（如正在连接/认证中），将持续自动重试。",
                new Thickness(14, 6, 14, 8), wrap: true));
            return;
        }
        int shown = 0;
        foreach (var net in networks)
        {
            if (shown++ >= 12) break; // 预览限制最多 12 个，避免面板太高
            bool isCurrent = string.Equals(net.Ssid, connectedSsid, StringComparison.Ordinal);
            host.Children.Add(CreateNearbyRow(net, isCurrent));
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _scanTimer?.Stop(); // 生命周期收口：窗口关闭停周期扫描（7438 配对纪律）
        _scanTimer = null;
        _nearbyHost = null;
        base.OnClosed(e);
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

    private static FrameworkElement CreateToggleRow(string label, Action<bool> onChanged, Action<ToggleSwitch>? onCreated = null)
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
            IsOn = true, // 初始占位；真实 radio 态由 LoadRadioStateAsync 异步回填（G3）
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        onCreated?.Invoke(toggle);
        toggle.Toggled += (_, args) =>
        {
            try { onChanged((bool)args); }
            catch (Exception ex) { DiagnosticLog.Trace("menu-bar.wifi", "WiFi toggle 切换处理失败: " + ex.Message); }
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

        // 信号图标：4 条条形（按 20/40/60/80 截断点亮条数；与 WifiGlyph 三弧折算是两套阈值，统一见 deferred）
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
                try
                {
                    // 检查是否有已保存配置文件
                    bool hasProfile = await System.Threading.Tasks.Task.Run(() => WifiEnumerator.HasSavedProfile(net.Ssid));
                    if (!hasProfile)
                    {
                        // 无保存配置：直接弹密码窗输入密码连接
                        owner?.OpenPasswordDialog(net.Ssid);
                        return;
                    }
                    // 有保存配置：先用保存的密码直接连接（密码正确时无需重复输入）。
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
                        try
                        {
                            // G4：统一等待器（含 SSID 核对，防把其它网络/适配器的连接误判为成功）。
                            bool connected = await WifiEnumerator.WaitForConnectionAsync(targetSsid).ConfigureAwait(false);
                            if (!connected)
                            {
                                // 失败/超时：先断开释放适配器，再删除错误 profile（connectionMode=auto 红线）。
                                await System.Threading.Tasks.Task.Run(() => WifiEnumerator.CleanupFailedConnection(targetSsid)).ConfigureAwait(false);
                            }
                            if (owner is not null)
                            {
                                await owner.Dispatcher.InvokeAsync(() =>
                                {
                                    owner.RefreshContent();
                                    if (!connected)
                                    {
                                        owner.OpenPasswordDialog(targetSsid);
                                    }
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            DiagnosticLog.Trace("menu-bar.wifi", $"连接等待任务异常（{targetSsid}）: {ex.Message}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Trace("menu-bar.wifi", $"连接请求处理异常（{net.Ssid}）: {ex.Message}");
                }
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

    /// <summary>链路速度显示（G1：入参为 bits/秒；旧实现的 ≥1Gbps 分支与 ≥1Mbps 分支逐字相同是死分支，已合并）。</summary>
    internal static string FormatSpeed(long bitsPerSec)
    {
        if (bitsPerSec <= 0) return "— Mbps";
        if (bitsPerSec >= 1_000_000) return $"{(bitsPerSec / 1_000_000.0):0} Mbps";
        if (bitsPerSec >= 1000) return $"{(bitsPerSec / 1000.0):0} Kbps";
        return $"{bitsPerSec} bps";
    }
}
