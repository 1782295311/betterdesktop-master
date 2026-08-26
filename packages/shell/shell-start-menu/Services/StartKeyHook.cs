using System;
using System.Runtime.InteropServices;

namespace BetterDesktop.Shell.StartMenu.Services;

/// <summary>
/// Win 键低层键盘钩子（WH_KEYBOARD_LL）：仅拦截「单独按下」的 Win 键（VK_LWIN / VK_RWIN），
/// 用于切换自绘开始菜单。组合键（Win+R / Win+D / Win+E 等）一律 CallNextHookEx 放行。
/// 钩子回调内 try-catch，异常绝不冒泡（M10）；回调运行在系统线程。
/// </summary>
public sealed class StartKeyHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmSyskeydown = 0x0104;
    private const int VkLwin = 0x5B;
    private const int VkRwin = 0x5C;
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkMenu = 0x12;

    private readonly HookProc _proc;
    private IntPtr _hook;
    private bool _installed;

    public StartKeyHook()
    {
        // 保存委托引用防 GC 回收导致回调崩溃。
        _proc = HookCallback;
    }

    /// <summary>单独按下 Win 键（菜单切换信号）。</summary>
    public event EventHandler? WinKeyPressed;

    public bool IsInstalled => _installed;

    public bool Install()
    {
        if (_installed)
        {
            return true;
        }

        try
        {
            _hook = SetWindowsHookEx(WhKeyboardLl, _proc, IntPtr.Zero, 0);
            _installed = _hook != IntPtr.Zero;
            return _installed;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (!_installed)
        {
            return;
        }

        try
        {
            _ = UnhookWindowsHookEx(_hook);
        }
        catch
        {
            // 卸载失败忽略
        }

        _hook = IntPtr.Zero;
        _installed = false;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && (wParam == (IntPtr)WmKeydown || wParam == (IntPtr)WmSyskeydown))
            {
                var vk = Marshal.ReadInt32(lParam);
                var isLWin = vk == VkLwin;
                var isRWin = vk == VkRwin;

                if (isLWin || isRWin)
                {
                    // 组合键判定：Ctrl/Shift/Alt 按下，或另一侧 Win 按下（同时按双 Win）。
                    var combo = IsKeyDown(VkControl) || IsKeyDown(VkShift) || IsKeyDown(VkMenu)
                        || (isLWin && IsKeyDown(VkRwin)) || (isRWin && IsKeyDown(VkLwin));

                    if (!combo)
                    {
                        WinKeyPressed?.Invoke(this, EventArgs.Empty);
                        return (IntPtr)1; // 吞掉该按键，菜单自行接管
                    }
                }
            }
        }
        catch
        {
            // 钩子回调异常绝不冒泡（M10）。
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsKeyDown(int vk)
    {
        // 高位为 1 表示按下。
        return (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
