using System;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterDesktop.Shell.Clipboard.Native;

/// <summary>
/// 剪贴板原生收口（全部 internal static，不外露——公共契约在 api 包）。
/// 覆盖：剪贴板格式监听、前台窗口来源、SendPaste 键鼠注入、DPAPI 加密（零 NuGet 依赖）。
/// </summary>
internal static partial class ClipboardNative
{
    // ---------- user32.dll ----------

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    /// <summary>剪贴板序列号：内容每次变化 +1。作为 WM_CLIPBOARDUPDATE 广播不可用时的轮询兜底依据。</summary>
    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    /// <summary>keybd_event：模拟 Ctrl+V 粘贴到前台窗口（探索版同款）。</summary>
    [DllImport("user32.dll")]
    internal static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    internal const byte VK_CONTROL = 0x11;
    internal const byte VK_V = 0x56;
    internal const uint KEYEVENTF_KEYUP = 0x0002;

    // ---------- crypt32.dll（DPAPI，当前用户加密） ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public uint cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>DPAPI 加密（当前用户，无需额外密钥；失败抛 Win32 异常）。</summary>
    internal static byte[] Protect(byte[] plain)
    {
        if (plain is null || plain.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var input = new DATA_BLOB { cbData = (uint)plain.Length, pbData = Marshal.AllocHGlobal(plain.Length) };
        try
        {
            Marshal.Copy(plain, 0, input.pbData, plain.Length);
            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out DATA_BLOB output))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CryptProtectData 失败");
            }

            try
            {
                var result = new byte[output.cbData];
                if (output.cbData > 0)
                {
                    Marshal.Copy(output.pbData, result, 0, (int)output.cbData);
                }

                return result;
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
        }
    }

    /// <summary>DPAPI 解密；失败抛 Win32 异常（调用方按损坏数据处理）。</summary>
    internal static byte[] Unprotect(byte[] cipher)
    {
        if (cipher is null || cipher.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var input = new DATA_BLOB { cbData = (uint)cipher.Length, pbData = Marshal.AllocHGlobal(cipher.Length) };
        try
        {
            Marshal.Copy(cipher, 0, input.pbData, cipher.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out DATA_BLOB output))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CryptUnprotectData 失败");
            }

            try
            {
                var result = new byte[output.cbData];
                if (output.cbData > 0)
                {
                    Marshal.Copy(output.pbData, result, 0, (int)output.cbData);
                }

                return result;
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
        }
    }
}
