using System;
using System.Collections.Generic;

namespace BetterDesktop.Shell.Hotkeys.Contracts;

/// <summary>
/// 热键实现机制。
/// <para>单一注册表承担两类注册：系统热键（Win32 <c>RegisterHotKey</c>，跨进程唯一、由中心窗口收
/// <c>WM_HOTKEY</c>）与低级钩子查表（<c>WH_KEYBOARD_LL</c>，命中且作用域活跃才消费、可吞键）。</para>
/// </summary>
public enum HotkeySource
{
    /// <summary>Win32 RegisterHotKey（系统级、跨进程唯一）。</summary>
    SystemHotkey,

    /// <summary>低级键盘钩子查表（WH_KEYBOARD_LL，壳内）。</summary>
    LowLevelHook,
}

/// <summary>
/// 作用域 Id（回答"此刻可用"）。约定：
/// <c>Global</c>（任何界面下都生效）/ <c>Surface.&lt;名称&gt;</c>（仅该表面活跃时）/
/// <c>Surface.Any</c>（任一 BetterDesktop 表面活跃即生效）。
/// </summary>
public sealed record HotkeyScope(string Id)
{
    /// <summary>任何界面下都生效。</summary>
    public static HotkeyScope Global { get; } = new("Global");

    /// <summary>任一 BetterDesktop 表面活跃即生效（如唤出侧板）。</summary>
    public static HotkeyScope Any { get; } = new("Surface.Any");

    /// <summary>仅指定表面活跃时生效。</summary>
    public static HotkeyScope Surface(string name) => new("Surface." + name);

    /// <inheritdoc />
    public override string ToString() => Id;
}

/// <summary>
/// 键位（规范串：修饰键在前、主键在后，<c>+</c> 分隔，固定顺序 Ctrl → Shift → Alt → Win → 主键；
/// 主键用 WPF <c>Key</c> 枚举名，如 <c>Ctrl+Shift+Enter</c>）。
/// <para>规范串的解析/归一/合法性校验唯一来源是 <c>HotkeySpec</c>（shell-clipboard-ipc），
/// 注册表只做存储与比较，不另写解析器（两边各写一遍必然漂移）。</para>
/// </summary>
public sealed record HotkeyChord(string Spec)
{
    /// <inheritdoc />
    public override string ToString() => Spec;
}

/// <summary>
/// 热键绑定（注册表条目，数据面；触发回调经 <see cref="IHotkeyRegistryService.Register"/> 携带）。
/// </summary>
/// <param name="Id">稳定标识：来源.动作，如 <c>clipboard.paste-next</c>、<c>desktop.toggle-icons</c>。</param>
/// <param name="Chord">当前键位（可能已被用户改键）。</param>
/// <param name="Scope">作用域。</param>
/// <param name="Description">中文功能说明（热键侧板正文就是它）。</param>
/// <param name="Source">实现机制。</param>
/// <param name="DefaultChord">默认键位（"恢复默认"用）。</param>
/// <param name="Owner">注册方（包名 / exe 名 / 引擎名）。</param>
/// <param name="Editable">系统保留或与系统冲突的项可置 false。</param>
/// <param name="Shadows">声明"在本质作用域内接管某条全局绑定"（被接管方 Id 或空）。</param>
public sealed record HotkeyBinding(
    string Id,
    HotkeyChord Chord,
    HotkeyScope Scope,
    string Description,
    HotkeySource Source,
    HotkeyChord DefaultChord,
    string Owner,
    bool Editable = true,
    string? Shadows = null);

/// <summary>注册结果（fail-closed：冲突即拒绝，不静默覆盖）。</summary>
/// <param name="Ok">是否成功。</param>
/// <param name="ConflictWithId">冲突时占用方绑定 Id（OS 占用 / 格式非法时为 null）。</param>
/// <param name="Reason">可读原因（中文）。</param>
public sealed record RegistrationResult(bool Ok, string? ConflictWithId, string? Reason);

/// <summary>
/// 热键视图模型（侧板 / 设置中心渲染）。<c>Visible=false</c> 表示被用户"忽略"——仅不显示，
/// 功能照常；但冲突/被占用/被接管/注册失败的项**无论是否隐藏都必须显示**（fail-visible 红线）。
/// </summary>
public sealed record HotkeyView(
    HotkeyBinding Binding,
    bool Enabled,
    bool Visible,
    bool ShadowedBy,
    bool OsConflict);

/// <summary>冲突描述（注册表可枚举，供侧板高亮与设置中心解决）。</summary>
/// <param name="Id">冲突绑定 Id。</param>
/// <param name="OtherId">占用方/接管方绑定 Id（OS 占用时为 null）。</param>
/// <param name="Chord">键位。</param>
/// <param name="Reason">可读原因（中文）。</param>
public sealed record HotkeyConflict(string Id, string? OtherId, string Chord, string Reason);

