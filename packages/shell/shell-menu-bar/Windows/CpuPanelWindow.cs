// CPU 面板：真实 CPU 占用柱状图（按秒采样的 30 根柱），标题 CPU。
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Status.Native;

namespace BetterDesktop.Shell.MenuBar.Windows;

internal sealed class CpuPanelWindow : MenuBarPopupWindow
{
    private TextBlock? usageLabel;
    private TextBlock? tempLabel;
    private ViewModel? _vm;

    public CpuPanelWindow(IVibrancyService vibrancy, IAppearanceService? appearance = null)
        : base(vibrancy, appearance)
    {
        Width = NativePanelStyles.DefaultWidth;
        MinWidth = NativePanelStyles.DefaultWidth;
        SizeToContent = SizeToContent.Height;
        Closed += (_, _) => _vm?.CloseSelf();
    }

    public FrameworkElement BuildPreviewContent() => BuildContent();

    protected override FrameworkElement BuildContent()
    {
        const int slots = 30;
        const double containerW = 290;
        const double containerH = 96;
        var canvas = new Canvas
        {
            Width = containerW,
            Height = containerH,
            Background = Brushes.Transparent,
            Margin = new Thickness(2, 2, 2, 2),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var vm = _vm = new ViewModel(slots);
        Action refresh = () =>
        {
            canvas.Children.Clear();
            var dashes = new DoubleCollection(new double[] { 1.5, 2.5 });
            for (int i = 0; i <= 4; i++)
            {
                var y = 4 + i * ((containerH - 8) / 4.0);
                canvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = 0,
                    X2 = containerW,
                    Y1 = y,
                    Y2 = y,
                    Stroke = NativePanelStyles.SeparatorBack,
                    StrokeThickness = 0.6,
                    StrokeDashArray = dashes
                });
            }
            var gapX = 2;
            var bw = (containerW - gapX * (slots + 1)) / slots;
            var values = vm.Values;
            for (int i = 0; i < slots; i++)
            {
                var v = values.Length > i ? values[i] / 100.0 : 0;
                v = Math.Clamp(v, 0, 1);
                var h = (containerH - 10) * v;
                if (h < 1.5) h = 1.5;
                var col = new Border
                {
                    Width = bw,
                    Height = h,
                    CornerRadius = new CornerRadius(bw / 2),
                    Background = NativePanelStyles.BarFill
                };
                Canvas.SetLeft(col, gapX + i * (bw + gapX));
                Canvas.SetTop(col, containerH - 5 - h);
                canvas.Children.Add(col);
            }
            // 更新当前数值标签
            if (usageLabel != null) usageLabel.Text = vm.UsagePercentLabel;
            if (tempLabel != null) tempLabel.Text = vm.TemperatureLabel;
        };
        vm.AttachRefresh(refresh);

        var root = NativePanelStyles.Root(withColumn: col =>
        {
            col.Children.Add(NativePanelStyles.Title("CPU"));
            // 处理器具体型号（SMBIOS Type4），不可用时隐藏。
            string model = string.Empty;
            try { model = CpuCoreNative.ReadModel(); } catch { /* ignore */ }
            if (!string.IsNullOrWhiteSpace(model))
            {
                col.Children.Add(new TextBlock
                {
                    Text = model,
                    Foreground = NativePanelStyles.TextSecondary,
                    FontSize = 10,
                    Margin = new Thickness(0, -2, 0, 4),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = NativePanelStyles.DefaultWidth - 24
                });
            }

            // 实时数值行：大号当前使用率 + 温度（右侧）。
            var summaryRow = new Grid { Margin = new Thickness(2, 2, 2, 2) };
            summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            usageLabel = new TextBlock
            {
                Text = "— %",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = NativePanelStyles.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            };
            summaryRow.Children.Add(usageLabel);
            tempLabel = new TextBlock
            {
                Text = "温度 —",
                FontSize = 10.5,
                Foreground = NativePanelStyles.TextSecondary,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(tempLabel, 1);
            summaryRow.Children.Add(tempLabel);
            col.Children.Add(summaryRow);

            col.Children.Add(canvas);
        });
        return root;
    }

    /// <summary>CpuPanel 内部 ViewModel。</summary>
    private sealed class ViewModel
    {
        public double[] Values { get; }
        private readonly DispatcherTimer _timer;
        private Action? _refresh;

        // ACPI 热区若不暴露，C++ 层读不到温度。这里接入 LibreHardwareMonitor
        // （内核态 Ring0 采样 CPU 传感器），保证桌面 CPU 温度可靠读取。
        private readonly LibreHardwareMonitor.Hardware.Computer _lhm;
        private bool _lhmOk;

        public string UsagePercentLabel { get; private set; } = "— %";
        public string TemperatureLabel { get; private set; } = "温度 —";

        public ViewModel(int slots)
        {
            Values = new double[slots];
            try { CpuCoreNative.ResetCounters(); } catch { /* ignore */ }
            _ = CpuCoreNative.ReadUtilization();

            _lhm = new LibreHardwareMonitor.Hardware.Computer { IsCpuEnabled = true };
            try
            {
                _lhm.Open();
                _lhmOk = true;
            }
            catch
            {
                // 初始化失败（如缺管理员权限），标记不可用并优雅降级。
                _lhmOk = false;
            }

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) =>
            {
                if (CpuCoreNative.IsAvailable)
                {
                    var u = CpuCoreNative.ReadUtilization();
                    PushValue(u.Utilization);
                    UsagePercentLabel = $"{u.Utilization} %";
                }
                TemperatureLabel = ReadTemperature() ?? "温度 —";
                _refresh?.Invoke();
            };
            _timer.Start();
        }

        /// <summary>读取 CPU 有效温度（℃）。优先 LibreHardwareMonitor；失败时回退 C++ ACPI 读取。</summary>
        private string? ReadTemperature()
        {
            if (_lhmOk)
            {
                try
                {
                    foreach (var hardware in _lhm.Hardware)
                    {
                        hardware.Update();
                        foreach (var sensor in hardware.Sensors)
                        {
                            if (sensor.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature &&
                                sensor.Value.HasValue)
                            {
                                return $"{sensor.Value.Value:0} °C";
                            }
                        }
                    }
                }
                catch
                {
                    _lhmOk = false;
                }
            }
            var t = CpuCoreNative.ReadTemperature();
            return t.HasValue ? $"{t.Value} °C" : null;
        }

        public void CloseSelf()
        {
            try { _timer.Stop(); _lhm.Close(); } catch { /* ignore */ }
        }

        public void AttachRefresh(Action refresh)
        {
            _refresh = refresh;
            _refresh?.Invoke();
        }

        private void PushValue(int value)
        {
            for (int i = 0; i < Values.Length - 1; i++) Values[i] = Values[i + 1];
            Values[Values.Length - 1] = value;
        }
    }
}
