using System;
using System.Runtime.InteropServices;

namespace BetterDesktop.Shell.Capture.Native;

/// <summary>
/// 剪贴板写入原生接口（截图落剪贴板：CF_DIB + "PNG" 注册格式）。
/// 写剪贴板是瞬时的（毫秒级），OpenClipboard 竞争按既有纪律重试有限次后放弃并报可读错误。
/// </summary>
internal static class NativeClipboard
{
    public const uint CF_DIB = 8;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    private const uint GMEM_MOVEABLE = 0x0002;

    /// <summary>注册「PNG」剪贴板格式（进程内缓存；0 = 失败）。</summary>
    public static uint PngFormat()
    {
        // RegisterClipboardFormatW 幂等且跨进程一致；每次调用成本极低，无需缓存。
        return RegisterClipboardFormatW("PNG");
    }

    /// <summary>
    /// 一次性写入多份格式（EmptyClipboard 只调一次；任一份失败则整次视为失败，由调用方报错）。
    /// 数据字节由本方法拷贝进全局内存后立即释放；调用方持有的 byte[] 生命周期不受影响。
    /// </summary>
    public static bool WriteFormats(params (uint Format, byte[] Data)[] formats)
    {
        if (formats.Length == 0)
        {
            return false;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                System.Threading.Thread.Sleep(50);
                continue;
            }

            try
            {
                if (!EmptyClipboard())
                {
                    return false;
                }

                bool ok = true;
                foreach (var (format, data) in formats)
                {
                    if (!SetFormat(format, data))
                    {
                        ok = false;
                        break;
                    }
                }
                return ok;
            }
            finally
            {
                CloseClipboard();
            }
        }
        return false;
    }

    private static bool SetFormat(uint format, byte[] data)
    {
        IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (hMem == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            IntPtr dst = GlobalLock(hMem);
            if (dst == IntPtr.Zero)
            {
                GlobalFree(hMem);
                return false;
            }

            try
            {
                Marshal.Copy(data, 0, dst, data.Length);
            }
            finally
            {
                GlobalUnlock(hMem);
            }

            // SetClipboardData 成功时接管 hMem；失败时归还给调用方释放（避免泄漏）。
            IntPtr result = SetClipboardData(format, hMem);
            if (result != IntPtr.Zero)
            {
                return true;
            }

            GlobalFree(hMem);
            return false;
        }
        catch
        {
            GlobalFree(hMem);
            return false;
        }
    }
}
