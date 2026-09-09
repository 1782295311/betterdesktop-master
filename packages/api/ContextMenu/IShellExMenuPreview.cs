// BetterDesktop.Api — ShellEx 扩展菜单只读预览契约（shell-context-menu 迁入，2026-09-08）
// 只读预览：后台线程实例化扩展 COM 处理器，让其主动吐出菜单内容（不触发任何动作）。
// 【红线】只读：绝不调用 ShellVerbItem.Invoke；5s 超时 + 异常隔离；失败返回降级文案，不假装有数据。

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>只读预览项（Depth 为多级子菜单缩进层级；IsGroupHeader 供 UI 分隔展示）。</summary>
public sealed record MenuPreviewItem(string Text, int Depth, bool IsGroupHeader);

/// <summary>ShellEx 菜单预览服务（接口抽象便于测试桩）。</summary>
public interface IShellExMenuPreview
{
    /// <summary>查询某 CLSID 扩展在指定场景下吐出的菜单内容（只读）。失败返回降级文案项，不抛。</summary>
    Task<IReadOnlyList<MenuPreviewItem>> QueryAsync(string clsid, string sceneKey, CancellationToken ct);
}
