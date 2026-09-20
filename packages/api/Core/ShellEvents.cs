// BetterDesktop.Shell.Core — ShellEvents 外壳层事件名常量
// 事件名约定「域/动作」，见 ADR-002 D4。跨程序集通信唯一通道，禁止跨程序集裸 C# event。
// 仿 kernel-hmr/HmrEvents 模式。

namespace BetterDesktop.Shell.Core;

/// <summary>外壳层事件名常量（事件名约定「域/动作」，见 ADR-002 D4）。</summary>
public static class ShellEvents
{
    /// <summary>设置键值变更（Set 时触发，加载时不触发）。载荷：SettingsChangedEventArgs。</summary>
    public const string SettingsChanged = "shell.settings/changed";

    /// <summary>外观令牌变更（含何种令牌变更，供订阅方按需局部重绘）。载荷：AppearanceChangedArgs。</summary>
    public const string AppearanceChanged = "shell.appearance/changed";

    /// <summary>
    /// 活动呈现变更（灵动岛等表面消费）。载荷：<see cref="Activity.Contracts.ActivityChangedNotice"/>。
    /// <para>
    /// 【为什么需要桥】<c>IActivityService</c> 按 ADR-002 D4 不在接口上声明裸 C# event，
    /// 变更通知由 shell-core 的具体实现类级 <c>Changed</c> 事件提供；跨包消费（岛渲染）经本事件名中转。
    /// 只在「当前活动身份」或「队列顺序」变化时触发；同 Id 的进度更新不触发（订阅方按帧/慢轮询采样 <c>Current</c>）。
    /// </para>
    /// </summary>
    public const string ActivityChanged = "shell.activity/changed";

    /// <summary>
    /// 剪贴板「按序粘贴 / 按格粘」会话状态（灵动岛等表面消费）。载荷：<see cref="Clipboard.Contracts.ClipboardPasteSessionNotice"/>。
    /// <para>
    /// 【为什么需要这条桥】会话队列活在**面板 exe 进程**（<c>ClipboardIpcClient</c> 的按序粘贴状态机），
    /// 壳进程看不到 → 面板经 <c>BetterDesktop.MenuCmd</c> 命名管道把**只含进度**的状态推给宿主，宿主再广播本条事件。
    /// **隐私红线**：载荷只有"第几项/共几项"，绝不含剪贴板内容（2026-09-16 用户明确要求：
    /// "随便复制就显示会暴露用户隐私"——因此岛不再因复制而弹出，只有用户主动发起的按序粘贴会话才上屏）。
    /// </para>
    /// </summary>
    public const string ClipboardPasteSession = "shell.clipboard/paste-session";
}
