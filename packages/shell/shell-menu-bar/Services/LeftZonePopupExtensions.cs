// BetterDesktop.Shell.MenuBar — 左区弹窗提供者扩展（2026-09-07 P0-3/C3）
// LogoMenu（快捷功能菜单）与 StacksPopup（位置/下载/文档全宽条带）原由 MenuBarLeftZone 直接 new，
// 现按 IMenuBarExtension 契约注册进 IMenuBarExtensionRegistry：
//   - GetVisual() 返回 null → 非按钮扩展，不参与右区装配（MenuBarWindow 装配已跳过 null）
//   - GetOrCreate() 供 MenuBarLeftZone 取窗口实例（保留动画/事件/定位的既有联动）
//   - OpenPopup/ClosePopup 实现契约（其他消费方可直接调起）
// 711 纪律：弹窗懒创建；创建/显示失败由调用方容错（本扩展不吞异常）。

using System.Windows;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Core.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Contracts;
using BetterDesktop.Shell.MenuBar.Windows;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.MenuBar.Services;

/// <summary>Logo 快捷功能菜单弹窗提供者（Id = "logo-menu"，非按钮扩展）。</summary>
internal sealed class LogoMenuBarExtension : IMenuBarExtension
{
    private readonly ISettingsWindowService? _settingsWindow;
    private readonly IVibrancyService _vibrancy;
    private readonly IAppearanceService? _appearance;
    private readonly IEventBus? _events;
    private LogoMenuWindow? _window;

    public LogoMenuBarExtension(
        ISettingsWindowService? settingsWindow,
        IVibrancyService vibrancy,
        IAppearanceService? appearance,
        IEventBus? events)
    {
        _settingsWindow = settingsWindow;
        _vibrancy = vibrancy;
        _appearance = appearance;
        _events = events;
    }

    public string Id => "logo-menu";

    /// <summary>非按钮扩展：不占右区。</summary>
    public FrameworkElement? GetVisual() => null;

    /// <summary>懒创建并返回菜单窗口实例（MenuBarLeftZone 联动动画/事件时使用）。</summary>
    public LogoMenuWindow GetOrCreate()
        => _window ??= new LogoMenuWindow(_settingsWindow, _vibrancy, _appearance, _events);

    /// <summary>打开菜单（契约入口；左区走 GetOrCreate + 自身定位）。</summary>
    public void OpenPopup(Point anchorScreenTopLeft)
    {
        var window = GetOrCreate();
        window.Left = anchorScreenTopLeft.X;
        window.Top = anchorScreenTopLeft.Y;
        window.Show();
    }

    /// <summary>收起菜单。</summary>
    public void ClosePopup() => _window?.Hide();
}

/// <summary>Stacks 全宽条带面板提供者（Id = "stacks-popup"，非按钮扩展）。</summary>
internal sealed class StacksPopupBarExtension : IMenuBarExtension
{
    private readonly IVibrancyService _vibrancy;
    private readonly IAppearanceService? _appearance;
    private readonly ISettingsService? _settings;
    private StacksPopupWindow? _window;

    public StacksPopupBarExtension(IVibrancyService vibrancy, IAppearanceService? appearance, ISettingsService? settings)
    {
        _vibrancy = vibrancy;
        _appearance = appearance;
        _settings = settings;
    }

    public string Id => "stacks-popup";

    /// <summary>非按钮扩展：不占右区。</summary>
    public FrameworkElement? GetVisual() => null;

    /// <summary>懒创建并返回条带面板实例（MenuBarLeftZone 打开位置/下载/文档时使用）。</summary>
    public StacksPopupWindow GetOrCreate()
        => _window ??= new StacksPopupWindow(_vibrancy, _appearance, _settings);

    /// <summary>打开条带（契约入口；左区走 GetOrCreate + Open/OpenPlaces 定位）。</summary>
    public void OpenPopup(Point anchorScreenTopLeft)
    {
        var window = GetOrCreate();
        window.Left = anchorScreenTopLeft.X;
        window.Top = anchorScreenTopLeft.Y;
        window.Show();
    }

    /// <summary>收起条带。</summary>
    public void ClosePopup() => _window?.Hide();
}
