// BetterDesktop.Kernel.Core — 「注册表该写哪个 DLL 路径」的解析规则（C# 侧实现）
//
// 【契约的唯一权威是 protocols/native-dll-path-test-vectors.json】，不是本文件。
// Rust 侧 core/src/shellmenu.rs 的 resolve_dll_path / path_eq 是同一份规则的第二个实现，
// 两侧跑**同一批共享向量**（见 kernel-tests/NativeDllPathContractTests.cs 与 Rust 的
// shellmenu::tests::path_resolution_matches_the_shared_vector）。
//
// 【为什么必须有这份契约】路径解析原先分散在两处、没有任何共享契约，直接导致过本仓库已实测的事故：
//   C# 的 ResolveNativeDllPath() 只看自己的 AppContext.BaseDirectory，
//   core 的 find_native_dll() 看 本进程目录 → 安装根 → LOCALAPPDATA，
//   两边各自自洽、都判「漂移了」、谁也不报错 —— 一旦接上自愈就是
//   「core 触发修复 → CLI 写下它自己那套路径 → core 再判漂移 → 再触发」的无限写注册表。
// 与 BDMC1 协议用共享向量是同一手法：消灭"两侧各自猜边界、各自单测都绿、真机对不上"。

using System;
using System.Collections.Generic;
using System.IO;

namespace BetterDesktop.Kernel.Core;

/// <summary>路径解析的输入（<b>纯数据</b> —— 因此可以被共享向量直接驱动）。</summary>
/// <remarks>
/// 每个字段都可为 null 且都有意义：<c>InstallRoot = null</c> 表示"指针不可读"，与
/// "指针指向一个不存在的目录"是**同一件事**（<see cref="BetterDesktop.Kernel.Deployment.DeploymentInfo.ResolveInstallRoot"/>
/// 已经把后者归并成前者）。把它们当成两种输入只会让规则多一条无用的分支。
/// </remarks>
public sealed record NativeDllPathInputs
{
    /// <summary>发起解析的进程所在目录。</summary>
    public string? ProcessDir { get; init; }

    /// <summary><c>deployment.json</c> 记录的安装根；<c>null</c> = 指针不可读。</summary>
    public string? InstallRoot { get; init; }

    /// <summary><c>%LOCALAPPDATA%</c>。</summary>
    public string? LocalAppData { get; init; }

    /// <summary>显式开发者模式（<c>--dev</c> opt-in）。</summary>
    public bool DevMode { get; init; }
}

/// <summary>
/// 「注册表该写哪个路径」的解析规则 —— 权威是 <c>protocols/native-dll-path-test-vectors.json</c>。
/// </summary>
/// <remarks>
/// <code>
/// ⓪ 输出必须是**绝对路径**；相对候选直接不采信（注册表里的相对路径行为未定义）。
/// ① 安装根有效 → 只认安装根：其中有 DLL 就用，没有则拒绝（不回退）。
/// ② 安装根无效 → 候选目录按序：
///       · 进程目录 —— 仅当 devMode 或 它位于 %LOCALAPPDATA%\BetterDesktop\ 之下
///       · %LOCALAPPDATA%\BetterDesktop（生产态兜底位，与"进程碰巧在哪"无关）
/// ③ 目录内优先 native\ 子目录，其次扁平同目录。
/// </code>
/// <para>
/// <b>为什么与运行时定位的顺序相反（不要"统一"它们）</b>：运行时定位（<c>DesktopControlLocator</c> /
/// <c>ComponentPaths</c> / <c>CoreEnsurer</c>）是"拉起一个进程"，调用方自己那份优先，让开发态 bin
/// 直接跑不受影响。而这里是决定<b>写进注册表的持久引用</b> —— explorer 每次右键都按它加载 DLL。
/// 让"执行进程碰巧在哪"决定它，就等于把 dev bin 路径写进注册表，而 dev bin 一次 clean 菜单就废
/// （仓库已实测到这种互斗）。<b>持久引用必须指向最持久的位置。</b>
/// </para>
/// <para>
/// <b>安装根有效却缺 DLL 时不回退</b>：那说明部署本身不完整。此时回退去注册别处的 DLL，
/// 会把"部署不全"<b>掩盖</b>成"注册成功"。
/// </para>
/// <para>
/// <b>刻意不处理</b>（见向量的 <c>_path_equivalence._non_goals</c>）：<c>.</c>/<c>..</c> 不展开、
/// 8.3 短名不解析、UNC 不特殊处理。这些形式不应出现在注册表里，出现即为异常 —— 按"不等价"处理
/// 并告警，而不是尽力去理解它。写下来是为了防下一个实现者按直觉实现（那正是本契约要消灭的漂移）。
/// </para>
/// </remarks>
public static class NativeDllPath
{
    /// <summary>原生 DLL 文件名（与 <c>native/include/BdShell.h</c> 及各打包脚本一致）。</summary>
    public const string DllName = "BetterDesktopShellMenu.dll";

    /// <summary>生产态根目录名（<c>%LOCALAPPDATA%</c> 之下）。</summary>
    public const string ProductFolderName = "BetterDesktop";

