// ImeCore.dll 的 C# Interop 薄封装（纯转发，无业务逻辑）。
using System;

namespace BetterDesktop.Shell.Status.Native;

/// <summary>ImeCore.dll 的薄封装：输入法循环切换按键模拟。仅转发，不做业务判断。</summary>
public static class ImeCoreNative
{
    private delegate int ImeCycleOnce();
    private delegate int ImeSimulateCtrlShift();

    private static readonly ImeCycleOnce? _cycle;
    private static readonly ImeSimulateCtrlShift? _ctrlShift;

    static ImeCoreNative()
    {
        _cycle = NativeLoader.GetExport<ImeCycleOnce>("ImeCore.dll", "Ime_CycleOnce");
        _ctrlShift = NativeLoader.GetExport<ImeSimulateCtrlShift>("ImeCore.dll", "Ime_SimulateCtrlShift");
    }

    public static bool IsAvailable => _cycle is not null;

    /// <summary>模拟 Win+Space（全局输入法循环切换一步）。返回是否成功。</summary>
    public static bool CycleOnce()
    {
        if (_cycle is null) return false;
        try { return _cycle() == 0; }
        catch { return false; }
    }

    /// <summary>模拟 Ctrl+Shift（遗留热键路径）。返回是否成功。</summary>
    public static bool SimulateCtrlShift()
    {
        if (_ctrlShift is null) return false;
        try { return _ctrlShift() == 0; }
        catch { return false; }
    }
}
