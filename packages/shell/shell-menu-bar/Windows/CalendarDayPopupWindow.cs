// BetterDesktop.Shell.MenuBar — 日历「日详情」面板。
// 点月视图某一天弹出：按来源分组列出该日全部信息（放假/补班、节气与节日、日程、天气、便签）。
// 便签当前为**预留占位**（数据接口 ICalendarEntryProvider 已就位，未来的 quick-note 实现后自动出现真实条目）。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterDesktop.Shell.Calendar.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;

namespace BetterDesktop.Shell.MenuBar.Windows;

internal sealed class CalendarDayPopupWindow : MenuBarPopupWindow
{
    private const double PanelWidth = 300;

    private readonly DateOnly _date;
    private readonly IReadOnlyList<CalendarEntry> _entries;
    private readonly CalendarDayInfo? _info;

    public CalendarDayPopupWindow(
        DateOnly date,
        IReadOnlyList<CalendarEntry> entries,
        CalendarDayInfo? info,
        IVibrancyService vibrancy,
        IAppearanceService? appearance = null)
        : base(vibrancy, appearance)
    {
        _date = date;
        _entries = entries;
        _info = info;
        Width = PanelWidth;
        MinWidth = PanelWidth;
        SizeToContent = SizeToContent.Height;
    }

    protected override FrameworkElement BuildContent()
    {
        var root = new Border { Padding = new Thickness(14) };
        var column = new StackPanel { Orientation = Orientation.Vertical };

        // 头部：公历 + 农历 + 休/班 状态
        var title = new TextBlock
        {
            Text = $"{_date:yyyy年M月d日} {GetDayNameLocalized(_date.DayOfWeek)}",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        };
        column.Children.Add(title);

        var lunar = _info is null
            ? string.Empty
            : $"农历{_info.LunarMonthText}{_info.LunarDayText} · {_info.GanZhiYearText}年（{_info.ZodiacText}）";
        if (!string.IsNullOrEmpty(lunar))
        {
            var lunarText = new TextBlock { Text = lunar, FontSize = 11, Opacity = 0.85, Margin = new Thickness(0, 2, 0, 0) };
            column.Children.Add(lunarText);
        }

        if (_info is not null)
        {
            var state = _info.IsMakeUpWorkday ? "调休补班（需上班）"
                : _info.IsDayOff ? "放假"
                : _info.IsWeekend ? "周末休息"
                : "工作日";
            var stateText = new TextBlock
            {
                Text = state,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
                FontWeight = FontWeights.Medium
            };
            SetThemeBinding(stateText, TextBlock.ForegroundProperty,
                _info.IsMakeUpWorkday || _info.IsDayOff ? "AccentBrush" : "ThemeMutedForeground");
            column.Children.Add(stateText);
        }

        column.Children.Add(Separator());

        // 分组列表：放假/补班 → 节气与节日 → 日程 → 天气 → 便签
        AddGroup(column, "放假 / 补班", CalendarEntryKind.Holiday, CalendarEntryKind.Workday);
        AddGroup(column, "节气与节日", CalendarEntryKind.SolarTerm, CalendarEntryKind.Festival);
        AddGroup(column, "日程", CalendarEntryKind.Event);
        AddGroup(column, "天气", CalendarEntryKind.Weather);
        AddNoteGroup(column);

        if (_entries.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "这一天没有额外信息。",
                FontSize = 11,
                Opacity = 0.6,
                Margin = new Thickness(0, 4, 0, 0)
            };
            column.Children.Add(empty);
        }

        root.Child = column;
        return root;
    }

    private void AddGroup(StackPanel column, string header, params CalendarEntryKind[] kinds)
    {
        var items = _entries.Where(e => kinds.Contains(e.Kind)).ToList();
        if (items.Count == 0)
        {
            return;
        }

        column.Children.Add(GroupHeader(header));
        foreach (var item in items)
        {
            column.Children.Add(ItemRow(item));
        }
    }

    /// <summary>
    /// 便签分组：**接口预留**。ICalendarEntryProvider 已就位（Kind=Note），
    /// 未来的 quick-note 实现并注册后，这里会自动列出真实便签；当前只显示说明，不编造内容。
    /// </summary>
    private void AddNoteGroup(StackPanel column)
    {
        var notes = _entries.Where(e => e.Kind == CalendarEntryKind.Note).ToList();
        column.Children.Add(GroupHeader("便签"));
        if (notes.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = "便签接口已预留（ICalendarEntryProvider · Kind=Note），接入后这里显示当天便签。",
                FontSize = 10.5,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            };
            column.Children.Add(hint);
            return;
        }

        foreach (var note in notes)
        {
            column.Children.Add(ItemRow(note));
        }
    }

    private static TextBlock GroupHeader(string text) => new()
    {
        Text = text,
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Opacity = 0.75,
        Margin = new Thickness(0, 8, 0, 2)
    };

    private FrameworkElement ItemRow(CalendarEntry entry)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 1, 0, 1) };

        if (entry.Time is { } time)
        {
            row.Children.Add(new TextBlock
            {
                Text = time.ToString("HH:mm"),
                FontSize = 10.5,
                Width = 40,
                Opacity = 0.75,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        var title = new TextBlock
        {
            Text = entry.Title,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        row.Children.Add(title);

        if (!string.IsNullOrWhiteSpace(entry.Subtitle))
        {
            var sub = new TextBlock
            {
                Text = entry.Subtitle,
                FontSize = 10,
                Opacity = 0.7,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(sub);
        }

        return row;
    }

    private FrameworkElement Separator()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 10, 0, 2) };
        SetThemeBinding(line, Border.BackgroundProperty, "ThemeSeparator");
        return line;
    }

    private static string GetDayNameLocalized(DayOfWeek dow)
    {
        try { return System.Globalization.CultureInfo.CurrentUICulture.DateTimeFormat.GetDayName(dow); }
        catch { return dow.ToString(); }
    }
}
