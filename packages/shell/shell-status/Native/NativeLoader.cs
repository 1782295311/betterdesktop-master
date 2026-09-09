// BetterDesktop.Shell.Status — 原生 DLL 共享装载器（纯转发，无业务逻辑）。
// 统一负责在 natives/ 或程序集目录下定位并装载 C++ 封装 DLL，供各 *CoreNative 使用。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace BetterDesktop.Shell.Status.Native;

/// <summary>原生 DLL 装载器：定位 natives/*.dll 并缓存句柄。</summary>
internal static class NativeLoader
{
    private static readonly Dictionary<string, IntPtr> _handles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按名称装载 DLL；失败返回 IntPtr.Zero（由调用方降级）。</summary>
    public static IntPtr Load(string dllName)
    {
        if (_handles.TryGetValue(dllName, out var h) && h != IntPtr.Zero)
        {
            return h;
        }
        foreach (var candidate in EnumerateCandidatePaths(dllName))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }
            if (NativeLibrary.TryLoad(candidate, out var lib))
            {
                _handles[dllName] = lib;
                return lib;
            }
        }
        _handles[dllName] = IntPtr.Zero;
        return IntPtr.Zero;
    }

    /// <summary>从已装载 DLL 解析导出函数委托；未装载或缺失返回 null。</summary>
    public static T? GetExport<T>(string dllName, string symbol) where T : Delegate
    {
        var h = Load(dllName);
        if (h == IntPtr.Zero)
        {
            return null;
        }
        if (NativeLibrary.TryGetExport(h, symbol, out var fn))
        {
            return Marshal.GetDelegateForFunctionPointer<T>(fn);
        }
        return null;
    }

    private static IEnumerable<string> EnumerateCandidatePaths(string dllName)
    {
        var dirs = new List<string>(capacity: 2);
        var assemblyDir = Path.GetDirectoryName(typeof(NativeLoader).Assembly.Location);
        if (!string.IsNullOrEmpty(assemblyDir))
        {
            dirs.Add(assemblyDir);
        }
        dirs.Add(AppContext.BaseDirectory);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            foreach (var file in new[] { dllName, Path.Combine("natives", dllName) })
            {
                var full = Path.Combine(dir, file);
                if (seen.Add(full))
                {
                    yield return full;
                }
            }
        }
    }
}
