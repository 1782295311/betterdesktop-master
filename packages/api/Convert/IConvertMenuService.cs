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

    /// <summary>
    /// 系统右键菜单（免宿主原生扩展）用的转换目标快照。
    ///
    /// 与 <see cref="BuildMenuItems"/> 的差异（刻意）：
    ///   · 只含 <c>Lossless == true</c> 且引擎就绪的目标 —— 系统级联/原生菜单无法置灰与弹风险确认，
    ///     出现即承诺"一定能无损转成功"（技术力文档 context-menu-declaration-registry 红线 8）；
    ///   · 不做多选语义分支（合并/合成）—— 那是自绘菜单的交互能力，原生菜单按 SourceExtensions 过滤后用同一批目标；
    ///   · 返回的是「目标 → 可用源扩展名全集」的矩阵，供原生侧按当前选中扩展名过滤。
    /// </summary>
    IReadOnlyList<ConvertSystemMenuTarget> BuildSystemMenuTargets();
}

/// <summary>
/// 系统右键菜单转换目标：引擎就绪 + 无损。
/// <paramref name="SourceExtensions"/> = 能无损转出该目标且引擎就绪的源扩展名全集（含点，小写）。
/// </summary>
public sealed record ConvertSystemMenuTarget(
    string Format,
    string Label,
    IReadOnlyList<string> SourceExtensions);
