using System;
using System.Diagnostics;
using System.IO;

namespace BetterDesktop.Shell.Core.Services;

/// <summary>
/// 应用条目菜单的**系统级动作**公共实现（应用提取器 / 菜单栏搜索共用）。
/// <para>
/// <b>为什么集中</b>：这些动作此前只在应用提取器里私有实现（<c>RunUninstaller</c> 等），
/// 菜单栏搜索面板另有一套或干脆缺失——同一动作两处行为不一致是「右键功能时有时无」的另一半来源
/// （2026-09-14 S4）。项集的**出现条件**归 <c>AppEntryMenuBuilder</c>（packages/api），
/// 动作的**执行**归这里；两者都不含 UI 渲染。
/// </para>
/// <para><b>纪律</b>：全部 try-catch 静默（M10）——动作失败不得冒泡打断菜单。
/// 用户主动取消（如 UAC 拒绝、用户点「否」）与真实失败在此不作区分，均不弹错误框。</para>
/// </summary>
public static class AppEntryActions
{
    /// <summary>在资源管理器中定位：目录 → 直接打开；文件 → 选中该文件。</summary>
    public static void RevealInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\""));
                return;
            }

            if (File.Exists(path))
            {
                _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
            }
        }
        catch
        {
            // 资源管理器启动失败静默（M10）
        }
    }

    /// <summary>以管理员身份运行（会弹 UAC；用户拒绝 → 异常，静默忽略）。</summary>
    public static void RunAsAdmin(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            _ = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch
        {
            // 用户拒绝 UAC 或目标不可提权：静默（M10）
        }
    }

    /// <summary>打开文件属性对话框（Shell 原生 verb，非自绘）。</summary>
    public static void ShowProperties(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            _ = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "properties"
            });
        }
        catch
        {
            // 属性对话框无法打开时静默（M10）
        }
    }

    /// <summary>
    /// 在目标**所在目录**打开终端（Windows Terminal 优先，缺失回退 <c>cmd.exe</c>）。
    /// 语义：不是「在终端里运行该程序」，而是「把终端开在它旁边」——这样才能自己带参数运行、看输出。
    /// </summary>
    public static void OpenInTerminal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            _ = Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{directory}\"")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            try
            {
                // 无 Windows Terminal（旧系统 / 已卸载）→ 回退控制台
                _ = Process.Start(new ProcessStartInfo("cmd.exe", $"/k cd /d \"{directory}\"")
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                // 终端启动失败静默（M10）
            }
        }
    }

    /// <summary>
    /// 执行注册表卸载命令。<c>UninstallString</c> 普遍带参数
    /// （<c>MsiExec.exe /X{GUID}</c>、<c>"…\unins000.exe" /S</c>），经 <c>cmd /c</c> 执行最稳；
    /// 按 FileName 拆参会破坏带引号路径。
    /// </summary>
    public static void RunUninstaller(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        try
        {
            // C1：`/c` 与命令**分成两个参数**传，不再拼成一行。
            // command 来自注册表 UninstallString，可能含引号/空格；拼接会让我们这一层先破坏它的
            // 引号边界（如 `/c "C:\A B\x.exe" /S`），后续由 cmd 重新分词时语义就不再确定。
            // 交给 ArgumentList 做参数引用，我们不再插手分词。
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
            _ = Process.Start(psi);
        }
        catch
        {
            // 卸载器启动失败静默（M10）
        }
    }

    /// <summary>复制文本到剪贴板（失败静默：剪贴板可能被其他进程独占）。</summary>
    public static void CopyToClipboard(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch
        {
            // 剪贴板占用时忽略（M10）
        }
    }
}
