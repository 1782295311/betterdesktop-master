// BetterDesktop.Clipboard.Panel — 引擎启动请求薄封装
// 实际实现已下沉到 IPC 包共享类 ClipboardEngineLauncher（宿主 ClipboardPlugin 与面板共用同一套组件名），
// 避免两侧各自硬编码"该找哪个 exe"导致「一边找得到、一边找不到」重复拉起抢管道。
// 本类只做 PanelLog 日志转发，保持 App.xaml.cs / PanelMainWindow 既有调用点不变。
//
// 【2026-09-20 收敛】不再有 Locate* —— exe 定位是 core 的职责（core/src/process.rs::resolve_exe
// 是"组件 exe 从哪来"的唯一定义）。面板只需说"请确保引擎在跑"，路径由 core 解析。

using BetterDesktop.Shell.Clipboard.Ipc;

namespace BetterDesktop.Clipboard.Panel;

/// <summary>引擎启动请求（转发 <see cref="ClipboardEngineLauncher"/>，日志落 panel.log）。</summary>
public static class EngineProber
{
    /// <summary>请 core 确保引擎在跑（不等待、不探活结果——连接由 IPC 客户端重连循环兜底）。</summary>
    public static bool EnsureEngine() => ClipboardEngineLauncher.EnsureEngine(PanelLog.Trace);
}
