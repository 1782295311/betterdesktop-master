// BetterDesktop.Shell.MenuBar — 蓝牙独立弹出面板（零硬编码，真实系统设备）。
// UI 结构（按截图布局）：
//   - 顶部：蓝牙 Toggle 开关 + "蓝牙偏好设置"跳转（ms-settings:bluetooth）
//   - 分隔线
//   - "设备" 分组：BluetoothEnumerator.Enumerate() → 系统已配对蓝牙设备
//     每行：图标 + 设备名 + 右侧状态（实心圆=已连接/加载中=空心转/未连接=空心）
//     设备分类：鼠标/耳机/手机/平板/TWS…（真实系统名称推导，无硬编码）
//
// v2 优化（修复连接后状态不更新 + 界面卡死 + 响应慢）：
//   1. 状态更新改为事件驱动：DeviceWatcher.Updated 直接带 System.Devices.Aep.IsConnected，
//      同时驱动 _known / _nearby 两列表，并即时唤醒等待中的 WaitForStateAsync（不再死等轮询）。
//   2. 状态轮询兜底改用 WinRT ConnectionStatus（BLE/经典都准），不再依赖经典枚举的 fConnected。
//   3. 渲染节流合并：所有高频事件只排队一次渲染（200ms 合并），并去掉 UI 线程上的
//      GetRadioState P/Invoke（无线电状态改为后台线程刷新 + 缓存）。
//   4. 配对对话框挂到真实父窗口、在 UI 线程弹出（模态自带消息泵），不再丢后台线程造成挂起。
//   5. 所有设备列表的增删改统一调度到 UI 线程，避免 watcher 后台线程与渲染线程并发读写集合。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Services;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace BetterDesktop.Shell.MenuBar.Windows;

// ── 本文件方法级白话索引（蓝牙面板，白话 → 方法）──
//   "开始/停止扫描蓝牙设备（经典 + BLE）" → StartScanning / StopScanning；BLE 广播回调 OnBleAdvertisementReceived → AddNearbyBle / FindBleItem
//   "蓝牙开关（无线电状态）"              → RefreshRadioStateAsync / OnRadioStateChanged
//   "渲染已配对/附近设备列表"             → RenderDeviceList（渲染节流 ScheduleRender/QueueRender/OnRenderThrottleTick）；面板内容 BuildPreviewContent/BuildContent
//   "配对/连接/断开某设备（点击总入口）"  → ToggleDeviceAsync；清理过期设备 CleanupStaleDevices；连接状态确认 WaitForStateAsync
//   扫描随面板可见性启停：OnVisibilityChanged → StartScanning / StopScanning（后者停全部 timer 与 watcher）。
// B9 已修：临时诊断基础设施（Diag 写盘日志、_diagPulseTimer、_uiWatchdog 及诊断计数器）已整体移除，不再随开关泄漏/写盘。
// ────────────────────────────────────

/// <summary>蓝牙独立弹出面板。支持持续扫描附近设备、所有设备点击配对/连接/断开。</summary>
internal sealed class BluetoothPopupWindow : MenuBarPopupWindow
{
    private const double DefaultWidth = 300;

    // ---- 渲染合并 ----
    private readonly DispatcherTimer _renderThrottle = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _renderQueued;

    // ---- 无线电状态缓存（后台刷新，避免 UI 线程 P/Invoke）----
    private BluetoothRadioState _radioState = BluetoothRadioState.On;
    private readonly DispatcherTimer _radioTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _radioRefreshQueued;

    private StackPanel _deviceHost = new();
    private TextBlock? _scanStatusText;
    private readonly List<BluetoothDeviceItem> _known = new();
    private readonly List<BluetoothDeviceItem> _nearby = new();
    private readonly HashSet<string> _knownNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _nearbyNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ulong> _pendingAddresses = new(); // 正在配对/连接中的设备地址
    private readonly HashSet<ulong> _bleAddresses = new(); // 已发现的 BLE 设备地址（去重）

    /// <summary>等待指定设备达到目标连接状态的完成源：DeviceWatcher.Updated 事件驱动完成。</summary>
    private readonly Dictionary<ulong, (TaskCompletionSource<bool> Tcs, bool ExpectConnected)> _stateWaiters = new();

    private DeviceWatcher? _deviceWatcher;
    private BluetoothLEAdvertisementWatcher? _bleWatcher;
    private FrameworkElement? _progressRing;
    private DispatcherTimer? _cleanupTimer;
    private bool _isScanning;

