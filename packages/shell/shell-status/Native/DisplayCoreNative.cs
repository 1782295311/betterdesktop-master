// BetterDesktop.Shell.Status — DisplayCore.dll 的 C# Interop 薄封装（纯转发，无业务逻辑）。
using System;
using System.Runtime.InteropServices;

namespace BetterDesktop.Shell.Status.Native;

/// <summary>显示器亮度原生快照。</summary>
public readonly record struct DisplayBrightnessNative(int Min, int Current, int Max, bool Ok);

/// <summary>DisplayCore.dll 的薄封装。仅转发，不做任何业务判断。</summary>
public static class DisplayCoreNative
{
    private delegate int DisplayGetBrightness(out int minVal, out int curVal, out int maxVal, out int ok);
    private delegate int DisplaySetBrightness(int val);

    private static readonly DisplayGetBrightness? _get;
    private static readonly DisplaySetBrightness? _set;

    static DisplayCoreNative()
    {
        _get = NativeLoader.GetExport<DisplayGetBrightness>("DisplayCore.dll", "Display_GetBrightness");
        _set = NativeLoader.GetExport<DisplaySetBrightness>("DisplayCore.dll", "Display_SetBrightness");
    }

    public static bool IsAvailable => _get is not null && _set is not null;

    /// <summary>读第一台可调亮度显示器的亮度。不 Ok 时全零。</summary>
    public static DisplayBrightnessNative GetBrightness()
    {
        if (_get is null)
        {
            return default;
        }
        try
        {
            int hr = _get(out int minVal, out int curVal, out int maxVal, out int ok);
            if (hr != 0)
            {
                return default;
            }
            return new DisplayBrightnessNative(minVal, curVal, maxVal, ok != 0);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>设置亮度。成功返回 0；无支持显示器时也返回 0（由调用方提示）。</summary>
    public static int SetBrightness(int val)
    {
        if (_set is null)
        {
            return -1;
        }
        try
        {
            return _set(val);
        }
        catch
        {
            return -1;
        }
    }
}