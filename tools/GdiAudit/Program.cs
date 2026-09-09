// GdiAudit —— GDI 对象表独立诊断工具（7445 变体 A 移植：Open-Shell TrackResources.cpp [verified]）。
// 用途：开发期枚举本进程 GDI 对象（类型/数量）诊断句柄泄漏——不进 shell 运行时（计划 P3c 范围红线）。
//
// 7445 红线落地：
//   ①GdiQueryTable 只取【地址】作偏移基准，绝不调用（把它当函数调用 = 未定义行为）；
//   ②偏移硬编码按版本分支：Win8+ 0x6b1b0 / Win7 0x29db0——跨版本漂移，【解引用前必须自检】；
//   ③自检 = VirtualQuery 确认 (fn+off) 指向内存可读 + 对象数与公开 API 基线交叉校验；失败降级 GetGuiResources；
//   ④GdiTableCell 布局（TrackResources.cpp）：pKernel(8) nProcess(2) nCount(2) nUpper(2) nType(2) pUser(8) = 24B；
//   ⑤枚举 65536 cell，pKernel!=0 且 nProcess==本进程 PID（低 16 位）即本进程对象，
//     句柄 = (nUpper<<16)|i，类型由 GetObjectType 权威判定。

using BetterDesktop.Tools.GdiAudit;

const int SampleIntervalMs = 2000;
Console.OutputEncoding = System.Text.Encoding.UTF8;

while (true)
{
    GdiAudit.DumpOnce();
    Console.WriteLine($"--- {SampleIntervalMs / 1000}s 后输出增量差异（Ctrl+C 退出）---");
    Thread.Sleep(SampleIntervalMs);
    GdiAudit.DumpOnce();
    Console.WriteLine();
    Thread.Sleep(SampleIntervalMs);
}

