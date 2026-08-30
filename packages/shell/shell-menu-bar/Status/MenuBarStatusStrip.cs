using BetterDesktop.Shell.Status.Contracts;
using BetterDesktop.Shell.Status.Native;
using BetterDesktop.Shell.MenuBar.Contracts;
using BetterDesktop.Shell.Settings.Contracts;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows;
using System;

namespace BetterDesktop.Shell.MenuBar.Status;

internal sealed class BatteryIcon : ContentControl, IDisposable
{
	private readonly IBatteryMonitor? _bat;

	private readonly Rectangle _fill;

	private readonly Path _bolt;

	private readonly TextBlock _percentText;

	private bool _disposed;

	public BatteryIcon(IBatteryMonitor? bat)
	{
		_bat = bat;
		base.Width = 54.0;
		base.Height = 13.0;
		Canvas canvas = new Canvas
		{
			Width = 58.0,
			Height = 16.0
		};
		Rectangle element = new Rectangle
		{
			Width = 22.0,
			Height = 10.0,
			RadiusX = 2.5,
			RadiusY = 2.5,
			Stroke = Brushes.White,
			StrokeThickness = 1.2,
			Fill = Brushes.Transparent
		};
		Canvas.SetLeft(element, 0.0);
		Canvas.SetTop(element, 2.0);
		canvas.Children.Add(element);
		Rectangle element2 = new Rectangle
		{
			Width = 2.0,
			Height = 4.0,
			Fill = Brushes.White,
			RadiusX = 1.0,
			RadiusY = 1.0
		};
		Canvas.SetLeft(element2, 22.0);
		Canvas.SetTop(element2, 5.0);
		canvas.Children.Add(element2);
		_fill = new Rectangle
		{
			Width = 18.0,
			Height = 6.0,
			RadiusX = 1.5,
			RadiusY = 1.5,
			Fill = Brushes.White
		};
		Canvas.SetLeft(_fill, 2.0);
		Canvas.SetTop(_fill, 4.0);
		canvas.Children.Add(_fill);
		_bolt = new Path
		{
			Data = Geometry.Parse("M12 2L7 8h3l-1.5 4 5-6H10z"),
			Fill = new SolidColorBrush(Color.FromRgb(byte.MaxValue, 215, 0)),
			Stretch = Stretch.Uniform,
			Width = 7.0,
			Height = 10.0,
			Visibility = Visibility.Collapsed
		};
		Canvas.SetLeft(_bolt, 7.5);
		Canvas.SetTop(_bolt, 2.0);
		canvas.Children.Add(_bolt);
		_percentText = new TextBlock
		{
			Foreground = Brushes.White,
			FontSize = 10.0,
			FontWeight = FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center,
			Visibility = Visibility.Visible
		};
		Canvas.SetLeft(_percentText, 28.0);
		Canvas.SetTop(_percentText, 2.0);
		canvas.Children.Add(_percentText);
		Canvas child = IconCropper.Crop(canvas, 0.0, 1.0, 58.0, 14.0);
		base.Content = new Viewbox
		{
			Child = child,
			Stretch = Stretch.Uniform,
			Width = 54.0,
			Height = 13.0
		};
		if (_bat != null)
		{
			_bat.Changed += OnBatteryChanged;
			try
			{
				UpdateBattery(_bat.GetSnapshot());
				return;
			}
			catch
			{
				return;
			}
		}
		_percentText.Text = "--%";
	}

	private void OnBatteryChanged(object? sender, StatusSnapshot e)
	{
		if (_disposed)
		{
			return;
		}
		UiDispatch.Run((DispatcherObject)(object)this, delegate
		{
			if (!_disposed)
			{
				UpdateBattery(e);
			}
		});
	}

	private void UpdateBattery(StatusSnapshot snap)
	{
		double num = ((snap.Progress >= 0.0) ? (snap.Progress / 100.0) : 0.8);
		string text = snap.HumanText + " " + snap.ShortText;
		bool flag = text.Contains("充电", StringComparison.OrdinalIgnoreCase) || text.Contains("电源", StringComparison.OrdinalIgnoreCase) || text.Contains("Charging", StringComparison.OrdinalIgnoreCase) || text.Contains("Plugged", StringComparison.OrdinalIgnoreCase) || text.Contains("AC", StringComparison.OrdinalIgnoreCase);
		_fill.Width = Math.Max(2.0, 18.0 * Math.Clamp(num, 0.0, 1.0));
		_percentText.Visibility = Visibility.Visible;
		_percentText.Text = $"{Math.Round(num * 100.0)}%";
		if (flag)
		{
			_fill.Fill = new SolidColorBrush(Color.FromRgb(76, 230, 154));
			_bolt.Visibility = Visibility.Visible;
			_percentText.Foreground = new SolidColorBrush(Color.FromRgb(76, 230, 154));
			return;
		}
		_fill.Fill = Brushes.White;
		_bolt.Visibility = Visibility.Collapsed;
		_percentText.Foreground = Brushes.White;
		if (num < 0.2)
		{
			_fill.Fill = new SolidColorBrush(Color.FromRgb(byte.MaxValue, 106, 106));
		}
	}

	public void Dispose()
	{
		_disposed = true;
		if (_bat != null)
		{
			_bat.Changed -= OnBatteryChanged;
		}
	}
}

internal sealed class BluetoothIcon : ContentControl
{
	public BluetoothIcon()
	{
		base.Width = 9.0;
		base.Height = 13.0;
		Canvas canvas = new Canvas
		{
			Width = 18.0,
			Height = 16.0
		};
		Path element = new Path
		{
			Data = Geometry.Parse("M17.71 7.71L12 2h-1v7.59L6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 11 14.41V22h1l5.71-5.71-4.3-4.29 4.3-4.29zM13 5.83l1.88 1.88L13 9.59V5.83zm1.88 10.46L13 18.17v-3.76l1.88 1.88z"),
			Fill = Brushes.White,
			Stretch = Stretch.Uniform,
			Width = 10.6,
			Height = 14.2
		};
		Canvas.SetLeft(element, 3.7);
		Canvas.SetTop(element, 0.9);
		canvas.Children.Add(element);
		Canvas child = IconCropper.Crop(canvas, 4.0, 0.0, 10.5, 16.0);
		base.Content = new Viewbox
		{
			Child = child,
			Stretch = Stretch.Uniform,
			Width = 9.0,
			Height = 13.0
		};
	}
}

internal sealed class BrightnessIcon : ContentControl, IDisposable
{
	private readonly IBrightnessMonitor? _brightness;

	private readonly Ellipse _sun;

	private readonly Line[] _rays = new Line[8];

	private bool _disposed;

