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
}
