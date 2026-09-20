using System;
using System.Collections.Generic;
using System.IO;

namespace BetterDesktop.Shell.AppSource.Services;

/// <summary>
/// 「全程序模式」的过滤规则：把「磁盘上存在的可执行文件」收敛到「用户真会双击的应用」。
/// <para>
/// <b>作用域纪律</b>：只由 <c>AppSourceService.ScanAllPrograms</c> 调用，
/// **绝不影响干净模式**（开始菜单 + 注册表）——干净模式的输入本身就是用户可感知的应用。
/// </para>
/// <para>
/// <b>语义基线</b>：<c>docs/analysis/2026-09-13-app-filter-derivation.md</c> §7.1（保留项里
/// 536/1487 = 36% 是「程序员在环境里用、没人双击」的 CLI/工具链）与 §7.4 的过滤层建议。
/// </para>
/// <para>
/// <b>粒度纪律</b>：按「**同一套件内区分主程序与工具链**」判定，**不按供应商目录整块滤**——
/// 整块滤会把 <c>Ssms.exe</c>（SQL Server Management Studio）、<c>devenv.exe</c>（VS）一起滤掉
/// （2026-09-13 用户修正）。因此规则命中落在**工具链子目录 / 文件名形迹**上，并设主程序白名单兜底。
/// </para>
/// <para>
/// <b>匹配纪律</b>（§6.2）：一律按**路径 / 文件名**判定，不用显示名——lnk 的
/// <c>GetDescription()</c> 常是整句文案，用 <c>Contains</c> 打在描述上会误杀（§4 的机制缺陷）。
/// </para>
/// </summary>
internal static class AppFilterRules
{
    /// <summary>
    /// 永不滤除的主程序文件名（套件主程序可能就落在被滤的工具链目录旁边）。
    /// 例：SSMS / VS / VMware Workstation —— 它们在混装目录里，但用户会双击。
    /// </summary>
    private static readonly HashSet<string> KeepFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ssms.exe",
        "devenv.exe",
        "vmware.exe",
    };

    /// <summary>
    /// 目录段形迹（**整段相等**）：命中即视为「CLI / 工具链环境」。
    /// <c>bin</c> 覆盖 <c>\usr\bin\</c>（Git / MSYS2，单这两处实测 246 项）。
    /// </summary>
    private static readonly string[] ToolchainDirectorySegments =
    {
        "bin",
        "Scripts",   // pip 控制台脚本（uvicorn / pygmentize 一类）
        "nodejs",
        "Roslyn",
    };

    /// <summary>目录段前缀形迹：<c>jre</c> / <c>jdk*</c>（Java 运行时与开发工具链）。</summary>
    private static readonly string[] ToolchainSegmentPrefixes =
    {
        "jre",
        "jdk",
    };

    /// <summary>跨段路径形迹（含分隔符，避免误伤同名长路径）。</summary>
    private static readonly string[] ToolchainPathFragments =
    {
        @"\VC\Tools\MSVC\",                  // MSVC 编译器（cl.exe / link.exe）
        @"\Common7\IDE\CommonExtensions\",   // VS 附属工具（ControlService 一类）
        @"Windows Kits",                     // 签名 / 打包工具（signtool / makeappx）
        @"Tesseract-OCR",                    // OCR 命令行
        @"\Docker\cli-plugins\",             // docker-buildx / compose 插件
    };

    /// <summary>
    /// 文件名形迹：后台服务与遥测 / 运行库分发。
    /// 刻意**不含**裸 <c>runtime</c>——实测该词会误伤用户真用的运行时类应用（§6.1 的谨慎项）。
    /// </summary>
    private static readonly string[] FilenameFragments =
    {
        "crash",        // crashpad_handler / crashreport
        "report",       // bugreport / crashreport
        "telemetry",
        "redist",       // vc_redist.x64.exe
        "vcruntime",
    };

    /// <summary>
    /// 该路径是否应从「全程序模式」列表中滤除。
    /// <para>判定顺序：主程序白名单 → 服务后缀 → 文件名形迹 → 路径形迹 → 目录段形迹。
    /// 白名单优先是刻意的：它必须能压过任何后续规则。</para>
    /// </summary>
    /// <param name="path">文件（或快捷方式目标）路径；为空时一律**不滤**（宁可多显示，不可误杀）。</param>
    public static bool ShouldFilter(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        // 0) 主程序白名单：优先于一切规则
        if (KeepFileNames.Contains(fileName))
        {
            return false;
        }

        // 1) 后台服务：*Service.exe（ASUSUpdateService 一类；用户不会双击）
        if (fileName.EndsWith("Service.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 2) 文件名形迹（遥测 / 崩溃上报 / 运行库分发）
        foreach (var fragment in FilenameFragments)
        {
            if (fileName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 3) 跨段路径形迹
        foreach (var fragment in ToolchainPathFragments)
        {
            if (path.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 4) 目录段形迹（最后一段是文件名，不参与目录判定）
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0)
            {
                continue;
            }

            foreach (var directory in ToolchainDirectorySegments)
            {
                if (segment.Equals(directory, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            foreach (var prefix in ToolchainSegmentPrefixes)
            {
                if (MatchesVersionedPrefix(segment, prefix))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 版本化段名前缀匹配：<c>jre1.8.0_291</c> / <c>jdk-17</c> / <c>jdk</c> 命中，
    /// 而 <c>Jreport</c> 这类「恰好以 jre 开头」的普通目录**不命中**
    /// （裸 <c>StartsWith</c> 会把它们一起滤掉）。
    /// </summary>
    private static bool MatchesVersionedPrefix(string segment, string prefix)
    {
        if (!segment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 整段就是 jre / jdk
        if (segment.Length == prefix.Length)
        {
            return true;
        }

        var next = segment[prefix.Length];
        return char.IsDigit(next) || next == '-' || next == '_' || next == '.';
    }
}
