using System.Windows;

namespace BetterDesktop.Shell.Core.Surface;

/// <summary>
/// 全局主题资源字典提供者：供插件宿主（<see cref="PluginHostWindow"/>）合并的共享资源字典。
/// <c>AppearanceService.SyncAppResources</c> 在推送 <c>Application.Current.Resources</c> 的同时
/// 同步写入本字典，保证插件 XAML 的 DynamicResource 令牌（ThemeForeground/CardBorderBrush/
/// CardShadowEffect 等）与内置窗口一致、随主题一键刷新。
/// </summary>
public static class ThemeResourceProvider
{
    /// <summary>全局主题字典（App 启动后由内核注册；推送方同步写入）。</summary>
    public static ResourceDictionary? GlobalDictionary { get; private set; }

    /// <summary>注册全局字典（仅调用一次；App 启动时创建并注册）。</summary>
    public static void Register(ResourceDictionary dictionary)
    {
        GlobalDictionary = dictionary;
    }

    /// <summary>确保全局字典存在（惰性创建，供推送方写入）。</summary>
    public static ResourceDictionary Ensure()
    {
        if (GlobalDictionary is not null)
        {
            return GlobalDictionary;
        }

        var dict = new ResourceDictionary();
        GlobalDictionary = dict;
        return dict;
    }
}
