// BetterDesktop.Shell.Desktop — 启动应用后的「窗口置前」兜底（2026-09-17 WPS 实证）
//
// 【用户报障】桌面右键「打开方式 → WPS Office」点了没反应（只有 WPS 这样，别的应用都正常）。
//
// 【真机实测（决定性）】用 5 种方式启动同一份 PDF 并逐窗口取证：
//   ① wps.exe /prometheus /pdf "f"   ② wps.exe /pdf "f"   ③ wpspdf.exe /pdf "f"
//   ④ ShellExecute 默认关联打开（explorer 路线）           ⑤ explorer.exe "f"
// 结论：**五条路都成功打开了文档**——总有一个 `OpusApp` 类窗口出现且标题就是该文件名
//   （如 `智能桌宠实操步骤.pdf - WPS Office`）；但该窗口 `IsWindowVisible == False`
//   （style 0x060F0000，缺 WS_VISIBLE 位；force-show 后变 0x160F0000 且立即可见）。
//   ⇒ WPS 把新文档交接给它**已运行的那个隐藏实例**（/prometheus 模式 = 单实例 handoff），
//     文档真的开了、窗口没显示，用户侧就表现为"点了没反应"。
//   ⇒ 这是 WPS 自身状态，跟我们用哪条路启动无关（连 explorer 自己都唤不起来）——
//     所以"特殊优化"的正确落点不是换启动方式，而是**启动后把文档窗口叫出来**。
//
// 【做法】启动成功后开一个短时后台观察（≤10s，命中即退）：找**刚被启动的那个应用**的
//   顶层窗口，若标题里含本次要打开的文件名、且窗口未显示（或最小化）→ ShowWindow + 置前。
//   命中条件故意收得很窄，保证不会误伤：
//     ① 必须是顶层窗口（GetAncestor 的 GA_ROOT == 自身）；
//     ② 排除 WS_EX_TOOLWINDOW（工具窗/浮动条，实测 WPS 主窗 exstyle=0x100 无该位，不会被误排除）；
//     ③ 标题必须含**本次文件名**（不含扩展名的部分，≥2 字符）——这是"这就是我让它开的那个文档"的强信号；
//     ④ 给了应用路径时，还要求窗口所属进程名 == 该应用 exe 名。
//   已经可见且未最小化的窗口**一律不碰**（Edge/Chrome 这类正常应用零影响，只做一次枚举就退）。

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.Desktop.Services;

/// <summary>启动应用后的「文档窗口置前」兜底（WPS 一类"交接给隐藏实例"的应用专用救济）。</summary>
internal static class LaunchedWindowPresenter
{
    /// <summary>轮询间隔（毫秒）。WPS 实测 1-3 秒内窗口才出现，400ms 足够跟手。</summary>
    private const int PollIntervalMs = 400;

    /// <summary>轮询上限（≈10 秒后放弃；不做常驻监听）。</summary>
    private const int PollAttempts = 25;

    private const int GaRoot = 2;
    private const int SwShow = 5;
    private const int SwRestore = 9;

    /// <summary>授予"下一个调 SetForegroundWindow 的进程"前台权（ASFW_ANY）。失败无副作用。</summary>
    private const int AsfwAny = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    /// <summary>
    /// 启动应用前调用：把前台权让给即将启动的那个进程，让它能把窗口带到前台
    /// （否则 Windows 前台锁会让新窗口只在任务栏闪一下）。best-effort，失败忽略。
    /// </summary>
    public static void GrantForegroundToNextApp()
    {
        try
        {
            _ = AllowSetForegroundWindow(AsfwAny);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.desktop", $"AllowSetForegroundWindow 失败（无害）: {ex.Message}");
        }
    }

