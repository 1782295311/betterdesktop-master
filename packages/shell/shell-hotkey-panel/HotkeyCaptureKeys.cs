using System.Windows.Input;
using BetterDesktop.Shell.Clipboard.Ipc;

namespace BetterDesktop.Shell.HotkeyPanel;

/// <summary>
/// 录键校验与显示（复制自 shell-clipboard ClipboardSection 的 HotkeyRow 私有模式，勿跨包重构；
/// 主键名统一用 WPF Key 枚举名，与 HotkeySpec 两侧约定一致）。
/// </summary>
internal static class HotkeyCaptureKeys
{
    /// <summary>空配置显示文案（未设置 = 默认鼠标中键入口）。</summary>
    public static string Describe(string? spec)
        => string.IsNullOrWhiteSpace(spec) ? "（未设置）" : HotkeySpec.Pretty(spec);

    /// <summary>
    /// 录制结果校验：主键不能还是修饰键、必须至少一个修饰键、不能与引擎内置全局热键冲突。
    /// </summary>
    public static bool TryBuild(ModifierKeys modifiers, Key key, out string spec, out string reason)
    {
        spec = string.Empty;
        reason = string.Empty;

        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            reason = "再按一个主键"; // 只按住修饰键 —— 组合还不完整
            return false;
        }

        if (modifiers == ModifierKeys.None)
        {
            reason = "需加 Ctrl/Alt/Shift"; // 裸键太易与系统/其它程序冲突
            return false;
        }

        var candidate = HotkeySpec.Build(
            modifiers.HasFlag(ModifierKeys.Control),
            modifiers.HasFlag(ModifierKeys.Shift),
            modifiers.HasFlag(ModifierKeys.Alt),
            modifiers.HasFlag(ModifierKeys.Windows),
            key.ToString());

        if (HotkeySpec.ConflictsWithBuiltIn(candidate))
        {
            reason = "与全局热键冲突";
            return false;
        }

        spec = candidate;
        return true;
    }
}
