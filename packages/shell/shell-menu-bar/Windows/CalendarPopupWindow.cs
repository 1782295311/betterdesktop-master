// BetterDesktop.Shell.MenuBar — 日历独立弹出面板（月视图，失焦关闭）
//
// 数据全部来自 ICalendarService（shell-calendar 包）与系统日期计算，零硬编码日期：
//   - 月视图：DateTime.DaysInMonth 动态算当月天数；每格 = 公历日 + 一行信息（休/班/节日/节气/农历）
//   - 信息来源（全部可插拔，新增数据源 UI 不用改）：
//       法定节假日与调休（年表 JSON）· 24 节气（天文算法）· 传统节日（规则推算）
//       · 系统日程（WinRT，未授权则隐藏）· 天气（无数据源则隐藏）· 便签（**接口预留**）
//   - 格子底部小圆点：有日程 / 有便签 / 有天气
//   - 点某天 → CalendarDayPopupWindow（日详情：该日全部条目分组列出）
// 服务缺失时降级为"纯农历月视图"（M10），绝不显示编造数据。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterDesktop.Shell.Calendar.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Contracts;

namespace BetterDesktop.Shell.MenuBar.Windows;

internal sealed class CalendarPopupWindow : MenuBarPopupWindow
{
    private const double DefaultWidth = 340;
    private const double RowHeight = 46;

    private readonly ICalendarService? _calendar;
    private readonly ChineseLunisolarCalendar _lunisolar = new();
    private DateTime _displayMonth; // 当次显示的月份（Day=1）
    private Grid? _dayGrid;
    private TextBlock? _headerText;
    private CalendarDayPopupWindow? _dayPopup;

    public CalendarPopupWindow(
        ICalendarService? calendar,
        IVibrancyService vibrancy,
        IAppearanceService? appearance = null)
        : base(vibrancy, appearance)
    {
        _calendar = calendar;
        Width = DefaultWidth;
        MinWidth = DefaultWidth;
        SizeToContent = SizeToContent.Height;
        _displayMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);

