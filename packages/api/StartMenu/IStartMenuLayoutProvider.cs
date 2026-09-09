using System.Windows;


namespace BetterDesktop.Shell.StartMenu.Contracts;

/// <summary>
/// 开始菜单布局扩展点：为菜单主体提供整体布局（默认 ClassicLayout 经典两栏）。
/// 由 StartMenuPlugin 注册；StartMenuWindow 使用活动布局构建 Content。
/// </summary>
public interface IStartMenuLayoutProvider
{
    /// <summary>布局唯一名。</summary>
    string Name { get; }

    /// <summary>构建布局 UI（可使用 service 的数据聚合方法）。</summary>
    FrameworkElement BuildLayout(IStartMenuDataService service);
}
