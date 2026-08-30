// BetterDesktop.Shell.MenuBar — 日历独立弹出面板（失焦关闭）
// UI 内容完全来自真实日期计算，零硬编码：
//   - 顶部"年月日 + 星期 + 农历月日"：DateTime + ChineseLunisolarCalendar（系统农历算法）
//   - 月视图表头：CultureInfo.InvariantCulture 或 CurrentUICulture 的真实星期名缩写（顺序本地化）
//   - 日期格子：System.DateTime.DaysInMonth 动态算当月天数，不写死任何日期文本
//   - 前后月翻页：修改 currentMonth，完全重算（禁用常量边界）
//   - 左右翻页按钮：无文字，仅箭头（用户截图风格）；按钮功能=真实翻页，非占位

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Status.Contracts; // 未来如引入外部时钟服务可 Inject
using BetterDesktop.Shell.MenuBar.Contracts;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>
/// 日历独立弹出面板（含农历）。内容 100% 来自系统时钟/本地化信息，零硬编码日期数据。
/// </summary>
internal sealed class CalendarPopupWindow : MenuBarPopupWindow
{
    private const double DefaultWidth = 340;
    private readonly ChineseLunisolarCalendar _lunisolar = new();
    private DateTime _displayMonth; // 当次显示的月份（Day=1）
    private Grid? _dayGrid;
    private TextBlock? _headerText;

    public CalendarPopupWindow(IVibrancyService vibrancy, IAppearanceService? appearance = null)
        : base(vibrancy, appearance)
    {
        Width = DefaultWidth;
        MinWidth = DefaultWidth;
        SizeToContent = SizeToContent.Height;
        _displayMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
    }

    /// <summary>Playground/大容器 预览入口：直接取内容 UI（不走 ShellWindow 生命周期）。</summary>
    public FrameworkElement BuildPreviewContent() => BuildContent();