	public BrightnessIcon(IBrightnessMonitor? brightness)
	{
		_brightness = brightness;
		base.Width = 13.0;
		base.Height = 13.0;
		Canvas canvas = new Canvas
		{
			Width = 22.0,
			Height = 16.0
		};
		_sun = new Ellipse
		{
			Width = 6.0,
			Height = 6.0,
			Fill = Brushes.White
		};
		Canvas.SetLeft(_sun, 8.0);
		Canvas.SetTop(_sun, 5.0);
		canvas.Children.Add(_sun);
		double[] array = new double[8] { 0.0, 45.0, 90.0, 135.0, 180.0, 225.0, 270.0, 315.0 };
		for (int i = 0; i < 8; i++)
		{
			double num = array[i] * Math.PI / 180.0;
			double x = 11.0 + 4.0 * Math.Cos(num);
			double y = 8.0 + 4.0 * Math.Sin(num);
			double x2 = 11.0 + 7.0 * Math.Cos(num);
			double y2 = 8.0 + 7.0 * Math.Sin(num);
			_rays[i] = new Line
			{
				X1 = x,
				Y1 = y,
				X2 = x2,
				Y2 = y2,
				Stroke = Brushes.White,
				StrokeThickness = 1.2,
				StrokeStartLineCap = PenLineCap.Round
			};
			canvas.Children.Add(_rays[i]);
		}
		Canvas child = IconCropper.Crop(canvas, 3.0, 0.0, 16.0, 16.0);
		base.Content = new Viewbox
		{
			Child = child,
			Stretch = Stretch.Uniform,
			Width = 13.0,
			Height = 13.0
		};
		if (_brightness != null)
		{
			_brightness.Changed += OnBrightnessChanged;
			try
			{
				UpdateBrightness(_brightness.GetSnapshot());
			}
			catch
			{
			}
		}
	}

	private void OnBrightnessChanged(object? sender, StatusSnapshot e)
	{
		if (_disposed)
		{
			return;
		}
		UiDispatch.Run((DispatcherObject)(object)this, delegate
		{
			if (!_disposed)
			{
				UpdateBrightness(e);
			}
		});
	}

	private void UpdateBrightness(StatusSnapshot snap)
	{
		double num = ((snap.Progress >= 0.0) ? (snap.Progress / 100.0) : 0.7);
		int num2 = (int)Math.Round(num * 8.0);
		SolidColorBrush solidColorBrush = new SolidColorBrush(Color.FromRgb(96, 96, 96));
		for (int i = 0; i < 8; i++)
		{
			_rays[i].Stroke = ((i < num2) ? Brushes.White : solidColorBrush);
		}
		byte g = (byte)(255.0 - (1.0 - num) * 60.0);
		byte b = (byte)(255.0 - (1.0 - num) * 120.0);
		_sun.Fill = new SolidColorBrush(Color.FromRgb(byte.MaxValue, g, b));
	}

	public void Dispose()
	{
		_disposed = true;
		if (_brightness != null)
		{
			_brightness.Changed -= OnBrightnessChanged;
		}
	}
}

internal sealed class CapsuleSwitch : ContentControl
{
	private readonly Border _capsule;

	private readonly Ellipse _knob;

	public CapsuleSwitch()
	{
		base.Width = 16.0;
		base.Height = 7.0;
		Canvas canvas = new Canvas
		{
			Width = 16.0,
			Height = 7.0
		};
		_capsule = new Border
		{
			Width = 16.0,
			Height = 7.0,
			CornerRadius = new CornerRadius(3.5),
			Background = new SolidColorBrush(Color.FromRgb(144, 144, 144))
		};
		canvas.Children.Add(_capsule);
		_knob = new Ellipse
		{
			Width = 5.0,
			Height = 5.0,
			Fill = Brushes.White
		};
		Canvas.SetLeft(_knob, 1.0);
		Canvas.SetTop(_knob, 1.0);
		canvas.Children.Add(_knob);
		base.Content = canvas;
	}

	public void SetOn(bool on)
	{
		if (on)
		{
			_capsule.Background = new SolidColorBrush(Color.FromRgb(76, 230, 154));
			Canvas.SetLeft(_knob, 10.0);
		}
		else
		{
			_capsule.Background = new SolidColorBrush(Color.FromRgb(144, 144, 144));
			Canvas.SetLeft(_knob, 1.0);
		}
	}
}

internal sealed class CpuIcon : ContentControl, IDisposable
{
	private readonly ICpuMonitor? _cpu;

	private readonly TextBlock _percentText;

	private bool _disposed;

	public CpuIcon(ICpuMonitor? cpu)
	{
		_cpu = cpu;
		base.Width = 24.0;
		base.Height = 13.0;
		_percentText = new TextBlock
		{
			Foreground = Brushes.White,
			FontSize = 11.0,
			FontWeight = FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Center,
			Text = "--%"
		};
		base.Content = _percentText;
		base.ToolTip = "CPU 利用率";
		if (_cpu != null)
		{
			_cpu.Changed += OnCpuChanged;
			try
			{
				UpdateCpu(_cpu.GetSnapshot());
			}
			catch
			{
			}
		}
	}

	private void OnCpuChanged(object? sender, StatusSnapshot e)
	{
		if (_disposed)
		{
			return;
		}
		UiDispatch.Run((DispatcherObject)(object)this, delegate
		{
			if (!_disposed)
			{
				UpdateCpu(e);
			}
		});
	}

	private void UpdateCpu(StatusSnapshot snap)
	{
		int value = ((snap.Progress >= 0.0) ? ((int)Math.Round(snap.Progress)) : 0);
		_percentText.Text = $"{value}%";
		_percentText.Foreground = StatusColor.ForSeverity(snap.Severity);
	}

	public void Dispose()
	{
		_disposed = true;
		if (_cpu != null)
		{
			_cpu.Changed -= OnCpuChanged;
		}
	}
}

internal sealed class FpsIcon : ContentControl, IDisposable
{
	private readonly TextBlock _fpsText;

	private readonly DispatcherTimer _timer;

	private int _frameCount;

	private bool _disposed;

	public FpsIcon()
	{
		//IL_00a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c2: Expected O, but got Unknown
		base.Width = 26.0;
		base.Height = 13.0;
		_fpsText = new TextBlock
		{
			Foreground = Brushes.White,
			FontSize = 11.0,
			FontWeight = FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Center,
			Text = "--"
		};
		base.Content = _fpsText;
		base.ToolTip = "渲染帧率 (FPS)";
		CompositionTarget.Rendering += OnFrame;
		_timer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(1.0)
		};
		_timer.Tick += delegate
		{
			if (!_disposed)
			{
				_fpsText.Text = $"{_frameCount}";
				_frameCount = 0;
			}
		};
		_timer.Start();
	}

	private void OnFrame(object? sender, EventArgs e)
	{
		_frameCount++;
	}

	public void Dispose()
	{
		_disposed = true;
		_timer.Stop();
		CompositionTarget.Rendering -= OnFrame;
	}
}

internal static class IconCropper
{
	public static Canvas Crop(Canvas inner, double x, double y, double w, double h)
	{
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		Canvas canvas = new Canvas
		{
			Width = w,
			Height = h,
			Clip = new RectangleGeometry(new Rect(0.0, 0.0, w, h))
		};
		Canvas.SetLeft(inner, 0.0 - x);
		Canvas.SetTop(inner, 0.0 - y);
		canvas.Children.Add(inner);
		return canvas;
	}
}

internal sealed class ImeIcon : ContentControl, IDisposable
{
	private readonly IImeMonitor? _ime;

	private readonly TextBlock _label;

	private bool _disposed;