    /// <summary>
    /// 路径<b>等价</b>判定 —— 归一化 = 分隔符统一为 <c>\</c> + 去尾分隔符 + 忽略大小写。
    /// </summary>
    /// <remarks>
    /// 它同时决定"漂移判定"与"幂等判定"，因此也必须两侧一致：C# 原先用
    /// <c>string.Equals(..., OrdinalIgnoreCase)</c>（只忽略大小写、不归一分隔符），Rust 的
    /// <c>same_path</c> 还会归一分隔符 —— 两侧对"等价"的判断不同，就会出现"一侧说漂移、
    /// 另一侧说没有"这类分歧。
    /// <para>
    /// <b>刻意保守</b>：等价判定过<b>宽</b>会让"路径确实变了"被跳过，自愈就不再发生；过<b>窄</b>
    /// 只是多触发一次幂等修复（有 RepairGate 兜底）。<b>宁可多修，不可漏修。</b>故不查文件系统
    /// 真实大小写、不解析符号链接、不展开 <c>.</c>/<c>..</c>、不展开 8.3 短名。
    /// </para>
    /// <para>
    /// 空串与 null 视为同一个值（"没有值"）：两侧都空 ⇒ 等价。但<b>空 ≠ 任意非空</b> ——
    /// 否则"注册表值为空"会被判成"与期望等价"，漂移就此被掩盖。
    /// </para>
    /// </remarks>
    public static bool PathEq(string? a, string? b) => Normalize(a) == Normalize(b);

    /// <summary>归一化（同 Rust 的 <c>normalize</c>：Trim + 分隔符统一 + 去尾分隔符 + 小写）。</summary>
    private static string Normalize(string? path) =>
        (path ?? string.Empty)
            .Trim()
            .Replace('/', '\\')
            .TrimEnd('\\')
            .ToLowerInvariant();

    /// <summary>
    /// <paramref name="child"/> 是否位于 <paramref name="parent"/> <b>之下</b>（按路径段比较）。
    /// </summary>
    /// <remarks>
    /// 必须按段比较而不能用字符串前缀：否则 <c>…\BetterDesktopTrap\bin</c> 会被判成
    /// <c>…\BetterDesktop</c> 之下，于是开发路径被误当成生产路径放行（向量里有这条用例）。
    /// </remarks>
    public static bool IsUnder(string? child, string? parent)
    {
        var c = Normalize(child);
        var p = Normalize(parent);
        if (p.Length == 0 || c.Length == 0)
        {
            return false;
        }

        return c == p || c.StartsWith(p + "\\", StringComparison.Ordinal);
    }

    /// <summary>
    /// 按规则解析应当被注册的原生 DLL 路径；返回 <c>null</c> = <b>拒绝解析</b>
    /// （= DllMissing，调用方<b>不得</b>注册，只记日志）。
    /// </summary>
    /// <param name="inputs">解析输入。</param>
    /// <param name="exists">文件是否存在（注入以便共享向量驱动 —— 契约测试不该去建真实文件树）。</param>
    public static string? Resolve(NativeDllPathInputs inputs, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(exists);

        // ⓪ 绝对路径**前置条件**：相对候选直接不采信（不是"采信后再比较"）。
        //   注册表里的相对路径，explorer 会在任意工作目录下加载它 —— 行为未定义。
        //   注意 `Path.IsPathFullyQualified` 而不是 `IsPathRooted`：后者对 `\foo` 也返回 true，
        //   而 `\foo` 是"当前驱动器相对"，同样不该进注册表。
        bool Acceptable(string p) => Path.IsPathFullyQualified(p) && exists(p);

        // ① 安装根有效 → 只认它（有则用，无则拒绝；**不回退**）
        if (!string.IsNullOrWhiteSpace(inputs.InstallRoot))
        {
            foreach (var candidate in Candidates(inputs.InstallRoot))
            {
                if (Acceptable(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        // ② 安装根无效 → 进程目录（受位置/开关约束）+ 生产态兜底位
        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(inputs.ProcessDir))
        {
            var productionRoot = string.IsNullOrWhiteSpace(inputs.LocalAppData)
                ? null
                : Path.Combine(inputs.LocalAppData, ProductFolderName);

            var productionPlace = productionRoot is not null && IsUnder(inputs.ProcessDir, productionRoot);

            // ②a 不满足条件时是"不纳入候选"，而不是"纳入后拒绝" —— 两者对日志与后续调试的含义不同
            if (inputs.DevMode || productionPlace)
            {
                dirs.Add(inputs.ProcessDir);
            }
        }

        if (!string.IsNullOrWhiteSpace(inputs.LocalAppData))
        {
            var production = Path.Combine(inputs.LocalAppData, ProductFolderName);
            // 去重：进程目录本身就等于生产态兜底位时不该查两遍
            if (!dirs.Exists(d => PathEq(d, production)))
            {
                dirs.Add(production);
            }
        }

        // ②b / ②c
        foreach (var dir in dirs)
        {
            foreach (var candidate in Candidates(dir))
            {
                if (Acceptable(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>某个目录下的候选 DLL（<c>native\</c> 子目录优先，其次扁平）。</summary>
    /// <remarks>
    /// 用 <see cref="Path.Combine(string, string)"/> 而不是手拼分隔符：它会正确处理安装根带尾分隔符
    /// （否则会拼出 <c>…1610\\native\\…</c> 这种双分隔符路径，向量里有这条用例）。
    /// </remarks>
    private static IEnumerable<string> Candidates(string dir)
    {
        yield return Path.Combine(dir, "native", DllName);
        yield return Path.Combine(dir, DllName);
    }
}
