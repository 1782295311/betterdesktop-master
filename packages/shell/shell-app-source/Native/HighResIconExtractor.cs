using System;
using System.Runtime.InteropServices;

namespace BetterDesktop.Shell.AppSource.Native;

/// <summary>
/// shell32 私有导出 <c>SHExtractIconsW</c> 高清图标提取（7444 契约 A 变体 A 移植：
/// Open-Shell ResourceHelper.cpp L270-300 [verified]）。
/// 红线落地：
///   ①必须 GetProcAddress 动态加载（无公开导出名，静态 DllImport 入口点不存在——红线 1）；
///   ②私有失败必须回退公开 ExtractIconEx（降级链是契约一部分——红线 2）；
///   ③返回 0 = 提取失败（不是图标数——红线/错误写法 4）；
///   ④签名含 pid 出参 + flags（比 ExtractIconEx 多 pid——红线 4）；
///   ⑤函数指针静态缓存只解析一次（红线 5）；
///   ⑥HICON 用后由调用方 DestroyIcon（本项目走 IconImageConverter.GetImageFromHIcon 自动释放路径）。
/// </summary>
internal static class HighResIconExtractor
{
    private const uint LrDefaultColor = 0x00000000; // Open-Shell 调用传 LR_DEFAULTCOLOR(0)

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate uint SHExtractIconsWDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        int iconIndex,
        int cxIcon,
        int cyIcon,
        out IntPtr phIcon,
        out uint pid,
        uint nIcons,
        uint flags);

    private static SHExtractIconsWDelegate? _extract; // 红线 5：静态缓存，只解析一次

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private static SHExtractIconsWDelegate? Resolve()
    {
        if (_extract is not null)
        {
            return _extract;
        }

        var hShell32 = GetModuleHandle("shell32.dll");
        if (hShell32 == IntPtr.Zero)
        {
            return null;
        }

        var proc = GetProcAddress(hShell32, "SHExtractIconsW");
        if (proc == IntPtr.Zero)
        {
            return null; // 红线 2 前半：私有导出不存在 → 调用方降级 ExtractIconEx
        }

        _extract = Marshal.GetDelegateForFunctionPointer<SHExtractIconsWDelegate>(proc);
        return _extract;
    }

    /// <summary>
    /// 从文件提取指定尺寸（含 256px Jumbo）图标。返回 HICON（调用方负责 DestroyIcon）；
    /// 私有导出不可用/提取失败返回 IntPtr.Zero（调用方降级公开路径）。
    /// </summary>
    public static IntPtr TryExtract(string path, int index, int size)
    {
        if (string.IsNullOrWhiteSpace(path) || size <= 0)
        {
            return IntPtr.Zero;
        }

        var fn = Resolve();
        if (fn is null)
        {
            return IntPtr.Zero;
        }

        try
        {
            // 红线 3：返回 0 = 提取失败，hIcon 无效。
            if (fn(path, index, size, size, out var hIcon, out _, 1, LrDefaultColor) == 0)
            {
                return IntPtr.Zero;
            }
            return hIcon;
        }
        catch
        {
            return IntPtr.Zero; // 平台差异不崩溃（7444：私有导出脆弱，降级兜底）
        }
    }

    /// <summary>
    /// 降级路径：公开 ExtractIconEx 提取（7444 红线 2 的回退半边）。返回 HICON 或 IntPtr.Zero。
    /// </summary>
    public static IntPtr ExtractViaPublicApi(string path, int index)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return IntPtr.Zero;
        }

        try
        {
            // ExtractIconExW(path, index, phIconLarge, phIconSmall, nIcons)：取大图标。
            if (ExtractIconEx(path, index, out var hIcon, IntPtr.Zero, 1) == 1)
            {
                return hIcon;
            }
        }
        catch
        {
        }
        return IntPtr.Zero;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, out IntPtr phiconLarge, IntPtr phiconSmall, uint nIcons);
}
