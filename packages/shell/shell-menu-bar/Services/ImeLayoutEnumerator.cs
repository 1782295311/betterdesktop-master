// ImeLayoutEnumerator：彻底抛弃 InputLanguageManager（Culture 维度），改用 KeyboardLayoutInterop
// 枚举真实已加载 + 用户实际启用的键盘布局/输入法（注册表 HKLM Keyboard Layouts + HKCU Preload 顺序）。

using System.Collections.Generic;
using BetterDesktop.Shell.Status.Native;

namespace BetterDesktop.Shell.MenuBar.Services;

/// <summary>一个要展示在 IME 面板里的布局/输入法项。</summary>
public sealed record ImeLayoutItem(
    string KlidHex,
    string DisplayName,       // 真实布局名，如 "美式键盘" / "微软拼音" / "搜狗拼音输入法"
    string CompactLabel,      // 方块字符图标，如 "美" / "中" / "拼" / "搜"
    bool IsIme,               // true = 输入法，false = 键盘布局
    bool IsActive,
    bool IsTs = false);       // true = TSF 文本服务（纯 TSF 输入法），false = 传统键盘布局/IMM

public static class ImeLayoutEnumerator
{
    public static IReadOnlyList<ImeLayoutItem> Enumerate()
    {
        var raw = KeyboardLayoutInterop.Enumerate();
        var result = new List<ImeLayoutItem>(capacity: raw.Count);
        foreach (var item in raw)
        {
            result.Add(new ImeLayoutItem(
                KlidHex: item.KlidHex,
                DisplayName: item.LayoutName,
                CompactLabel: ImeNaming.ToLetter(item.LayoutName, item.IsIme),
                IsIme: item.IsIme,
                IsActive: item.IsActive,
                IsTs: item.IsTs));
        }
        return result;
    }

    public static bool Activate(string klidHex, bool isTs) => KeyboardLayoutInterop.Activate(klidHex, isTs);

    /// <summary>切换到下一个输入法/键盘布局（直接激活，不弹系统输入法选择器 UI）。</summary>
    public static bool CycleOnce() => KeyboardLayoutInterop.CycleOnce();

    /// <summary>获取指定输入法/键盘布局的图标句柄（HICON）。调用方负责释放。失败返回 IntPtr.Zero。</summary>
    public static IntPtr GetIconHandle(string klidHex, bool isTs) => KeyboardLayoutInterop.GetLayoutIconHandle(klidHex, isTs);

    /// <summary>向后兼容：默认按 IMM 处理。新代码请用 Activate(klidHex, isTs)。</summary>
    public static bool Activate(string klidHex) => KeyboardLayoutInterop.Activate(klidHex, isTs: false);

    /// <summary>秒切当前激活输入法的中/英文模式。返回切换后状态（true=中文/false=英文），TSF 降级时返回 null。</summary>
    public static bool? ToggleChineseEnglish() => KeyboardLayoutInterop.ToggleChineseEnglish();

    /// <summary>把指定输入法/布局列入启用（默认加到末尾）。成功返回 true。</summary>
    public static bool Add(string klidHex) => KeyboardLayoutInterop.AddLayout(klidHex);

    /// <summary>从启用列表移除指定输入法/布局。成功返回 true。</summary>
    public static bool Remove(string klidHex) => KeyboardLayoutInterop.RemoveLayout(klidHex);

    /// <summary>把指定输入法/布局移动到目标下标（0 = 设为默认）。成功返回 true。</summary>
    public static bool Move(string klidHex, int targetIndex) => KeyboardLayoutInterop.MoveLayoutTo(klidHex, targetIndex);
}
