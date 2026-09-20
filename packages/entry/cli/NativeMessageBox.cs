using System.Runtime.InteropServices;

namespace BetterDesktop.Cli;

/// <summary>原生消息框（P/Invoke user32，零 WPF 依赖——CLI 不启动 Application，反馈走 Win32）。</summary>
internal static class NativeMessageBox
{
    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;
    private const uint MbIconInformation = 0x00000040;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    public static void ShowError(string text) => MessageBoxW(IntPtr.Zero, text, "BetterDesktop", MbOk | MbIconError);

    public static void ShowInfo(string text) => MessageBoxW(IntPtr.Zero, text, "BetterDesktop", MbOk | MbIconInformation);
}