	public ImeIcon(IImeMonitor? ime)
	{
		_ime = ime;
		base.Width = 16.0;
		base.Height = 13.0;
		_label = new TextBlock
		{
			Text = "中",
			Foreground = Brushes.White,
			FontSize = 11.0,
			FontWeight = FontWeights.SemiBold,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		base.Content = _label;
		if (_ime != null)
		{
			_ime.Changed += OnImeChanged;
			try
			{
				UpdateIme(_ime.GetSnapshot());
			}
			catch
			{
			}
		}
	}

	private void OnImeChanged(object? sender, StatusSnapshot e)
	{
		if (_disposed)
		{
			return;
		}
		UiDispatch.Run((DispatcherObject)(object)this, delegate
		{
			if (!_disposed)
			{
				UpdateIme(e);
			}
		});
	}

	private void UpdateIme(StatusSnapshot snap)
	{
		string text = (string.IsNullOrEmpty(snap.ShortText) ? "中" : snap.ShortText);
		_label.Text = ((text.Length > 2) ? text.Substring(0, 2) : text);
	}

	public void Dispose()
	{
		_disposed = true;
		if (_ime != null)
		{
			_ime.Changed -= OnImeChanged;
		}
	}
}

internal sealed class MemoryIcon : ContentControl, IDisposable
{
	private readonly IMemoryMonitor? _mem;

	private readonly TextBlock _percentText;

	private bool _disposed;

	public MemoryIcon(IMemoryMonitor? mem)
	{
		_mem = mem;
		base.Width = 26.0;
		base.Height = 13.0;
		_percentText = new TextBlock
		{
			Foreground = Brushes.White,
			FontSize = 11.0,
			FontWeight = FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Center,
			Text = "--%"
		};
		base.Content = _percentText;
		base.ToolTip = "内存利用率";
		if (_mem != null)
		{
			_mem.Changed += OnMemChanged;
			try
			{
				UpdateMem(_mem.GetSnapshot());
			}
			catch
			{
			}
		}
	}

	private void OnMemChanged(object? sender, StatusSnapshot e)
	{
		if (_disposed)
		{
			return;
		}
		UiDispatch.Run((DispatcherObject)(object)this, delegate
		{
			if (!_disposed)
			{
				UpdateMem(e);
			}
		});
	}

	private void UpdateMem(StatusSnapshot snap)
	{
		int value = ((snap.Progress >= 0.0) ? ((int)Math.Round(snap.Progress)) : 0);
		_percentText.Text = $"{value}%";
		_percentText.Foreground = StatusColor.ForSeverity(snap.Severity);
	}

	public void Dispose()
	{
		_disposed = true;
		if (_mem != null)
		{
			_mem.Changed -= OnMemChanged;
		}
	}
}

public sealed class MenuBarStatusButtonClickedEventArgs : EventArgs
{
	public MenuBarStatusButtonId Button { get; }

	public bool IsRightButton { get; }

	public FrameworkElement Source { get; }

	public MenuBarStatusButtonClickedEventArgs(MenuBarStatusButtonId button, bool isRightButton, FrameworkElement source)
	{
		Button = button;
		IsRightButton = isRightButton;
		Source = source;
	}
}

public enum MenuBarStatusButtonId
{
	SystemTray,
	Fps,
	Cpu,
	Memory,
	Wifi,
	NetworkTraffic,
	Brightness,
	Ime,
	Bluetooth,
	Volume,
	Microphone,
	Battery,
	Notification,
	Extensions,
	DateTime,
	Desktop
}

public sealed class MenuBarStatusStrip : StackPanel, IDisposable
{
	private readonly DispatcherTimer _clockTimer;

	private readonly TextBlock _dateTimeText;

	private readonly VolumeIcon _volumeIcon;

	private readonly MicIcon _micIcon;

	private readonly BatteryIcon _batteryIcon;

	private readonly ImeIcon _imeIcon;

	private readonly BrightnessIcon _brightnessIcon;

	private readonly WifiSignalIcon _wifiSignalIcon;

	private readonly NetworkTrafficIcon _networkTrafficIcon;

	private readonly CpuIcon _cpuIcon;

	private readonly MemoryIcon _memoryIcon;

	private readonly FpsIcon _fpsIcon;

	private readonly SystemTrayIcon _systemTrayIcon;

	private bool _disposed;

	/// <summary>按钮注册表：每一项菜单栏按钮对应的 Border，供扩展中心运行时显隐控制。</summary>
	private readonly Dictionary<MenuBarStatusButtonId, Border> _buttons = new();

	public event EventHandler<MenuBarStatusButtonClickedEventArgs>? ButtonClicked;

