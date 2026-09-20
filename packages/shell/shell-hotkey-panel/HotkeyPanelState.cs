namespace BetterDesktop.Shell.HotkeyPanel;

/// <summary>
/// 侧板模式（P2-3 状态机）：只读常驻 / 可操作 / 已忽略管理。
/// 改键已移出侧板（用户裁定"侧板改键反人类"）→ 正常窗口（设置中心"热键"分节）承担，
/// 侧板只做显示与隐藏管理，故状态机不再有录键分支。
/// </summary>
internal enum HotkeyPanelMode
{
    /// <summary>只读常驻：完全点击穿透，按上下文过滤显示。</summary>
    ReadOnly,

    /// <summary>可操作：已解除穿透，列出全部条目（含非活跃作用域灰显 + 作用域徽标，P0-1）。</summary>
    Interactive,

    /// <summary>已忽略管理浮层（P0-3）：隐藏项列表 + 恢复，绝不静默丢失。</summary>
    Manage,
}

/// <summary>
/// 侧板交互状态机（纯模型，无 UI 依赖，可单测）。
/// <para>P2-3：窗口只做渲染；门控翻转 / 刷新节流 / 忽略恢复的**允许动作**全在这里——
/// P1-1（交互/管理停表）、P1-3（仅 ReadOnly 可进可操作）这类 bug 由此层测试兜底。</para>
/// </summary>
internal sealed class HotkeyPanelState
{
    private HotkeyPanelMode _previousMode = HotkeyPanelMode.ReadOnly;

    public HotkeyPanelMode Mode { get; private set; } = HotkeyPanelMode.ReadOnly;

    /// <summary>是否处于可操作区（行内显示操作按钮；P0-1 可操作态全量列表依据）。</summary>
    public bool IsInteractive => Mode == HotkeyPanelMode.Interactive;

    /// <summary>是否列出全部条目（P0-1）：可操作态含非活跃作用域；只读态才按上下文过滤。</summary>
    public bool ShowsAllRows => Mode is HotkeyPanelMode.Interactive or HotkeyPanelMode.Manage;

    /// <summary>500ms 轮询是否可刷新（P1-1）：交互/管理中停表，防整表重建打断点击。</summary>
    public bool CanTimerRefresh => Mode == HotkeyPanelMode.ReadOnly;

    /// <summary>门控通过进入可操作态（P1-3：右 Alt 按下 且 点击落在侧板矩形内，由窗口钩子判定后调用）。</summary>
    public bool EnterInteractive()
    {
        if (Mode != HotkeyPanelMode.ReadOnly)
        {
            return false;
        }

        Mode = HotkeyPanelMode.Interactive;
        return true;
    }

    /// <summary>退出到只读（松开门控 / 强制回退）：关管理。</summary>
    public void ExitToReadOnly()
    {
        Mode = HotkeyPanelMode.ReadOnly;
    }

    /// <summary>底部"已忽略 N 项"入口：进管理浮层；再点/已恢复完退出回进入前状态。</summary>
    public void ToggleManage()
    {
        if (Mode == HotkeyPanelMode.Manage)
        {
            Mode = _previousMode;
            return;
        }

        _previousMode = Mode;
        Mode = HotkeyPanelMode.Manage;
    }

    /// <summary>管理浮层自动退出（全部恢复，hidden==0）。</summary>
    public void LeaveManage()
    {
        if (Mode == HotkeyPanelMode.Manage)
        {
            Mode = _previousMode;
        }
    }
}
