using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Status.Contracts;

namespace BetterDesktop.Shell.Status.Services;

/// <summary>
/// 用户头像服务实现（7444 契约 B 变体 A 移植：Open-Shell MenuPaint.cpp L129-135 [verified]）。
/// shell32 序数 #261 SHGetUserPicturePath 取当前用户头像图片路径。
/// 红线落地：
///   ①必须序数调用（GetProcAddress + MAKEINTRESOURCEA(261) 低 16 位序数——按名调用入口点不存在，红线 1）；
///   ②第一参传 NULL（红线 2）；
///   ③路径缓冲由调用方提供（StringBuilder(512)，红线 3）；
///   ④GetProcAddress 失败/HRESULT 非 0 → 返回 null 降级，不崩溃（红线 4）。
/// </summary>
public sealed class UserAvatarService : IUserAvatarService
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate int SHGetUserPicturePathDelegate(
        IntPtr p1,             // 第一参传 NULL（红线 2）
        uint flags,            // 0x80000000
        [MarshalAs(UnmanagedType.LPWStr)] StringBuilder path,
        uint pathLength);

    private static SHGetUserPicturePathDelegate? _fn; // 静态缓存，只解析一次

    /// <inheritdoc />
    public Task<string?> GetUserPicturePathAsync()
    {
        // 序数解析与调用都是纯同步 Win32（微秒级），无需专门线程。
        return Task.FromResult(GetUserPicturePath());
    }

    /// <summary>同步取当前用户头像图片文件路径。失败/系统不支持返回 null。</summary>
    public static string? GetUserPicturePath()
    {
        try
        {
            if (_fn is null)
            {
                var hShell32 = NativeMethods.GetModuleHandle("shell32.dll");
                if (hShell32 == IntPtr.Zero)
                {
                    return null;
                }

                // MAKEINTRESOURCEA(261) = 序数 261 的资源名（低 16 位为序数）。
                var proc = NativeMethods.GetProcAddress(hShell32, (IntPtr)261);
                if (proc == IntPtr.Zero)
                {
                    return null; // 红线 4 前半：不判 NULL 直接调 → 崩溃
                }

                _fn = Marshal.GetDelegateForFunctionPointer<SHGetUserPicturePathDelegate>(proc);
            }

            var sb = new StringBuilder(512); // 红线 3：缓冲不足返回错误
            return _fn(IntPtr.Zero, 0x80000000u, sb, (uint)sb.Capacity) == 0 && sb.Length > 0
                ? sb.ToString()
                : null; // 红线 4 后半：HRESULT 非 0 → 降级（不显示头像）
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or AccessViolationException)
        {
            _fn = null; // 序数在不同系统可能漂移：重置缓存，下次重试解析
            return null;
        }
        catch
        {
            return null;
        }
    }


}
