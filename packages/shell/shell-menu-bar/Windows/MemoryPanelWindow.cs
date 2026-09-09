// MEM 面板（内存占用条 + TOP 进程 + 物理内存条规格）。
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Status.Native;

namespace BetterDesktop.Shell.MenuBar.Windows;

// ── 本文件方法级白话索引（内存面板，白话 → 方法）──
//   "内存占用直方图"            → BuildHistogram（返回 refresh 回调供外部推数据）
//   "内存占用百分比条"          → BuildUsageBar
//   "占内存最多的进程行"        → BuildProcessRow（进程名友好化 ResolveFriendlyName、图标 BuildProcessIcon）
//   "内存条/物理插槽信息行"     → BuildRamRow
//   "外部把数据推进面板"        → Attach；定时刷新 OnTick；占用条动画 PushBar
// 面板族同构：均继承 MenuBarPopupWindow，Build* 静态构 UI、Attach 接数据、*ViewModel 承载数据。
// B8 已修：IsVisibleChanged 隐藏 Pause/显示 Resume、Closed CloseSelf 停 timer 并释放控件树闭包。
// ────────────────────────────────────

internal sealed class MemoryPanelWindow : MenuBarPopupWindow
{
    private ViewModel? _vm;

    public MemoryPanelWindow(IVibrancyService vibrancy, IAppearanceService? appearance = null)
        : base(vibrancy, appearance)
    {
        Width = NativePanelStyles.DefaultWidth;
        MinWidth = NativePanelStyles.DefaultWidth;
        SizeToContent = SizeToContent.Height;
        // B8：面板隐藏即停 1s 轮询（按需刷新，不再对不可见窗口空转、不再经 refresh 闭包钉住控件树）；
        // 重新显示时恢复并立即刷一帧；窗口最终关闭时彻底停止并释放闭包。
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) _vm?.Resume();
            else _vm?.Pause();
        };
        Closed += (_, _) => _vm?.CloseSelf();
    }

    public FrameworkElement BuildPreviewContent() => BuildContent();

    protected override FrameworkElement BuildContent()
    {
        var vm = _vm = new ViewModel();
        var root = NativePanelStyles.Root(withColumn: col =>
        {
            col.Children.Add(NativePanelStyles.Title("MEM"));

            // 直方图（内存使用可视化）
            col.Children.Add(BuildHistogram(vm, out var refreshHist));

            col.Children.Add(NativePanelStyles.Separator(top: 6, bottom: 6));

            // 占用条（已用 / 总量，数值由 ViewModel.OnTick 实时填充）
            col.Children.Add(BuildUsageBar(vm, out var refreshUsage));

            col.Children.Add(NativePanelStyles.Separator(top: 10, bottom: 4));

            col.Children.Add(new TextBlock
            {
                Text = "进程",
                Foreground = NativePanelStyles.TextSecondary,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 2, 0, 4)
            });
            var processBox = new StackPanel { Name = "ProcessBox" };
            col.Children.Add(processBox);

            col.Children.Add(NativePanelStyles.Separator(top: 10, bottom: 4));

            col.Children.Add(new TextBlock
            {
                Text = "RAM",
                Foreground = NativePanelStyles.TextSecondary,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 2, 0, 4)
            });
            var ramBox = new StackPanel { Name = "RamBox" };
            col.Children.Add(ramBox);

            vm.Attach(refreshHist, refreshUsage, vms =>
            {
                processBox.Children.Clear();
                foreach (var p in vms.processes)
                {
                    processBox.Children.Add(BuildProcessRow(p));
                }
                ramBox.Children.Clear();
                foreach (var r in vms.ram)
                {
                    ramBox.Children.Add(BuildRamRow(r));
                }
            });
        });
        return root;
    }

    private static FrameworkElement BuildHistogram(ViewModel vm, out Action<double[]> refresh)
    {
        const int slots = 30;
        const double containerW = 290;
        const double containerH = 46;
        var canvas = new Canvas
        {
            Width = containerW,
            Height = containerH,
            Background = Brushes.Transparent,
            Margin = new Thickness(2, 2, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        refresh = values =>
        {
            canvas.Children.Clear();
            var dashes = new DoubleCollection(new double[] { 1.5, 2.5 });
            for (int i = 0; i <= 4; i++)
            {
                var y = 2 + i * ((containerH - 4) / 4.0);
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
            for (int i = 0; i < slots; i++)
            {
                var v = values.Length > i ? values[i] : 0;
                var h = (containerH - 6) * v;
                if (h < 1.5) h = 1.5;
                var col = new Border
                {
                    Width = bw,
                    Height = h,
                    CornerRadius = new CornerRadius(bw / 2),
                    Background = NativePanelStyles.BarFill
                };
                Canvas.SetLeft(col, gapX + i * (bw + gapX));
                Canvas.SetTop(col, containerH - 3 - h);
                canvas.Children.Add(col);
            }
        };
        return canvas;
    }

    private static FrameworkElement BuildUsageBar(ViewModel vm, out Action refresh)
    {
        var host = new StackPanel();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock
        {
            Text = vm.UsageLabel,
            Foreground = NativePanelStyles.TextPrimary,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        host.Children.Add(grid);

        var track = NativePanelStyles.BuildProgressTrack(vm.UsagePercent01, 14,
            new SolidColorBrush(Color.FromRgb(0, 122, 255)));
        track.Margin = new Thickness(0, 6, 0, 0);
        host.Children.Add(track);

        refresh = () =>
        {
            label.Text = vm.UsageLabel;
            // 重绘：通过父容器找到 track 的 child grid 重算比例
            if (track is Border wrap && wrap.Child is Grid g && g.ColumnDefinitions.Count == 2)
            {
                var p = Math.Clamp(vm.UsagePercent01, 0, 1);
                g.ColumnDefinitions[0].Width = new GridLength(p, GridUnitType.Star);
                g.ColumnDefinitions[1].Width = new GridLength(1d - p, GridUnitType.Star);
            }
        };
        return host;
    }

    private static FrameworkElement BuildProcessRow(MemoryTopProcessNative p)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 用真实进程图标替换占位符（与声音面板一致，复用 ProcessAppInfo.GetIcon）。
        var icon = BuildProcessIcon(p.Pid);
        row.Children.Add(icon);

        var name = new TextBlock
        {
            Text = ResolveFriendlyName(p.Pid, p.Name),
            FontSize = 11.5,
            Foreground = NativePanelStyles.TextPrimary,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        var size = new TextBlock
        {
            Text = FormatGb(p.WorkingSetKb),
            FontSize = 11,
            Foreground = NativePanelStyles.TextPrimary,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(size, 2);
        row.Children.Add(size);
        return row;
    }

    /// <summary>按进程 ID 找到可展示的友好名（复用 ProcessAppInfo 的版本信息解析），
    /// PID 失效或取不到时回退到进程名。</summary>
    private static string ResolveFriendlyName(uint pid, string processName)
    {
        if (pid > 0)
        {
            var friendly = ProcessAppInfo.GetName((int)pid);
            if (!string.IsNullOrWhiteSpace(friendly)) return friendly;
        }
        // 回退：去掉 ".exe" 只留主名，避免整串太长撑破单行。
        if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return processName[..^4];
        }
        return string.IsNullOrWhiteSpace(processName) ? "进程" : processName;
    }

    /// <summary>真实进程图标（22x22）；PID 或图标取不到时回退为占位方形 + 圆点字形。</summary>
    private static FrameworkElement BuildProcessIcon(uint pid)
    {
        var source = pid > 0 ? ProcessAppInfo.GetIcon((int)pid) : null;
        var box = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (source is not null)
        {
            var img = new System.Windows.Controls.Image
            {
                Source = source,
                Width = 18,
                Height = 18,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            box.Child = img;
        }
        else
        {
            box.Child = new TextBlock
            {
                Text = "▢",
                FontSize = 11,
                Foreground = NativePanelStyles.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }
        return box;
    }

    private static FrameworkElement BuildRamRow(MemoryPhysicalSlotNative r, int index = -1)
    {
        // 名称列必须用 Star 才会触发 TextTrimming；用 Auto 会按内容撑满并把右侧规格挤出去。
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        string tag = index >= 0 ? $"#{index + 1}" : "#";
        var left = new TextBlock
        {
            Text = tag + " " + (string.IsNullOrEmpty(r.PartNumber) ? "PhysicalMemory" : r.PartNumber),
            FontSize = 11,
            Foreground = NativePanelStyles.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(left, 0);
        row.Children.Add(left);

        string spec = $"{NativePanelStyles.FormatBytes(r.SizeKb * 1024)}";
        if (r.SpeedMhz > 0) spec += $" {r.SpeedMhz}MHz";
        var right = new TextBlock
        {
            Text = spec,
            FontSize = 11,
            Foreground = NativePanelStyles.TextPrimary,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(right, 1);
        row.Children.Add(right);
        return row;
    }

    private static string FormatGb(ulong kb)
    {
        double gb = kb / (1024.0 * 1024.0);
        if (gb >= 1) return $"{gb:F2}G";
        double mb = kb / 1024.0;
        if (mb >= 1) return $"{mb:F0}M";
        return kb + "K";
    }

    /// <summary>MemoryPanel 内部 ViewModel。</summary>
    private sealed class ViewModel
    {
        private readonly DispatcherTimer _timer;
        private readonly double[] _hist = new double[30];
        private Action<double[]>? _refreshHist;
        private Action? _refreshUsage;
        private Action<(IReadOnlyList<MemoryTopProcessNative> processes, IReadOnlyList<MemoryPhysicalSlotNative> ram)>? _setLists;

        // 中性初值，真实占用由构造末尾与 Attach 的 OnTick 立即覆盖（不展示硬编码示例数值）。
        public string UsageLabel { get; private set; } = "—\n—";
        public double UsagePercent01 { get; private set; }

        public ViewModel()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += OnTick;
            _timer.Start();
            OnTick(null, EventArgs.Empty);
        }

        /// <summary>面板隐藏：停止 1s 轮询，避免对不可见 UI 空转（B8）。</summary>
        public void Pause() => _timer.Stop();

        /// <summary>面板重新显示：恢复轮询并立即刷一帧，避免先看到旧数据（B8）。</summary>
        public void Resume()
        {
            if (!_timer.IsEnabled) _timer.Start();
            OnTick(null, EventArgs.Empty);
        }

        /// <summary>窗口最终关闭：停轮询并释放 refresh 闭包对控件树的引用（B8）。</summary>
        public void CloseSelf()
        {
            _timer.Stop();
            _refreshHist = null;
            _refreshUsage = null;
            _setLists = null;
        }

        public void Attach(Action<double[]> refreshHist, Action refreshUsage,
            Action<(IReadOnlyList<MemoryTopProcessNative> processes, IReadOnlyList<MemoryPhysicalSlotNative> ram)> setLists)
        {
            _refreshHist = refreshHist;
            _refreshUsage = refreshUsage;
            _setLists = setLists;
            _refreshHist?.Invoke(_hist);
            _refreshUsage?.Invoke();
            OnTick(null, EventArgs.Empty);
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (MemoryCoreNative.IsAvailable)
            {
                var g = MemoryCoreNative.ReadGlobal();
                if (g.TotalPhysKb > 0)
                {
                    var used = Math.Max(0, (long)g.TotalPhysKb - (long)g.AvailPhysKb);
                    var usedGb = used / (1024.0 * 1024.0);
                    var totalGb = g.TotalPhysKb / (1024.0 * 1024.0);
                    UsageLabel = $"{usedGb:F2} GB\n{totalGb:F2} GB";
                    UsagePercent01 = totalGb > 0 ? Math.Clamp(used / (double)g.TotalPhysKb, 0, 1) : 0;
                    PushBar(UsagePercent01);
                }
                var procs = MemoryCoreNative.ReadTopProcesses(5);
                var slots = MemoryCoreNative.ReadPhysicalSlots(6);
                if (slots.Count == 0 && g.TotalPhysKb > 0)
                {
                    // 降级：只显示总容量一行（避免"RAM"节空）
                    slots = new List<MemoryPhysicalSlotNative>
                    {
                        new MemoryPhysicalSlotNative("PhysicalMemory", g.TotalPhysKb, 0)
                    };
                }
                _setLists?.Invoke((procs, slots));
            }

            _refreshHist?.Invoke(_hist);
            _refreshUsage?.Invoke();
        }

        private void PushBar(double v)
        {
            for (int i = 0; i < _hist.Length - 1; i++) _hist[i] = _hist[i + 1];
            _hist[_hist.Length - 1] = v;
        }
    }
}
