// BetterDesktop 启动器 —— 把用户勾选的开关**落地**（跨进程）。
//
// 【落地路径为什么是 CLI】见 SettingsSnapshot 的文件头：设置服务没有文件监视，
// 外部进程直写 settings.json 时**已经开着的**菜单栏 / Dock / 桌面不会重载（真机："点了没反应"）。
// CLI 的 --toggle-key 会先尝试管道转发给宿主 → 宿主进程内改设置并广播 SettingsChanged → 立即生效；
// 宿主不在时才本地落盘（下次启动生效）。这是仓库既有的唯一正确通道，启动器必须走它。
//
// 【翻转语义的陷阱】--toggle-key / --toggle-desktop 是"取反"，不是"设为"。
// 所以本类的算法固定为：读当前值 → 与目标值比对 → **只对不一致的**调用翻转 → 复查落盘结果。
// 绝不"按用户勾选直接翻转"（那会在"本来就开着"时把功能关掉）。
//
// 【失败不得静默】每条开关的结果都带 Note 回给界面：成功写"已生效"，失败写清原因与补救方式。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace BetterDesktop.Launcher.Services;

/// <summary>单条开关的落地结果。</summary>
/// <param name="Label">开关显示名。</param>
/// <param name="Applied">是否确认落地（false = 用户看到"没生效"时必须有解释）。</param>
/// <param name="Note">给用户看的一句话。</param>
internal sealed record ToggleOutcome(string Label, bool Applied, string Note);

/// <summary>开关落地器。</summary>
internal static class ToggleApplier
{
    /// <summary>落盘为异步 debounce（SettingsService 500ms），复查窗口取 2.5s 足够。</summary>
    private const int VerifyTimeoutMs = 2500;

    private const int VerifyPollMs = 150;

    /// <summary>
    /// 应用一批开关。
    /// </summary>
    /// <param name="desired">键 → 目标值（顺序不保证，结果按目录顺序返回）。</param>
    /// <param name="integration">本次启动采到的整合状态（"系统右键菜单"的当前态来源）。</param>
    public static IReadOnlyList<ToggleOutcome> Apply(
        IReadOnlyDictionary<string, bool> desired,
        IntegrationStatus integration)
    {
        var outcomes = new List<ToggleOutcome>();
        var snapshot = SettingsSnapshot.Load();

        if (!snapshot.Readable)
        {
            // 当前值不可信 → 一律不动。翻转式动作在"当前值未知"时可能把功能反向切换，
            // 对用户来说就是"我明明没改，它自己变了"（比"没生效"糟糕得多）。
            foreach (var toggle in FeatureToggleCatalog.All)
            {
                outcomes.Add(new ToggleOutcome(
                    toggle.Label,
                    false,
                    "设置文件暂时无法读取（可能正被写入），本次未改动"));
            }

            return outcomes;
        }

        foreach (var toggle in FeatureToggleCatalog.All)
        {
            if (!desired.TryGetValue(toggle.Key, out var target))
            {
                continue;
            }

            // 当前值来源优先级：
            //   ① 系统整合类（register/unregister）以**实际注册态**为准 —— 设置键可能是陈旧的
            //      （用户显式注销后 shellmenu.comExtension 往往还是 true，按它判断就会"以为已经开着"）；
            //   ② 其余读设置文件；
            //   ③ 键不在文件里 = 从未设置过 → 等于该开关的默认值（这是可判断的，不是"未知"）。
            bool? current = toggle.EnableArgs is not null && integration.ComRegistered.HasValue
                ? integration.ComRegistered.Value
                : snapshot.Get(toggle.Key);
            current ??= toggle.Default;

            if (current == target)
            {
                outcomes.Add(new ToggleOutcome(toggle.Label, true, target ? "保持开启" : "保持关闭"));
                continue;
            }

            outcomes.Add(ApplyOne(toggle, target));
        }

        return outcomes;
    }

    private static ToggleOutcome ApplyOne(FeatureToggle toggle, bool target)
    {
        if (toggle.IsFlip)
        {
            var flip = CliRunner.Run(toggle.FlipArgs!);
            if (flip.ExitCode != 0)
            {
                return new ToggleOutcome(
                    toggle.Label,
                    false,
                    $"切换失败（CLI 退出码 {flip.ExitCode}）");
            }

            // 复查：CLI 说成功不等于真的生效（可能宿主不在、或写入被覆盖）。
            return WaitForValue(toggle, target)
                ? new ToggleOutcome(toggle.Label, true, "已生效")
                : new ToggleOutcome(toggle.Label, false, "已提交但未确认（主程序未在运行时会下次启动生效）");
        }

        // 幂等设定式（系统整合 register / unregister）。
        var args = target ? toggle.EnableArgs! : toggle.DisableArgs!;
        var result = CliRunner.Run(args);
        if (result.ExitCode != 0)
        {
            return new ToggleOutcome(
                toggle.Label,
                false,
                $"{(target ? "启用" : "停用")}失败（CLI 退出码 {result.ExitCode}）");
        }

        return new ToggleOutcome(toggle.Label, true, target ? "已启用" : "已停用");
    }

    /// <summary>轮询设置文件直到键的值等于目标值（跨进程写入是异步 debounce 落盘的）。</summary>
    private static bool WaitForValue(FeatureToggle toggle, bool target)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var snapshot = SettingsSnapshot.Load();
            if (snapshot.Readable && snapshot.Get(toggle.Key) == target)
            {
                return true;
            }

            if (sw.ElapsedMilliseconds >= VerifyTimeoutMs)
            {
                return false;
            }

            Thread.Sleep(VerifyPollMs);
        }
    }
}
