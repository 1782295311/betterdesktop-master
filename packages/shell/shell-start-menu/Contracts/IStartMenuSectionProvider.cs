using System.Windows;
using BetterDesktop.Shell.StartMenu.Services;

namespace BetterDesktop.Shell.StartMenu.Contracts;

/// <summary>
/// 开始菜单栏目扩展点：为菜单右栏提供自定义栏目（如最近程序、位置、电源）。
/// 由 StartMenuPlugin 注册；经典布局右栏按注册顺序渲染。
/// </summary>
public interface IStartMenuSectionProvider
{
    /// <summary>栏目唯一名。</summary>
    string Name { get; }

    /// <summary>构建栏目 UI（可使用 service 的数据聚合方法）。</summary>
    FrameworkElement BuildSection(StartMenuService service);
}
