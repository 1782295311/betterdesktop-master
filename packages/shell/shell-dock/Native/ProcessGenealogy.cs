// BetterDesktop.Shell.Dock — 进程父子链（T3 溯源用）
//
// 【为什么需要它】运行区的项来自"当前在跑的窗口"，而那个进程**未必是用户认知里的那个应用**：
//   · Electron 类应用的主窗口可能挂在 helper/子进程上；
//   · 启动器拉起的本体（launcher.exe → 游戏本体）在应用索引里没有登记。
// 判定"该固定谁"只能靠**事实**（谁启动了谁），不能靠路径相似 —— 后者正是 2026-09-14 误绑的成因
//（S8 第 ④ 级按"同安装根"把 WorkBuddy 错指到 CodeBuddy CN.exe）。挑谁由
// <see cref="Services.RunningAppOriginResolver"/> 决定（纯函数、可单测）；本类只提供"链"。
//
// 【两个实现选择】
//   ① exe 路径走 **QueryFullProcessImageNameW**（PROCESS_QUERY_LIMITED_INFORMATION），
//      而不是 .NET 的 Process.MainModule —— 后者对**高完整性进程**（管理员应用）会抛访问拒绝，
//      拿不到路径就会被误判成"未知"，溯源在管理员应用上直接失效；
//   ② 快照失败/取不到路径 → 该节点**跳过**（Resolver 把空白当未知），绝不因此短路整条链。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterDesktop.Shell.Dock.Native;

internal static class ProcessGenealogy
{
    private const uint Th32csSnapProcess = 0x00000002;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>上溯深度上限（防异常链）；8 层足够覆盖"启动器 → 宿主 → 本体"这类现实结构。</summary>
    private const int MaxDepth = 8;

    private static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>
    /// 取进程祖先链的 exe 路径，**由近到远**（<c>[0]</c> = <paramref name="pid"/> 自身）。
    /// 取不到路径的节点被跳过；无环保护命中或到达根则停止。永不抛（失败返回已收集的部分）。
    /// </summary>
    public static IReadOnlyList<string> GetAncestorPaths(int pid)
    {
        var result = new List<string>(4);
        if (pid <= 0)
        {
            return result;
        }

        try
        {
            var parents = SnapshotParentPids();
            var visited = new HashSet<int>();
            var current = pid;

            for (int depth = 0; depth < MaxDepth && current > 0; depth++)
            {
                if (!visited.Add(current))
                {
                    break; // 环保护（理论上不该出现，但链是外部数据）
                }

                var path = TryGetExePath(current);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    result.Add(path);
                }

                if (!parents.TryGetValue(current, out var parent) || parent == current)
                {
                    break;
                }

                current = parent;
            }
        }
        catch
        {
            // 取链失败不是致命问题：调用方拿到短链/空链后按"未知"处理并退回原路径
        }

        return result;
    }

    private static Dictionary<int, int> SnapshotParentPids()
    {
        var map = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
        {
            return map;
        }

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return map;
            }

            do
            {
                map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }

        return map;
    }

    private static string? TryGetExePath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null; // 系统进程 / 受保护进程：拿不到就按未知跳过
        }

        try
        {
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size)
                ? buffer.ToString()
                : null;
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW", SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW", SetLastError = true)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
}