	public MenuBarStatusStrip(IVolumeMonitor? vol, IMicrophoneMonitor? mic, IBatteryMonitor? bat, IImeMonitor? ime, IBrightnessMonitor? brightness, INetworkMonitor? net, IMemoryMonitor? mem, ICpuMonitor? cpu, ISettingsService? settings = null)
	{
		//IL_041c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0421: Unknown result type (might be due to invalid IL or missing references)
		//IL_043b: Expected O, but got Unknown
		base.Orientation = Orientation.Horizontal;
		base.VerticalAlignment = VerticalAlignment.Center;
		_systemTrayIcon = new SystemTrayIcon();
		Border trayBtn = CreateButton(MenuBarStatusButtonId.SystemTray, "系统托盘", _systemTrayIcon, 18.0);
		// 托盘图标数量是运行时可变量（应用启停都会增删），宽度必须随之伸缩：
		// 固定宽度会在图标多时裁切、图标少时留白。每图标 16px + 折叠箭头约 10px。
		bool trayExpanded = true;
		void SyncTrayWidth()
		{
			trayBtn.Width = trayExpanded
				? Math.Max(18.0, _systemTrayIcon.IconCount * 16.0 + 10.0)
				: 18.0;
		}
		_systemTrayIcon.IconCountChanged += delegate
		{
			SyncTrayWidth();
		};
		_systemTrayIcon.ExpandedChanged += delegate(object? _, bool expanded)
		{
			trayExpanded = expanded;
			SyncTrayWidth();
		};
		SyncTrayWidth();
		base.Children.Add(trayBtn);
		_fpsIcon = new FpsIcon();
		base.Children.Add(CreateButton(MenuBarStatusButtonId.Fps, "帧率", _fpsIcon, 28.0));
		_cpuIcon = new CpuIcon(cpu);
		base.Children.Add(CreateButton(MenuBarStatusButtonId.Cpu, "CPU利用率", _cpuIcon, 26.0));
		_memoryIcon = new MemoryIcon(mem);
		base.Children.Add(CreateButton(MenuBarStatusButtonId.Memory, "内存利用率", _memoryIcon, 28.0));
		_wifiSignalIcon = new WifiSignalIcon(net);
		base.Children.Add(CreateButton(MenuBarStatusButtonId.Wifi, "WiFi信号", _wifiSignalIcon, 18.0));
		_networkTrafficIcon = new NetworkTrafficIcon(net);
		base.Children.Add(CreateButton(MenuBarStatusButtonId.NetworkTraffic, "网络流量", _networkTrafficIcon, 38.0));
		_brightnessIcon = new BrightnessIcon(brightness);
		base.Children.Add(CreateButton(MenuBarStatusButtonId.Brightness, "亮度", _brightnessIcon, 15.0));
		_imeIcon = new ImeIcon(ime);
		base.Children.Add(CreateButton(MenuBarStatusButtonId.Ime, "输入法", _imeIcon, 18.0));
		BluetoothIcon content = new BluetoothIcon();
		base.Children.Add(CreateButton(MenuBarStatusButtonId.Bluetooth, "蓝牙", content, 11.0));
		_volumeIcon = new VolumeIcon(vol);
		base.Children.Add(CreateButton(MenuBarStatusButtonId.Volume, "音量", _volumeIcon, 26.0));
		_micIcon = new MicIcon(mic);
		base.Children.Add(CreateButton(MenuBarStatusButtonId.Microphone, "麦克风", _micIcon, 14.0));
		_batteryIcon = new BatteryIcon(bat);
		base.Children.Add(CreateButton(MenuBarStatusButtonId.Battery, "电池", _batteryIcon, 56.0));
		NotificationToggle notifyToggle = new NotificationToggle();
		Border border = CreateButton(MenuBarStatusButtonId.Notification, "通知中心", notifyToggle, 16.0);
		border.MouseLeftButtonUp += delegate
		{
			notifyToggle.Toggle();
		};
		base.Children.Add(border);
		TextBlock content2 = new TextBlock
		{
			Text = "+",
			Foreground = Brushes.White,
			FontSize = 15.0,
			FontWeight = FontWeights.Light,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Center
		};
		base.Children.Add(CreateButton(MenuBarStatusButtonId.Extensions, "扩展中心", content2, 17.0));
		_dateTimeText = new TextBlock
		{
			Foreground = Brushes.White,
			FontSize = 10.0,
			FontWeight = FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Center
		};
		UpdateDateTime();
		base.Children.Add(CreateButton(MenuBarStatusButtonId.DateTime, "日期时间", _dateTimeText, 80.0));
		Border border2 = CreateButton(MenuBarStatusButtonId.Desktop, "桌面覆盖", CreateDesktopIcon(), 19.0);
		border2.MouseLeftButtonUp += delegate
		{
			ShowDesktop();
		};
		base.Children.Add(border2);
		// 显示精度到分钟（M月d日 HH:mm）。原先固定 30s 轮询会让分钟切换最多延迟 30s，
		// 出现"系统时钟已跳分、菜单栏还是旧值"。改为每次对齐到下一分钟边界重排：
		// 分钟切换零延迟，且唤醒次数比 1s 轮询少 60 倍。
		_clockTimer = new DispatcherTimer();
		_clockTimer.Tick += delegate
		{
			UpdateDateTime();
			ScheduleNextMinuteTick();
		};
		ScheduleNextMinuteTick();
		_clockTimer.Start();

		// 应用扩展中心持久化的组件显隐：映射到菜单栏按钮的条目，按 settings 的 extensions.<id>.enabled 控制显示。
		// 默认全部显示（未设置视为启用）。外部扩展（无菜单栏映射）仅持久化意图，不影响此处。
		foreach (var ext in ExtensionCatalog.All)
		{
			if (ext.MenuBarButton is { } btnId)
			{
				SetComponentVisible(btnId, settings?.Get(ext.SettingsKey, true) ?? true);
			}
		}
	}

	/// <summary>运行时显隐某个菜单栏组件（供扩展中心开关直接控制）。</summary>
	public void SetComponentVisible(MenuBarStatusButtonId id, bool visible)
	{
		if (_buttons.TryGetValue(id, out var border))
		{
			border.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
		}
	}

	/// <summary>读取某个菜单栏组件当前是否可见。</summary>
	public bool IsComponentVisible(MenuBarStatusButtonId id)
	{
		return _buttons.TryGetValue(id, out var border) && border.Visibility == Visibility.Visible;
	}

	/// <summary>把时钟定时器的下一次触发对齐到系统时钟的下一分钟边界。</summary>
	private void ScheduleNextMinuteTick()
	{
		DateTime now = DateTime.Now;
		// 余量 50ms：正好落在边界上时系统时钟可能仍读到上一分钟。
		TimeSpan delay = TimeSpan.FromMilliseconds(
			(60 - now.Second) * 1000 - now.Millisecond + 50);
		if (delay < TimeSpan.FromMilliseconds(200))
		{
			delay = TimeSpan.FromMilliseconds(200);
		}
		_clockTimer.Interval = delay;
	}

	private void UpdateDateTime()
	{
		if (!_disposed)
		{
			_dateTimeText.Text = DateTime.Now.ToString("M月d日 HH:mm", CultureInfo.CurrentUICulture);
		}
	}

	private static void ShowDesktop()
	{
		try
		{
			ShellExecute(IntPtr.Zero, "open", "shell:::{3080F90D-D7AD-11D9-BD98-0000947B0257}", null, null, 0);
		}
		catch
		{
			try
			{
				keybd_event(91, 0, 0u, UIntPtr.Zero);
				keybd_event(68, 0, 0u, UIntPtr.Zero);
				keybd_event(68, 0, 2u, UIntPtr.Zero);
				keybd_event(91, 0, 2u, UIntPtr.Zero);
			}
			catch
			{
			}
		}
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern nint ShellExecute(nint hwnd, string lpOperation, string lpFile, string? lpParameters, string? lpDirectory, int nShowCmd);

	[DllImport("user32.dll")]
	private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

	private Border CreateButton(MenuBarStatusButtonId id, string tooltip, UIElement content, double width)
	{
		if (content is FrameworkElement frameworkElement)
		{
			frameworkElement.VerticalAlignment = VerticalAlignment.Center;
			frameworkElement.HorizontalAlignment = HorizontalAlignment.Center;
		}
		Border btn = new Border
		{
			Width = width,
			Height = 14.0,
			Background = Brushes.Transparent,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Center,
			Child = content,
			ToolTip = tooltip,
			SnapsToDevicePixels = true
		};
		btn.MouseEnter += delegate
		{
			btn.Background = new SolidColorBrush(Color.FromArgb(51, byte.MaxValue, byte.MaxValue, byte.MaxValue));
		};
		btn.MouseLeave += delegate
		{
			btn.Background = Brushes.Transparent;
		};
		btn.MouseLeftButtonDown += delegate
		{
			btn.Background = new SolidColorBrush(Color.FromArgb(85, byte.MaxValue, byte.MaxValue, byte.MaxValue));
		};
		btn.MouseLeftButtonUp += delegate
		{
			btn.Background = new SolidColorBrush(Color.FromArgb(51, byte.MaxValue, byte.MaxValue, byte.MaxValue));
			this.ButtonClicked?.Invoke(this, new MenuBarStatusButtonClickedEventArgs(id, isRightButton: false, btn));
		};
		btn.MouseRightButtonUp += delegate
		{
			this.ButtonClicked?.Invoke(this, new MenuBarStatusButtonClickedEventArgs(id, isRightButton: true, btn));
		};
		_buttons[id] = btn;
		return btn;
	}

	private static UIElement CreateDesktopIcon()
	{
		Canvas canvas = new Canvas
		{
			Width = 18.0,
			Height = 14.0
		};
		Rectangle element = new Rectangle
		{
			Width = 16.0,
			Height = 10.0,
			RadiusX = 1.5,
			RadiusY = 1.5,
			Stroke = Brushes.White,
			StrokeThickness = 1.2
		};
		Canvas.SetLeft(element, 1.0);
		Canvas.SetTop(element, 1.0);
		canvas.Children.Add(element);
		Rectangle element2 = new Rectangle
		{
			Width = 7.0,
			Height = 1.5,
			Fill = Brushes.White,
			RadiusX = 0.5,
			RadiusY = 0.5
		};
		Canvas.SetLeft(element2, 5.5);
		Canvas.SetTop(element2, 12.0);
		canvas.Children.Add(element2);
		return new Viewbox
		{
			Child = canvas,
			Width = 17.0,
			Height = 13.0,
			Stretch = Stretch.Uniform
		};
	}

	public void Dispose()
	{
		_disposed = true;
		_clockTimer.Stop();
		_volumeIcon.Dispose();
		_micIcon.Dispose();
		_batteryIcon.Dispose();
		_imeIcon.Dispose();
		_brightnessIcon.Dispose();
		_wifiSignalIcon.Dispose();
		_networkTrafficIcon.Dispose();
		_cpuIcon.Dispose();
		_memoryIcon.Dispose();
		_fpsIcon.Dispose();
	}
}

internal sealed class MicIcon : ContentControl, IDisposable
{
	private readonly IMicrophoneMonitor? _mic;