        if (_calendar is not null)
        {
            _calendar.EntriesChanged += OnCalendarEntriesChanged;
        }
    }

    /// <summary>Playground/大容器 预览入口：直接取内容 UI（不走 ShellWindow 生命周期）。</summary>
    public FrameworkElement BuildPreviewContent() => BuildContent();

    protected override FrameworkElement BuildContent()
    {
        var root = new Border
        {
            Padding = new Thickness(14),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        var column = new StackPanel { Orientation = Orientation.Vertical };

        // 顶部：今天日期 + 农历；左右翻月
        _headerText = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };

        var headerRow = new Grid();
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_headerText, 0);
        headerRow.Children.Add(_headerText);
        headerRow.Children.Add(ArrowButton("‹", -1, 1));
        headerRow.Children.Add(ArrowButton("›", 1, 2));
        column.Children.Add(headerRow);

        // 星期表头（本地化缩写）
        column.Children.Add(BuildWeekHeader());

        _dayGrid = new Grid();
        for (var i = 0; i < 7; i++) _dayGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        column.Children.Add(_dayGrid);

        // 图例：让"休/班/圆点"自解释，避免用户猜
        column.Children.Add(BuildLegend());

        Refresh();
        root.Child = column;
        return root;
    }

    private Button ArrowButton(string text, int delta, int columnIndex)
    {
        var button = new Button
        {
            Content = text,
            Width = 22,
            Height = 22,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        button.Click += (_, _) => ChangeMonth(delta);
        Grid.SetColumn(button, columnIndex);
        return button;
    }

    private static FrameworkElement BuildWeekHeader()
    {
        var weekHeader = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        for (var i = 0; i < 7; i++) weekHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var dayNames = CultureInfo.CurrentUICulture.DateTimeFormat.AbbreviatedDayNames;
        for (var i = 0; i < 7; i++)
        {
            var tb = new TextBlock
            {
                Text = dayNames[i] ?? i.ToString(),
                FontSize = 11,
                TextAlignment = TextAlignment.Center
            };
            SetThemeBinding(tb, TextBlock.ForegroundProperty, "ThemeMutedForeground");
            Grid.SetColumn(tb, i);
            weekHeader.Children.Add(tb);
        }
        return weekHeader;
    }

    private FrameworkElement BuildLegend()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0)
        };
        void Add(string text, bool accent)
        {
            var tb = new TextBlock { Text = text, FontSize = 10, Margin = new Thickness(0, 0, 10, 0) };
            SetThemeBinding(tb, TextBlock.ForegroundProperty, accent ? "AccentBrush" : "ThemeMutedForeground");
            panel.Children.Add(tb);
        }
        Add("休=放假", true);
        Add("班=调休补班", false);
        Add("● 日程/天气/便签", false);
        return panel;
    }

    private void ChangeMonth(int deltaMonths)
    {
        try
        {
            _displayMonth = _displayMonth.AddMonths(deltaMonths);
        }
        catch
        {
            return; // 超出 DateTime 范围忽略
        }
        Refresh();
    }

    private void Refresh()
    {
        if (_headerText is null || _dayGrid is null)
        {
            return;
        }

        var today = DateTime.Today;
        var ymd = _displayMonth;

        // 顶部：今天 + 农历（服务可用时走服务，缺服务降级到本地算法）
        var todayDate = DateOnly.FromDateTime(today);
        var todayInfo = _calendar?.GetDayInfo(todayDate);
        var todayLunar = todayInfo is not null
            ? $"农历{todayInfo.LunarMonthText}{todayInfo.LunarDayText}"
            : FormatLunarFullLocal(today);
        _headerText.Text = $"{today:yyyy年M月d日} {GetDayNameLocalized(today.DayOfWeek)} {todayLunar}";

        var first = new DateTime(ymd.Year, ymd.Month, 1);
        var daysInMonth = DateTime.DaysInMonth(ymd.Year, ymd.Month);
        var offset = (int)first.DayOfWeek;

        // 一次取整月条目（区间查询，禁止逐日跨层调用）
        var rangeFrom = DateOnly.FromDateTime(first);
        var rangeTo = DateOnly.FromDateTime(new DateTime(ymd.Year, ymd.Month, daysInMonth));
        var byDate = new Dictionary<DateOnly, List<CalendarEntry>>();
        if (_calendar is not null)
        {
            try
            {
                foreach (var entry in _calendar.GetEntries(rangeFrom, rangeTo))
                {
                    if (!byDate.TryGetValue(entry.Date, out var list))
                    {
                        list = new List<CalendarEntry>();
                        byDate[entry.Date] = list;
                    }
                    list.Add(entry);
                }
            }
            catch
            {
                // 聚合失败：降级为无条目月视图（M10）
            }
        }

        _dayGrid.Children.Clear();
        _dayGrid.RowDefinitions.Clear();
        var totalCells = offset + daysInMonth;
        var totalRows = (int)Math.Ceiling(totalCells / 7.0);
        for (var r = 0; r < totalRows; r++) _dayGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RowHeight) });

        var idx = 0;
        for (var i = 0; i < offset; i++, idx++)
        {
            var place = new Border { Width = 42, Height = 42 };
            Grid.SetColumn(place, idx % 7);
            Grid.SetRow(place, idx / 7);
            _dayGrid.Children.Add(place);
        }

        for (var day = 1; day <= daysInMonth; day++, idx++)
        {
            var cellDate = new DateTime(ymd.Year, ymd.Month, day);
            var dateOnly = DateOnly.FromDateTime(cellDate);
            var info = _calendar?.GetDayInfo(dateOnly);
            var entries = byDate.TryGetValue(dateOnly, out var list) ? list : new List<CalendarEntry>();
            var cell = CreateDayCell(day, cellDate == today, info, entries);
            Grid.SetColumn(cell, idx % 7);
            Grid.SetRow(cell, idx / 7);
            _dayGrid.Children.Add(cell);
        }
    }

    /// <summary>单个日期格：公历数字 + 一行信息（休/班/节日/节气/农历）+ 底部来源圆点。</summary>
    private FrameworkElement CreateDayCell(int day, bool isToday, CalendarDayInfo? info, IReadOnlyList<CalendarEntry> entries)
    {
        var date = DateOnly.FromDateTime(new DateTime(_displayMonth.Year, _displayMonth.Month, day));

        var outer = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = isToday ? new CornerRadius(21) : new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        if (isToday)
        {
            SetThemeBinding(outer, Border.BackgroundProperty, "AccentBrush");
        }

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // 1) 公历数字
        var dayText = new TextBlock
        {
            Text = day.ToString(),
            FontSize = 13,
            FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Regular,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        if (isToday)
        {
            dayText.Foreground = MenuBarTheme.Foreground;
        }
        else if (info is not null && info.IsDayOff)
        {
            SetThemeBinding(dayText, TextBlock.ForegroundProperty, "AccentBrush"); // 放假：强调色
        }
        else if (info is not null && (info.IsWeekend || info.IsMakeUpWorkday))
        {
            SetThemeBinding(dayText, TextBlock.ForegroundProperty, "ThemeMutedForeground"); // 周末/补班：弱化
        }
        stack.Children.Add(dayText);

        // 2) 一行信息：休 > 班 > 节日 > 节气 > 农历日
        var label = ResolveLabel(day, info, entries);
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 9,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        if (isToday)
        {
            labelText.Foreground = MenuBarTheme.Foreground;
        }
        else if (label is "休" or "班")
        {
            SetThemeBinding(labelText, TextBlock.ForegroundProperty, "AccentBrush");
        }
        else
        {
            SetThemeBinding(labelText, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        }
        stack.Children.Add(labelText);

        // 3) 来源圆点：日程 / 天气 / 便签
        var dots = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 1, 0, 0)
        };
        if (entries.Any(e => e.Kind == CalendarEntryKind.Event)) dots.Children.Add(Dot("AccentBrush"));
        if (entries.Any(e => e.Kind == CalendarEntryKind.Weather)) dots.Children.Add(Dot("ThemeMutedForeground"));
        if (entries.Any(e => e.Kind == CalendarEntryKind.Note)) dots.Children.Add(Dot("AccentBrush"));
        stack.Children.Add(dots);

        outer.Child = stack;
        outer.MouseLeftButtonUp += (_, _) => ShowDay(date);
        return outer;
    }

    private FrameworkElement Dot(string brushToken)
    {
        var dot = new Border
        {
            Width = 3,
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            Margin = new Thickness(1, 0, 1, 0)
        };
        SetThemeBinding(dot, Border.BackgroundProperty, brushToken);
        return dot;
    }

    /// <summary>格子第二行的文案优先级：休/班 → 节日 → 节气 → 农历日。</summary>
    private string ResolveLabel(int day, CalendarDayInfo? info, IReadOnlyList<CalendarEntry> entries)
    {
        if (info is not null)
        {
            if (info.IsDayOff) return "休";
            if (info.IsMakeUpWorkday) return "班";
        }

        var festival = entries.FirstOrDefault(e => e.Kind == CalendarEntryKind.Festival);
        if (festival is not null) return Shorten(festival.Title, 3);

        var term = entries.FirstOrDefault(e => e.Kind == CalendarEntryKind.SolarTerm);
        if (term is not null) return Shorten(term.Title, 3);

        // 服务可用走服务（含闰月等完整信息），缺失时退回本地农历算法
        return info is not null
            ? info.LunarDayText
            : FormatLunarShortLocal(new DateTime(_displayMonth.Year, _displayMonth.Month, day));
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>点某天 → 日详情面板（贴着日历右侧展开，越界回拉）。</summary>
    private void ShowDay(DateOnly date)
    {
        if (_calendar is null)
        {
            return;
        }

        try
        {
            var entries = _calendar.GetEntries(date);
            var info = _calendar.GetDayInfo(date);
            _dayPopup?.Close();
            _dayPopup = new CalendarDayPopupWindow(date, entries, info, VibrancyService!, AppearanceService);

            var area = MenuBarScreen.GetWorkArea(new Point(Left, Top));
            var x = Math.Clamp(Left + Width + 6, area.Left, Math.Max(area.Left, area.Right - _dayPopup.Width));
            var y = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - 320));
            _dayPopup.ShowAt(new Point(x, y));
        }
        catch
        {
            // 日详情打开失败：静默（M10）
        }
    }

    private string FormatLunarShortLocal(DateTime dt)
    {
        try
        {
            return ToChineseDayNumber(_lunisolar.GetDayOfMonth(dt));
        }
        catch
        {
            return string.Empty;
        }
    }

    private string FormatLunarFullLocal(DateTime dt)
    {
        try
        {
            var year = _lunisolar.GetSexagenaryYear(dt);
            return $"{ToGanZhi(year)}年 {FormatLunarShortLocal(dt)}";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetDayNameLocalized(DayOfWeek dow)
    {
        try { return CultureInfo.CurrentUICulture.DateTimeFormat.GetDayName(dow); }
        catch { return dow.ToString(); }
    }

    private void OnCalendarEntriesChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(Refresh), System.Windows.Threading.DispatcherPriority.Background);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_calendar is not null)
        {
            _calendar.EntriesChanged -= OnCalendarEntriesChanged;
        }
        _dayPopup?.Close();
        _dayPopup = null;
        base.OnClosed(e);
    }

    // ---- 降级路径（无 ICalendarService 时用本地农历算法） ----
    private static readonly string[] ChineseNumbers = { "一", "二", "三", "四", "五", "六", "七", "八", "九", "十" };
    private static readonly string[] Tiangan = { "甲", "乙", "丙", "丁", "戊", "己", "庚", "辛", "壬", "癸" };
    private static readonly string[] Dizhi = { "子", "丑", "寅", "卯", "辰", "巳", "午", "未", "申", "酉", "戌", "亥" };

    private static string ToChineseDayNumber(int d)
    {
        if (d == 10) return "初十";
        if (d == 20) return "二十";
        if (d == 30) return "三十";
        if (d < 10) return $"初{ChineseNumbers[d - 1]}";
        if (d < 20) return $"十{ChineseNumbers[d - 10 - 1]}";
        if (d < 30) return $"廿{ChineseNumbers[d - 20 - 1]}";
        return d.ToString();
    }

    private static string ToGanZhi(int sexagenaryIndex)
    {
        var i = ((sexagenaryIndex - 1) % 60 + 60) % 60;
        return $"{Tiangan[i % 10]}{Dizhi[i % 12]}";
    }
}
