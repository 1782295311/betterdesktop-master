using System.Collections.Generic;
using BetterDesktop.Shell.AppSource.Models;

namespace BetterDesktop.Shell.StartMenu.Windows.Layouts;

/// <summary>
/// 图标文件夹模型（规格长尾功能「拖拽/图标文件夹」）：
/// 固定磁贴区由若干 <see cref="AppItem"/> 与 <see cref="PinnedFolder"/> 组成；
/// 文件夹收纳多个应用磁贴，点击钻入查看其成员，可继续作为落下目标收纳新应用。
/// 生命周期仅存在于内存中的磁贴编排（本地滑动布局），由 <c>_pinnedItems</c> 持有；
/// 不含持久化层（布局重建时由固定应用快照重新填充）。
/// </summary>
internal sealed class PinnedFolder
{
    /// <summary>文件夹在磁贴区显示的标题。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>收纳的应用集合。</summary>
    public List<AppItem> Items { get; } = new();
}
