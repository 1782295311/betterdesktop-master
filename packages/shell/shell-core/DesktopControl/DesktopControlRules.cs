// BetterDesktop.Shell.Core —「桌面控制」与其它功能"默认隐藏"的优先级规则（纯函数，可单测）
//
// 【2026-09-17 用户拍板】「桌面控制」的显式选择，优先级**高于**其它功能的默认隐藏效果。
//
// 现实里唯一的冲突：**dock 启用即默认隐藏原生任务栏**（2026-09-02 定稿的联动——dock 独占底部条带）。
// 旧实现只有单向优先级（源码注释原文："显式关闭 → 无条件隐藏，压倒 dock 联动；保持开启 → 回退 dock 独占联动"）
// → 用户实测的错位：菜单里「隐藏任务栏」显示"未勾（= 没隐藏）"，可任务栏其实是被 dock 藏起来的；
//   再点它只会往"更隐藏"方向翻 → 看着像"点了没反应"。
//
// 现规则双向对称：
//   · 显式隐藏（components.wintaskbar=false）             → 隐藏；
//   · 显式显示（wintaskbar=true 且留痕键 true）           → **显示**（dock 的默认隐藏让位）；
//   · 从未动过（wintaskbar 默认 true，留痕键 false/缺失） → 沿用 dock 联动（默认隐藏）——保持既有观感不变。
//
// 为什么规则要单独成纯函数：它此前埋在 host/Bootstrap.cs 的局部函数里，改错也没人拦得住；
// 抽出来 + 单测钉住（shell-core-tests/DesktopControlRulesTests），再让 Bootstrap 调用同一份。

namespace BetterDesktop.Shell.Core.DesktopControl;

/// <summary>「桌面控制」开关之间、以及开关与其它功能默认行为之间的优先级裁决。</summary>
public static class DesktopControlRules
{
    /// <summary>
    /// 原生任务栏此刻应当可见吗？
    /// </summary>
    /// <param name="wintaskbar">
    /// <c>components.wintaskbar</c>：用户对任务栏的**意图**（true = 要它可见；默认 true）。
    /// </param>
    /// <param name="dock">
    /// <c>components.dock</c>：dock 是否启用。启用时**默认隐藏**原生任务栏（其它功能的默认隐藏效果）。
    /// </param>
    /// <param name="explicitVisible">
    /// <c>desktop.taskbarExplicitVisible</c>：用户是否曾**显式**要求任务栏可见（桌面控制 / 托盘 翻到"显示"时留痕）。
    /// </param>
    public static bool ShouldShowNativeTaskbar(bool wintaskbar, bool dock, bool explicitVisible)
        => wintaskbar && (!dock || explicitVisible);
}
