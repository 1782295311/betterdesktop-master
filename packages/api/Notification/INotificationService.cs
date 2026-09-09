using System.Collections.Generic;
using BetterDesktop.Shell.AppSource.Models;

namespace BetterDesktop.Shell.Notification.Contracts;

/// <summary>
/// 通知服务：从 Dock 独立出的新装应用通知能力。
/// 订阅 IAppSourceService.AppSourceChanged 自动触发；也允许其他插件主动调用。
/// 所有方法 try-catch，异常记日志不冒泡（M10）。
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// 弹出"新装应用"提醒（右下角卡片，支持一键固定到 Dock / 全部忽略）。
    /// 已有弹窗时不重复创建；必须从 UI 线程调用。
    /// </summary>
    void ShowNewAppsNotification(IReadOnlyList<AppItem> apps);
}
