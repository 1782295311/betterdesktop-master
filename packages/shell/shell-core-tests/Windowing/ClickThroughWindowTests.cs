using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Core.Windowing;
using Xunit;

namespace BetterDesktop.Shell.Core.Tests.Windowing;

/// <summary>
/// 点击穿透两态原语测试（对真实隐藏窗口断言扩展样式位）。
/// </summary>
public class ClickThroughWindowTests : IDisposable
{
    private readonly List<IntPtr> _windows = new();
    private readonly List<NativeMethods.WndProcDelegate> _procs = new(); // 强引用防 GC

    [Fact]
    public void SetClickThrough_sets_and_clears_bit()
    {
        var hwnd = CreateHiddenWindow();
        Assert.False(ClickThroughWindow.IsClickThrough(hwnd));

        ClickThroughWindow.SetClickThrough(hwnd, true);
        Assert.True(ClickThroughWindow.IsClickThrough(hwnd));

        ClickThroughWindow.SetClickThrough(hwnd, false);
        Assert.False(ClickThroughWindow.IsClickThrough(hwnd));
    }

    [Fact]
    public void Interactive_clears_no_activate_for_keyboard_input()
    {
        // 可操作态（录键等键盘输入）必须去掉 WS_EX_NOACTIVATE；只读态恢复（常驻不抢焦点）。
        var hwnd = CreateHiddenWindow();
        ClickThroughWindow.SetClickThrough(hwnd, true);
        Assert.NotEqual(0, NativeMethods.GetWindowLong(hwnd, -20) & 0x08000000);

        ClickThroughWindow.SetClickThrough(hwnd, false);
        Assert.Equal(0, NativeMethods.GetWindowLong(hwnd, -20) & 0x08000000);

        ClickThroughWindow.SetClickThrough(hwnd, true);
        Assert.NotEqual(0, NativeMethods.GetWindowLong(hwnd, -20) & 0x08000000);
    }

    [Fact]
    public void Interactive_can_keep_no_activate_for_mouse_only_panels()
    {
        // 纯鼠标操作的常驻浮层（热键侧板）：可操作态必须**保留** NOACTIVATE——
        // 点一下隐藏/恢复按钮不该把用户正在用的应用顶到后台（侧板不做键盘输入）。
        var hwnd = CreateHiddenWindow();
        ClickThroughWindow.SetClickThrough(hwnd, true); // 只读态：带上 NOACTIVATE
        ClickThroughWindow.SetClickThrough(hwnd, false, keepNoActivate: true);
        Assert.False(ClickThroughWindow.IsClickThrough(hwnd));
        Assert.NotEqual(0, NativeMethods.GetWindowLong(hwnd, -20) & 0x08000000);
    }

    [Fact]
    public void SetClickThrough_is_idempotent()
    {
        var hwnd = CreateHiddenWindow();
        ClickThroughWindow.SetClickThrough(hwnd, true);
        ClickThroughWindow.SetClickThrough(hwnd, true);
        Assert.True(ClickThroughWindow.IsClickThrough(hwnd));

        ClickThroughWindow.SetClickThrough(hwnd, false);
        ClickThroughWindow.SetClickThrough(hwnd, false);
        Assert.False(ClickThroughWindow.IsClickThrough(hwnd));
    }

    [Fact]
    public void Zero_hwnd_is_noop()
    {
        ClickThroughWindow.SetClickThrough(IntPtr.Zero, true); // 不抛
        Assert.False(ClickThroughWindow.IsClickThrough(IntPtr.Zero));
    }

    [Fact]
    public void Composes_with_make_floating_no_activate()
    {
        var hwnd = CreateHiddenWindow();
        WindowStyleHelper.MakeFloatingNoActivate(hwnd);
        ClickThroughWindow.SetClickThrough(hwnd, true);
        Assert.True(ClickThroughWindow.IsClickThrough(hwnd));
    }

    private IntPtr CreateHiddenWindow()
    {
        var proc = new NativeMethods.WndProcDelegate((h, m, w, l) => NativeMethods.DefWindowProc(h, m, w, l));
        _procs.Add(proc);
        var className = "bdClickThroughTest_" + Guid.NewGuid().ToString("N");
        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = className,
        };
        Assert.NotEqual(0u, NativeMethods.RegisterClassEx(ref wc));
        var hwnd = NativeMethods.CreateWindowExW(
            0, className, string.Empty, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero,
            NativeMethods.GetModuleHandle(null), IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, hwnd);
        _windows.Add(hwnd);
        return hwnd;
    }

    public void Dispose()
    {
        foreach (var hwnd in _windows)
        {
            _ = NativeMethods.DestroyWindow(hwnd);
        }

        _windows.Clear();
        _procs.Clear();
    }
}
