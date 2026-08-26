using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterDesktop.Shell.PluginSdk;
using BetterDesktop.Shell.Core.Vibrancy;

namespace BetterDesktop.Shell.Core.Surface;

/// <summary>
/// 插件窗口宿主：把第三方插件内容"套进"统一外壳。
/// 继承 <see cref="ShellWindow"/>，对基类而言与 Dock/Settings 等内置窗口完全一致——
/// 三步仪式（注入双服务 + 赋值 ChromeBorder）由本宿主自动完成，插件零感知。
/// 对插件而言，只能看到 <see cref="IShellPluginWindow"/> 契约与只读 <see cref="ThemeSnapshot"/>，
/// 拿不到 <c>IAppearanceService</c>/<c>IVibrancyService</c>，也无法修改外壳（背景/描边/圆角）。
/// </summary>
public sealed class PluginHostWindow : ShellWindow
{
    private readonly IShellPluginWindow _pluginWindow;
    private readonly string _pluginId;

    /// <summary>插件内容容器：外壳（ChromeBorder）完全由基类管控，插件只能操作此内容区。</summary>
    private readonly ContentPresenter _contentPresenter = new();

    public PluginHostWindow(IShellPluginWindow pluginWindow, string pluginId,
        IAppearanceService? appearance = null, IVibrancyService? vibrancy = null)
        : base(appearance, vibrancy) // 步骤①②：基类构造注入双服务并应用窗口行为虚属性（经 CanSetProperty 校验）
    {
        _pluginWindow = pluginWindow;
        _pluginId = pluginId;

        // 步骤③：内核自动创建根 ChromeBorder，插件内容注入内部
        var chromeBorder = new Border
        {
            BorderThickness = new Thickness(1), // 预留描边，与内置窗口对齐
            Child = _contentPresenter
        };
        ChromeBorder = chromeBorder;
        Content = chromeBorder;

        // 插件内容：只进内容区，不碰外壳
        _contentPresenter.Content = pluginWindow.Content;
        _contentPresenter.Margin = new Thickness(1);

        // 白名单配置：权限校验后生效
        ApplyPluginConfig(pluginWindow.Config);

        // 订阅外观变更：把皮肤/主题变化以只读 ThemeSnapshot 抛给插件（路径 B 自定义绘制协调）。
        // 根背景/描边/圆角已由基类 ShellWindow 自动处理（路径 A），此处仅补"插件自定义绘制"的快照通知。
        if (AppearanceService is not null)
        {
            AppearanceService.Changed += OnAppearanceChangedForPlugin;
        }
    }

    private void OnAppearanceChangedForPlugin(object? sender, AppearanceChangedArgs e)
    {
        if (AppearanceService is null) return;
        // 仅当对插件可见的维度变化时才抛（避免无关维度打扰）；皮肤/模式/强调/描边/字号均影响自定义绘制。
        if (!e.SkinChanged && !e.ThemeModeChanged && !e.AccentChanged && !e.BorderChanged && !e.FontScaleChanged)
        {
            return;
        }
        var snapshot = new ThemeSnapshot
        {
            IsDarkMode = AppearanceService.Mode != ThemeMode.Light,
            FontScale = AppearanceService.FontScale,
            ForegroundColor = AppearanceService.ForegroundColor,
            CardBorderColor = AppearanceService.CardBorderColor,
            AccentColor = AppearanceService.Accent,
            SkinIsActive = AppearanceService.SkinKind != SkinKind.None,
            SkinBackgroundBrush = AppearanceService.BackgroundBrush,
            SkinAccentColor = AppearanceService.Accent
        };
        try { _pluginWindow.OnThemeChanged(snapshot); }
        catch { /* 插件处理异常不阻断内核 */ }
    }

    /// <summary>当前插件 ID（权限校验与日志用）。</summary>
    public string PluginId => _pluginId;

    /// <summary>
    /// 插件宿主属性白名单校验：内核默认放行窗口基础样式，但"置顶"仅当插件声明且权限服务允许。
    /// 实际权限来源由宿主注入方通过 <see cref="PermissionService"/> 配置（默认放行声明值）。
    /// </summary>
    protected override bool CanSetProperty(string propertyName)
    {
        // 置顶是高权限能力：声明 + 权限服务裁决双通过才放行。
        return propertyName switch
        {
            "Topmost" => _pluginWindow.Config.AllowTopmost && PermissionService.AllowTopmost(_pluginId),
            _ => base.CanSetProperty(propertyName)
        };
    }

    /// <summary>
    /// ChromeBorder 就绪：注入内核主题资源字典，插件 XAML 可直接 DynamicResource 引用令牌
    /// （ThemeForeground/CardBorderBrush 等），字号/前景色经基类附加属性继承自动传导。
    /// </summary>
    protected override void OnChromeBorderReady()
    {
        base.OnChromeBorderReady();
        // 合并全局主题字典（由 ThemeResourceProvider 维护，AppearanceService 推送时同步更新）。
        if (ThemeResourceProvider.GlobalDictionary is { } dict && !Resources.MergedDictionaries.Contains(dict))
        {
            Resources.MergedDictionaries.Add(dict);
        }
    }

    /// <summary>
    /// 卸载清理：解绑基类外观订阅 + 清空插件内容引用，允许插件程序集卸载。
    /// </summary>
    public void DetachPlugin()
    {
        base.DetachWindow();
        _contentPresenter.Content = null;
        if (ChromeBorder is not null) ChromeBorder.Child = null;
    }

    private void ApplyPluginConfig(PluginWindowConfig config)
    {
        Width = config.DefaultWidth;
        Height = config.DefaultHeight;
        ShowInTaskbar = CanSetProperty("ShowInTaskbar") && config.ShowInTaskbar;
        if (CanSetProperty("ResizeMode")) ResizeMode = config.AllowResize;
        if (CanSetProperty("Topmost")) Topmost = config.AllowTopmost && PermissionService.AllowTopmost(_pluginId);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }
}
