// BetterDesktop.Shell.ContextMenus — ShellEx 扩展菜单只读预览（2026-09-05，v4「欺骗式预览」）
// 原理：伪装资源管理器——后台线程实例化扩展 COM 处理器（CLSID），调用其 IContextMenu::QueryContextMenu，
// 让扩展主动吐出菜单内容，递归枚举 HMENU 树（完整复用 ShellMenuInterop：IShellExtInit 初始化 /
// WM_INITMENUPOPUP 懒填充泵 / owner-draw 文本回退链 / STA 常驻线程 / COM 引用释放 全部已具备）。
// 【红线】只读：绝不调用 ShellVerbItem.Invoke（不触发任何动作）；后台线程 + 5s 超时 + 异常隔离；
//         预览失败返回降级文案，不假装有数据。
// 【架构上限】ShellEx 小功能由扩展每次动态构建，注册表无按项概念——预览可看、整体可开关，
//             但无法按项禁用（Windows 无此机制）；UI 不得为预览项伪造开关。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Shell.ContextMenus.Services;

public sealed class ShellExMenuPreview : IShellExMenuPreview
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async Task<IReadOnlyList<MenuPreviewItem>> QueryAsync(string clsid, string sceneKey, CancellationToken ct)
    {
        try
        {
            var work = Task.Run(() => QueryCore(clsid, sceneKey), ct);
            var done = await Task.WhenAny(work, Task.Delay(Timeout, ct)).ConfigureAwait(true);
            if (done != work)
            {
                return [new MenuPreviewItem("预览超时：该扩展未在 5 秒内响应", 0, false)];
            }
            return await work.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return [new MenuPreviewItem("预览已取消", 0, false)];
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("menu-manager", $"预览失败 clsid={clsid}: {ex.Message}");
            return [new MenuPreviewItem("无法预览：该扩展不响应模拟查询", 0, false)];
        }
    }

    private static IReadOnlyList<MenuPreviewItem> QueryCore(string clsid, string sceneKey)
    {
        // 背景场景：pidlFolder=桌面、pDataObj=NULL 的标准初始化形态（NVIDIA 控制面板等）
        if (sceneKey is "DesktopBackground" or "DirectoryBackground")
        {
            return Flatten(ShellMenuInterop.QueryBackground([clsid]));
        }

        // 对象场景：构造一个真实存在的临时目标（ILCreateFromPath 需要存在路径）
        var isDirScene = sceneKey is "Directory" or "Folder" or "Drive" or "AllObjects";
        var tempPath = Path.Combine(Path.GetTempPath(),
            "bdmenu_preview_" + Guid.NewGuid().ToString("N") + (isDirScene ? string.Empty : ".tmp"));
        try
        {
            if (isDirScene)
            {
                Directory.CreateDirectory(tempPath);
            }
            else
            {
                File.WriteAllText(tempPath, string.Empty);
            }
            return Flatten(ShellMenuInterop.Query([tempPath], [clsid]));
        }
        finally
        {
            try
            {
                if (isDirScene) { Directory.Delete(tempPath, recursive: true); }
                else { File.Delete(tempPath); }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Trace("menu-manager", $"预览临时目标清理失败: {ex.Message}");
            }
        }
    }

    /// <summary>ShellVerbItem 树 → 扁平预览列表（多级子菜单按 Depth 缩进；分隔线降级为横线项）。</summary>
    private static List<MenuPreviewItem> Flatten(List<ShellVerbItem> tree)
    {
        var result = new List<MenuPreviewItem>();
        FlattenInto(tree, 0, result);
        return result;
    }

    private static void FlattenInto(IReadOnlyList<ShellVerbItem> items, int depth, List<MenuPreviewItem> result)
    {
        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                result.Add(new MenuPreviewItem("──", depth, IsGroupHeader: true));
                continue;
            }
            result.Add(new MenuPreviewItem(item.Text, depth, item.IsSubMenu));
            if (item.Children.Count > 0)
            {
                FlattenInto(item.Children, depth + 1, result);
            }
        }
    }
}
