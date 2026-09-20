using System;
using System.Text.Json;

namespace BetterDesktop.Kernel.Core;

/// <summary>
/// 组件启停的**唯一入口**（core 是唯一生命周期所有者）。
/// </summary>
/// <remarks>
/// <para>
/// 【为什么需要这一层】在它之前，壳侧的每个包都自己 <c>Process.Start</c> 拉起引擎/面板/桌面服务：
/// 于是"谁有权拉起进程"这件事**分散在 N 个包里**，而每个包各自还记得一套 exe 定位候选路径 ——
/// 一边找得到、一边找不到就会**重复拉起抢管道**（历史上真机修过多次）。
/// 更根本的问题是：拉起点一分散，core 的组件表就不再是"能到达什么"的真相源，
/// 监护、gate、退避、熔断**全部只对 core 知道的那部分生效**。
/// </para>
/// <para>
/// 【为什么 <c>ensureCore</c> 传 null】<see cref="CoreControlClient"/> 的补救动作由调用方注入，
/// kernel 是零依赖契约层，自己 <c>Process.Start</c> 会同时违反分层与"拉起点必须可登记"。
/// 但**壳侧的调用者也不该补**：core 是地基，它不在就意味着整条链路都不在，
/// 此时"顺手把它拉起来"是把一个明确的失败换成一次来源不明的启动。故如实失败、如实记日志。
/// </para>
/// <para>
/// 【为什么这里会有<em>启动延迟</em>的疑问】core 在跑时，本类每个动作就是一次本机管道往返
/// （连接 + 一行写 + 一行读，毫秒级），**不引入任何等待**；core 不在时 <c>ensureCore=null</c>
/// 让客户端**不进入 5 秒补救循环**，立刻返回失败。所以收敛到 core 不会拖慢冷启动 ——
/// 唯一真正变慢的场景是"core 没跑"，而那时任何本地拉起也都是白拉（没有监护、没有 gate）。
/// </para>
/// </remarks>
public static class CoreComponents
{
    // ── 组件名 ──
    //
    // **与 core/components.json 的 `name` 逐字一致**，由门禁 `verify-boundaries` 双向校验
    //（C# 里有而表里没有 = 拼错了名字，运行时表现为 unknown-component；
    //  表里有而 C# 里没有 = 这台机器上"从 core 出发到达不了"的功能）。
    // 常量集中在这里而不是散在各包，就是为了让那门禁有一个可比较的落点。

    /// <summary>主程序（菜单栏 / Dock）—— core/components.json 的 `shell`。</summary>
    public const string Shell = "shell";

    /// <summary>自绘桌面服务 —— `desktop`。</summary>
    public const string Desktop = "desktop";

    /// <summary>桌面控制菜单（一次性菜单进程，`--desktop-controls`）—— `desktop-controls`。</summary>
    public const string DesktopControls = "desktop-controls";

    /// <summary>剪贴板历史引擎 —— `clipboard-engine`。</summary>
    public const string ClipboardEngine = "clipboard-engine";

    /// <summary>剪贴板历史面板（**装配入口**形态，不带 <c>--open</c>）—— `clipboard-panel`。</summary>
    public const string ClipboardPanel = "clipboard-panel";

    /// <summary>
    /// 剪贴板历史面板的**打开**形态（带 <c>--open</c>）—— `clipboard-panel-open`。
    /// </summary>
    /// <remarks>
    /// 【为什么必须与 <see cref="ClipboardPanel"/> 分成两个组件】它们是**两个不同的动作**：
    /// "装配常驻入口（侧边手柄）"与"把完整面板打开给用户看"。合成一个组件就没法表达差异 ——
    /// 宿主启动时若用带 <c>--open</c> 的那条，"确保入口存在"会顺带每次把面板弹出来。
    /// 面板 exe 是单实例：带 <c>--open</c> 启动会把"打开"请求转交给已在跑的实例。
    /// 这与 <see cref="Capture"/>（同一形态：一次性动作 + 固定参数）是同一种建模方式。
    /// </remarks>
    public const string ClipboardPanelOpen = "clipboard-panel-open";

