// NETWORK 独立面板（公网 IP+地理 / WLAN 本地 IP / 实时流量图 / 上下行速度 & 累计）。
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BetterDesktop.Shell.MenuBar.Contracts;
using BetterDesktop.Shell.MenuBar.Services;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Status.Native;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>NETWORK 独立面板（截图左一）。</summary>
internal sealed class NetworkPanelWindow : MenuBarPopupWindow
{
    public NetworkPanelWindow(IVibrancyService vibrancy, IAppearanceService? appearance = null)
        : base(vibrancy, appearance)
    {
        Width = NativePanelStyles.DefaultWidth;
        MinWidth = NativePanelStyles.DefaultWidth;
        SizeToContent = SizeToContent.Height;
    }

    public FrameworkElement BuildPreviewContent() => BuildContent();

    /// <summary>创建一个绑定到 ViewModel 某属性的 TextBlock（数据变化自动实时刷新）。</summary>
    private static TextBlock BoundText(
        NetworkPanelViewModel vm, string propertyName,
        double fontSize, Brush foreground, FontWeight? weight = null)
    {
        var tb = new TextBlock
        {
            FontSize = fontSize,
            Foreground = foreground
        };
        if (weight.HasValue) tb.FontWeight = weight.Value;
        BindingOperations.SetBinding(tb, TextBlock.TextProperty,
            new Binding(propertyName) { Source = vm });
        return tb;
    }

    protected override FrameworkElement BuildContent()
    {
        var vm = new NetworkPanelViewModel();
        var root = NativePanelStyles.Root(withColumn: col =>
        {
            // ---------- 顶部标题 ----------
            col.Children.Add(NativePanelStyles.Title("NETWORK"));

            // ---------- 公网 IP + 地理 ----------
            col.Children.Add(BuildPublicSection(vm));

            col.Children.Add(NativePanelStyles.Separator(top: 10, bottom: 8));

            // ---------- WLAN 本地 IP / 连接摘要 ----------
            col.Children.Add(BuildLocalSection(vm));

            col.Children.Add(NativePanelStyles.Separator(top: 10, bottom: 4));

            // ---------- 实时流量图 + 当下速率 + 累计 ----------
            col.Children.Add(BuildTrafficChart(vm, out var refresh));

            col.Children.Add(NativePanelStyles.Separator(top: 6, bottom: 8));

            col.Children.Add(BuildSpeedSummary(vm));

            col.Children.Add(NativePanelStyles.Separator(top: 6, bottom: 4));

            // 与麦克风/亮度/电池面板共用同一份跳转件（配色、点击行为完全一致）
            col.Children.Add(NativePanelStyles.CreateSettingsLink("网络偏好设置…", "ms-settings:network-status", fontSize: 10.5));

            vm.AttachRefresh(refresh);
        });

        return root;
    }

