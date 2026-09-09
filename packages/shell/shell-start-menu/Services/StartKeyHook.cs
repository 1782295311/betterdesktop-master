using System;
using System.Runtime.InteropServices;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.StartMenu.Services;

/// <summary>
/// Win 键低层键盘钩子（WH_KEYBOARD_LL）：拦截「纯单独按下」的 Win 键（VK_LWIN / VK_RWIN），
/// 用于切换自绘开始菜单。组合键（Win+Space / Win+E / Win+D / Win+R 等）一律 CallNextHookEx 放行。
///
/// 灵敏度修复：原实现 WM_KEYDOWN 时立即判定"单独按下"并触发+吞键，导致用户想按 Win+R 时
/// 先按 Win 的瞬间被误判（R 尚未按下），开始菜单弹出且组合键失效。
/// 现改为：WM_KEYDOWN 仅记录状态不触发；期间任何非修饰键按下标记为组合键；
/// WM_KEYUP 时若为纯单独按下才触发开始菜单。取舍：菜单在松开 Win 时弹出（轻微延迟），
/// 但彻底避免误触组合键，与用户实际按键意图一致。
///
/// v4（已废弃）：WM_KEYDOWN 阶段不吞 Win（依赖 v3 之前的 SendInput 模拟 Win+Space），
///   按下 Win 的瞬间系统开始菜单弹出，随后任何按键都被开始菜单消费成"命令"。
/// v5（已废弃）：WM_KEYDOWN 吞 Win DOWN——试图从源头阻断系统开始菜单弹出，但代价是
///   **所有系统级 Win 组合键（含 Win+Space 切换输入法、Win+E 资源管理器等）一并被吞**，
///   用户反馈"我自己使用 win+空格键都无法切换"。
/// v6（已废弃）：物理 Win DOWN **放行**（不吞），让系统立即识别热键（Win+Space 等正常工作）；
///   单按意图在 WM_KEYUP 检测，此时系统开始菜单已弹出（Win10/11 在 Win DOWN 瞬间弹出），
///   我们发 Esc 关闭它再触发自绘菜单。注入的 Win 键（LLKHF_INJECTED，来自 CycleOnce 模拟
///   Win+Space / SendInput / keybd_event）一律放行，让程序内的输入法切换正常工作。
///   **恶性 bug**：物理 Win UP 被吞（return 1），系统逻辑上认为 Win 键仍按住，
///   自绘菜单弹出后用户按任何键都被解释成 Win 组合键（Win+字母=命令）。
/// v7（当前, 2026-08-30）：物理 Win **UP 一律放行**——让系统正确释放 Win 键状态，
///   后续按键恢复正常。单按 Win 的菜单触发仍在 WM_KEYUP 检测（发 Esc 关系统开始菜单 +
///   触发自绘菜单），组合键检测逻辑不变。注入的 Win 键仍然放行。
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
    // KBDLLHOOKSTRUCT.flags 在 lParam 偏移 8 字节；LLKHF_INJECTED=0x10 表示按键来自 SendInput/keybd_event
    private const int LlkhfInjected = 0x10;
    // Esc：用于单按 Win 触发自绘菜单时关掉被放行的系统开始菜单
    private const byte VkEscape = 0x1B;
    private const uint KeyeventfKeyup = 0x0002;

    private readonly NativeMethods.LowLevelMouseProc _proc;
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
            _hook = NativeMethods.SetWindowsHookEx(WhKeyboardLl, _proc, IntPtr.Zero, 0);
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
            _ = NativeMethods.UnhookWindowsHookEx(_hook);
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
                // KBDLLHOOKSTRUCT.flags 在 lParam + 8；用它识别 SendInput/keybd_event 注入的键
                var flags = Marshal.ReadInt32(lParam, 8);
                var isWin = vk == VkLwin || vk == VkRwin;
                var isInjected = (flags & LlkhfInjected) != 0;

                // 注入的 Win 键（来自 CycleOnce 模拟 Win+Space / SendInput / keybd_event）：
                // 一律放行，让系统正常处理模拟热键——这是恢复"完美运行过"的输入法切换所必需。
                if (isWin && isInjected)
                {
                    return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
                }

                if (msg == WmKeydown || msg == WmSyskeydown)
                {
                    if (isWin && !_winDown)
                    {
                        // v6 (2026-08-30)：物理 Win DOWN **不再吞**——
                        // v5 吞 Win DOWN 会破坏所有系统级组合键（Win+Space 切换输入法、Win+E 资源管理器、
                        // Win+D 显示桌面、Win+L 锁屏 等），与用户"我自己使用 win+空格键都无法切换"的反馈直接冲突。
                        // 新策略：物理 Win DOWN 放行，让系统立即识别热键；单按意图在 WM_KEYUP 检测——
                        // 此时系统开始菜单已弹出（Win10/11 开始菜单在 Win DOWN 瞬间弹出，~150ms 动画），
                        // 我们发 Esc 关闭它（Win10/11 开始菜单响应 Esc），再触发自绘菜单。
                        _winDown = true;
                        _winVk = vk;
                        _comboDetected = false;
                        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam); // 放行
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
                        // Win 键松开：若期间没有组合键（纯单独按下），触发自绘菜单
                        bool wasCombo = _comboDetected;
                        _winDown = false;
                        _winVk = 0;
                        _comboDetected = false;

                        if (!wasCombo)
                        {
                            // 纯单按 Win：放行 Win DOWN 时系统开始菜单已弹出，发 Esc 关掉它，
                            // 然后触发自绘菜单——开始菜单会被 Esc 关闭动画（~100ms），用户看到一闪即过的关闭。
                            SendEscapeToDismissStartMenu();
                            WinKeyPressed?.Invoke(this, EventArgs.Empty);
                        }
                        // v7 (2026-08-30)：物理 Win UP **放行**（不吞）——
                        // v6 吞 UP 导致系统逻辑上认为 Win 键仍按住，自绘菜单弹出后用户按任何键
                        // 都被解释成 Win 组合键（"按什么都是命令"的恶性 bug）。
                        // 放行让系统收到真实的 UP，正确释放 Win 键状态；系统开始菜单已 Esc 关闭，
                        // UP 不会让它重新弹出（Win10/11 开始菜单 toggle 只在 DOWN 时触发）。
                        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
                    }
                }
            }
        }
        catch
        {
            // 钩子回调异常绝不冒泡（M10）。
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>发 Esc 关闭刚被放行 Win DOWN 触发的系统开始菜单。Esc 注入会被本钩子放行到达系统。</summary>
    private void SendEscapeToDismissStartMenu()
    {
        try
        {
            NativeMethods.keybd_event(VkEscape, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(VkEscape, 0, KeyeventfKeyup, UIntPtr.Zero);
        }
        catch
        {
            // Esc 发送失败忽略——开始菜单没关掉不影响自绘菜单触发
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

}
