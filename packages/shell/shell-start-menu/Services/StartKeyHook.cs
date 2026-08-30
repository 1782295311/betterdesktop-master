using System;
using System.Runtime.InteropServices;

namespace BetterDesktop.Shell.StartMenu.Services;

/// <summary>
/// Win 键低层键盘钩子（WH_KEYBOARD_LL）：拦截「纯单独按下」的 Win 键（VK_LWIN / VK_RWIN），
/// 用于切换自绘开始菜单。组合键（Win+R / Win+D / Win+E / Win+Space 等）一律 CallNextHookEx 放行。
///
/// 灵敏度修复：原实现 WM_KEYDOWN 时立即判定"单独按下"并触发+吞键，导致用户想按 Win+R 时
/// 先按 Win 的瞬间被误判（R 尚未按下），开始菜单弹出且组合键失效。
/// 现改为：WM_KEYDOWN 仅记录状态不触发；期间任何非修饰键按下标记为组合键；
/// WM_KEYUP 时若为纯单独按下才触发开始菜单。取舍：菜单在松开 Win 时弹出（轻微延迟），
/// 但彻底避免误触组合键，与用户实际按键意图一致。
///
/// v4（已废弃）：WM_KEYDOWN 阶段不吞 Win，导致按下 Win 的瞬间系统开始菜单弹出，
/// 随后任何按键都被开始菜单消费成"命令"（用户反馈"按什么成命令了"）。
/// v5：**恢复吞 Win DOWN**——WM_KEYDOWN 时 Win 按下立即 return 1 吞掉，仅记录状态。
///   输入法切换已改为"枚举 → 直接激活下一个布局"（TSF 走 ITfInputProcessorProfiles::ActivateProfile、
///   IMM 走 PostMessage WM_INPUTLANGCHANGEREQUEST），不再依赖系统 Win+Space 热键，
///   因此吞掉 Win DOWN 不再影响输入法切换——这是 v4 放行 Win 的唯一理由，现已消失。
/// 取舍（与 v2/v3 一致）：系统级 Win 组合键（Win+R / Win+E / Win+D 等）会被一并吞掉，
/// 用户若要使用需经自绘开始菜单或其它入口；这是"Win 键接管为自绘菜单"的固有代价。
/// 钩子回调内 try-catch，异常绝不冒泡（M10）；回调运行在系统线程。
/// </summary>
public sealed class StartKeyHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmKeyup = 0x0101;
    private const int WmSyskeydown = 0x0104;
    private const int WmSyskeyup = 0x0105;
    private const int VkLwin = 0x5B;
    private const int VkRwin = 0x5C;
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkMenu = 0x12;

    private readonly HookProc _proc;
    private IntPtr _hook;
    private bool _installed;
    // Win 键按下期间的状态：哪一侧 Win 按下、是否检测到组合键（其他非修饰键按下）
    private bool _winDown;
    private int _winVk;
    private bool _comboDetected;

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
            if (nCode >= 0)
            {
                var msg = (int)wParam;
                var vk = Marshal.ReadInt32(lParam);
                var isWin = vk == VkLwin || vk == VkRwin;

                if (msg == WmKeydown || msg == WmSyskeydown)
                {
                    if (isWin && !_winDown)
                    {
                        // Win 键按下：仅记录状态，不立即触发。
                        // v5：吞掉 Win DOWN（return 1），防止系统开始菜单在按下瞬间弹出；
                        // 否则后续任何按键都会被开始菜单消费（v4 实机反馈"按什么成命令了"）。
                        // 输入法切换已改为直接激活布局，不依赖系统 Win+Space，吞 Win 无副作用。
                        _winDown = true;
                        _winVk = vk;
                        _comboDetected = false;
                        return (IntPtr)1;
                    }
                    else if (_winDown && !isWin)
                    {
                        // Win 键按下期间按了其他键 → 组合键，标记并放行
                        // 修饰键（Ctrl/Shift/Alt）不算组合键触发标记，但仍放行
                        if (vk != VkControl && vk != VkShift && vk != VkMenu)
                        {
                            _comboDetected = true;
                        }
                    }
                }
                else if (msg == WmKeyup || msg == WmSyskeyup)
                {
                    if (isWin && _winDown && vk == _winVk)
                    {
                        // Win 键松开：若期间没有组合键（纯单独按下），触发开始菜单
                        bool wasCombo = _comboDetected;
                        _winDown = false;
                        _winVk = 0;
                        _comboDetected = false;

                        if (!wasCombo)
                        {
                            WinKeyPressed?.Invoke(this, EventArgs.Empty);
                        }
                        // 吞掉 Win 键的松开消息
                        return (IntPtr)1;
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
