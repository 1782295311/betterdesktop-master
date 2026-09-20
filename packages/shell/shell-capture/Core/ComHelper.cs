using System;
using System.Runtime.InteropServices;

namespace BetterDesktop.Shell.Capture.Core;

/// <summary>COM 线程模型初始化（采集在 MTA 工作线程执行；已初始化则跳过，不破坏调用方状态）。</summary>
internal static class ComHelper
{
    private const uint COINIT_MULTITHREADED = 0x0;

    /// <summary>RPC_E_CHANGED_MODE：线程已按其他模型初始化（如 WPF STA）——不得 CoUninitialize。</summary>
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    /// <summary>尝试把当前线程初始化为 MTA；返回需要成对 CoUninitialize 的令牌（或 null = 无需操作）。</summary>
    public static IDisposable? TryInitializeMta()
    {
        int hr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
        // S_OK=0（新初始化）或 S_FALSE=1（已是 MTA）都必须成对 CoUninitialize。
        return hr is 0 or 1 ? new Uninit() : null;
    }

    private sealed class Uninit : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                CoUninitialize();
            }
        }
    }
}
