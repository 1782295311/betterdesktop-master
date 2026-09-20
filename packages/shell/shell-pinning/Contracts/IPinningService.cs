using System;
using System.Collections.Generic;
using BetterDesktop.Shell.AppSource.Models;

namespace BetterDesktop.Shell.Pinning.Contracts;

/// <summary>
/// 通用固定 / 收藏服务：按 zone 分区（dock / startmenu / taskbar 等）管理固定项。
/// 从 Dock 的固定能力抽出，供 Dock、未来开始菜单、任务栏共用（M7）。
/// 所有方法均 try-catch，异常记日志不冒泡；持久化失败保持内存状态不崩（M10）。
/// </summary>
public interface IPinningService
{
    /// <summary>返回指定 zone 的固定项（按固定顺序的只读快照）。</summary>
    IReadOnlyList<PinnedItem> GetPinned(string zone);

    /// <summary>将指定应用固定到 zone（按 <see cref="AppItem"/> 快照持久化，按 AppItemId 去重）。</summary>
    void Pin(string zone, AppItem appItem);

    /// <summary>从 zone 取消固定指定应用（按 AppItemId）。</summary>
    void Unpin(string zone, AppItemId appId);

    /// <summary>按给定 AppItemId 顺序排列指定 zone 的固定项（未在列表中的项追加到末尾，保持稳定）。</summary>
    void Reorder(string zone, IReadOnlyList<AppItemId> order);

    /// <summary>指定应用是否已固定在 zone。</summary>
    bool IsPinned(string zone, AppItemId appId);

    /// <summary>从持久化存储恢复所有 zone 的固定列表（含旧 dock-pinned.json 的一次性迁移）。</summary>
    void Load();

    /// <summary>持久化所有 zone 的固定列表。</summary>
    void Save();

    /// <summary>固定列表变更（新增 / 移除 / 排序）通知，携带变更发生的 zone 与新状态。</summary>
    event EventHandler<PinnedChangedEventArgs>? PinnedChanged;

    /// <summary>
    /// 静默回写固定项快照（路径自愈 / 手动重绑用）：按 <paramref name="appId"/> 定位，把快照的路径信息
    /// 替换为 <paramref name="appItem"/> 的，**保持原主键不变**，且**不触发** <see cref="PinnedChanged"/>。
    /// <para><b>为什么保持主键</b>：主键是「这次固定」的身份——分组归属（AppGroupStore）与排序都以它为键，
    /// 换成新路径派生的主键会连带丢失分组。</para>
    /// <para><b>为什么不发事件</b>：回写发生在「读取固定列表」过程中（显示层已用新路径渲染），
    /// 再发事件会形成「事件 → 重绘 → 再读取 → 再回写」的重入。</para>
    /// </summary>
    void UpdateSnapshot(string zone, AppItemId appId, AppItem appItem);
}
