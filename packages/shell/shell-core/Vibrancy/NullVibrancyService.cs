// BetterDesktop.Shell.Core — IVibrancyService 的空实现（无毛玻璃的宿主/headless/预览场景）
//
// 【2026-09-14 收口】此前有 5 份各自私有的空壳（desktop / quick-note / menu-bar ×2 / 组件演练场），
// 其中一份的注释写明"与 MenuBarPlugin.NullVibrancy 独立，避免相互 internal 引用"——
// 即**重复的原因只是 internal 不可见**。这里给一个 public 单例，重复的理由随之消失。

using System;

namespace BetterDesktop.Shell.Core.Vibrancy;

/// <summary>
/// 无毛玻璃实现的空壳：把 <see cref="IVibrancyService"/> 的空缺补齐，保证调用方不必判空。
/// 语义 = "什么都不做"（窗口保持 WPF 自身的透明/纯色，不叠加任何系统材质）。
/// </summary>
public sealed class NullVibrancyService : IVibrancyService
{
    /// <summary>单例（无状态，可跨窗口复用）。</summary>
    public static NullVibrancyService Instance { get; } = new();

    private NullVibrancyService()
    {
    }

    /// <inheritdoc />
    public void Apply(IntPtr hwnd, VibrancyStyle style, bool roundCorners, bool smallRadius)
    {
    }

    /// <inheritdoc />
    public void Disable(IntPtr hwnd)
    {
    }
}
