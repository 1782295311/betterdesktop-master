using System.Windows;

namespace BetterDesktop.Shell.Settings.Contracts;

/// <summary>
/// 设置分区契约：每个插件贡献一个设置分区（侧栏一项 + 内容区 UI）。
/// 通过 <see cref="ISettingsSectionRegistry.Register"/> 注册，设置窗口自动收集展示。
/// </summary>
public interface ISettingsSection
{
    /// <summary>分区标题（侧栏显示，如"通用"/"外观"/"任务栏"）。</summary>
    string Title { get; }

    /// <summary>分区图标键（可选，主题令牌未就绪前可为 null）。</summary>
    string? IconKey { get; }

    /// <summary>
    /// 构建分区内容 UI。返回的控件会被设置窗口缓存复用；
    /// 组件内通过 <paramref name="settings"/> 读写，通过 <paramref name="tokens"/> 取主题色（避免硬编码）。
    /// </summary>
    UIElement Build(ISettingsService settings, IThemeTokens tokens);
}
