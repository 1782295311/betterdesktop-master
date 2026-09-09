using System;
using System.Collections.Generic;
using BetterDesktop.Shell.AppSource.Models;

namespace BetterDesktop.Shell.AppSource.Contracts;

/// <summary>
/// 应用数据来源服务：统一的应用扫描接口。
/// </summary>
public interface IAppSourceService
{
    /// <summary>
    /// 扫描开始菜单，返回候选应用列表（不自动写入任何持久化）。
    /// </summary>
    IReadOnlyList<AppItem> ScanStartMenu();

    /// <summary>
    /// 扫描已安装程序（卸载注册表项 + UWP/Store 应用），返回可作为应用启动的候选项。
    /// 结果 = 注册表已安装来源+ Apps 文件夹
    /// （Apps 文件夹来源；与开始菜单 .lnk 同名者已去重）。
    /// </summary>
    IReadOnlyList<AppItem> ScanInstalledApps();

    /// <summary>
    /// 单独扫描 UWP / Microsoft Store 应用（`shell:appsfolder` 虚拟文件夹）。
    /// 与 <see cref="ScanInstalledApps"/> 的区别：仅返回 Store 来源项（供 Store 应用管理界面等专用视图）。
    /// </summary>
    IReadOnlyList<AppItem> ScanStoreApps();

    /// <summary>
    /// 全程序大盘点：遍历常见程序根目录（Program Files / Program Files (x86) /
    /// LocalAppData Programs / 各固定盘 Program Files），排除 Windows 系统目录，
    /// 深度受控地收录磁盘上存在的可运行程序。与干净模式（开始菜单 + 已安装注册表）互补。
    /// </summary>
    IReadOnlyList<AppItem> ScanAllPrograms();

    /// <summary>
    /// 返回开始菜单 "Programs" 的文件夹层级树（用户 + 公共两目录合并，同名子文件夹递归合并），
    /// 供经典开始菜单"所有程序"树 / 全应用浏览视图使用。
    /// </summary>
    ProgramFolder GetProgramTree();

    /// <summary>
    /// 返回检测到的"新安装应用"（相对上次已见快照新增）。
    /// 首次调用会把当前已安装程序全部记为已见并返回空，避免一次性弹出所有旧应用。
    /// </summary>
    IReadOnlyList<AppItem> GetNewlyInstalledApps();

    /// <summary>
    /// 将指定应用标记为"已见"，纳入快照，后续不再重复提醒。
    /// </summary>
    void MarkAppsSeen(IReadOnlyList<AppItem> apps);

    /// <summary>
    /// 按路径解析应用项。
    /// </summary>
    AppItem? ResolveFromPath(string path);

    /// <summary>
    /// 使开始菜单/已安装扫描的内存缓存立即失效（如应用卸载/安装后需强制重扫）。
    /// </summary>
    void InvalidateCache();

    /// <summary>
    /// 开始菜单内容变化（创建 / 删除 / 改名）时触发（FileSystemWatcher 防抖 1s 后）。
    /// 事件在后台线程触发，订阅方应切回 UI 线程更新界面。
    /// </summary>
    event EventHandler? AppSourceChanged;
}