    /// <summary>
    /// 启动成功后调用：短时观察该应用是否给出了**未显示**的文档窗口，是则叫出来。
    /// 后台线程执行，不阻塞菜单/UI。
    /// </summary>
    /// <param name="appPathHint">刚启动的应用 exe（可空——双击"打开"走默认关联时没有明确应用）。</param>
    /// <param name="filePath">本次要打开的文件（标题匹配用）。</param>
    public static void EnsurePresented(string? appPathHint, string filePath)
    {
        // 标题匹配串：优先"含扩展名的完整文件名"；短名（<3 字符）只认完整名，避免"ab"这类子串误命中。
        var fullName = Path.GetFileName(filePath);
        var stem = Path.GetFileNameWithoutExtension(filePath);
        if (fullName.Length == 0)
        {
            return;
        }
        var needles = stem.Length >= 3 ? new[] { fullName, stem } : new[] { fullName };

        var exeName = string.IsNullOrEmpty(appPathHint)
            ? null
            : Path.GetFileNameWithoutExtension(appPathHint);

        _ = Task.Run(() => Watch(exeName, needles));
    }

    private static void Watch(string? exeName, string[] needles)
    {
        for (var attempt = 0; attempt < PollAttempts; attempt++)
        {
            Thread.Sleep(PollIntervalMs);

            try
            {
                if (TryPresent(exeName, needles))
                {
                    return; // 已处理 / 应用本来就把窗口显示好了 → 收工
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Trace("shell.desktop", $"启动后置前观察异常（忽略）: {ex.Message}");
                return;
            }
        }
    }

    /// <summary>找到目标窗口则按需显示并返回 true（"已收工"）；没找到返回 false（继续观察）。</summary>
    private static bool TryPresent(string? exeName, string[] needles)
    {
        var hwnd = IntPtr.Zero;
        var iconic = false;
        var visible = false;
        var matched = string.Empty;

        NativeMethods.EnumWindows((h, _) =>
        {
            if (hwnd != IntPtr.Zero)
            {
                return true;
            }
            if (NativeMethods.GetAncestor(h, GaRoot) != h)
            {
                return true; // 只看顶层窗口
            }
            if ((NativeMethods.GetWindowLong(h, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOOLWINDOW) != 0)
            {
                return true; // 工具窗/浮动条不是文档窗
            }

            var len = NativeMethods.GetWindowTextLength(h);
            if (len <= 0)
            {
                return true;
            }
            var title = new StringBuilder(len + 2);
            if (NativeMethods.GetWindowText(h, title, title.Capacity) <= 0)
            {
                return true;
            }

            var text = title.ToString();
            var hitNeedle = string.Empty;
            foreach (var needle in needles)
            {
                if (text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hitNeedle = needle;
                    break;
                }
            }
            if (hitNeedle.Length == 0)
            {
                return true; // 标题不含本次文件名 → 不是我要找的那个文档窗
            }
            if (exeName is not null && !BelongsToExe(h, exeName))
            {
                return true;
            }

            hwnd = h;
            matched = hitNeedle;
            visible = NativeMethods.IsWindowVisible(h);
            iconic = NativeMethods.IsIconic(h);
            return true;
        }, IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            return false; // 还没出现，继续等
        }

        if (visible && !iconic)
        {
            return true; // 正常显示（Edge/Chrome 等）——不碰
        }

        _ = NativeMethods.ShowWindow(hwnd, iconic ? SwRestore : SwShow);
        _ = NativeMethods.SetForegroundWindow(hwnd);
        DiagnosticLog.Trace("shell.desktop",
            $"启动后置前：应用未显示文档窗口（iconic={iconic}）→ 已 ShowWindow+置前 hwnd=0x{hwnd.ToInt64():X} 匹配='{matched}'");
        return true;
    }

    /// <summary>窗口所属进程名是否等于目标应用 exe 名（取不到进程时保守放行标题匹配结果）。</summary>
    private static bool BelongsToExe(IntPtr hwnd, string exeName)
    {
        try
        {
            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
            {
                return true;
            }
            using var proc = Process.GetProcessById((int)pid);
            return string.Equals(proc.ProcessName, exeName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 进程刚好退出 / 拒绝访问：不因此否掉标题这条强信号
            return true;
        }
    }
}
