using System.IO;

namespace BetterDesktop.Kernel.Core;

/// <summary>
/// 命令入口路径解析（M3.1）：系统右键注册表命令统一指向 BetterDesktop.Cli.exe（与宿主同目录部署），
/// 由 DesktopSystemMenuRegistrar（数据驱动注册）与 ContextMenuRegistry（插件声明注册）共用，
/// 保证"命令目标 exe"单点维护；剪贴板等需宿主完整在线的项例外（保持指向宿主，见调用方注释）。
/// </summary>
public static class MenuCommandPaths
{
    /// <summary>
    /// CLI 入口绝对路径。优先取同目录 BetterDesktop.Cli.exe；缺失（老部署未带 CLI）时回退当前进程路径，
    /// 保证注册动作不失败（点击时由 CLI 缺失场景的宿主兼容路径兜底）。
    /// </summary>
    public static string GetCliPath()
    {
        var dir = AppContext.BaseDirectory;
        var cli = Path.Combine(dir, "BetterDesktop.Cli.exe");
        return File.Exists(cli) ? cli : (Environment.ProcessPath ?? cli);
    }

    /// <summary>宿主自身路径（剪贴板等需宿主完整在线的菜单项使用；与 CLI 路径并存）。</summary>
    public static string? GetHostPath() => Environment.ProcessPath;
}
