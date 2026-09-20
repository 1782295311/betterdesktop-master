// BetterDesktop.Shell.ContextMenus — 崩溃熔断的执行端（跨语言候选评估 §4.1 第一步）
//
// 分工：**归因**在 HandlerCrashGuard（哪个 CLSID 把 broker 搞死了、连续几次），**执行**在这里（达阈值就停用 + 记可见状态）。
//
// 两个入口：
//   ① 宿主启动 ApplyPending()——消费上一轮已记账但尚未处置的（上次进程退出前没处理完）；
//   ② 运行中 broker 报回非 0 退出 → DisableNow()——当场停用，不必等重启。
//
// 【停用走既有通道】MenuManagerService.Toggle（ShellEx=键移入 -ContextMenuHandlers；HKLM 项走 HKCU 影子
//   屏蔽），写前 RegTreeBackup 备份、全程可逆——熔断不发明新的注册表写路径。
//
// 【可见状态】CLSID 记入设置键 HandlerCrashGuard.AutoDisabledKey，设置中心「菜单管理」显示
//   "已因连续崩溃自动停用"，用户手动启用即清除标记（Clear）。
//
// 设置服务经 Attach 注入（ambient，同 DiagnosticLog.SetSink 的既有范式）：ContextMenuPlugin 在 LoadAsync
// 里 Attach，之后连静态的 MenuBrokerClient 也能写可见状态。

using System;
using System.Collections.Generic;
using System.Linq;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>崩溃熔断执行端：达阈值 → 自动停用（可逆）+ 写设置键供设置中心显示。</summary>
internal static class HandlerCrashBreaker
{
    private static ISettingsService? _settings;

    /// <summary>注入设置服务（宿主启动时一次；null = 只停用不记录可见状态）。</summary>
    internal static void Attach(ISettingsService? settings) => _settings = settings;

    /// <summary>宿主启动时消费已记账的熔断：达阈值的（进程外 broker 归因）自动停用。</summary>
    internal static void ApplyPending()
    {
        foreach (var pending in HandlerCrashGuard.Pending())
        {
            DisableNow(pending.Clsid, pending.DisplayName, pending.Consecutive);
        }
    }

    /// <summary>
    /// 达阈值立即停用。返回是否在注册表里真的停用成功（null 结果 = 未找到启用中的该项）。
    /// 无论是否落地都清零计数：避免每次启动反复尝试（找不到 = 已卸载/已停用）。
    /// </summary>
    internal static bool DisableNow(string clsid, string? displayName, int consecutive = HandlerCrashGuard.Threshold)
    {
        bool disabled;
        try
        {
            disabled = DisableShellexByClsid(clsid);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.contextmenu", $"崩溃熔断自动停用失败（{clsid}）：{ex.Message}");
            disabled = false;
        }

        HandlerCrashGuard.Acknowledge(clsid);

        var who = string.IsNullOrWhiteSpace(displayName) ? clsid : displayName!;
        DiagnosticLog.Trace("shell.contextmenu",
            $"崩溃熔断{(disabled ? "已停用" : "未找到启用项")}：{who}（连续 {consecutive} 次异常退出）");

        if (!disabled)
        {
            return false;
        }

        var list = _settings?.Get<List<string>>(HandlerCrashGuard.AutoDisabledKey) ?? [];
        if (!list.Contains(clsid, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(clsid);
            _settings?.Set(HandlerCrashGuard.AutoDisabledKey, list);
        }
        return true;
    }

    /// <summary>是否被崩溃熔断自动停用（设置中心显示用）。</summary>
    internal static bool IsAutoDisabled(string clsid)
    {
        var key = HandlerCrashGuard.Normalize(clsid);
        if (key.Length == 0)
        {
            return false;
        }
        var list = _settings?.Get<List<string>>(HandlerCrashGuard.AutoDisabledKey);
        return list is not null && list.Any(x => HandlerCrashGuard.Normalize(x) == key);
    }

    /// <summary>用户手动恢复：清"自动停用"标记 + 清嫌疑计数（需重新连续崩溃 3 次才会被再停用）。</summary>
    internal static void Clear(string clsid)
    {
        var key = HandlerCrashGuard.Normalize(clsid);
        if (key.Length == 0)
        {
            return;
        }

        HandlerCrashGuard.Acknowledge(clsid);

        var list = _settings?.Get<List<string>>(HandlerCrashGuard.AutoDisabledKey);
        if (list is { Count: > 0 } && list.RemoveAll(x => HandlerCrashGuard.Normalize(x) == key) > 0)
        {
            _settings!.Set(HandlerCrashGuard.AutoDisabledKey, list);
        }
    }

    /// <summary>
    /// 按 CLSID 停用全部场景下的 ShellEx 项（复用 MenuManagerService 的枚举 + Toggle：写前备份、可逆）。
    /// 首轮停用后该项转为 Enabled=false（移键/影子），后续场景自然跳过。
    /// </summary>
    private static bool DisableShellexByClsid(string clsid)
    {
        var anyDisabled = false;
        foreach (var scene in MenuManagerService.Scenes)
        {
            foreach (var entry in MenuManagerService.Enumerate(scene.Key))
            {
                if (entry.Kind != "Shellex" || !entry.Enabled
                    || !string.Equals(entry.Clsid, clsid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                try
                {
                    MenuManagerService.Toggle(entry);
                    anyDisabled = true;
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Trace("shell.contextmenu",
                        $"自动停用失败 {scene.Key}/{entry.KeyName}：{ex.Message}");
                }
            }
        }
        return anyDisabled;
    }
}
