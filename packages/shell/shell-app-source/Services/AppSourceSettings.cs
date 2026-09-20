using System;
using System.Collections.Generic;
using System.Text.Json;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.AppSource.Services;

/// <summary>
/// 本包的设置键与「设置 → 服务」映射。
/// <para>
/// <b>为什么单独一层</b>：键名与默认值此前会在「设置分区」（写）与「插件」（读）两处各写一遍，
/// 一处改字面量另一处不改就是静默失效。这里让键名、解析、应用只有一份。
/// </para>
/// <para>
/// 依赖说明：契约接口（<see cref="ISettingsService"/>）定义在 <c>packages/api</c>，
/// 本包已引用 api，故**无需引用 shell-settings** 即可读写设置
/// （实现实例由 Bootstrap 在插件加载前 Provide）。
/// </para>
/// </summary>
internal static class AppSourceSettings
{
    /// <summary>应用扫描目录（口袋目录）：JSON 字符串数组——<c>ISettingsService</c> 只支持基本类型。</summary>
    public const string ExtraRootsKey = "app-source.extra-roots";

    /// <summary>桌面快捷方式是否纳入干净模式（默认**纳入**，§12 Q3 裁决：桌面纳入、下载不纳入）。</summary>
    public const string ScanDesktopShortcutsKey = "app-source.scan-desktop-shortcuts";

    /// <summary>解析目录列表（坏数据 → 空列表 + Warn，不静默）。</summary>
    public static string[] ParseRoots(string? json, IKernelLogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            var roots = JsonSerializer.Deserialize<string[]>(json);
            return roots ?? Array.Empty<string>();
        }
        catch (JsonException e)
        {
            logger?.Warn($"[app-source] 口袋目录设置解析失败（{e.Message}）→ 本次按空处理：{json}");
            return Array.Empty<string>();
        }
    }

    /// <summary>序列化目录列表（去空项，保留用户顺序）。</summary>
    public static string SerializeRoots(IEnumerable<string> roots)
    {
        var list = new List<string>();
        foreach (var root in roots)
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                list.Add(root.Trim());
            }
        }

        return JsonSerializer.Serialize(list);
    }

    /// <summary>把设置映射进服务（键名与默认值只在本类出现）。</summary>
    public static void Apply(ISettingsService? settings, AppSourceService service, IKernelLogger? logger = null)
    {
        if (settings is null)
        {
            return;
        }

        var roots = ParseRoots(settings.Get<string>(ExtraRootsKey), logger);
        var desktopShortcuts = settings.Get(ScanDesktopShortcutsKey, true);

        service.SetExtraScanRoots(roots);
        service.SetDesktopShortcutsEnabled(desktopShortcuts);

        logger?.Info(
            $"[app-source] 设置已应用：口袋目录 {roots.Length} 个、桌面快捷方式{(desktopShortcuts ? "纳入" : "不纳入")}");
    }
}
