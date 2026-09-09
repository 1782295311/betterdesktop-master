using System.Collections.Generic;
using BetterDesktop.Shell.Dock.Models;

namespace BetterDesktop.Shell.Dock.Services;

/// <summary>
/// Dock 固定应用服务：负责固定应用录入、持久化、排序与状态维护。
/// </summary>
public interface IDockAppsService
{
    /// <summary>
    /// 当前固定应用列表（只读快照）。
    /// </summary>
    IReadOnlyList<DockItemData> Pinned { get; }

    /// <summary>
    /// 从持久化存储恢复固定列表。
    /// </summary>
    void Load();

    /// <summary>
    /// 持久化当前固定列表。
    /// </summary>
    void Save();

    /// <summary>
    /// 按应用路径添加固定项（LNK / EXE / URL / AppX 由实现层解析）。
    /// </summary>
    void AddByPath(string path);

    /// <summary>
    /// 按业务主键移除固定项。
    /// </summary>
    void RemoveById(DockItemId id);

    /// <summary>
    /// 按给定业务主键顺序重新排列固定列表。
    /// </summary>
    void Reorder(IReadOnlyList<DockItemId> order);

    /// <summary>
    /// 扫描开始菜单，返回候选应用列表（不自动写入固定列表）。
    /// </summary>
    IReadOnlyList<DockItemData> ScanStartMenu();

    /// <summary>
    /// 扫描已安装程序（卸载注册表项），返回可作为应用启动的候选项。
    /// 仅返回能解析出可执行目标的项目，过滤系统组件/更新包。
    /// </summary>
    IReadOnlyList<DockItemData> ScanInstalledApps();

    /// <summary>
    /// 全程序模式：遍历磁盘上常见程序目录，收录所有可运行程序（exe/bat/cmd/com/msc 等），
    /// 按所在文件夹分类。排除 Windows 系统目录，避免把系统工具大量捞入。
    /// 用于"全程序大盘点"视图，与主列表（干净模式）的三类来源互补。
    /// </summary>
    IReadOnlyList<DockItemData> ScanAllPrograms();

    /// <summary>
    /// 返回检测到的"新安装应用"（相对上次已见快照新增、且尚未固定到 Dock 的应用）。
    /// 首次调用（快照为空）会把当前已安装程序全部记为已见并返回空，避免一次性弹出所有旧应用提醒。
    /// </summary>
    IReadOnlyList<DockItemData> GetNewlyInstalledApps();

    /// <summary>
    /// 将指定应用标记为"已见"，纳入快照，后续不再重复提醒。
    /// 用于通知被忽略或处理完成后调用。
    /// </summary>
    void MarkInstalledAppsSeen(IReadOnlyList<DockItemData> apps);

    /// <summary>
    /// 固定列表变更（新增/移除/排序）通知。
    /// </summary>
    event EventHandler? PinnedChanged;

    /// <summary>
    /// 将指定应用加入"排除列表"（干净模式下不再显示）。按业务主键去重。
    /// 用于过滤干净模式扫描的漏网之鱼（本应隐藏却出现的程序）。
    /// </summary>
    void ExcludeApp(DockItemData app);

    /// <summary>
    /// 判断指定应用是否已被排除（干净模式隐藏）。
    /// </summary>
    bool IsExcluded(DockItemId id);

    /// <summary>
    /// 使底层应用扫描（开始菜单/已安装注册表）的内存缓存立即失效。
    /// 卸载/安装应用后调用，强制下次扫描重新读取，避免列表残留已卸载程序。
    /// </summary>
    void InvalidateScanCache();

    /// <summary>
    /// 固定项启动路径自愈：快照路径已失效（版本化目录应用如 Edge/Chrome 自动更新后旧路径被清）时，
    /// 按名称反查已安装列表取最新路径。路径仍有效返回原项；应用真被卸载返回 null。
    /// </summary>
    DockItemData? TryRefreshStalePath(DockItemData item);
}