    public BluetoothPopupWindow(IVibrancyService vibrancy, IAppearanceService? appearance = null)
        : base(vibrancy, appearance)
    {
        Width = DefaultWidth;
        MinWidth = DefaultWidth;
        SizeToContent = SizeToContent.Height;
        IsVisibleChanged += OnVisibilityChanged;

        // 所有 DispatcherTimer 都在 UI 线程构造；事件回调里只通过 ScheduleRender() 排队
        _renderThrottle.Tick += OnRenderThrottleTick;
        _radioTimer.Tick += OnRadioTimerTick;
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue) StartScanning();
        else StopScanning();
    }

    /// <summary>启动搜索：DeviceWatcher 发现经典蓝牙未配对设备 + BluetoothLEAdvertisementWatcher 主动扫描 BLE 设备。</summary>
    private void StartScanning()
    {
        if (_isScanning) return;
        _isScanning = true;

        if (_progressRing is not null)
            _progressRing.Visibility = Visibility.Visible;
        if (_scanStatusText is not null)
        {
            _scanStatusText.Text = "正在搜索附近设备…";
            _scanStatusText.Visibility = Visibility.Visible;
        }

        // 无线电状态后台刷新（首帧用缓存值，随后校正）
        _radioTimer.Start();
        RefreshRadioStateAsync();

        // 1. DeviceWatcher：监听所有蓝牙设备（用宽泛选择器确保能收到事件，已配对设备在 Added 中过滤）
        try
        {
            var selector = BluetoothDevice.GetDeviceSelector();
            var additionalProperties = new[]
            {
                "System.Devices.Aep.IsConnected",
                "System.Devices.Aep.IsPaired",
                "System.Devices.Aep.Bluetooth.ClassOfDevice",
                "System.Devices.Aep.DeviceAddress"
            };
            _deviceWatcher = DeviceInformation.CreateWatcher(selector, additionalProperties);
            _deviceWatcher.Added += OnDeviceAdded;
            _deviceWatcher.Updated += OnDeviceUpdated;
            _deviceWatcher.Removed += OnDeviceRemoved;
            _deviceWatcher.Start();
        }
        catch { /* DeviceWatcher 不可用时静默，BLE 扫描仍在运行 */ }

        // 2. BluetoothLEAdvertisementWatcher：主动扫描 BLE 广播（发现一个触发一次 Received）
        try
        {
            _bleWatcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };
            _bleWatcher.Received += OnBleAdvertisementReceived;
            _bleWatcher.Start();
        }
        catch { /* BLE 扫描不可用时静默 */ }

        // 3. 过时清理定时器：每 5 秒移除超过 15 秒未再出现的设备
        _cleanupTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _cleanupTimer.Tick += (_, _) => CleanupStaleDevices();
        _cleanupTimer.Start();
    }

    // ==================== 渲染合并 ====================

    /// <summary>排队一次渲染（200ms 内多个事件合并为一次），可在任意线程调用。</summary>
    private void ScheduleRender()
    {
        if (Dispatcher.CheckAccess()) QueueRender();
        else _ = Dispatcher.BeginInvoke(QueueRender);
    }

    private void QueueRender()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        _renderThrottle.Stop();
        _renderThrottle.Start();
    }

    private void OnRenderThrottleTick(object? sender, EventArgs e)
    {
        _renderThrottle.Stop();
        _renderQueued = false;
        RenderDeviceList();
        UpdateScanStatus();
    }

    // ==================== 无线电状态（后台刷新 + 缓存） ====================

    private void OnRadioTimerTick(object? sender, EventArgs e) => RefreshRadioStateAsync();

    private async void RefreshRadioStateAsync()
    {
        if (_radioRefreshQueued) return;
        _radioRefreshQueued = true;
        try
        {
            var state = await RadioInterop.GetStateAsync(RadioKind.Bluetooth);
            var mapped = state switch
            {
                RadioState.On => BluetoothRadioState.On,
                RadioState.Off => BluetoothRadioState.Off,
                _ => BluetoothRadioState.NoAdapter
            };
            if (_radioState != mapped)
            {
                _radioState = mapped;
                ScheduleRender();
            }
        }
        catch { /* 保持上次状态 */ }
        finally { _radioRefreshQueued = false; }
    }

    // ==================== 附近设备数据 ====================

    /// <summary>移除超过 15 秒未再收到广告的设备（已离开范围）。</summary>
    private void CleanupStaleDevices()
    {
        var cutoff = DateTime.Now.AddSeconds(-15);
        int removed = _nearby.RemoveAll(d => d.LastSeen < cutoff);
        if (removed > 0)
        {
            // 同步清理去重集合
            var currentNames = new HashSet<string>(_nearby.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
            _nearbyNames.IntersectWith(currentNames);
            var currentAddresses = new HashSet<ulong>(_nearby.Select(d => d.Address));
            _bleAddresses.IntersectWith(currentAddresses);
            ScheduleRender();
        }
    }

    /// <summary>
    /// BLE 广告接收：只有带名称的设备才加入列表，渐进式显示。
    /// 不再在广告回调里做 WinRT 设备/图标查询（高频广告 × 阻塞式查询会造成资源风暴与卡顿），
    /// 列表维护统一走 UI 线程的 AddNearbyBle（去重 + LastSeen 更新）。
    /// </summary>
    private void OnBleAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        // 无名称的设备直接跳过（不显示 MAC 地址，减少重复和过时）
        var rawName = args.Advertisement.LocalName;
        if (string.IsNullOrWhiteSpace(rawName)) return;

        var address = args.BluetoothAddress;
        var name = rawName.Trim();
        var now = DateTime.Now;

        // 列表变更统一在 UI 线程执行，避免与 RenderDeviceList 并发读写集合
        _ = Dispatcher.BeginInvoke(() => AddNearbyBle(address, name, now));
    }

    /// <summary>在 UI 线程维护 BLE 附近设备列表（新增或仅刷新 LastSeen）。</summary>
    private void AddNearbyBle(ulong address, string name, DateTime now)
    {
        // 已有设备：只更新 LastSeen，不重复添加
        int existingIdx = _nearby.FindIndex(d => d.Address == address);
        if (existingIdx >= 0)
        {
            _nearby[existingIdx] = _nearby[existingIdx] with { LastSeen = now };
            return;
        }

        // 新设备：地址去重 + 名称去重
        if (!_bleAddresses.Add(address)) return;
        if (_knownNames.Contains(name)) return;
        if (!_nearbyNames.Add(name)) return;

        var item = new BluetoothDeviceItem(
            Address: address,
            Name: name,
            DeviceIconGlyph: "\uE85D",
            DeviceTypeName: "蓝牙设备",
            IsConnected: false,
            IsPaired: false)
        { LastSeen = now };
        _nearby.Add(item);
        ScheduleRender();
    }

    /// <summary>查找 BLE 附近设备条目（图标加载用；找不到时返回 null）。</summary>
    private BluetoothDeviceItem? FindBleItem(ulong address, string name)
    {
        int idx = _nearby.FindIndex(d => d.Address == address);
        if (idx >= 0) return _nearby[idx];
        int ni = _nearby.FindIndex(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        return ni >= 0 ? _nearby[ni] : null;
    }

    /// <summary>将 48 位蓝牙地址格式化为 XX:XX:XX:XX:XX:XX 形式。</summary>
    private static string FormatBleAddress(ulong address)
    {
        var bytes = BitConverter.GetBytes(address);
        // 取低 6 字节（48 位），大端序显示
        return $"{bytes[5]:X2}:{bytes[4]:X2}:{bytes[3]:X2}:{bytes[2]:X2}:{bytes[1]:X2}:{bytes[0]:X2}";
    }

    /// <summary>创建真正的圆形进度环（背景圆环 + 270° 圆弧旋转）。</summary>
    private static FrameworkElement CreateProgressRing()
    {
        const double size = 18;
        const double radius = 7.5;
        const double cx = size / 2;
        const double cy = size / 2;

        var grid = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };

        // 背景圆环
        grid.Children.Add(new Ellipse
        {
            Stroke = ThemeBrushes.Tint("ThemeForeground", 0.2),
            StrokeThickness = 2,
            Width = size - 2,
            Height = size - 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        // 前景圆弧：270°，从顶部顺时针到左侧
        var rotate = new RotateTransform { CenterX = cx, CenterY = cy };
        var arc = new System.Windows.Shapes.Path
        {
            Stroke = ThemeBrushes.AccentTint(0.86),
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            RenderTransform = rotate,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Data = new PathGeometry
            {
                Figures = new PathFigureCollection
                {
                    new PathFigure
                    {
                        StartPoint = new Point(cx, cy - radius), // 顶部
                        Segments = new PathSegmentCollection
                        {
                            new ArcSegment
                            {
                                Point = new Point(cx - radius, cy), // 左侧
                                Size = new Size(radius, radius),
                                RotationAngle = 0,
                                SweepDirection = SweepDirection.Clockwise,
                                IsLargeArc = true // 270° 大弧
                            }
                        }
                    }
                }
            }
        };
        grid.Children.Add(arc);

        // 无限旋转动画
        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromSeconds(1.0),
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
        };
        rotate.BeginAnimation(RotateTransform.AngleProperty, anim);

        return grid;
    }

    /// <summary>创建 12x12 迷你进度环（用于设备行"连接中"状态，黄色）。</summary>
    private static FrameworkElement CreateMiniProgressRing()
    {
        const double size = 14;
        const double radius = 5.5;
        const double cx = size / 2;
        const double cy = size / 2;

        var grid = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };

        grid.Children.Add(new Ellipse
        {
            Stroke = ThemeBrushes.Tint("StatusWarning", 0.2),
            StrokeThickness = 2,
            Width = size - 2,
            Height = size - 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        var rotate = new RotateTransform { CenterX = cx, CenterY = cy };
        var arc = new System.Windows.Shapes.Path
        {
            Stroke = ThemeBrushes.Tint("StatusWarning", 0.9),
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            RenderTransform = rotate,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Data = new PathGeometry
            {
                Figures = new PathFigureCollection
                {
                    new PathFigure
                    {
                        StartPoint = new Point(cx, cy - radius),
                        Segments = new PathSegmentCollection
                        {
                            new ArcSegment
                            {
                                Point = new Point(cx - radius, cy),
                                Size = new Size(radius, radius),
                                RotationAngle = 0,
                                SweepDirection = SweepDirection.Clockwise,
                                IsLargeArc = true
                            }
                        }
                    }
                }
            }
        };
        grid.Children.Add(arc);

        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromSeconds(0.8),
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
        };
        rotate.BeginAnimation(RotateTransform.AngleProperty, anim);

        return grid;
    }

    private void UpdateScanStatus()
    {
        if (_scanStatusText is null) return;
        _scanStatusText.Text = _nearby.Count > 0
            ? $"发现 {_nearby.Count} 个附近设备"
            : "正在搜索附近设备…";
    }

    private void StopScanning()
    {
        _isScanning = false;
        _radioTimer.Stop();
        _renderThrottle.Stop();
        _renderQueued = false;
        try
        {
            _cleanupTimer?.Stop();
            _cleanupTimer = null;
            if (_progressRing is not null)
                _progressRing.Visibility = Visibility.Collapsed;
            if (_deviceWatcher is not null)
            {
                _deviceWatcher.Added -= OnDeviceAdded;
                _deviceWatcher.Updated -= OnDeviceUpdated;
                _deviceWatcher.Removed -= OnDeviceRemoved;
                if (_deviceWatcher.Status == DeviceWatcherStatus.Started)
                    _deviceWatcher.Stop();
                _deviceWatcher = null;
            }
            if (_bleWatcher is not null)
            {
                _bleWatcher.Received -= OnBleAdvertisementReceived;
                if (_bleWatcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
                    _bleWatcher.Stop();
                _bleWatcher = null;
            }
            _bleAddresses.Clear();
        }
        catch { /* ignore */ }
    }

    // ==================== DeviceWatcher 事件 ====================

    /// <summary>发现新设备时立即添加到列表并刷新 UI（渐进式显示）。列表变更统一在 UI 线程执行。</summary>
    private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation device)
    {
        // 跳过已配对设备（通过 IsPaired 属性）
        if (device.Properties.TryGetValue("System.Devices.Aep.IsPaired", out var paired) && paired is true) return;
        // 无名称设备跳过（不显示地址/ID，减少重复和过时）
        if (string.IsNullOrWhiteSpace(device.Name)) return;

        var name = device.Name.Trim();
        _ = Dispatcher.BeginInvoke(() => AddNearbyClassic(device, name));
    }

    private void AddNearbyClassic(DeviceInformation device, string name)
    {
        if (_knownNames.Contains(name)) return;
        if (!_nearbyNames.Add(name)) return;

        var item = DeviceInfoToItem(device);
        // 经典蓝牙设备由 DeviceWatcher 发现，无持续广告更新，设置为不过时
        item = item with { LastSeen = DateTime.MaxValue };
        _nearby.Add(item);
        LoadDeviceIconAsync(device, item);
        ScheduleRender();
    }

    /// <summary>
    /// 更新已有设备的连接/配对状态。核心：DeviceWatcher.Updated 事件驱动状态刷新，
    /// 同时驱动 _known / _nearby 两列表，并唤醒等待中的 WaitForStateAsync（即时响应）。
    /// 列表变更统一在 UI 线程执行。
    /// </summary>
    private void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        _ = Dispatcher.BeginInvoke(() => ApplyDeviceUpdate(update));
    }

    private void ApplyDeviceUpdate(DeviceInformationUpdate update)
    {
        ulong address = ExtractAddress(update.Id);
        if (address == 0) return;

        var item = FindDevice(address);
        if (item is null) return;

        bool changed = false;
        if (update.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var connected) && connected is bool isConnected)
        {
            if (item.IsConnected != isConnected)
            {
                item = item with { IsConnected = isConnected };
                changed = true;
                NotifyDeviceState(address, isConnected);
            }
        }
        if (update.Properties.TryGetValue("System.Devices.Aep.IsPaired", out var pairedObj) && pairedObj is bool isPaired)
        {
            if (item.IsPaired != isPaired)
            {
                item = item with { IsPaired = isPaired };
                changed = true;
            }
        }
        if (changed)
        {
            UpdateDeviceItem(address, item);
            ScheduleRender();
        }
    }

    private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        ulong address = ExtractAddress(update.Id);
        _ = Dispatcher.BeginInvoke(() =>
        {
            int removed = _nearby.RemoveAll(d => d.Address == address);
            if (removed > 0)
            {
                _nearbyNames.RemoveWhere(n => _nearby.All(d => d.Name != n));
                ScheduleRender();
            }
        });
    }

    /// <summary>从 DeviceInformation 提取蓝牙设备信息（名称、地址、CoD、连接状态）。</summary>
    private static BluetoothDeviceItem DeviceInfoToItem(DeviceInformation device)
    {
        ulong address = 0;
        if (device.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var addrObj) && addrObj is string addrStr)
        {
            address = ParseBluetoothAddress(addrStr);
        }
        if (address == 0)
        {
            // 从 DeviceId 中解析远程设备 MAC（格式：Bluetooth#BluetoothXX:XX:...-YY:YY:...）
            address = ExtractAddress(device.Id);
        }

        ulong classOfDevice = 0;
        if (device.Properties.TryGetValue("System.Devices.Aep.Bluetooth.ClassOfDevice", out var codObj) && codObj is uint codUint)
        {
            classOfDevice = codUint;
        }

        bool isConnected = false;
        if (device.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var connObj))
        {
            isConnected = connObj is true;
        }

        var (glyph, typeName) = BluetoothEnumerator.ClassifyIconPublic(classOfDevice, device.Name);
        return new BluetoothDeviceItem(address, device.Name, glyph, typeName, isConnected, IsPaired: false);
    }

    /// <summary>从 DeviceId（Bluetooth#BluetoothXX:XX:...-YY:YY:...）解析末尾 MAC 为 48 位地址。</summary>
    private static ulong ExtractAddress(string deviceId)
    {
        try
        {
            var parts = deviceId.Split('-');
            if (parts.Length >= 2)
            {
                var mac = ParseBluetoothAddress(parts[^1]);
                if (mac != 0) return mac;
            }
        }
        catch { /* ignore */ }
        return 0;
    }

    /// <summary>将 "XX:XX:XX:XX:XX:XX" 格式的 MAC 地址解析为 48 位 ulong。</summary>
    private static ulong ParseBluetoothAddress(string mac)
    {
        try
        {
            var clean = mac.Replace(":", "").Replace("-", "").Trim();
            if (clean.Length == 12 && ulong.TryParse(clean, System.Globalization.NumberStyles.HexNumber, null, out var result))
                return result;
        }
        catch { /* ignore */ }
        return 0;
    }

    /// <summary>从 DeviceInformation 异步加载系统自带的设备图标（GetGlyphThumbnailAsync），加载完成后刷新 UI。</summary>
    private async void LoadDeviceIconAsync(DeviceInformation device, BluetoothDeviceItem? item)
    {
        if (item is null) return;
        try
        {
            using var thumbnail = await device.GetGlyphThumbnailAsync();
            if (thumbnail is null) return;
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.StreamSource = thumbnail.AsStream();
            bitmap.EndInit();
            bitmap.Freeze();
            item.IconSource = bitmap;
            ScheduleRender();
        }
        catch
        {
            // 图标加载失败静默，保持 glyph 降级显示
        }
    }

    /// <summary>为所有已配对设备异步加载系统图标（通过 BluetoothDevice.FromBluetoothAddressAsync）。</summary>
    private async void LoadPairedDeviceIconsAsync()
    {
        foreach (var device in _known.ToArray())
        {
            if (device.Address == 0 || device.IconSource is not null) continue;
            try
            {
                var btDevice = await BluetoothDevice.FromBluetoothAddressAsync(device.Address);
                if (btDevice is null) continue;
                var di = await DeviceInformation.CreateFromIdAsync(btDevice.DeviceId);
                if (di is not null)
                    LoadDeviceIconAsync(di, device);
            }
            catch { /* ignore */ }
        }
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

        // ========== Toggle + 偏好设置 ==========
        column.Children.Add(CreateToggleRow("蓝牙", _radioState != BluetoothRadioState.Off, on => _ = SetBluetoothAsync(on)));
        column.Children.Add(CreatePrefLinkRow("蓝牙偏好设置", "ms-settings:bluetooth"));
        column.Children.Add(CreateSeparator());

        // ========== 已配对设备标题 ==========
        var pairedHeader = new TextBlock
        {
            Text = "已配对设备",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(14, 4, 0, 4)
        };
        // 分组标题：次要前景走主题令牌
        SetThemeBinding(pairedHeader, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        column.Children.Add(pairedHeader);

        SeedKnownDevices();
        _deviceHost.MinHeight = 0;
        column.Children.Add(_deviceHost);

        // ========== 附近设备标题 + 转圈动画 ==========
        var nearbyHeader = new Grid
        {
            Margin = new Thickness(14, 8, 14, 2)
        };
        nearbyHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nearbyHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var nearbyTitle = new TextBlock
        {
            Text = "附近设备",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        // 分组标题：次要前景走主题令牌
        SetThemeBinding(nearbyTitle, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        nearbyHeader.Children.Add(nearbyTitle);
        // 转圈动画：真正的圆形进度环（背景圆环 + 旋转圆弧）
        _progressRing = CreateProgressRing();
        Grid.SetColumn(_progressRing, 1);
        nearbyHeader.Children.Add(_progressRing);
        column.Children.Add(nearbyHeader);
        _scanStatusText = new TextBlock
        {
            Text = "正在搜索附近设备…",
            FontSize = 10,
            Margin = new Thickness(14, 0, 0, 4)
        };
        // 扫描状态提示：次要前景走主题令牌
        SetThemeBinding(_scanStatusText, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        column.Children.Add(_scanStatusText);
        _nearbyHost.MinHeight = 0;
        column.Children.Add(_nearbyHost);

        RenderDeviceList();
        LoadPairedDeviceIconsAsync();
        StartScanning();
        root.Child = column;
        return root;
    }

    private readonly StackPanel _nearbyHost = new();

    /// <summary>打开面板时刷新设备列表（重新枚举系统蓝牙设备，反映最新连接状态）。</summary>
    internal void Refresh()
    {
        _known.Clear();
        _knownNames.Clear();
        SeedKnownDevices();
        RenderDeviceList();
        LoadPairedDeviceIconsAsync();
    }

    /// <summary>打开面板时先载入系统已配对/已连接设备（快速枚举，不发起查询）。</summary>
    private void SeedKnownDevices()
    {
        foreach (var d in BluetoothEnumerator.Enumerate())
        {
            if (_knownNames.Add(d.Name)) _known.Add(d);
        }
    }

    /// <summary>依据蓝牙开关状态与已收集设备，重建设备列表区（使用缓存的无线电状态，UI 线程不 P/Invoke）。</summary>
    private void RenderDeviceList()
    {
        _deviceHost.Children.Clear();
        _nearbyHost.Children.Clear();
        var radio = _radioState;
        if (radio == BluetoothRadioState.NoAdapter)
        {
            _deviceHost.Children.Add(EmptyText("系统无蓝牙适配器或蓝牙服务未启动"));
            return;
        }
        if (radio == BluetoothRadioState.Off)
        {
            _deviceHost.Children.Add(EmptyText("蓝牙已关闭，启用后可在此看到设备"));
            return;
        }
        if (_known.Count == 0)
        {
            _deviceHost.Children.Add(EmptyText("未检测到已配对设备"));
        }
        foreach (var d in _known)
        {
            _deviceHost.Children.Add(CreateDeviceRow(d, isNearby: false));
        }
        foreach (var d in _nearby)
        {
            _nearbyHost.Children.Add(CreateDeviceRow(d, isNearby: true));
        }
    }

    private static TextBlock EmptyText(string message)
    {
        var tb = new TextBlock
        {
            Text = message,
            FontSize = 11,
            Margin = new Thickness(14, 6, 14, 8),
            TextWrapping = TextWrapping.Wrap
        };
        // 空态提示：次要前景走主题令牌
        SetThemeBinding(tb, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        return tb;
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

    private async Task SetBluetoothAsync(bool on)
    {
        var ok = await RadioInterop.SetStateAsync(RadioKind.Bluetooth, on);
        if (ok)
        {
            _radioState = on ? BluetoothRadioState.On : BluetoothRadioState.Off;
            await Dispatcher.InvokeAsync(RenderDeviceList);
        }
    }

    private static FrameworkElement CreateToggleRow(string label, bool isOn, Action<bool> onChanged)
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
            IsOn = isOn,
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
        var row = new Grid
        {
            Height = 36,
            Margin = new Thickness(8, 0, 8, 2)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        // 操作/链接入口：强调色走主题令牌
        SetThemeBinding(text, TextBlock.ForegroundProperty, "AccentBrush");
        text.MouseLeftButtonUp += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(settingsUri) { UseShellExecute = true });
            }
            catch { /* ignore */ }
        };
        Grid.SetColumn(text, 0);
        row.Children.Add(text);
        return row;
    }

    private FrameworkElement CreateDeviceRow(BluetoothDeviceItem device, bool isNearby)
    {
        bool isPending = _pendingAddresses.Contains(device.Address);
        var row = new Grid
        {
            Height = 48,
            Margin = new Thickness(8, 0, 8, 0),
            Background = device.IsConnected
                ? ThemeBrushes.AccentTint(0.16)
                : Brushes.Transparent
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 左图标块：优先使用系统自带设备图标，降级为 Segoe MDL2 glyph
        FrameworkElement iconChild;
        if (device.IconSource is not null)
        {
            iconChild = new Image
            {
                Width = 24,
                Height = 24,
                Source = device.IconSource,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode((Image)iconChild, BitmapScalingMode.HighQuality);
        }
        else
        {
            iconChild = new TextBlock
            {
                Text = device.DeviceIconGlyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }
        var icon = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(6),
            Child = iconChild,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        // 图标块：已连接=强调色背景，未连接=内容层背景，均走主题令牌
        SetThemeBinding(icon, Border.BackgroundProperty, device.IsConnected ? "AccentBrush" : "ThemeContentBackground");
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        // 设备名 + 设备类型 + 状态文字
        var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var name = new TextBlock
        {
            Text = device.Name,
            FontSize = 12,
            FontWeight = device.IsConnected ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        // 已连接设备名：强调色走主题令牌；其余继承主题前景
        SetThemeBinding(name, TextBlock.ForegroundProperty, device.IsConnected ? "AccentBrush" : "ThemeForeground");
        nameStack.Children.Add(name);

        // 设备类型名 + 状态
        var subText = device.DeviceTypeName;
        if (isPending)
        {
            if (isNearby || !device.IsPaired) subText = "配对中…";
            else if (device.IsConnected) subText = "断开中…";
            else subText = "连接中…";
        }
        else if (device.IsConnected) subText = "已连接";
        else if (isNearby) subText = device.DeviceTypeName + " · 点击配对";
        var sub = new TextBlock
        {
            Text = subText,
            Foreground = isPending
                ? ThemeBrushes.Tint("StatusWarning", 0.78)
                : device.IsConnected
                    ? ThemeBrushes.Tint("SkinAccentFromSkin", 0.78)
                    : ThemeBrushes.Tint("ThemeForeground", 0.55),
            FontSize = 10,
            Margin = new Thickness(0, 1, 0, 0)
        };
        nameStack.Children.Add(sub);
        Grid.SetColumn(nameStack, 1);
        row.Children.Add(nameStack);

        // 右侧状态指示：进行中=小转圈，已连接=实心蓝点，未连接=空心圈
        FrameworkElement statusIndicator;
        if (isPending)
        {
            statusIndicator = CreateMiniProgressRing();
        }
        else
        {
            statusIndicator = new Ellipse
            {
                Width = 12,
                Height = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Stroke = device.IsConnected
                    ? ThemeBrushes.Get("SkinAccentFromSkin")
                    : ThemeBrushes.Tint("ThemeForeground", 0.47),
                StrokeThickness = 2,
                Fill = device.IsConnected
                    ? ThemeBrushes.Get("SkinAccentFromSkin")
                    : Brushes.Transparent
            };
        }
        statusIndicator.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(statusIndicator, 2);
        row.Children.Add(statusIndicator);

        // 所有设备可点击：附近未配对→配对，已配对未连接→连接，已连接→断开
        row.Cursor = isPending ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Hand;
        row.MouseLeftButtonUp += async (_, _) => await ToggleDeviceAsync(device, isNearby);
        return row;
    }

    // ==================== 连接 / 断开 / 配对 ====================

    /// <summary>统一处理设备点击：未配对→配对，已配对未连接→连接，已连接→断开。</summary>
    private async Task ToggleDeviceAsync(BluetoothDeviceItem device, bool isNearby)
    {
        if (_pendingAddresses.Contains(device.Address)) return;
        if (device.Address == 0) return;

        _pendingAddresses.Add(device.Address);
        RenderDeviceList(); // 立即显示"配对中/连接中/断开中"

        try
        {
            if (isNearby || !device.IsPaired)
            {
                // 未配对设备：发起配对请求。
                // 配对对话框在 UI 线程弹出（模态自带消息泵），父窗口挂真实句柄，避免后台线程弹窗挂起。
                bool ok = BluetoothEnumerator.PairDevice(device.Address, device.Name, GetOwnerHandle());
                if (ok)
                {
                    await WaitForStateAsync(device.Address, expectConnected: true, maxWaitSeconds: 20);
                    await RefreshKnownDevicesAsync();
                    _nearby.RemoveAll(d => d.Address == device.Address || string.Equals(d.Name, device.Name, StringComparison.OrdinalIgnoreCase));
                    _nearbyNames.RemoveWhere(n => string.Equals(n, device.Name, StringComparison.OrdinalIgnoreCase));
                }
            }
            else if (device.IsConnected)
            {
                // 已连接：断开
                bool ok = await RunWithTimeout(() => BluetoothEnumerator.DisconnectDevice(device), 8);
                if (ok)
                {
                    await WaitForStateAsync(device.Address, expectConnected: false, maxWaitSeconds: 10);
                }
            }
            else
            {
                // 已配对未连接：连接
                bool ok = await RunWithTimeout(() => BluetoothEnumerator.ConnectDevice(device), 10);
                // 无论申请是否返回成功，都以实际枚举状态为准轮询确认：
                // 部分设备枚举已安装服务为空但连接仍能建立，以真实状态为准，避免"连上了却不显示"。
                await WaitForStateAsync(device.Address, expectConnected: true, maxWaitSeconds: ok ? 12 : 6);
            }
        }
        catch
        {
            // 配对/连接/断开失败不在此弹窗打断：finally 的全量枚举刷新会让 UI 回到真实状态。
        }
        finally
        {
            _pendingAddresses.Remove(device.Address);
            await RefreshKnownDevicesAsync(); // 全量刷新，保留已有图标
            RenderDeviceList();
        }
    }

    /// <summary>解析本弹窗可用的父窗口句柄（弹窗未显示时回退到进程主窗口）。</summary>
    private IntPtr GetOwnerHandle()
    {
        try
        {
            var window = Window.GetWindow(this);
            if (window is not null)
            {
                var h = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                if (h != IntPtr.Zero) return h;
            }
        }
        catch { /* fall through */ }
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            if (process.MainWindowHandle != IntPtr.Zero) return process.MainWindowHandle;
        }
        catch { /* fall through */ }
        return IntPtr.Zero;
    }

    /// <summary>在后台线程执行操作并带超时，避免 P/Invoke 无限阻塞。</summary>
    private static async Task<bool> RunWithTimeout(Func<bool> action, int timeoutSeconds)
    {
        var task = Task.Run(action);
        var winner = await Task.WhenAny(task, Task.Delay(timeoutSeconds * 1000));
        return winner == task && task.Result;
    }

    /// <summary>在后台线程重新枚举已配对设备并更新 _known 列表（保留已有设备的系统图标）。</summary>
    private async Task RefreshKnownDevicesAsync()
    {
        var devices = await Task.Run(() => BluetoothEnumerator.Enumerate());
        // 保存已有设备的图标，避免全量替换后图标丢失
        var iconCache = new Dictionary<ulong, System.Windows.Media.ImageSource>();
        foreach (var old in _known)
        {
            if (old.IconSource is not null && old.Address != 0)
                iconCache[old.Address] = old.IconSource;
        }
        _known.Clear();
        _knownNames.Clear();
        foreach (var d in devices)
        {
            if (_knownNames.Add(d.Name))
            {
                if (d.Address != 0 && iconCache.TryGetValue(d.Address, out var cachedIcon))
                    d.IconSource = cachedIcon;
                _known.Add(d);
            }
        }
    }

    // ==================== 状态等待（事件驱动 + WinRT 兜底轮询） ====================

    /// <summary>
    /// 等待设备达到期望连接状态。两条路径谁先到算谁：
    ///   - 事件驱动：DeviceWatcher.Updated 报出 System.Devices.Aep.IsConnected 变化 → NotifyDeviceState 完成 TCS（即时）；
    ///   - 兜底轮询：每 1.2 秒用 WinRT BluetoothLEDevice/BluetoothDevice.ConnectionStatus 查询（BLE/经典都准），
    ///     不依赖经典枚举的 fConnected，避免"连接成功但状态永不更新"。
    /// </summary>
    /// <summary>
    /// 等待设备达到期望连接状态。两条路径并行，谁先达成算谁：
    ///   - 事件驱动：DeviceWatcher.Updated 报出连接状态变化 → NotifyDeviceState 完成 TCS（即时，可用时）；
    ///   - 兜底轮询：每 1 秒用经典 bthprops 枚举读取真实连接状态（对已配对经典设备最可靠，
    ///     不依赖可能不触发的 DeviceWatcher.Updated），次选 WinRT ConnectionStatus。
    /// 轮询全程在后台线程执行，UI 线程只做数据应用与渲染，绝不同步阻塞。
    /// </summary>
    private async Task WaitForStateAsync(ulong address, bool expectConnected, int maxWaitSeconds)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stateWaiters) _stateWaiters[address] = (tcs, expectConnected);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
            while (DateTime.UtcNow < deadline)
            {
                var winner = await Task.WhenAny(tcs.Task, Task.Delay(1000));
                if (winner == tcs.Task)
                {
                    UpdateDeviceConnectedState(address, expectConnected);
                    ScheduleRender();
                    break; // 事件驱动已达成，立即返回
                }

                bool? connected = await PollConnectionStateAsync(address);
                if (connected.HasValue)
                {
                    UpdateDeviceConnectedState(address, connected.Value);
                    ScheduleRender();
                    if (connected.Value == expectConnected)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            lock (_stateWaiters) _stateWaiters.Remove(address);
        }
    }

    /// <summary>
    /// 轮询设备真实连接状态（后台执行）：经典 bthprops 枚举优先（对已配对/已记住设备最快最准，
    /// 无 SDP 主动查询），找不到时回退 WinRT。任一来源都加超时，避免原生调用卡死拖住流程。
    /// </summary>
    private static async Task<bool?> PollConnectionStateAsync(ulong address)
    {
        // 1) 经典 bthprops 枚举：fConnected 直接反映连接状态（无 Inquiry）。
        try
        {
            var enumTask = Task.Run(() => BluetoothEnumerator.Enumerate());
            var done = await Task.WhenAny(enumTask, Task.Delay(3000));
            if (done == enumTask)
            {
                foreach (var d in enumTask.Result)
                {
                    if (d.Address == address) return d.IsConnected;
                }
            }
        }
        catch { /* 继续尝试 WinRT */ }
        // 2) WinRT：经典设备优先，其次 BLE。
        try
        {
            var cl = await BluetoothDevice.FromBluetoothAddressAsync(address);
            if (cl is not null)
                return cl.ConnectionStatus == BluetoothConnectionStatus.Connected;
        }
        catch { /* try BLE */ }
        try
        {
            var ble = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (ble is not null)
                return ble.ConnectionStatus == BluetoothConnectionStatus.Connected;
        }
        catch { /* ignore */ }
        return null; // 设备未找到
    }

    /// <summary>设备连接状态变化事件驱动回调：完成对应地址的等待任务（任意线程安全）。</summary>
    private void NotifyDeviceState(ulong address, bool isConnected)
    {
        (TaskCompletionSource<bool> tcs, bool expect)? entry;
        lock (_stateWaiters)
        {
            if (_stateWaiters.TryGetValue(address, out var e)) entry = e;
            else entry = null;
        }
        if (entry is { } waiter && waiter.tcs.Task.IsCompleted == false && waiter.expect == isConnected)
        {
            waiter.tcs.TrySetResult(true);
        }
    }

    /// <summary>在 _known / _nearby 中查找指定地址设备。</summary>
    private BluetoothDeviceItem? FindDevice(ulong address)
    {
        int ki = _known.FindIndex(x => x.Address == address);
        if (ki >= 0) return _known[ki];
        int ni = _nearby.FindIndex(x => x.Address == address);
        return ni >= 0 ? _nearby[ni] : null;
    }

    /// <summary>更新 _known 和 _nearby 中指定地址设备的记录。</summary>
    private void UpdateDeviceItem(ulong address, BluetoothDeviceItem item)
    {
        int ki = _known.FindIndex(x => x.Address == address);
        if (ki >= 0) _known[ki] = item;
        int ni = _nearby.FindIndex(x => x.Address == address);
        if (ni >= 0) _nearby[ni] = item;
    }

    /// <summary>更新 _known 和 _nearby 中指定地址设备的连接状态（兼容旧调用）。</summary>
    private void UpdateDeviceConnectedState(ulong address, bool isConnected)
    {
        var item = FindDevice(address);
        if (item is not null)
            UpdateDeviceItem(address, item with { IsConnected = isConnected });
    }

    // ------------------- Helpers -------------------

    private static void WriteRegistryBool(string valueName, bool enabled)
    {
        // 蓝牙面板写入开关需要管理员；此处保留钩子，真实写入建议走 C++ 封装 + 提升。
        _ = valueName;
        _ = enabled;
    }
}
