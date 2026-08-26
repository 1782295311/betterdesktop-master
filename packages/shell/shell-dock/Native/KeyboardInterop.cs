using System;
using System.Runtime.InteropServices;

namespace BetterDesktop.Shell.Dock.Native;

/// <summary>
/// 键盘模拟的 Interop 封装（SendInput）。
/// 仅用于开始菜单右键菜单里无法用普通命令启动的项（Win+R 运行对话框 / Win+D 显示桌面）。
/// SendInput 返回值必须检查：UIPI 前台锁或桌面行为差异时返回 0 即失败，调用方据此降级（M9/M10）。
/// </summary>
internal static class KeyboardInterop
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x2;
    private const uint KeyEventScanCode = 0x8;
    private const byte VkLwin = 0x5B;
    private const byte VkR = 0x52;
    private const byte VkD = 0x44;
    private const byte VkX = 0x58;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public KEYBDINPUT Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private static INPUT KeyDown(byte vk)
        => new() { Type = InputKeyboard, Ki = new KEYBDINPUT { Vk = vk, Flags = KeyEventScanCode } };

    private static INPUT KeyUp(byte vk)
        => new() { Type = InputKeyboard, Ki = new KEYBDINPUT { Vk = vk, Flags = KeyEventKeyUp | KeyEventScanCode } };

    /// <summary>发送 Win+R（打开运行对话框）。失败返回 false（不抛异常）。</summary>
    public static bool OpenRunDialog()
        => SendWinCombo(VkR);

    /// <summary>发送 Win+D（显示桌面 / 恢复窗口）。失败返回 false（不抛异常）。</summary>
    public static bool ShowDesktop()
        => SendWinCombo(VkD);

    /// <summary>发送 Win+X（弹出系统原生「电源用户」菜单，即开始按钮右键菜单）。失败返回 false（不抛异常）。</summary>
    public static bool ShowWindowsXMenu()
        => SendWinCombo(VkX);

    /// <summary>按下并松开 Win + key 组合键，检查 SendInput 返回值（一次调用送入 4 个 INPUT，返回值须为 4 才算成功）。</summary>
    private static bool SendWinCombo(byte key)
    {
        try
        {
            var inputs = new[]
            {
                KeyDown(VkLwin),
                KeyDown(key),
                KeyUp(key),
                KeyUp(VkLwin)
            };
            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            return sent == inputs.Length;
        }
        catch
        {
            return false;
        }
    }
}