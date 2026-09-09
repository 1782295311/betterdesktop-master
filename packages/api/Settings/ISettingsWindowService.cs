namespace BetterDesktop.Shell.Settings.Contracts;

/// <summary>
/// 设置窗口服务：单实例打开/激活设置窗口，可选定位到指定分区。
/// 供 menu-bar / context-menu / Dock 的入口（如"设置"菜单项）调用。
/// </summary>
public interface ISettingsWindowService
{
    /// <summary>打开设置窗口（已开则激活到前台）。</summary>
    void Show();

    /// <summary>打开设置窗口并定位到指定分区标题。</summary>
    void ShowSection(string sectionTitle);
}
