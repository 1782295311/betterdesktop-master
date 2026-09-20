// BetterDesktop.Shell.Core —「桌面服务」exe 定位（宿主 / CLI 共用，避免各自写一份查找逻辑）
//
// 【为什么需要两份路径】与 watchdog/Program.cs 的 BuildTargets 同一约定：
//   ① 调用方**同目录** —— 生产合并部署（publish 把所有 exe 放在一个文件夹）；
//   ② `%LOCALAPPDATA%\BetterDesktop` —— 开发/局部部署形态（剪贴板面板/引擎、索引引擎就部署在那里，
//      各自的 deploy 脚本负责把产物放进去）。
//
// 【2026-09-17 真机教训】只查 ① 在开发态必然失败：Host / Cli / DesktopControl 各自有自己的
// `bin\Debug\...` 目录，"同目录"只在发布布局成立。用户重启主程序后日志给出
// `[desktop] 桌面服务未部署（同目录无 BetterDesktop.DesktopControl.exe），自绘桌面不可用` —— 即本类要修的问题。

using System;
using System.IO;

namespace BetterDesktop.Shell.Core.DesktopControl;

/// <summary>「桌面服务」可执行文件定位。</summary>
public static class DesktopControlLocator
{
    /// <summary>可执行文件名（跨进程契约：托盘 / CLI / 宿主 / 看门狗 / 发布清单都按此名找）。</summary>
    public const string ExeName = "BetterDesktop.DesktopControl.exe";

    /// <summary>定位 exe；未部署返回 null（调用方负责降级并**留痕**，不得静默）。</summary>
    public static string? Find()
    {
        try
        {
            var sameDir = Path.Combine(AppContext.BaseDirectory, ExeName);
            if (File.Exists(sameDir))
            {
                return sameDir;
            }

            // ② 安装根（2026-09-17 安装器级）：deployment.json 记录的"当前安装"目录。
            //    放在"同目录"之后：调用方自己那份永远优先（开发态 bin 直接跑不受影响）；
            //    只有当调用方不在安装目录里（例如旧副本 / 局部部署）时，才由它把大家统一到当前安装。
            var installRoot = BetterDesktop.Kernel.Deployment.DeploymentInfo.ResolveInstallRoot();
            if (installRoot is not null)
            {
                var installed = Path.Combine(installRoot, ExeName);
                if (File.Exists(installed))
                {
                    return installed;
                }
            }

            var deployed = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BetterDesktop",
                ExeName);
            if (File.Exists(deployed))
            {
                return deployed;
            }

            // ③ `%LOCALAPPDATA%\BetterDesktop\DesktopControl\` —— 开发态的**自包含**部署位：
            //    整份产物（exe + 自己的 dll + desktop.yml）放同一个子目录，不与剪贴板面板/索引引擎
            //    共用目录而互相覆盖共享 DLL（那两个引擎已经部署在上一层）。由
            //    scripts/deploy-desktop-service.ps1 投递。
            var isolated = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BetterDesktop",
                "DesktopControl",
                ExeName);
            return File.Exists(isolated) ? isolated : null;
        }
        catch (Exception ex)
        {
            BetterDesktop.Kernel.Core.DiagnosticLog.Trace("shell.desktop", $"定位桌面服务失败: {ex.Message}");
            return null;
        }
    }
}
