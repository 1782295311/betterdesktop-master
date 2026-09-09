// BetterDesktop.Shell.ContextMenus — 常驻 STA COM 工作线程（P2-D 修复，2026-09-03 诊断批）
//
// in-proc shell COM（IContextMenu/IExplorerCommand）的 RCW 必须在其创建的 STA 套间内使用：
// 跨套间调用需要该套间泵消息。此前 ShellMenuInterop.Query 每次 new 一个临时 STA 线程并 Join——
// 线程在 Query 返回后即退出，用户点击菜单项（UI 线程）时跨套间调 InvokeCommand 封送目标已死
// → 调用失败被空 catch 吞掉 = "第三方项看到了点了没反应"。
//
// 现改为单例常驻线程跑 Dispatcher.Run（WPF Dispatcher 自带 Win32 消息泵，满足 STA 泵要求）；
// Query 与 Invoke 全部经 Dispatcher 编队执行。惰性启动（首次 COM 调用才建线程）。

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace BetterDesktop.Shell.ContextMenus.Services;

internal static class StaComWorker
{
    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;

    public static T Run<T>(Func<T> func)
        => EnsureStarted().Invoke(func, DispatcherPriority.Normal);

    public static void Run(Action action)
        => Run<object?>(() =>
        {
            action();
            return null;
        });

    /// <summary>
    /// 异步派发（不阻塞调用线程）。原生弹层的 TrackPopupMenuEx 是模态循环——必须 fire-and-forget，
    /// UI 线程同步 Run 会把菜单模态周期变成宿主卡死（M0 分流纪律）。
    /// </summary>
    public static void Begin(Action action)
        => EnsureStarted().BeginInvoke(action, DispatcherPriority.Normal);

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr pvReserved);

    private static Dispatcher EnsureStarted()
    {
        lock (Gate)
        {
            if (_dispatcher is { HasShutdownStarted: false } live)
            {
                return live;
            }

            Dispatcher? captured = null;
            using var ready = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                // 2026-09-04 崩溃修复：shell 扩展宿主线程必须 OleInitialize（Ole 层：剪贴板/拖放/激活）。
                // CLR 只隐式 CoInitializeEx(STA)，不会调 OleInitialize——缺它时部分背景场景第三方
                // handler 在 QueryContextMenu 内部 Ole 调用上 AV（真机崩溃栈实证两次：
                // QueryBackground → QueryContextMenu → coreclr 0xc0000005）。
                // OleInitialize 在 STA 线程上同时完成 CoInitialize；已初始化/变更模式时忽略。
                try { _ = OleInitialize(IntPtr.Zero); } catch { /* ole32 缺失不阻断 */ }
                captured = Dispatcher.CurrentDispatcher;
                _dispatcher = captured;
                ready.Set();
                Dispatcher.Run(); // 消息泵：STA COM 跨套间调用依赖它
            })
            {
                IsBackground = true,
                Name = "shell-context-menu-sta",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
            return captured!;
        }
    }
}
