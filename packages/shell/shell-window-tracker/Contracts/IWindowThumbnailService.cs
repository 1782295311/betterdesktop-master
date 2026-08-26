using System;
using System.Windows;

namespace BetterDesktop.Shell.WindowTracker.Contracts;

/// <summary>
/// DWM 窗口缩略图服务（原生句柄级 API）。
/// 自 shell-dock 下沉（步骤5）：DWM 缩略图注册/更新/注销的统一入口，
/// 供 Dock 预览窗与未来任务栏 / 开始菜单窗口预览使用（M7）。
/// 底层互操作集中在 shell-window-tracker（Thumbnail/DwmThumbnailInterop），其他包不直接写 DWM。
/// </summary>
public interface IWindowThumbnailService
{
    /// <summary>
    /// 在目标窗口内注册对源窗口的实时缩略图，返回缩略图句柄。
    /// 任一句柄为 Zero、或 DWM 合成未启用时返回 <see cref="IntPtr.Zero"/>（调用方应降级为静态预览）。
    /// </summary>
    IntPtr RegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource);

    /// <summary>
    /// 更新缩略图显示属性（可见、位置、不透明度）。destinationRect 为 WPF 逻辑矩形，
    /// 本服务按 1:1 转 DWM 物理像素（不做 DPI 换算，高 DPI 场景调用方应先换算）。
    /// 句柄无效时静默忽略。
    /// </summary>
    void UpdateThumbnail(IntPtr thumbnail, Rect destinationRect, byte opacity);

    /// <summary>
    /// 取消注册缩略图。句柄无效时静默忽略。
    /// </summary>
    void UnregisterThumbnail(IntPtr thumbnail);
}
