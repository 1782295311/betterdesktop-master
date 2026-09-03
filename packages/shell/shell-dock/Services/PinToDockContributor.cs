// BetterDesktop.Shell.Dock — 「固定到 Dock」菜单贡献者（计划 E1；MENU-SPECS §2 规划落空项）
// 依赖方向：shell-dock 已引用 shell-context-menu（与 shell-convert 贡献者同构）。
// 门控：文件夹 / 可执行 / 快捷方式 / .lnk/.url（IDockAppsService.AddByPath 由实现层解析）。
// 已固定判定：按 TargetPath/ShortcutPath 与固定列表比对（Pinned 快照）；反查到即提供取消固定，
// 反查不到只显示固定（计划 E1 降级路径；IPinningService 反查需 AppItemId，此处用路径直比更稳）。

using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.Dock.Models;

namespace BetterDesktop.Shell.Dock.Services;

public sealed class PinToDockContributor(IDockAppsService apps, MenuScope scope) : IContextMenuContributor
{
    public int Priority => -160;

    public MenuScope Scope { get; } = scope;

    public IReadOnlyList<MenuItemDef> Build(MenuRequest request)
    {
        if (request.File is not { } id)
        {
            return [];
        }

        var path = id.Path;
        var pinnable = id.Kind is FileKind.Folder or FileKind.Executable or FileKind.Shortcut
            || id.Kind == FileKind.File && path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            || id.Kind == FileKind.File && path.EndsWith(".url", StringComparison.OrdinalIgnoreCase);
        if (!pinnable)
        {
            return [];
        }

        var pinned = apps.Pinned.FirstOrDefault(d =>
            string.Equals(d.TargetPath, path, StringComparison.OrdinalIgnoreCase)
            || string.Equals(d.ShortcutPath, path, StringComparison.OrdinalIgnoreCase));
        if (pinned is not null)
        {
            return
            [
                new MenuItemDef
                {
                    Id = "dock.unpin", Text = "从 Dock 取消固定", Group = MenuGroup.Contribution,
                    Command = () => apps.RemoveById(pinned.Id),
                }
            ];
        }

        return
        [
            new MenuItemDef
            {
                Id = "dock.pin", Text = "固定到 Dock", Group = MenuGroup.Contribution,
                Command = () => apps.AddByPath(path),
            }
        ];
    }
}
