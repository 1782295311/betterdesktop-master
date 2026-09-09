using BetterDesktop.Shell.ContextMenus.Contracts;

namespace BetterDesktop.Shell.Convert.Contracts;

/// <summary>
/// 转换菜单服务（2026-09-07 恢复挂点）：桌面右键 / 系统右键（--menu-cmd convert）共用的
/// 转换菜单构建 + 执行闭环。菜单"要全"：矩阵全部目标都列出，引擎缺失的目标置灰（不隐藏），
/// 点击不可用项不执行。执行完成以 MessageBox 反馈（成功/失败计数）。
/// </summary>
public interface IConvertMenuService
{
    /// <summary>
    /// 构建转换菜单项（含 PDF 快捷项 + 「转换为 ▸」全部目标子菜单）。
    /// <paramref name="paths"/>：单选 = [文件]；多选 = 全部选中路径。
    /// 多选全 PDF → 仅「合并 PDF」；多选全图片 → 「合成 PDF」+ 图片互转子菜单；混合类型 → 空。
    /// </summary>
    IReadOnlyList<MenuItemDef> BuildMenuItems(IReadOnlyList<string> paths);
}