/// <summary>
/// 热键注册表服务（shell 层基础设施，全仓系统热键/钩子查表的唯一真相源）。
/// <para>职责：注册/注销/改键/启停/显隐/恢复默认/作用域上报/查询/冲突列表。</para>
/// <para>实现落 shell-core <c>Hotkeys/HotkeyRegistryService.cs</c>：一个 <c>HWND_MESSAGE</c> 中心窗口
/// 统一注册系统热键（<c>WM_HOTKEY</c> 按 Id 分派），一个低级键盘钩子查键表。</para>
/// <para>持久化：<c>settings.json</c> 的 <c>hotkeys.&lt;Id&gt;</c> 键（注册表是唯一读写者，
/// 默认值在代码的 <c>DefaultChord</c>）。</para>
/// <para>【变更通知与 ADR-002 D4】接口**不声明**跨程序集裸 C# event（D4 禁止）；
/// 变更通知由具体实现（shell-core）的类级 <c>Changed</c> 事件提供，跨包消费（热键侧板 P5）届时走
/// 内核 IEventBus 桥接。</para>
/// </summary>
public interface IHotkeyRegistryService
{
    /// <summary>
    /// 注册绑定。冲突（同作用域同键位 / 跨作用域未声明 Shadows / 内置键）即拒绝（fail-closed）。
    /// <paramref name="onTrigger"/> 为键触发回调（可空）；回调在中心窗口泵线程或钩子线程执行，
    /// 涉及 UI 的操作须自行 <c>Dispatcher.InvokeAsync</c> 编队回 UI 线程。
    /// </summary>
    RegistrationResult Register(HotkeyBinding binding, Action<HotkeyBinding>? onTrigger = null);

    /// <summary>
    /// 声明外部热键（跨进程/外部来源，如截图 exe、剪贴板面板 exe、Rust 引擎、开始菜单）。
    /// <para>与 <see cref="Register"/> 的差异：**不注册键位、无触发回调**——键位由声明方进程自己
    /// 注册（避免宿主与外部进程重复占用，0x581）；注册表只承担展示（侧板/设置中心）、冲突检测、
    /// 隐藏/停用/改键的持久化。冲突校验链与 Register 完全一致（fail-closed）。</para>
    /// <para>声明条目的 Rebind/SetEnabled/SetVisible/ResetToDefault 只更新内存与持久化，
    /// 不触碰中心窗口/低级钩子；生效面由声明方按其既有通道消费（如面板读配置键）。</para>
    /// </summary>
    RegistrationResult Declare(HotkeyBinding binding);

    /// <summary>注销绑定（释放键位；该 Id 的持久化改键保留，重新注册时恢复）。</summary>
    void Unregister(string id);

    /// <summary>改键：释放旧键、注册新键（冲突即拒绝并返回占用方）。</summary>
    RegistrationResult Rebind(string id, HotkeyChord chord);

    /// <summary>停用/启用：停用 = 释放键位（可被其他绑定占用），启用 = 重新注册。</summary>
    void SetEnabled(string id, bool enabled);

    /// <summary>隐藏/显示：仅影响侧板显示，**不改变键位注册状态**（隐藏 ≠ 静音）。</summary>
    void SetVisible(string id, bool visible);

    /// <summary>恢复默认键位（并清空该 Id 的持久化改键）。</summary>
    void ResetToDefault(string id);

    /// <summary>上报当前上下文栈（各表面生命周期 show/hide 时调用；非 Global 绑定的"此刻可用"依据）。</summary>
    void SetActiveScopes(IReadOnlyList<string> scopeIds);

    /// <summary>当前可用热键（侧板渲染用）：Global ∪ 上下文栈上的 Surface.*，已过滤停用项。</summary>
    IReadOnlyList<HotkeyView> GetActive();

    /// <summary>全部绑定（设置中心用，含停用项）。</summary>
    IReadOnlyList<HotkeyView> GetAll();

    /// <summary>当前冲突列表（被其他程序占用 / 被高优先级作用域接管）。</summary>
    IReadOnlyList<HotkeyConflict> GetConflicts();
}

/// <summary>
/// 作用域跟踪器（各表面对热键注册表的"此刻可用"上报通道）。
/// <para>注册表 <c>SetActiveScopes</c> 是**全量覆盖**语义，多表面并发活跃必须经本接口聚合后再上报；
/// 宿主内表面（设置窗口/开始菜单/菜单栏弹层/Dock 提取器等）在 show/hide 时 EnterScope/ExitScope。
/// 实现落 shell-core <c>Hotkeys/HotkeyScopeTracker.cs</c>；静态桥 <c>SurfaceScopeBridge</c> 供
/// 不便于注入服务的窗口经全局通道上报（PopupWindowBase 基类统一接线）。</para>
/// </summary>
public interface IHotkeyScopeTracker
{
    /// <summary>当前已上报的作用域 Id 集合。</summary>
    IReadOnlyCollection<string> Current { get; }

    /// <summary>表面活跃（show）。幂等。</summary>
    void EnterScope(string scopeId);

    /// <summary>表面收起（hide）。幂等。</summary>
    void ExitScope(string scopeId);

    /// <summary>全量覆盖（宿主启动时可注入初始上下文）。</summary>
    void SetScopes(IEnumerable<string>? scopeIds);
}
