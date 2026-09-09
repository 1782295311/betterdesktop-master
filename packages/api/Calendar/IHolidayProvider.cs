using System;
using System.Collections.Generic;

namespace BetterDesktop.Shell.Calendar.Services;

/// <summary>法定节假日的一天。workday=false=放假；workday=true=调休补班（本该休息却要上班）。</summary>
public sealed record HolidayDay(DateOnly Date, string Name, bool IsWorkday);

/// <summary>
/// 法定节假日数据源。**调休补班无法推算**（国务院每年另行通知），必须来自数据，因此这里留接口：
/// - 现实现：JsonHolidayProvider（实现类在 shell-calendar）（内置随包年表 + 用户本地覆盖文件）；
/// - 未来实现：在线年表 / 企业内部日历，实现本接口替换即可，UI 与聚合层零改动。
/// </summary>
public interface IHolidayProvider
{
    /// <summary>取指定年份的节假日表；无数据返回空列表（不抛）。</summary>
    IReadOnlyList<HolidayDay> GetHolidays(int year);
}
