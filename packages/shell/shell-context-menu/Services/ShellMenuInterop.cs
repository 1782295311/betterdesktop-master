// BetterDesktop.Shell.ContextMenus — ShellEx 第三方 handler 查询（唯一实现 = 进程外 broker）
//
// 【为什么只有一份实现】第三方 handler 的「实例化 → IShellExtInit.Initialize → IContextMenu.QueryContextMenu
//   → HMENU 树解析（懒填充泵 / owner-draw 文本回退链）」全部在进程外 BetterDesktopMenuBroker.exe 里做
//   （原生；与免宿主 B 路同一 CMake 工程、同一份 JsonLite/MenuModel/Launcher 源码）。
//   宿主进程内那份 in-proc 实现在 2026-09-14 的查重里删除：它与 broker 是同一件事的两份实现，而且宿主
//   那份**就是崩溃面本身**（SEH 级 AV 无法托管隔离）。§4.1 的验收不变量写的是"broker 被杀后降级为空菜单"，
//   不是"回宿主进程内重跑"——保留 fallback 既重复，又与自己的验收冲突。
//
// 【唯一降级路径】broker 不可用（未部署 / 启动失败 / 超时 / 应答非法）→ 返回**一条可见说明项**，
//   绝不静默返回空菜单（fail-visible 纪律）：空列表会被 UI 显示成"该扩展没有内容"，那是撒谎。
//
// 【崩溃归因】broker 非 0 退出 = 这个 CLSID 把 broker 干掉了 → 当场记账（HandlerCrashGuard.RecordExternalCrash），
//   连续 3 次由 HandlerCrashBreaker 自动停用（走既有 Toggle 通道，写前备份、可逆）。
//
// 【不覆盖】NativeMenuPopup 的系统聚合菜单（`GetUIObjectOf` 拿到的是 shell 聚合 `IContextMenu`，第三方
//   handler 在聚合内部跑）：HMENU 不能跨进程，且归属不到具体 CLSID。
//
// 【性能】每次调用 spawn 一次 broker、**一个 CLSID 一个进程**（崩溃才能精确归因）。调用方只有设置里的
//   ShellEx 只读预览（用户点开才走），菜单高频路径不经过这里。

using System;
using System.Collections.Generic;

namespace BetterDesktop.Shell.ContextMenus.Services;

// ── 本文件方法级白话索引 ──
//   "实例化某个扩展 CLSID 并取出它贡献的菜单项（文件目标）" → Query
//   "桌面空白场景下取扩展菜单项"                        → QueryBackground
// ────────────────────────────────────

/// <summary>COM 透传条目（树）。Invoke 留空：broker 侧 invoke 命令已实现，但当前无生产消费方。</summary>
public sealed record ShellVerbItem(
    string Text,
    bool IsSeparator,
    bool IsSubMenu,
    List<ShellVerbItem> Children,
    Action? Invoke);

internal static class ShellMenuInterop
{
    /// <summary>对一组路径查询这些 handler 贡献的菜单项；broker 不可用时返回一条可见说明项。</summary>
    public static List<ShellVerbItem> Query(
        IReadOnlyList<string> paths, IReadOnlyList<string> handlerClsids,
        IReadOnlyDictionary<string, string>? handlerNames = null, bool extendedVerbs = false)
        => RunOrExplain(handlerClsids, paths, background: false, extendedVerbs, handlerNames);

    /// <summary>桌面空白（背景）场景：pidlFolder=桌面、pDataObj=nullptr（背景 handler 的标准初始化形态）。</summary>
    public static List<ShellVerbItem> QueryBackground(
        IReadOnlyList<string> handlerClsids, IReadOnlyDictionary<string, string>? handlerNames = null)
        => RunOrExplain(handlerClsids, [], background: true, extendedVerbs: false, handlerNames);

    private static List<ShellVerbItem> RunOrExplain(
        IReadOnlyList<string> handlerClsids, IReadOnlyList<string> paths, bool background,
        bool extendedVerbs, IReadOnlyDictionary<string, string>? handlerNames)
    {
        if (!background && paths.Count == 0)
        {
            return [];
        }

        if (MenuBrokerClient.TryQuery(handlerClsids, paths, background, extendedVerbs, handlerNames,
                MenuBrokerClient.DefaultTimeoutMs, out var items))
        {
            return items;
        }

        return
        [
            new ShellVerbItem(
                "无法读取该扩展的菜单：进程外组件不可用（原生产物缺失或启动失败）",
                IsSeparator: false,
                IsSubMenu: false,
                [],
                Invoke: null),
        ];
    }
}