	private readonly Path _micPath;

	private readonly Line _muteLine;

	private bool _disposed;

	public MicIcon(IMicrophoneMonitor? mic)
	{
		_mic = mic;
		base.Width = 12.0;
		base.Height = 13.0;
		Canvas canvas = new Canvas
		{
			Width = 18.0,
			Height = 16.0
		};
		_micPath = new Path
		{
			Data = Geometry.Parse("M9 3a2 2 0 0 0-2 2v4a2 2 0 0 0 4 0V5a2 2 0 0 0-2-2zm4 6a4 4 0 0 1-8 0H3a6 6 0 0 0 5 5.91V17h2v-2.09A6 6 0 0 0 15 9h-2z"),
			Fill = Brushes.White,
			Stretch = Stretch.Uniform,
			Width = 14.0,
			Height = 14.0
		};
		Canvas.SetLeft(_micPath, 2.0);
		Canvas.SetTop(_micPath, 1.0);
		canvas.Children.Add(_micPath);
		_muteLine = new Line
		{
			X1 = 2.0,
			Y1 = 14.0,
			X2 = 16.0,
			Y2 = 2.0,
			Stroke = new SolidColorBrush(Color.FromRgb(byte.MaxValue, 106, 106)),
			StrokeThickness = 1.5,
			Visibility = Visibility.Collapsed
		};
		canvas.Children.Add(_muteLine);
		Canvas child = IconCropper.Crop(canvas, 1.5, 0.0, 15.0, 16.0);
		base.Content = new Viewbox
		{
			Child = child,
			Stretch = Stretch.Uniform,
			Width = 12.0,
			Height = 13.0
		};
		if (_mic != null)
		{
			_mic.Changed += OnMicChanged;
			try
			{
				UpdateMic(_mic.GetSnapshot());
			}
			catch
			{
			}
		}
	}

	private void OnMicChanged(object? sender, StatusSnapshot e)
	{
		if (_disposed)
		{
			return;
		}
		UiDispatch.Run((DispatcherObject)(object)this, delegate
		{
			if (!_disposed)
			{
				UpdateMic(e);
			}
		});
	}

	private void UpdateMic(StatusSnapshot snap)
	{
		bool flag = snap.Severity == StatusSeverity.Warning || snap.Severity == StatusSeverity.Critical;
		_muteLine.Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
		_micPath.Fill = (flag ? new SolidColorBrush(Color.FromRgb(128, 128, 128)) : Brushes.White);
	}

	public void Dispose()
	{
		_disposed = true;
		if (_mic != null)
		{
			_mic.Changed -= OnMicChanged;
		}
	}
}

internal sealed class NetworkTrafficIcon : ContentControl, IDisposable
{
	private readonly INetworkMonitor? _net;

	private readonly TextBlock _upText;

	private readonly TextBlock _downText;

	private readonly DispatcherTimer _speedTimer;

	private NetworkPrimaryNative _prev;

	private DateTime _prevTs;

	private bool _disposed;

	public NetworkTrafficIcon(INetworkMonitor? net)
	{
		//IL_02db: Unknown result type (might be due to invalid IL or missing references)
		//IL_02e0: Unknown result type (might be due to invalid IL or missing references)
		//IL_02fa: Expected O, but got Unknown
		_net = net;
		base.Width = 36.0;
		base.Height = 13.0;
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Vertical,
			VerticalAlignment = VerticalAlignment.Center,
			RenderTransform = new TranslateTransform(0.0, -0.5)
		};
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center
		};
		Path element = new Path
		{
			Data = Geometry.Parse("M0 5 L3.5 0 L7 5 Z"),
			Fill = Brushes.White,
			Width = 7.0,
			Height = 6.0,
			Margin = new Thickness(0.0, 0.0, 3.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center
		};
		_upText = new TextBlock
		{
			Foreground = Brushes.White,
			FontSize = 8.0,
			FontWeight = FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center,
			Text = "--"
		};
		stackPanel2.Children.Add(element);
		stackPanel2.Children.Add(_upText);
		stackPanel.Children.Add(stackPanel2);
		StackPanel stackPanel3 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0.0, 1.0, 0.0, 0.0)
		};
		Path element2 = new Path
		{
			Data = Geometry.Parse("M0 1 L3.5 6 L7 1 Z"),
			Fill = Brushes.White,
			Width = 7.0,
			Height = 6.0,
			Margin = new Thickness(0.0, 0.0, 3.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center
		};
		_downText = new TextBlock
		{
			Foreground = Brushes.White,
			FontSize = 8.0,
			FontWeight = FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center,
			Text = "--"
		};
		stackPanel3.Children.Add(element2);
		stackPanel3.Children.Add(_downText);
		stackPanel.Children.Add(stackPanel3);
		base.Content = new Viewbox
		{
			Child = stackPanel,
			Stretch = Stretch.Uniform,
			Width = 36.0,
			Height = 13.0
		};
		_speedTimer = new DispatcherTimer((DispatcherPriority)4)
		{
			Interval = TimeSpan.FromSeconds(1.0)
		};
		_speedTimer.Tick += OnSpeedTick;
		_speedTimer.Start();
		if (_net != null)
		{
			_net.Changed += OnNetChanged;
			try
			{
				UpdateConnection(_net.GetSnapshot());
			}
			catch
			{
			}
		}
	}

	private void OnSpeedTick(object? sender, EventArgs e)
	{
		if (_disposed)
		{
			return;
		}
		if (!NetworkCoreNative.IsAvailable)
		{
			_upText.Text = "--";
			_downText.Text = "--";
			return;
		}
		NetworkPrimaryNative prev = NetworkCoreNative.ReadPrimary();
		if (!prev.Ok)
		{
			_upText.Text = "--";
			_downText.Text = "--";
			_prev = default(NetworkPrimaryNative);
			_prevTs = default(DateTime);
			return;
		}
		DateTime utcNow = DateTime.UtcNow;
		if (_prevTs != default(DateTime) && _prev.IfIndex == prev.IfIndex)
		{
			double totalSeconds = (utcNow - _prevTs).TotalSeconds;
			if (totalSeconds > 0.05)
			{
				ulong num = ((prev.RxBytes >= _prev.RxBytes) ? (prev.RxBytes - _prev.RxBytes) : 0);
				ulong num2 = ((prev.TxBytes >= _prev.TxBytes) ? (prev.TxBytes - _prev.TxBytes) : 0);
				_upText.Text = FormatSpeed((double)num2 / totalSeconds);
				_downText.Text = FormatSpeed((double)num / totalSeconds);
			}
		}
		_prev = prev;
		_prevTs = utcNow;
	}

