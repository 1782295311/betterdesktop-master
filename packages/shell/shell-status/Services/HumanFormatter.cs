// BetterDesktop.Shell.Status — 人话格式化（语义层的排版助手）
// 把字节/秒数等裸值排版成用户能直接读懂的中文/英文短句，供各 monitor 拼装 HumanText。

using System.Globalization;

namespace BetterDesktop.Shell.Status.Services;

/// <summary>通用人话排版工具：字节容量、时长、百分比。</summary>
internal static class HumanFormatter
{
    /// <summary>把字节数排版为可读容量，如 "1.5 GB"、"512 MB"。</summary>
    public static string FormatBytes(ulong bytes)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var value = (double)bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }
        // 字节级整数展示，其余保留一位小数
        var text = unitIndex == 0
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.#", CultureInfo.InvariantCulture);
        return $"{text} {units[unitIndex]}";
    }

    /// <summary>把秒数排版为可读时长（分钟精度），如 "约 1 小时 20 分"、"约 5 分钟"。</summary>
    public static string FormatDuration(ulong seconds)
    {
        var totalMinutes = seconds / 60ul;
        if (totalMinutes < 1)
        {
            return "不到 1 分钟";
        }

        var hours = totalMinutes / 60ul;
        var minutes = totalMinutes % 60ul;
        if (hours > 0 && minutes > 0)
        {
            return $"约 {hours} 小时 {minutes} 分";
        }
        if (hours > 0)
        {
            return $"约 {hours} 小时";
        }
        return $"约 {minutes} 分钟";
    }

    /// <summary>是否接近未知：剩余时间大于等于 0xFFFF0000 视为未知/接电估算无意义。</summary>
    public static bool IsUnknownTime(ulong seconds) => seconds >= 0xFFFF0000ul;

    /// <summary>稳定性辅助：值是否异常（如余额超过 100 或为未知标记）。</summary>
    public static bool IsSentinelByte(byte value, byte sentinel)
        => value == sentinel || value > 100;
}