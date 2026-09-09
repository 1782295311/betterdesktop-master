// BetterDesktop.Shell.StartMenu — IStartMenuService 契约
// 开始菜单能力服务：自绘 WPF 菜单（StartMenuService）为唯一后端。
// Show/Hide/Toggle/IsOpen 面向自绘窗口；Enable/Disable 挂/卸 Win 键钩子。

using System;

namespace BetterDesktop.Shell.StartMenu.Contracts;

/// <summary>开始菜单能力服务（自绘 WPF 单一后端）。</summary>
public interface IStartMenuService
{
    /// <summary>Win 键钩子是否已启用（接管 Win 键）。</summary>
    bool IsActive { get; }

    /// <summary>自绘菜单是否打开。</summary>
    bool IsOpen { get; }

    /// <summary>启用自绘后端（挂 Win 键钩子）。</summary>
    bool Enable();

    /// <summary>停用自绘后端（卸 Win 键钩子并关闭菜单）。</summary>
    bool Disable();

    /// <summary>切换菜单开/关。</summary>
    bool ToggleMenu();

    /// <summary>打开菜单。</summary>
    void Show();

    /// <summary>关闭菜单。</summary>
    void Hide();

    /// <summary>切换菜单开/关。</summary>
    void Toggle();

    /// <summary>菜单打开状态变化。</summary>
    event EventHandler? OpenStateChanged;
}
