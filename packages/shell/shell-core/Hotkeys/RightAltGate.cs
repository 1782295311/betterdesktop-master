using System;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.Core.Hotkeys;

/// <summary>
/// 右 Alt 门控（热键侧板两态切换的输入前提，见热键实施计划 §8）。
/// <para>
/// 语义：**按住门控键 + 目标命中（侧板卡片被鼠标指向）→ 允许切到可操作态**；
/// 何时回只读由消费方决定（热键侧板：鼠标一离开卡片即恢复穿透，另有空闲超时与「完成」按钮）。
/// 门控键默认右 Alt（<c>VK_RMENU</c>），可配置（右 Ctrl / 右 Shift / 仅当 BetterDesktop 表面前台时）。
/// </para>
/// <para>
/// 【AltGr 冲突（必须处理的真机风险）】欧语布局下 AltGr 由 <c>Ctrl + 右 Alt</c> 合成，输入
/// <c>@</c>/<c>{</c>/<c>}</c> 都要按住右 Alt。因此门控必须满足"按住 **且** 点击落在侧板上"
/// 两个条件同时成立才切换，不允许"一按右 Alt 就切态"；且门控键为右 Alt 时若 Ctrl 同时按住
/// （疑似 AltGr 输入），保守判定不激活——见 <see cref="ShouldSwitchToInteractive"/>。
/// </para>
/// </summary>
public sealed class RightAltGate : IDisposable
{
    private readonly KeyboardHook _hook;

    /// <summary>门控键虚拟键码（默认右 Alt）。</summary>
    public int GateVk { get; }

    /// <summary>门控键当前是否按住（钩子线程更新；UI 订阅 <see cref="StateChanged"/> 自行编队）。</summary>
    public bool IsDown { get; private set; }

    /// <summary>门控键起落变化。</summary>
    public event Action? StateChanged;

    /// <param name="gateVk">门控键虚拟键码，默认 <see cref="NativeMethods.VK_RMENU"/>。</param>
    public RightAltGate(int gateVk = NativeMethods.VK_RMENU)
    {
        GateVk = gateVk;
        _hook = new KeyboardHook(OnKey);
        _hook.Start();
    }

    private bool OnKey(int msg, NativeMethods.KBDLLHOOKSTRUCT info)
    {
        if (info.vkCode == GateVk)
        {
            if (msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
            {
                if (!IsDown)
                {
                    IsDown = true;
                    StateChanged?.Invoke();
                }
            }
            else if (msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
            {
                if (IsDown)
                {
                    IsDown = false;
                    StateChanged?.Invoke();
                }
            }
        }

        return false; // 不吞键：门控只观察
    }

    /// <summary>
    /// 双条件判定（纯函数，单测覆盖）：门控键按住 **且** 目标命中 → 允许切可操作态。
    /// <para>
    /// 这里只判"是否允许"；"目标命中"的判据由消费方给出（热键侧板当前用**鼠标悬停在卡片内**，
    /// 而不是点击 —— 点击是瞬时事件，在 50ms 轮询下会漏检，见 HotkeyPanelWindow.PollGate）。
    /// </para>
    /// <para>
    /// AltGr 防护：门控键为右 Alt 且 Ctrl 同时按住 → 视为 AltGr 字符输入，不激活；
    /// 门控键为右 Ctrl / 右 Shift 时不做该防护（AltGr 合成不涉及这两个键位）。
    /// </para>
    /// </summary>
    /// <param name="gateKeyDown">门控键是否按住。</param>
    /// <param name="ctrlHeld">Ctrl（左或右）是否按住。</param>
    /// <param name="targetHit">目标（侧板卡片）是否命中。</param>
    /// <param name="gateVk">门控键虚拟键码。</param>
    public static bool ShouldSwitchToInteractive(bool gateKeyDown, bool ctrlHeld, bool targetHit, int gateVk)
    {
        if (!gateKeyDown || !targetHit)
        {
            return false;
        }

        if (gateVk == NativeMethods.VK_RMENU && ctrlHeld)
        {
            return false; // 右 Alt + Ctrl = AltGr 输入（欧语布局），保守不激活
        }

        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _hook.Dispose();
    }
}