    /// <summary>索引引擎 —— `index-engine`。</summary>
    public const string IndexEngine = "index-engine";

    /// <summary>设置中心 —— `settings`。</summary>
    public const string Settings = "settings";

    /// <summary>截图 —— `capture`。</summary>
    public const string Capture = "capture";

    /// <summary>
    /// 全部组件名（顺序无关，供门禁与诊断遍历）。
    /// </summary>
    public static readonly string[] All =
    {
        Shell, Desktop, DesktopControls, ClipboardEngine, ClipboardPanel, ClipboardPanelOpen,
        IndexEngine, Settings, Capture,
    };

    /// <summary>请 core 拉起组件（已在跑 → core 自己判活，不会重复拉起）。</summary>
    public static bool Start(string component, Action<string>? log = null) => Request("start", component, log);

    /// <summary>请 core 停掉组件（受 gate 约束：开关关掉时 core 本来就会停它）。</summary>
    public static bool Stop(string component, Action<string>? log = null) => Request("stop", component, log);

    /// <summary>请 core 翻转组件（面板类 = 唤起/收起由目标单实例决定）。</summary>
    public static bool Toggle(string component, Action<string>? log = null) => Request("toggle", component, log);

    /// <summary>
    /// 读某组件**此刻是否在跑**（来自 core 的 <c>status</c>，而不是本地枚举同名进程）。
    /// </summary>
    /// <remarks>
    /// 【为什么必须问 core】本地按进程名判活是真机事故的老来源：桌面控制菜单与常驻桌面服务
    /// **同名**（都是 BetterDesktop.DesktopControl.exe），按名字判活会把"正在弹菜单"读成"服务在运行"
    /// → 真服务死了不被拉起。core 按声明判活（组件表里的 exe + 管道判活），是唯一正确的来源。
    /// 读不到（core 不在 / 响应畸形）返回 false，由调用方如实降级，**不猜**。
    /// </remarks>
    public static bool TryGetRunning(string component, out bool running)
    {
        running = false;
        var result = CoreControlClient.Send("status", string.Empty, ensureCore: null);
        return TryReadRunning(result, component, out running);
    }

    /// <summary>
    /// 从一次 <c>status</c> 结果里读 <c>actual[component]</c>（纯函数，单测锚点）。
    /// </summary>
    /// <remarks>
    /// 公开而非 internal：它是一条**独立可用**的能力（"解析 core 的状态响应"），
    /// 而 kernel 不声明 InternalsVisibleTo —— 做成 internal 就只能靠测试程序集特批才测得到，
    /// 那等于把这条判据的保护范围缩小到"只有我们自己的单测"。
    /// </remarks>
    public static bool TryReadRunning(ControlResult result, string component, out bool running)
    {
        running = false;
        if (!result.Succeeded || result.Response.Data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!result.Response.Data.TryGetProperty("actual", out var actual)
            || actual.ValueKind != JsonValueKind.Object
            || !actual.TryGetProperty(component, out var value))
        {
            return false;
        }

        if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
        {
            return false;
        }

        running = value.GetBoolean();
        return true;
    }

    /// <summary>发一条组件请求；失败**必须**留痕（静默失败会表现为"点了没反应"）。</summary>
    private static bool Request(string verb, string component, Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(component))
        {
            log?.Invoke($"组件名为空，拒绝发送 {verb}");
            return false;
        }

        var result = CoreControlClient.Send(verb, component, ensureCore: null);
        if (result.Succeeded)
        {
            log?.Invoke($"core 已受理 {verb} {component}");
            return true;
        }

        // 分类报错：RemoteError 是**业务拒绝**（组件名写错 / gate 关掉），不是"core 挂了"。
        // 混成一句"失败"会把排查方向指错 —— 例如把 gate 关闭读成"core 有问题"。
        var kind = result.Failure == ControlFailure.RemoteError
            ? $"core 拒绝（{result.Response.Error}: {result.Response.Message}）"
            : $"core 不可达（{result.Failure}）";
        log?.Invoke($"{verb} {component} 失败：{kind}{(string.IsNullOrEmpty(result.FailureDetail) ? string.Empty : "；" + result.FailureDetail)}");
        return false;
    }
}