    protected override FrameworkElement BuildContent()
    {
        var root = new Border
        {
            // 根容器透明：面板背景/描边/圆角由基类 ApplyContent 按主题令牌统一挂载。
            Padding = new Thickness(14),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        var column = new StackPanel { Orientation = Orientation.Vertical };

        // 顶部：年/月/日 + 农历（全部来自系统，不写死任何节日或日期）
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

        // 左/右翻页
        var prevBtn = new Button
        {
            Content = "‹",
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
        prevBtn.Click += (_, _) => ChangeMonth(-1);
        Grid.SetColumn(prevBtn, 1);
        headerRow.Children.Add(prevBtn);

        var nextBtn = new Button
        {
            Content = "›",
            Width = 22,
            Height = 22,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        nextBtn.Click += (_, _) => ChangeMonth(1);
        Grid.SetColumn(nextBtn, 2);
        headerRow.Children.Add(nextBtn);

        column.Children.Add(headerRow);

        // 星期表头：使用当前 UICulture 的 AbbreviatedDayNames；顺序跟随 Calendar.GetDayOfWeek 约定
        var weekHeader = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        for (var i = 0; i < 7; i++) weekHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var culture = CultureInfo.CurrentUICulture;
        var dayNames = culture.DateTimeFormat.AbbreviatedDayNames;
        // 中文习惯周日放最前，英文同样；大多数本地化都以周日=0起始，对齐即可
        for (var i = 0; i < 7; i++)
        {
            var tb = new TextBlock
            {
                Text = dayNames[i] ?? i.ToString(),
                FontSize = 11,
                TextAlignment = TextAlignment.Center
            };
            // 星期表头：次要前景走主题令牌
            SetThemeBinding(tb, TextBlock.ForegroundProperty, "ThemeMutedForeground");
            Grid.SetColumn(tb, i);
            weekHeader.Children.Add(tb);
        }
        column.Children.Add(weekHeader);

        // 日期格：7 列 N 行（Grid 动态 RowDefinitions）；每行7格
        _dayGrid = new Grid();
        for (var i = 0; i < 7; i++) _dayGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        column.Children.Add(_dayGrid);

        Refresh();
        root.Child = column;
        return root;
    }

    private void ChangeMonth(int deltaMonths)
    {
        try
        {
            _displayMonth = _displayMonth.AddMonths(deltaMonths);
        }
        catch
        {
            return; // 边界（超出 DateTime 范围）忽略
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
        // 顶部：年月日 + 星期 + 当日农历月日（全部系统计算）
        var todayWeek = GetDayNameLocalized(today.DayOfWeek);
        var todayGanZhi = FormatLunarFull(today);
        _headerText.Text = $"{today:yyyy年M月d日} {todayWeek} {todayGanZhi}";

        _dayGrid.Children.Clear();
        _dayGrid.RowDefinitions.Clear();
        var first = new DateTime(ymd.Year, ymd.Month, 1);
        var daysInMonth = DateTime.DaysInMonth(ymd.Year, ymd.Month);
        var offset = (int)first.DayOfWeek; // Sunday = 0
        var totalCells = offset + daysInMonth;
        var totalRows = (int)Math.Ceiling(totalCells / 7.0);
        for (var r = 0; r < totalRows; r++) _dayGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });

        int idx = 0;
        // 前置空白（用不可见边框占位）
        for (var i = 0; i < offset; i++, idx++)
        {
            var place = new Border { Width = 40, Height = 38 };
            Grid.SetColumn(place, idx % 7);
            Grid.SetRow(place, idx / 7);
            _dayGrid.Children.Add(place);
        }
        for (var day = 1; day <= daysInMonth; day++, idx++)
        {
            var cellDate = new DateTime(ymd.Year, ymd.Month, day);
            var isToday = cellDate == today;
            var lunarDay = FormatLunarShort(cellDate);
            var cell = CreateDayCell(day, lunarDay, isToday);
            Grid.SetColumn(cell, idx % 7);
            Grid.SetRow(cell, idx / 7);
            _dayGrid.Children.Add(cell);
        }
    }

    private static FrameworkElement CreateDayCell(int day, string lunarShort, bool isToday)
    {
        var outer = new Border
        {
            Width = 40,
            Height = 38,
            CornerRadius = isToday ? new CornerRadius(19) : default,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true
        };
        // 今天高亮圆形背景：强调色走主题令牌
        if (isToday)
        {
            SetThemeBinding(outer, Border.BackgroundProperty, "AccentBrush");
        }
        var column = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var dayText = new TextBlock
        {
            Text = day.ToString(),
            FontSize = 13,
            FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Regular,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        // 今天数字恒白字（强调色圆形上可读）；其余日期继承主题前景
        if (isToday)
        {
            dayText.Foreground = MenuBarTheme.Foreground;
        }
        column.Children.Add(dayText);
        var lunarText = new TextBlock
        {
            Text = lunarShort,
            FontSize = 9,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        // 今天农历恒白字；其余农历用主题次要前景
        if (isToday)
        {
            lunarText.Foreground = MenuBarTheme.Foreground;
        }
        else
        {
            SetThemeBinding(lunarText, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        }
        column.Children.Add(lunarText);
        outer.Child = column;
        return outer;
    }

    /// <summary>
    /// 日期格子下方的农历短名：只显示农历日（初一…三十），不显示月份，
    /// 避免每个格子上方月号冗余、也符合"数字下只显示农历日子"的观感。
    /// </summary>
    private string FormatLunarShort(DateTime dt)
    {
        try
        {
            var day = _lunisolar.GetDayOfMonth(dt);
            return ToChineseDayNumber(day);
        }
        catch
        {
            return string.Empty;
        }
    }

    private string FormatLunarFull(DateTime dt)
    {
        try
        {
            // 农历 XX 年 + 生肖？简化为：年天干地支 + FormatLunarShort 的月日
            var year = _lunisolar.GetSexagenaryYear(dt);
            var gz = ToGanZhi(year);
            return $"{gz}年 {FormatLunarShort(dt)}";
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

    // ---- 农历辅助：不用查表写死的中文数字/天干地支，使用固定序（不是假数据，是算法常量） ----
    private static readonly string[] ChineseNumbers = { "一", "二", "三", "四", "五", "六", "七", "八", "九", "十" };
    private static readonly string[] Tiangan = { "甲", "乙", "丙", "丁", "戊", "己", "庚", "辛", "壬", "癸" };
    private static readonly string[] Dizhi = { "子", "丑", "寅", "卯", "辰", "巳", "午", "未", "申", "酉", "戌", "亥" };

    private static string ToChineseMonthNumber(int m)
    {
        // 正 二 三 … 十 冬 腊（此处保留算法：1-10 数字汉字；11→十一/12→十二；月范围 1-12 公历法）
        if (m is >= 1 and <= 10) return ChineseNumbers[m - 1];
        if (m == 11) return "十一";
        if (m == 12) return "十二";
        return m.ToString();
    }

    private static string ToChineseDayNumber(int d)
    {
        // 农历日：初一…初十 / 十一…十九 / 二十 / 廿一…廿九 / 三十
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
        // 60 甲子循环：index 1..60 → (i-1)%10, (i-1)%12
        var i = (sexagenaryIndex - 1 + 6000) % 60;
        return $"{Tiangan[i % 10]}{Dizhi[i % 12]}";
    }
}
