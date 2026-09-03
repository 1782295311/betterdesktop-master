// BetterDesktop.Shell.ContextMenus — 终端定位（回退链）
// Win10 老菜单的"在此处打开命令窗口"语义：优先 Windows Terminal，回退 PowerShell 7 →
// Windows PowerShell → cmd。没找到就返回 null（菜单项隐藏，不放假入口）。

using System.IO;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>终端程序定位（wt → pwsh → powershell → cmd）。</summary>
public static class TerminalLocator
{
    /// <summary>返回可用终端 exe 路径；全都没有 → null。</summary>
    public static string? Resolve()
    {
        foreach (var candidate in Candidates())
        {
            try
            {
                var path = Environment.ExpandEnvironmentVariables(candidate);
                if (File.Exists(path))
                {
                    return path;
                }
            }
            catch
            {
                // 单项失败继续下一个（M10）
            }
        }
        return null;
    }

    private static string[] Candidates() =>
    [
        "%LOCALAPPDATA%\\Microsoft\\WindowsApps\\wt.exe",
        "%ProgramFiles%\\PowerShell\\7\\pwsh.exe",
        "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe",
        "C:\\Windows\\System32\\cmd.exe",
    ];
}
