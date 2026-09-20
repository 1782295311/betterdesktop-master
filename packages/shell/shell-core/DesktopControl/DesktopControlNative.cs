// BetterDesktop.Shell.Core —「桌面控制」免宿主立即生效原语（直接操作 explorer 原生层）
//
// 【为什么需要】用户 2026-09-17 拍板：主程序没跑时，「桌面图标显隐 / 隐藏任务栏」要**当场生效**，
// 不能只写设置等宿主来消费（旧行为 = 点了没反应，正是用户实测到的问题）。
// 宿主在线时这两项无需本类（宿主自己的 DesktopPlugin / Bootstrap 会按设置变更应用）；
// 本类是**宿主缺席**那条路径的唯一实现，避免第三份 ShowWindow 拷贝（此前已有 5 份查找拷贝，见 DesktopHostWindow 文件头）。
//
// 【为什么是原生层而不是 LVM_SETITEMSTATE / WM_COMMAND 0x7402】
// 与既有实现保持同一套已验证原语：图标 = ShowWindow(SysListView32)、任务栏 = NativeTaskbarManager 双态处理。
// 0x7402 等"切换桌面图标"老技巧在本仓已被明确禁用（DesktopPlugin 注释有真机记录）。

using System;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Core.Windowing;

namespace BetterDesktop.Shell.Core.DesktopControl;

/// <summary>「桌面控制」中不依赖宿主即可立即生效的原生层动作。</summary>
public static class DesktopControlNative
{
    private const int SwHide = 0;
    private const int SwShow = 5;

    /// <summary>
    /// 原生桌面图标显隐（explorer 的 <c>SysListView32</c>）。
    /// 已是目标状态则直接返回 true（幂等，避免无谓的 ShowWindow）。
    /// 找不到图标 ListView（explorer 未就绪/被第三方接管）返回 false——调用方只记日志，不弹错。
    /// </summary>
    public static bool ApplyIconsHidden(bool hidden)
    {
        try
        {
            var listView = DesktopHostWindow.FindIconListView();
            if (listView == IntPtr.Zero)
            {
                return false;
            }

            if (NativeMethods.IsWindowVisible(listView) == !hidden)
            {
                return true;
            }

            _ = NativeMethods.ShowWindow(listView, hidden ? SwHide : SwShow);
            return true;
        }
        catch (Exception)
        {
            return false; // 跨进程窗口操作失败不致命：设置已落盘，宿主上线后仍会归位
        }
    }

    /// <summary>
    /// 原生任务栏显隐（<paramref name="visible"/> = 显示）。
    /// Win11 XAML 任务栏必须走样式+SetWindowPos+ShowWindow 组合，逻辑在 <see cref="NativeTaskbarManager"/> 单点。
    /// </summary>
    public static bool ApplyTaskbarVisible(bool visible)
    {
        try
        {
            NativeTaskbarManager.SetTaskbarVisible(visible);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 按命令名套用原生效果。<paramref name="value"/> = **该开关设置键的新值**
    /// （icons → "是否已隐藏"；taskbar → "是否显示"），语义差异由本方法内部吸收。
    /// 未知命令名或该开关无原生效果 → 返回 false（不是错误）。
    /// </summary>
    public static bool Apply(string name, bool value)
    {
        if (!DesktopToggleCatalog.TryGet(name, out var spec) || !spec.NativeEffective)
        {
            return false;
        }

        return spec.Name switch
        {
            DesktopToggleCatalog.Icons => ApplyIconsHidden(value),
            DesktopToggleCatalog.Taskbar => ApplyTaskbarVisible(value),
            _ => false,
        };
    }
}