    private static FrameworkElement BuildPublicSection(NetworkPanelViewModel vm)
    {
        var grid = new Grid { Margin = new Thickness(4, 0, 4, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new Viewbox
        {
            Width = 22,
            Height = 22,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new Canvas
            {
                Width = 24,
                Height = 24,
                Children =
            {
                new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M12 2C7 2 2.5 6 1.2 11.3c-.2.9.5 1.7 1.4 1.7h1.1c.6 0 1.2-.4 1.4-1C5.6 8.3 8.4 6 12 6s6.4 2.3 6.9 6c.2.6.8 1 1.4 1h1.1c.9 0 1.6-.8 1.4-1.7C21.5 6 17 2 12 2zm0 6c-2.8 0-5.2 1.8-5.9 4.3-.2.7.3 1.3 1 1.3H8c.5 0 .9-.4 1-.9.5-1.4 1.8-2.4 3-2.4s2.5 1 3 2.4c.1.5.5.9 1 .9h.9c.7 0 1.2-.6 1-1.3C17.2 9.8 14.8 8 12 8z"),
                    Fill = NativePanelStyles.TextPrimary
                }
            }
            }
        });

        // IP 与归属地放在同一行：IP 主题前景 SemiBold 13，归属地次要前景 10.5。
        // 这样 NETWORK 标题区只占一行高度，与下面 WLAN 卡片视觉节奏更整齐。
        var row = new TextBlock
        {
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        // 绑定 IP：Source 是 VM，整段文字需要随 IP 刷新
        var ipRun = new System.Windows.Documents.Run
        {
            FontWeight = FontWeights.SemiBold
        };
        BindingOperations.SetBinding(ipRun, System.Windows.Documents.Run.TextProperty,
            new Binding(nameof(NetworkPanelViewModel.PublicIp)) { Source = vm, Mode = BindingMode.OneWay });
        row.Inlines.Add(ipRun);
        // 分隔点 + 归属地（动态拼接，IP 没出来时也不显示 " · "）
        row.Inlines.Add(new System.Windows.Documents.Run(" · ")
        {
            Foreground = NativePanelStyles.TextSecondary,
            FontSize = 10.5
        });
        var geoRun = new System.Windows.Documents.Run
        {
            Foreground = NativePanelStyles.TextSecondary,
            FontSize = 10.5
        };
        BindingOperations.SetBinding(geoRun, System.Windows.Documents.Run.TextProperty,
            new Binding(nameof(NetworkPanelViewModel.PublicGeo)) { Source = vm, Mode = BindingMode.OneWay });
        row.Inlines.Add(geoRun);
        Grid.SetColumn(row, 1);
        grid.Children.Add(row);

        Grid.SetColumn(new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = Brushes.Transparent,
            Child = new TextBlock
            {
                Text = "⤷",
                Foreground = NativePanelStyles.TextSecondary,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        }, 2);

        return grid;
    }

    private static FrameworkElement BuildLocalSection(NetworkPanelViewModel vm)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 左侧 WLAN 小卡片（透明背景融入窗口，与音量胶囊同属性；加长避免本地 IP 被截断）
        var tile = new Border
        {
            Width = 80,
            CornerRadius = new CornerRadius(10),
            BorderBrush = NativePanelStyles.SeparatorBack,
            BorderThickness = new Thickness(0.4),
            Padding = new Thickness(4, 6, 4, 6),
            VerticalAlignment = VerticalAlignment.Stretch,
            // 透明背景：与窗口属性一致，无独立黑色色块
            Background = Brushes.Transparent
        };
        var localIpTb = BoundText(vm, nameof(NetworkPanelViewModel.LocalIp),
            10, NativePanelStyles.TextSecondary);
        localIpTb.Margin = new Thickness(0, 3, 0, 0);
        localIpTb.HorizontalAlignment = HorizontalAlignment.Center;
        localIpTb.TextTrimming = TextTrimming.CharacterEllipsis;

        tile.Child = new StackPanel
        {
            Children =
            {
                BuildWifiGlyph(),
                new TextBlock
                {
                    Text = "WLAN",
                    FontSize = 9.5,
                    Foreground = NativePanelStyles.TextPrimary,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                localIpTb
            }
        };
        grid.Children.Add(tile);

        // 右侧当前网速汇总（实时）
        var col = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var throughputTb = BoundText(vm, nameof(NetworkPanelViewModel.CurrentThroughputLabel),
            11, NativePanelStyles.TextSecondary);
        throughputTb.HorizontalAlignment = HorizontalAlignment.Right;
        col.Children.Add(throughputTb);
        Grid.SetColumn(col, 1);
        grid.Children.Add(col);

        return grid;
    }

    private static FrameworkElement BuildTrafficChart(NetworkPanelViewModel vm, out Action<int, double[]> refresh)
    {
        const int slots = 30;
        const double containerW = 290;
        const double containerH = 62;
        var canvas = new Canvas
        {
            Width = containerW,
            Height = containerH,
            Background = Brushes.Transparent,
            Margin = new Thickness(2, 4, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        refresh = (_, values) =>
        {
            canvas.Children.Clear();
            // 画 5 条水平参考虚线
            var dashes = new DoubleCollection(new double[] { 1.5, 2.5 });
            for (int i = 0; i <= 4; i++)
            {
                var y = 4 + i * ((containerH - 8) / 4.0);
                var line = new System.Windows.Shapes.Line
                {
                    X1 = 0,
                    X2 = containerW,
                    Y1 = y,
                    Y2 = y,
                    Stroke = NativePanelStyles.SeparatorBack,
                    StrokeThickness = 0.6,
                    StrokeDashArray = dashes
                };
                canvas.Children.Add(line);
            }
            // 画柱
            var gapX = 2;
            var bw = (containerW - gapX * (slots + 1)) / slots;
            var max = values.DefaultIfEmpty(0).Max();
            if (max < 1) max = 1;
            for (int i = 0; i < slots; i++)
            {
                var h = (containerH - 10) * (values.Length > i ? values[i] / max : 0);
                if (h < 1.5) h = 1.5;
                var col = new Border
                {
                    Width = bw,
                    Height = h,
                    CornerRadius = new CornerRadius(bw / 2),
                    Background = NativePanelStyles.BarFill
                };
                Canvas.SetLeft(col, gapX + i * (bw + gapX));
                Canvas.SetTop(col, containerH - 4 - h);
                canvas.Children.Add(col);
            }
        };

        return canvas;
    }

    private static FrameworkElement BuildSpeedSummary(NetworkPanelViewModel vm)
    {
        // 上/下行分行上下对应：每行 = 方向标签 + 实时网速 + 累计用量。
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < 3; c++)
        {
            grid.ColumnDefinitions.Add(c == 1
                ? new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                : new ColumnDefinition { Width = GridLength.Auto });
        }

        // 第 0 行：上行
        grid.Children.Add(MakeDirectionCell("上行", vm, nameof(NetworkPanelViewModel.UploadSpeedLabel),
            nameof(NetworkPanelViewModel.UploadTotalLabel), "▲", NativePanelStyles.AccentBack, false, 0));
        // 第 1 行：下行
        grid.Children.Add(MakeDirectionCell("下行", vm, nameof(NetworkPanelViewModel.DownloadSpeedLabel),
            nameof(NetworkPanelViewModel.DownloadTotalLabel), "▼", NativePanelStyles.BarFillBlueLight, true, 1));

        return grid;
    }

    /// <summary>构造单行：方向 + 实时网速 + 累计用量（speedProp/usageProp 绑定 ViewModel，随数据实时刷新）。</summary>
    private static Border MakeDirectionCell(string direction, NetworkPanelViewModel vm,
        string speedProp, string usageProp, string arrow,
        Brush accent, bool right, int row)
    {
        var cell = new Border
        {
            Margin = new Thickness(0, right ? 6 : 0, 0, 0),
            Background = right ? new SolidColorBrush(Color.FromArgb(26, 0, 0, 0)) : Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 6, 8, 6)
        };
        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dir = new TextBlock
        {
            Text = arrow + " " + direction,
            Foreground = accent,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        inner.Children.Add(dir);

        var speedTb = BoundText(vm, speedProp, 12, NativePanelStyles.TextPrimary, FontWeights.SemiBold);
        speedTb.Margin = new Thickness(10, 0, 0, 0);
        speedTb.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(speedTb, 1);
        inner.Children.Add(speedTb);

        var usageTb = BoundText(vm, usageProp, 10.5, NativePanelStyles.TextSecondary);
        usageTb.HorizontalAlignment = HorizontalAlignment.Right;
        usageTb.VerticalAlignment = VerticalAlignment.Center;
        usageTb.TextTrimming = TextTrimming.CharacterEllipsis;
        usageTb.MaxWidth = 110;
        Grid.SetColumn(usageTb, 2);
        inner.Children.Add(usageTb);

        cell.Child = inner;
        Grid.SetRow(cell, row);
        Grid.SetColumnSpan(cell, 3); // 占满三列
        return cell;
    }

    /// <summary>
    /// WLAN 小卡片里的 Wi‑Fi 图标：复用 <see cref="WifiGlyph"/>（与菜单栏状态条同一份绘制），
    /// 不再自己抄一份几何（旧版画布只有 12 高，底部圆点溢出被裁，且永远满格不反映真实强度）。
    /// </summary>
    private static FrameworkElement BuildWifiGlyph()
    {
        int level;
        try
        {
            var info = WifiEnumerator.ReadCurrentConnection();
            level = info.IsConnected
                ? (info.SignalQuality > 0 ? WifiGlyph.LevelFromQuality(info.SignalQuality) : 3)
                : 0;
        }
        catch
        {
            level = 0;
        }

        WifiGlyph.BuildFan(NativePanelStyles.TextPrimary, level, out var canvas, out _, out _);
        return WifiGlyph.Wrap(canvas, 24, 24);
    }
}

/// <summary>NETWORK 面板的轻量 ViewModel（纯 UI 聚合层，不进 Contracts）。
/// 实现 INotifyPropertyChanged，使上下行速率/用量等数据变化实时反映到绑定的 UI 上。</summary>
internal sealed class NetworkPanelViewModel : INotifyPropertyChanged
{
    private string _publicIp = "58.241.3.98（查询中…）";
    private string _publicGeo = "中国 江苏 无锡市";
    private string _localIp = "10.2.83.51";
    private string _currentThroughputLabel = "282.8 KB";
    private string _uploadSpeedLabel = "26.5KB/s";
    private string _uploadTotalLabel = "117.7 MB";
    private string _downloadSpeedLabel = "277.4KB/s";
    private string _downloadTotalLabel = "163.2 MB";

    public string PublicIp { get => _publicIp; private set => Set(ref _publicIp, value); }
    public string PublicGeo { get => _publicGeo; private set => Set(ref _publicGeo, value); }
    public string LocalIp { get => _localIp; private set => Set(ref _localIp, value); }
    public string CurrentThroughputLabel { get => _currentThroughputLabel; private set => Set(ref _currentThroughputLabel, value); }
    public string UploadSpeedLabel { get => _uploadSpeedLabel; private set => Set(ref _uploadSpeedLabel, value); }
    public string UploadTotalLabel { get => _uploadTotalLabel; private set => Set(ref _uploadTotalLabel, value); }
    public string DownloadSpeedLabel { get => _downloadSpeedLabel; private set => Set(ref _downloadSpeedLabel, value); }
    public string DownloadTotalLabel { get => _downloadTotalLabel; private set => Set(ref _downloadTotalLabel, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>赋值并在变化时触发属性变更通知，让数据绑定实时刷新。</summary>
    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private readonly DispatcherTimer _timer;
    private readonly double[] _bars = new double[30];
    private readonly List<double> _history = new(capacity: 32);
    private Action<int, double[]>? _refreshChart;
    private NetworkPrimaryNative _prev;
    private DateTime _prevTs;

    public NetworkPanelViewModel()
    {
        LoadPublicIpAsyncFireAndForget();

        var snap = NetworkCoreNative.IsAvailable ? NetworkCoreNative.ReadPrimary() : default;
        if (snap.Ok)
        {
            _prev = snap;
            _prevTs = DateTime.UtcNow;
            PublicIp = !string.IsNullOrEmpty(snap.IpV4) ? snap.IpV4 : "—";
            LocalIp = string.IsNullOrEmpty(snap.IpV4) ? LocalIp : snap.IpV4;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public void AttachRefresh(Action<int, double[]> refresh)
    {
        _refreshChart = refresh;
        _refreshChart?.Invoke(_bars.Length, _bars);
    }

    private async void LoadPublicIpAsyncFireAndForget()
    {
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            var ipTxt = await http.GetStringAsync("https://api.ipify.org", cts.Token).ConfigureAwait(false);
            var ip = ipTxt?.Trim() ?? "";
            string geo = "";
            try
            {
                var geoTxt = await http.GetStringAsync($"https://ipapi.co/{ip}/json/", cts.Token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(geoTxt);
                var country = doc.RootElement.GetPropertyOrDefault("country_name")?.GetString() ?? "";
                var region = doc.RootElement.GetPropertyOrDefault("region")?.GetString() ?? "";
                var city = doc.RootElement.GetPropertyOrDefault("city")?.GetString() ?? "";
                geo = string.Join(" ", new[] { country, region, city }.Where(s => !string.IsNullOrEmpty(s)));
            }
            catch
            {
                geo = "";
            }

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!string.IsNullOrEmpty(ip)) PublicIp = ip;
                if (!string.IsNullOrEmpty(geo)) PublicGeo = geo;
            });
        }
        catch
        {
            // 离线场景：不中断面板
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!NetworkCoreNative.IsAvailable) return;
        var snap = NetworkCoreNative.ReadPrimary();
        if (!snap.Ok) return;
        if (!string.IsNullOrEmpty(snap.IpV4)) LocalIp = snap.IpV4;

        var now = DateTime.UtcNow;
        if (_prevTs != default && _prev.IfIndex == snap.IfIndex)
        {
            var dt = (now - _prevTs).TotalSeconds;
            if (dt > 0.05)
            {
                var rx = (double)(snap.RxBytes >= _prev.RxBytes ? snap.RxBytes - _prev.RxBytes : 0);
                var tx = (double)(snap.TxBytes >= _prev.TxBytes ? snap.TxBytes - _prev.TxBytes : 0);
                var rxSpeed = rx / dt;
                var txSpeed = tx / dt;
                UploadSpeedLabel = NativePanelStyles.FormatBytes(txSpeed) + "/s";
                DownloadSpeedLabel = NativePanelStyles.FormatBytes(rxSpeed) + "/s";
                UploadTotalLabel = NativePanelStyles.FormatBytes(snap.TxBytes);
                DownloadTotalLabel = NativePanelStyles.FormatBytes(snap.RxBytes);
                var total = rx + tx;
                CurrentThroughputLabel = NativePanelStyles.FormatBytes(total);
                PushBar(total);
            }
        }
        _prev = snap;
        _prevTs = now;
        _refreshChart?.Invoke(_bars.Length, _bars);
    }

    private void PushBar(double value)
    {
        for (int i = 0; i < _bars.Length - 1; i++) _bars[i] = _bars[i + 1];
        _bars[_bars.Length - 1] = value;
    }
}

file static class JsonExtensions
{
    public static JsonElement? GetPropertyOrDefault(this JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        return el.TryGetProperty(name, out var v) ? v : null;
    }
}
