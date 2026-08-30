// BetterDesktop.Shell.Status — 输入法监控（采集 + 语义）
// 优先用 KeyboardLayoutInterop 拿真实键盘布局名（"美式键盘" / "微软拼音" / "搜狗拼音输入法"），
// 而不是 CultureInfo 的 "中文" / "English"。菜单栏按钮文本取激活项 LayoutName 的首 1-2 字。

using BetterDesktop.Shell.Status.Contracts;
using BetterDesktop.Shell.Status.Native;

namespace BetterDesktop.Shell.Status.Services;

/// <summary>输入法 / 键盘布局状态监控实现。</summary>
public sealed class ImeMonitor : IImeMonitor, IStatusChangeSource, IEventDrivenMonitor
{
    private readonly ISystemSource _source;

    public ImeMonitor(ISystemSource source)
    {
        _source = source;
    }

    public string MonitorId => "ime";

    // IME/键盘布局切换走前台窗口事件：StatusPoller 为 IEventDrivenMonitor 挂的前台 WinEvent 泵
    // 会在前台切换时即时拉取（含用户按 Shift 切中/英）。此处保留 500ms 周期仅作异常兜底，保证按钮显示快速同步。
    public int PollIntervalMilliseconds => 500;

    public event EventHandler<StatusSnapshot>? Changed;

    public void RaiseChanged(StatusSnapshot snapshot) => Changed?.Invoke(this, snapshot);

    public StatusSnapshot GetSnapshot()
    {
        // 优先从真实键盘布局枚举拿当前激活的 LayoutName
        KeyboardLayoutItem? active = null;
        IReadOnlyList<KeyboardLayoutItem>? allLayouts = null;
        try
        {
            allLayouts = KeyboardLayoutInterop.Enumerate();
            active = allLayouts.FirstOrDefault(x => x.IsActive);
            // 兜底：枚举到了布局但没有标记为活动（常见于 TSF 输入法未被旧逻辑识别），
            // 取列表第一项作为当前布局显示，避免降级到粗糙的 KLID → "中文" 映射。
            if (active is null && allLayouts.Count > 0)
            {
                active = allLayouts[0];
            }
        }
        catch
        {
            // 忽略，降级到旧的 KLID 映射
        }

        if (active is not null && !string.IsNullOrEmpty(active.LayoutName))
        {
            // 中/英文模式：对激活的输入法读取 ImmGetConversionStatus 的 NATIVE 位，
            // 用户按 Shift 切中/英后按钮立刻反映（这也是"当前输入法"最核心的语义）。
            bool? chineseMode = null;
            if (active.IsIme)
            {
                // 优先经输入法窗口读（设计稿路径，对 TSF 输入法有效）；
                // 失败再退 ImmGetConversionStatus（对传统 IMM 输入法有效）。
                chineseMode = KeyboardLayoutInterop.ReadConversionModeViaImeWindow()
                    ?? KeyboardLayoutInterop.ReadConversionNativeMode(KeyboardLayoutInterop.GetActiveHkl());
            }
            var shortLabel = ImeNaming.ForMenuBar(active.LayoutName, active.IsIme, chineseMode, active.LangId);
            // 模式确实读不到时留空，不再显示"模式未知"——按钮已按语言给出中/英推断，
            // 悬浮提示再写"未知"只会让人以为功能坏了。
            var modeText = active.IsIme
                ? chineseMode is null
                    ? string.Empty
                    : chineseMode.Value ? "中文模式" : "英文模式"
                : string.Empty;
            return new StatusSnapshot
            {
                MonitorId = MonitorId,
                ShortText = shortLabel,
                HumanText = active.IsIme
                    ? (string.IsNullOrEmpty(modeText) ? active.LayoutName : $"{active.LayoutName} · {modeText}")
                    : $"当前键盘布局：{active.LayoutName}",
                Severity = StatusSeverity.Normal,
                Progress = -1,
                IconKey = active.IsIme ? "ime" : "keyboard"
            };
        }

        // 降级：用 KLID 查注册表布局名，再用 ImeNaming 生成代表字符（不再直接返回"中文"）
        var layoutId = _source.ReadKeyboardLayoutId();
        if (string.IsNullOrEmpty(layoutId) || layoutId == ImeInterop.UnknownLayout)
        {
            return new StatusSnapshot
            {
                MonitorId = MonitorId,
                ShortText = "中/英",
                HumanText = "输入法状态未知",
                Severity = StatusSeverity.Info,
                Progress = -1,
                IconKey = "ime-unknown"
            };
        }

        // 从已注册布局中找到对应名称，用统一的代表字符规则显示
        string layoutName = layoutId;
        bool isIme = false;
        try
        {
            var registered = KeyboardLayoutInterop.GetRegisteredLayouts();
            var match = registered.FirstOrDefault(x =>
                string.Equals(x.KlidHex, layoutId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                layoutName = match.LayoutName;
                isIme = match.IsIme;
            }
        }
        catch { /* 注册表读失败，用 KLID 兜底 */ }

        var fallbackLabel = ImeNaming.ForMenuBar(layoutName, isIme, null);
        return new StatusSnapshot
        {
            MonitorId = MonitorId,
            ShortText = fallbackLabel,
            HumanText = $"当前布局：{layoutName}",
            Severity = StatusSeverity.Normal,
            Progress = -1,
            IconKey = isIme ? "ime" : "keyboard"
        };
    }
}