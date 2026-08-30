// MemoryCore.dll 的 C# Interop 薄封装（纯转发，无业务逻辑）。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterDesktop.Shell.Status.Native;

public readonly record struct MemoryGlobalNative(ulong TotalPhysKb, ulong AvailPhysKb);

public readonly record struct MemoryTopProcessNative(string Name, uint Pid, ulong WorkingSetKb);

public readonly record struct MemoryPhysicalSlotNative(string PartNumber, ulong SizeKb, uint SpeedMhz);

/// <summary>MemoryCore.dll 的薄封装。仅转发，不做任何业务判断。</summary>
public static class MemoryCoreNative
{
    private delegate int MemReadGlobal(out ulong totalPhysKb, out ulong availPhysKb);

    private delegate int MemReadTopProcesses(
        int topCount,
        [Out] ushort[] names, int nameStrideChars,
        [Out] uint[] pids,
        [Out] ulong[] workingSetKb,
        out int filled);

    private delegate int MemReadPhysicalSlots(
        int slotCount,
        [Out] ushort[] slotPartNumbers, int nameStrideChars,
        [Out] ulong[] slotSizeKb,
        [Out] uint[] slotSpeedMhz,
        out int filled);

    private const int MaxProcChars = 128;
    private const int MaxPartChars = 64;

    private static readonly MemReadGlobal? _readGlobal;
    private static readonly MemReadTopProcesses? _readTop;
    private static readonly MemReadPhysicalSlots? _readSlots;

    static MemoryCoreNative()
    {
        _readGlobal = NativeLoader.GetExport<MemReadGlobal>("MemoryCore.dll", "Mem_ReadGlobal");
        _readTop = NativeLoader.GetExport<MemReadTopProcesses>("MemoryCore.dll", "Mem_ReadTopProcesses");
        _readSlots = NativeLoader.GetExport<MemReadPhysicalSlots>("MemoryCore.dll", "Mem_ReadPhysicalSlots");
    }

    public static bool IsAvailable => _readGlobal is not null;

    public static MemoryGlobalNative ReadGlobal()
    {
        if (_readGlobal is null) return default;
        try
        {
            int hr = _readGlobal(out ulong t, out ulong a);
            return hr == 0 ? new MemoryGlobalNative(t, a) : default;
        }
        catch { return default; }
    }

    public static IReadOnlyList<MemoryTopProcessNative> ReadTopProcesses(int topCount = 8)
    {
        if (topCount <= 0 || _readTop is null) return Array.Empty<MemoryTopProcessNative>();
        if (topCount > 16) topCount = 16;
        try
        {
            var namesBuf = new ushort[topCount * MaxProcChars];
            var pids = new uint[topCount];
            var ws = new ulong[topCount];
            int hr = _readTop(topCount, namesBuf, MaxProcChars, pids, ws, out int filled);
            if (hr != 0 || filled <= 0) return Array.Empty<MemoryTopProcessNative>();
            var result = new List<MemoryTopProcessNative>(filled);
            for (int i = 0; i < filled; i++)
            {
                var name = ExtractRow(namesBuf, i, MaxProcChars);
                result.Add(new MemoryTopProcessNative(name, pids[i], ws[i]));
            }
            return result;
        }
        catch { return Array.Empty<MemoryTopProcessNative>(); }
    }

    public static IReadOnlyList<MemoryPhysicalSlotNative> ReadPhysicalSlots(int slotCount = 8)
    {
        if (slotCount <= 0 || _readSlots is null) return Array.Empty<MemoryPhysicalSlotNative>();
        if (slotCount > 16) slotCount = 16;
        try
        {
            var partsBuf = new ushort[slotCount * MaxPartChars];
            var sizeKb = new ulong[slotCount];
            var speed = new uint[slotCount];
            int hr = _readSlots(slotCount, partsBuf, MaxPartChars, sizeKb, speed, out int filled);
            if (hr != 0 || filled <= 0) return Array.Empty<MemoryPhysicalSlotNative>();
            var result = new List<MemoryPhysicalSlotNative>(filled);
            for (int i = 0; i < filled; i++)
            {
                var part = ExtractRow(partsBuf, i, MaxPartChars);
                result.Add(new MemoryPhysicalSlotNative(part, sizeKb[i], speed[i]));
            }
            return result;
        }
        catch { return Array.Empty<MemoryPhysicalSlotNative>(); }
    }

    private static string ExtractRow(ushort[] buf, int row, int stride)
    {
        var sb = new StringBuilder(stride);
        int start = row * stride;
        for (int i = 0; i < stride; i++)
        {
            var c = buf[start + i];
            if (c == '\0') break;
            sb.Append((char)c);
        }
        return sb.ToString();
    }
}
