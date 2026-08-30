// CpuCore.dll 的 C# Interop 薄封装（纯转发，无业务逻辑）。
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterDesktop.Shell.Status.Native;

public readonly record struct CpuUtilizationNative(int Utilization, bool Ok);

/// <summary>CpuCore.dll 的薄封装。仅转发，不做任何业务判断。</summary>
public static class CpuCoreNative
{
    private delegate int CpuReadUtilization(out int utilization, out int ok);
    private delegate void CpuResetCounters();
    private delegate int CpuReadModel([Out] ushort[] name, int maxChars);
    private delegate int CpuReadTemperature(out int temperatureCelsius, out int ok);

    private static readonly CpuReadUtilization? _read;
    private static readonly CpuResetCounters? _reset;
    private static readonly CpuReadModel? _readModel;
    private static readonly CpuReadTemperature? _readTmp;

    static CpuCoreNative()
    {
        _read = NativeLoader.GetExport<CpuReadUtilization>("CpuCore.dll", "Cpu_ReadUtilization");
        _reset = NativeLoader.GetExport<CpuResetCounters>("CpuCore.dll", "Cpu_ResetCounters");
        _readModel = NativeLoader.GetExport<CpuReadModel>("CpuCore.dll", "Cpu_ReadModel");
        _readTmp = NativeLoader.GetExport<CpuReadTemperature>("CpuCore.dll", "Cpu_ReadTemperature");
    }

    public static bool IsAvailable => _read is not null;

    public static CpuUtilizationNative ReadUtilization()
    {
        if (_read is null) return default;
        try
        {
            int hr = _read(out int u, out int ok);
            if (hr != 0) return default;
            return new CpuUtilizationNative(u, ok != 0);
        }
        catch
        {
            return default;
        }
    }

    public static void ResetCounters()
    {
        try { _reset?.Invoke(); } catch { /* ignore */ }
    }

    /// <summary>读取处理器具体型号；不可用返回空串。</summary>
    public static string ReadModel()
    {
        if (_readModel is null) return string.Empty;
        try
        {
            var buf = new ushort[512];
            int hr = _readModel(buf, 512);
            if (hr != 0) return string.Empty;
            var sb = new StringBuilder(512);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i] == '\0') break;
                sb.Append((char)buf[i]);
            }
            return sb.ToString();
        }
        catch { return string.Empty; }
    }

    /// <summary>读取热区温度（摄氏度）。不可用返回 null。</summary>
    public static int? ReadTemperature()
    {
        if (_readTmp is null) return null;
        try
        {
            int hr = _readTmp(out int celsius, out int ok);
            if (hr != 0 || ok == 0) return null;
            return celsius;
        }
        catch { return null; }
    }
}