namespace BetterDesktop.Tools.GdiAudit
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.InteropServices;

    internal static class GdiAudit
    {
        private const int CellCount = 65536;
        private const ulong OffsetWin8Plus = 0x6b1b0;
        private const ulong OffsetWin7 = 0x29db0;
        private const uint GrGdiObjects = 0;
        private const uint GrUserObjects = 1;
        private const uint SigdnBaselineTolerance = 64;

        private static readonly Dictionary<uint, string> TypeNames = new()
        {
            [1] = "OBJ_PEN",
            [2] = "OBJ_BRUSH",
            [3] = "OBJ_DC",
            [4] = "OBJ_METADC",
            [5] = "OBJ_PAL",
            [6] = "OBJ_FONT",
            [7] = "OBJ_BITMAP",
            [8] = "OBJ_REGION",
            [9] = "OBJ_METAFILE",
            [10] = "OBJ_MEMDC",
            [11] = "OBJ_EXTPEN",
            [12] = "OBJ_ENHMETADC",
            [13] = "OBJ_METAFILEDC",
        };

        public static void DumpOnce()
        {
            var gdiCount = GetGuiResources(GetCurrentProcess(), GrGdiObjects);
            var userCount = GetGuiResources(GetCurrentProcess(), GrUserObjects);
            Console.WriteLine($"[基线/公开 API] GDI={gdiCount} USER={userCount}");

            var snapshot = TryReadGdiTable();
            if (snapshot is null)
            {
                // 7445 红线 3/4：偏移自检失败 → 降级 GetGuiResources（不崩溃、不继续读表）。
                Console.WriteLine("[GdiQueryTable] 表读取不可用（偏移自检失败或导出缺失）——已降级 GetGuiResources 基线。");
                Console.WriteLine("    提示：7445 硬编码偏移跨版本漂移，接入前须在本机实测校正 0x6b1b0/0x29db0。");
                return;
            }

            Console.WriteLine($"[GdiQueryTable] 本进程 GDI 对象 {snapshot!.Count} 个（类型分布）：");
            foreach (var (type, name) in TypeNames)
            {
                if (snapshot.Types.TryGetValue(type, out var n) && n > 0)
                {
                    Console.WriteLine($"    {name,-16} {n}");
                }
            }
            var unclassified = snapshot.Count - snapshot.Types.Values.Sum();
            if (unclassified > 0)
            {
                Console.WriteLine($"    {"(未知类型)",-16} {unclassified}");
            }
        }

        private static Snapshot? TryReadGdiTable()
        {
            var gdi32 = GetModuleHandle("gdi32.dll");
            if (gdi32 == IntPtr.Zero)
            {
                return null;
            }

            // 红线①：只取地址，不调用。
            var fn = GetProcAddress(gdi32, "GdiQueryTable");
            if (fn == IntPtr.Zero)
            {
                return null;
            }

            // 红线②：按版本分支选偏移（7445 偏移表）。
            var offset = IsWindows8OrGreater() ? OffsetWin8Plus : OffsetWin7;
            var tablePtr = IntPtr.Add(fn, checked((int)offset));

            // 红线③自检：解引用前确认 (fn+off) 指向内存可读（防跨版本漂移读到未映射页 → AV）。
            if (!IsReadable(tablePtr, 8))
            {
                return null;
            }

            unsafe
            {
                var cells = *(GdiTableCell**)tablePtr;
                if (cells == null)
                {
                    return null;
                }

                // 自检第二关：表首 cell 必须可读。
                if (!IsReadable((IntPtr)cells, sizeof(GdiTableCell)))
                {
                    return null;
                }

                var pid = (ushort)GetCurrentProcessId();
                var types = new Dictionary<uint, int>();
                var total = 0;
                for (var i = 0; i < CellCount; i++)
                {
                    var cell = cells[i];
                    if (cell.pKernel == IntPtr.Zero || cell.nProcess != pid)
                    {
                        continue;
                    }

                    // 权威类型判定走 GetObjectType（句柄 = (nUpper<<16)|i，TrackResources.cpp 同式）。
                    var handle = (IntPtr)((cell.nUpper << 16) | i);
                    var type = GetObjectType(handle);
                    if (type != 0)
                    {
                        types[type] = types.TryGetValue(type, out var n) ? n + 1 : 1;
                        total++;
                    }
                }

                // 自检第三关：合理表中本进程对象数不应远超公开 API 报告数（±64 容忍采样窗口差异）。
                var baseline = GetGuiResources(GetCurrentProcess(), GrGdiObjects);
                if (Math.Abs(total - (int)baseline) > SigdnBaselineTolerance)
                {
                    return null; // 偏移漂移的典型症状 → 判自检失败，走降级
                }

                return new Snapshot(total, types);
            }
        }

        private static bool IsWindows8OrGreater()
        {
            var v = Environment.OSVersion.Version;
            return v.Major > 6 || (v.Major == 6 && v.Minor >= 2) || v.Major >= 10;
        }

        private static bool IsReadable(IntPtr address, int size)
        {
            unsafe
            {
                if (VirtualQuery(address, out var info, (uint)sizeof(MEMORY_BASIC_INFORMATION)) == 0)
                {
                    return false;
                }

                const uint commit = 0x1000;
                const uint readable = 0x02 | 0x04 | 0x06 | 0x20 | 0x40 | 0x80; // READONLY|READWRITE|WRITECOPY|EXEC_* READ
                return info.State == commit && (info.Protect & readable) != 0 && (info.Protect & 0x100) == 0 // 排除 PAGE_GUARD
                    && (ulong)address + (ulong)(uint)size <= (ulong)info.BaseAddress + info.RegionSize;
            }
        }

        private sealed record Snapshot(int Count, Dictionary<uint, int> Types);

        [StructLayout(LayoutKind.Sequential)]
        private struct GdiTableCell
        {
            public IntPtr pKernel;
            public ushort nProcess;
            public ushort nCount;
            public ushort nUpper;
            public ushort nType;
            public IntPtr pUser;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public nuint RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("user32.dll")]
        private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        [DllImport("gdi32.dll")]
        private static extern uint GetObjectType(IntPtr h);

        [DllImport("kernel32.dll")]
        private static extern nuint VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, uint dwLength);
    }
}