	private static string FormatSpeed(double bytesPerSec)
	{
		if (!(bytesPerSec < 1024.0))
		{
			if (!(bytesPerSec < 1048576.0))
			{
				if (!(bytesPerSec < 1073741824.0))
				{
					return $"{bytesPerSec / 1073741824.0:F1}G/s";
				}
				return $"{bytesPerSec / 1048576.0:F1}M/s";
			}
			return $"{bytesPerSec / 1024.0:F1}K/s";
		}
		return $"{bytesPerSec:F0}B/s";
	}

	private void OnNetChanged(object? sender, StatusSnapshot e)
	{
		if (_disposed)
		{
			return;
		}
		UiDispatch.Run((DispatcherObject)(object)this, delegate
		{
			if (!_disposed)
			{
				UpdateConnection(e);
			}
		});
	}

	private void UpdateConnection(StatusSnapshot snap)
	{
		if (snap.Severity != StatusSeverity.Normal)
		{
			_upText.Text = "--";
			_downText.Text = "--";
			_prev = default(NetworkPrimaryNative);
			_prevTs = default(DateTime);
		}
	}

	public void Dispose()
	{
		_disposed = true;
		_speedTimer.Stop();
		if (_net != null)
		{
			_net.Changed -= OnNetChanged;
		}
	}
}

internal sealed class NotificationToggle : ContentControl
{
	private readonly CapsuleSwitch _topSwitch;

	private readonly CapsuleSwitch _bottomSwitch;

	private bool _topOn = true;

	public NotificationToggle()
	{
		base.Width = 14.0;
		base.Height = 13.0;
		Canvas canvas = new Canvas
		{
			Width = 20.0,
			Height = 18.0
		};
		_topSwitch = new CapsuleSwitch();
		Canvas.SetLeft(_topSwitch, 2.0);
		Canvas.SetTop(_topSwitch, 1.0);
		canvas.Children.Add(_topSwitch);
		_bottomSwitch = new CapsuleSwitch();
		Canvas.SetLeft(_bottomSwitch, 2.0);
		Canvas.SetTop(_bottomSwitch, 9.0);
		canvas.Children.Add(_bottomSwitch);
		Canvas child = IconCropper.Crop(canvas, 1.0, 0.5, 18.0, 17.0);
		base.Content = new Viewbox
		{
			Child = child,
			Stretch = Stretch.Uniform,
			Width = 14.0,
			Height = 13.0
		};
		UpdateState();
	}

	public void Toggle()
	{
		_topOn = !_topOn;
		UpdateState();
	}

	private void UpdateState()
	{
		_topSwitch.SetOn(_topOn);
		_bottomSwitch.SetOn(!_topOn);
	}
}

internal static class StatusColor
{
	public static Brush ForSeverity(StatusSeverity sev)
	{
		if (1 == 0)
		{
		}
		SolidColorBrush result = sev switch
		{
			StatusSeverity.Critical => new SolidColorBrush(Color.FromRgb(byte.MaxValue, 106, 106)), 
			StatusSeverity.Warning => new SolidColorBrush(Color.FromRgb(byte.MaxValue, 215, 0)), 
			_ => Brushes.White, 
		};
		if (1 == 0)
		{
		}
		return result;
	}
}

// SystemTray: 移植自 cairoshell SystemTray.xaml.cs + SystemTrayIcon.xaml.cs
//
// 依赖 ManagedShell 的托盘接管机制：本进程注册一个 Shell_TrayWnd 并置顶（把 explorer 的压到最底），
// 广播 TaskbarCreated 让各应用重新 Shell_NotifyIcon，图标数据经 WM_COPYDATA 进入 NotificationArea.TrayIcons。
//
// 【踩坑记录 · 必读】ShellConfig 是 struct，未显式赋值的字段为默认值。
//   PinnedNotifyIcons 若为 null，SysTrayCallback 内的 notifyIcon.SetPinValues() 会执行
//   _notificationArea.PinnedNotifyIcons.Length 而抛 NullReferenceException；该异常发生在
//   TrayIcons.Add(notifyIcon) 之前，且被 ManagedShell 用 catch 吞掉仅写 ShellLogger，
//   表现为"托盘永远为空、却没有任何可见报错"。因此 PinnedNotifyIcons 必须显式赋值。
internal sealed class SystemTrayIcon : ContentControl, IDisposable
{
    private static readonly object _initLock = new object();
    private static ManagedShell.ShellManager? _shellManager;
    private static ManagedShell.WindowsTray.NotificationArea? _notificationArea;
    private static int _refCount;

    private readonly StackPanel? _iconPanel;
    private readonly TextBlock? _chevron;
    private readonly System.Collections.Generic.Dictionary<ManagedShell.WindowsTray.NotifyIcon, FrameworkElement> _elements = new System.Collections.Generic.Dictionary<ManagedShell.WindowsTray.NotifyIcon, FrameworkElement>();
    private bool _expanded = true;
    private bool _disposed;

    /// <summary>图标数量变化（宿主据此调整按钮宽度）。</summary>
    public event EventHandler<int>? IconCountChanged;

    public event EventHandler<bool>? ExpandedChanged;

    /// <summary>当前渲染出的托盘图标数量。</summary>
    public int IconCount => _elements.Count;

    public SystemTrayIcon()
    {
        try
        {
            Height = 16;
            try
            {
                lock (_initLock)
                {
                    if (_notificationArea is null)
                    {
                        var config = new ManagedShell.ShellConfig
                        {
                            EnableTrayService = true,
                            AutoStartTrayService = true,
                            // 关键：不可省略。null 会导致 SetPinValues() 抛 NRE，图标永远进不了集合。
                            PinnedNotifyIcons = ManagedShell.WindowsTray.NotificationArea.DEFAULT_PINNED,
                        };
                        _shellManager = new ManagedShell.ShellManager(config);
                        _notificationArea = _shellManager.NotificationArea;
                        if (_notificationArea.Handle == IntPtr.Zero)
                        {
                            try { _notificationArea.Initialize(); }
                            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SystemTrayIcon] NotificationArea.Initialize 失败: " + ex.Message); }
                        }
                    }
                    _refCount++;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SystemTrayIcon] ShellManager init failed: " + ex.Message);
                _notificationArea = null;
                _shellManager = null;
            }

            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _iconPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _chevron = new TextBlock
            {
                Text = "\u2039",
                Foreground = Brushes.White,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0),
                ToolTip = "折叠托盘",
                Cursor = System.Windows.Input.Cursors.Hand
            };
            _chevron.MouseLeftButtonUp += (_, _) => Toggle();
            panel.Children.Add(_iconPanel);
            panel.Children.Add(_chevron);
            Content = panel;

            if (_notificationArea is not null)
            {
                try
                {
                    _notificationArea.PinnedIcons.CollectionChanged += OnIconsChanged;
                    _notificationArea.UnpinnedIcons.CollectionChanged += OnIconsChanged;
                    RebuildIcons();
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SystemTrayIcon] Subscribe failed: " + ex.Message); }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[SystemTrayIcon] FATAL ctor failed: " + ex.Message);
            Content = new TextBlock { Text = string.Empty, Width = 1, Height = 1 };
        }
    }

    private void OnIconsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_disposed) return;
        UiDispatch.Run(this, () =>
        {
            if (_disposed) return;
            try
            {
                // PinnedIcons 带 SortDescriptions，排序视图在部分变更下只发 Reset（NewItems/OldItems 均为 null），
                // 逐项增删会漏图标，因此 Reset 一律整表重建。
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset
                    || (e.NewItems is null && e.OldItems is null))
                {
                    RebuildIcons();
                    return;
                }
                if (e.OldItems is not null)
                {
                    foreach (ManagedShell.WindowsTray.NotifyIcon icon in e.OldItems) RemoveIconElement(icon);
                }
                if (e.NewItems is not null)
                {
                    foreach (ManagedShell.WindowsTray.NotifyIcon icon in e.NewItems) AddIconElement(icon);
                }
                IconCountChanged?.Invoke(this, _elements.Count);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SystemTrayIcon] OnIconsChanged failed: " + ex.Message); }
        });
    }

    /// <summary>按 Pinned → Unpinned 顺序整表重建图标（Reset 场景与初始化共用）。</summary>
    private void RebuildIcons()
    {
        if (_notificationArea is null || _iconPanel is null) return;
        _iconPanel.Children.Clear();
        _elements.Clear();
        foreach (ManagedShell.WindowsTray.NotifyIcon icon in _notificationArea.PinnedIcons) AddIconElement(icon);
        foreach (ManagedShell.WindowsTray.NotifyIcon icon in _notificationArea.UnpinnedIcons) AddIconElement(icon);
        IconCountChanged?.Invoke(this, _elements.Count);
    }

    private void AddIconElement(ManagedShell.WindowsTray.NotifyIcon icon)
    {
        if (_elements.ContainsKey(icon)) return;
        var img = new System.Windows.Controls.Image { Width = 14, Height = 14, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        img.SetBinding(System.Windows.Controls.Image.SourceProperty, new System.Windows.Data.Binding("Icon") { Source = icon });
        var border = new Border
        {
            Width = 16, Height = 16, Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            Child = img, DataContext = icon, Cursor = System.Windows.Input.Cursors.Hand
        };
        // NotifyIcon 暴露的提示文本属性是 Title（没有 TooltipText，写错只会静默不显示）。
        border.SetBinding(Border.ToolTipProperty, new System.Windows.Data.Binding("Title") { Source = icon });
        border.MouseEnter += (s, _) => UpdateAndForward(s, (i, p) => i.IconMouseEnter(p));
        border.MouseLeave += (s, _) =>
        {
            if ((s as Border)?.DataContext is ManagedShell.WindowsTray.NotifyIcon i) i.IconMouseLeave(GetCursorPos());
        };
        border.MouseMove += (s, _) =>
        {
            if ((s as Border)?.DataContext is ManagedShell.WindowsTray.NotifyIcon i) i.IconMouseMove(GetCursorPos());
        };
        border.MouseDown += (s, e) =>
        {
            e.Handled = true;
            UpdateAndForward(s, (i, p) => { SetTrayHostSize(); i.IconMouseDown(e.ChangedButton, p, DoubleClickTime); });
        };
        border.MouseUp += (s, e) =>
        {
            e.Handled = true;
            UpdateAndForward(s, (i, p) => i.IconMouseUp(e.ChangedButton, p, DoubleClickTime));
        };
        _elements[icon] = border;
        _iconPanel!.Children.Add(border);
    }

    private void RemoveIconElement(ManagedShell.WindowsTray.NotifyIcon icon)
    {
        if (_elements.TryGetValue(icon, out var el)) { _iconPanel!.Children.Remove(el); _elements.Remove(icon); }
    }

    /// <summary>先把图标的屏幕矩形回写给 NotifyIcon（应用弹菜单要靠它定位），再转发鼠标事件。</summary>
    private void UpdateAndForward(object sender, Action<ManagedShell.WindowsTray.NotifyIcon, uint> action)
    {
        var decorator = sender as Border;
        var icon = decorator?.DataContext as ManagedShell.WindowsTray.NotifyIcon;
        if (icon is null || decorator is null) return;
        try
        {
            Point loc = decorator.PointToScreen(new Point(0, 0));
            double dpi = 1.0;
            try
            {
                var src = PresentationSource.FromVisual(decorator);
                if (src?.CompositionTarget != null) dpi = src.CompositionTarget.TransformToDevice.M11;
            }
            catch (InvalidOperationException) { /* 未连入可视树时保持 dpi=1 */ }
            icon.Placement = new ManagedShell.Interop.NativeMethods.Rect
            {
                Top = (int)loc.Y,
                Left = (int)loc.X,
                Bottom = (int)(loc.Y + decorator.ActualHeight * dpi),
                Right = (int)(loc.X + decorator.ActualWidth * dpi),
            };
            action(icon, GetCursorPos());
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SystemTrayIcon] UpdatePlacement failed: " + ex.Message); }
    }

    /// <summary>系统双击判定阈值（原实现硬编码 500ms，与用户设置不一致会误判双击）。</summary>
    private static int DoubleClickTime
    {
        get
        {
            try { return (int)GetDoubleClickTime(); }
            catch (Exception) { return 500; }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    /// <summary>光标位置打包成 LPARAM（ManagedShell 的鼠标转发接口要求 uint，不能传 null）。</summary>
    private static uint GetCursorPos()
    {
        try { return ManagedShell.Common.Helpers.MouseHelper.GetCursorPositionParam(); }
        catch (Exception) { return 0u; }
    }

    private void SetTrayHostSize()
    {
        try
        {
            if (_notificationArea is null) return;
            Point loc = PointToScreen(new Point(0, 0));
            double dpi = 1.0;
            try
            {
                var src = PresentationSource.FromVisual(this);
                if (src?.CompositionTarget != null) dpi = src.CompositionTarget.TransformToDevice.M11;
            }
            catch (InvalidOperationException) { /* 未连入可视树时保持 dpi=1 */ }
            _notificationArea.SetTrayHostSizeData(new ManagedShell.WindowsTray.TrayHostSizeData
            {
                edge = ManagedShell.Interop.NativeMethods.ABEdge.ABE_TOP,
                rc = new ManagedShell.Interop.NativeMethods.Rect
                {
                    Top = (int)loc.Y,
                    Left = (int)loc.X,
                    Bottom = (int)(loc.Y + ActualHeight * dpi),
                    Right = (int)(loc.X + ActualWidth * dpi),
                },
            });
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SystemTrayIcon] SetTrayHostSize failed: " + ex.Message); }
    }

    private void Toggle()
    {
        _expanded = !_expanded;
        _iconPanel!.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        _chevron!.Text = _expanded ? "\u2039" : "\u203A";
        ExpandedChanged?.Invoke(this, _expanded);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_notificationArea != null)
            {
                _notificationArea.PinnedIcons.CollectionChanged -= OnIconsChanged;
                _notificationArea.UnpinnedIcons.CollectionChanged -= OnIconsChanged;
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SystemTrayIcon] 退订失败: " + ex.Message); }
        lock (_initLock)
        {
            _refCount--;
            if (_refCount <= 0 && _shellManager != null)
            {
                try { _shellManager.Dispose(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SystemTrayIcon] ShellManager.Dispose 失败: " + ex.Message); }
                _shellManager = null;
                _notificationArea = null;
            }
        }
    }
}

internal static class UiDispatch
{
	public static void Run(DispatcherObject target, Action action)
	{
		if (target.Dispatcher.CheckAccess())
		{
			action();
		}
		else
		{
			target.Dispatcher.BeginInvoke((Delegate)action, Array.Empty<object>());
		}
	}
}

internal sealed class VolumeIcon : ContentControl, IDisposable
{
	private readonly IVolumeMonitor? _vol;

	private readonly Path _speaker;

	private readonly Path[] _waves = new Path[3];

	private bool _disposed;

	public VolumeIcon(IVolumeMonitor? vol)
	{
		_vol = vol;
		base.Width = 23.0;
		base.Height = 13.0;
		Canvas canvas = new Canvas
		{
			Width = 28.0,
			Height = 16.0
		};
		_speaker = new Path
		{
			Data = Geometry.Parse("M3 5v6h2l3 3V2L5 5H3z"),
			Fill = Brushes.White,
			Stretch = Stretch.Uniform,
			Width = 9.0,
			Height = 12.0
		};
		Canvas.SetLeft(_speaker, 0.0);
		Canvas.SetTop(_speaker, 2.0);
		canvas.Children.Add(_speaker);
		string[] array = new string[3] { "M0 1Q2.5 3 0 5", "M0 0Q4 4 0 8", "M0 -1Q5.5 4.5 0 10" };
		double[] array2 = new double[3] { 11.0, 14.0, 17.0 };
		double[] array3 = new double[3] { 5.0, 3.0, 1.0 };
		double[] array4 = new double[3] { 4.0, 6.0, 8.0 };
		double[] array5 = new double[3] { 6.0, 10.0, 14.0 };
		for (int i = 0; i < 3; i++)
		{
			_waves[i] = new Path
			{
				Data = Geometry.Parse(array[i]),
				Stroke = Brushes.White,
				StrokeThickness = 1.2,
				Stretch = Stretch.Uniform,
				Width = array4[i],
				Height = array5[i],
				Visibility = Visibility.Visible
			};
			Canvas.SetLeft(_waves[i], array2[i]);
			Canvas.SetTop(_waves[i], array3[i]);
			canvas.Children.Add(_waves[i]);
		}
		base.Content = new Viewbox
		{
			Child = canvas,
			Stretch = Stretch.Uniform,
			Width = 23.0,
			Height = 13.0
		};
		if (_vol != null)
		{
			_vol.Changed += OnVolumeChanged;
			try
			{
				UpdateVolume(_vol.GetSnapshot());
			}
			catch
			{
			}
		}
	}

	private void OnVolumeChanged(object? sender, StatusSnapshot e)
	{
		if (_disposed)
		{
			return;
		}
		UiDispatch.Run((DispatcherObject)(object)this, delegate
		{
			if (!_disposed)
			{
				UpdateVolume(e);
			}
		});
	}

	private void UpdateVolume(StatusSnapshot snap)
	{
		double num = ((snap.Progress >= 0.0) ? (snap.Progress / 100.0) : 0.5);
		bool flag = snap.Severity == StatusSeverity.Warning;
		int num2 = ((!flag) ? ((num < 0.33) ? 1 : ((num < 0.66) ? 2 : 3)) : 0);
		SolidColorBrush solidColorBrush = new SolidColorBrush(Color.FromRgb(96, 96, 96));
		for (int i = 0; i < 3; i++)
		{
			_waves[i].Visibility = Visibility.Visible;
			_waves[i].Stroke = ((i < num2) ? Brushes.White : solidColorBrush);
		}
		_speaker.Fill = (flag ? solidColorBrush : Brushes.White);
	}

	public void Dispose()
	{
		_disposed = true;
		if (_vol != null)
		{
			_vol.Changed -= OnVolumeChanged;
		}
	}
}

internal sealed class WifiSignalIcon : ContentControl, IDisposable
{
	private readonly INetworkMonitor? _net;

	private readonly Path[] _arcs = new Path[3];

	private readonly Ellipse _dot;

	private bool _disposed;

	public WifiSignalIcon(INetworkMonitor? net)
	{
		_net = net;
		base.Width = 16.0;
		base.Height = 13.0;
		Canvas canvas = new Canvas
		{
			Width = 16.0,
			Height = 16.0
		};
		double[] array = new double[3] { 3.0, 5.0, 7.0 };
		for (int i = 0; i < 3; i++)
		{
			double num = array[i];
			double num2 = num * 0.7071;
			Path path = new Path
			{
				Data = Geometry.Parse($"M {8.0 - num2:F2},{11.5 - num2:F2} A {num},{num} 0 0 1 {8.0 + num2:F2},{11.5 - num2:F2}"),
				Stroke = Brushes.White,
				StrokeThickness = 1.3,
				StrokeStartLineCap = PenLineCap.Round,
				StrokeEndLineCap = PenLineCap.Round,
				SnapsToDevicePixels = true
			};
			_arcs[i] = path;
			canvas.Children.Add(path);
		}
		_dot = new Ellipse
		{
			Width = 2.2,
			Height = 2.2,
			Fill = Brushes.White
		};
		Canvas.SetLeft(_dot, 6.9);
		Canvas.SetTop(_dot, 10.4);
		canvas.Children.Add(_dot);
		Canvas child = IconCropper.Crop(canvas, 2.5, 4.0, 11.0, 9.0);
		base.Content = new Viewbox
		{
			Child = child,
			Stretch = Stretch.Uniform,
			Width = 16.0,
			Height = 13.0
		};
		if (_net != null)
		{
			_net.Changed += OnNetChanged;
			try
			{
				UpdateSignal(_net.GetSnapshot());
			}
			catch
			{
			}
		}
	}

	private void OnNetChanged(object? sender, StatusSnapshot e)
	{
		if (_disposed)
		{
			return;
		}
		UiDispatch.Run((DispatcherObject)(object)this, delegate
		{
			if (!_disposed)
			{
				UpdateSignal(e);
			}
		});
	}

	private void UpdateSignal(StatusSnapshot snap)
	{
		SolidColorBrush solidColorBrush = ((snap.Severity == StatusSeverity.Normal) ? Brushes.White : new SolidColorBrush(Color.FromRgb(96, 96, 96)));
		Path[] arcs = _arcs;
		foreach (Path path in arcs)
		{
			path.Stroke = solidColorBrush;
		}
		_dot.Fill = solidColorBrush;
	}

	public void Dispose()
	{
		_disposed = true;
		if (_net != null)
		{
			_net.Changed -= OnNetChanged;
		}
	}
}

