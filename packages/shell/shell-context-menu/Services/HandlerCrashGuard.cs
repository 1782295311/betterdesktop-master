// BetterDesktop.Shell.ContextMenus — 第三方 COM handler 崩溃归因（跨语言候选评估 §4.1）
//
// 【一句话】记"哪个 CLSID 把进程搞死了、连续几次"，达阈值交给 HandlerCrashBreaker 停用。
//
// 【归因来源只有一条】进程外 broker 的非 0 退出码（协议见 native/include/ShellBroker.h）：broker 被
//   某个 handler 干掉 → 宿主还活着 → 当场记账。宿主侧 in-proc 实现与其 inflight 落盘归因已在
//   2026-09-14 的查重里删除（那份实现与 broker 重复），所以本类不再有"下次启动归因"的第二条路径。
//
// 【连续语义】同一 CLSID 连续 3 次 → 达阈值（与 ExternalPluginAdapter 的 3 次同阈值；差别是粒度：
//   那个管"每个进程"，这里管"每个 CLSID"）。中间只要成功一次（NoteSuccess）即清零——只有"连续"才算数。
//
// 【落地不可逆性】本类不碰注册表、不读设置：停用动作由 HandlerCrashBreaker 走既有 MenuManagerService.Toggle
//   通道（写前备份、可逆）。状态落 %LocalAppData%\BetterDesktop\MenuManager\guard\suspects.json。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>一次崩溃归因结果。</summary>
internal sealed record HandlerCrashReport(string Clsid, string DisplayName, int Consecutive, bool ReachedThreshold);

/// <summary>第三方 handler 崩溃计数器（每 CLSID 一份，连续 3 次达阈值）。</summary>
internal static class HandlerCrashGuard
{
    /// <summary>连续崩溃阈值（与 docs/cross-language/原生重写候选评估.md §4.1 及 ExternalPluginAdapter 的 3 次一致）。</summary>
    internal const int Threshold = 3;

    /// <summary>自动停用名单设置键（List&lt;string&gt; = 规范化 CLSID）；设置中心据此显示"已自动停用"。</summary>
    public const string AutoDisabledKey = "context-menu.com.autodisabled";

    private static readonly object Gate = new();
    private static string? _rootOverride;

    /// <summary>状态目录：%LocalAppData%\BetterDesktop\MenuManager\guard（与 RegTreeBackup.BackupDirectory 同级）。</summary>
    internal static string StoreRoot => _rootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BetterDesktop", "MenuManager", "guard");

    private static string SuspectsPath => Path.Combine(StoreRoot, "suspects.json");

    /// <summary>测试缝：重定向状态目录（null 恢复默认）。</summary>
    internal static void OverrideStoreRoot(string? root)
    {
        lock (Gate)
        {
            _rootOverride = root;
        }
    }

    /// <summary>注册表里的 CLSID 形态不一（带/不带花括号、大小写），比较前统一。</summary>
    internal static string Normalize(string? clsid)
    {
        if (string.IsNullOrWhiteSpace(clsid))
        {
            return string.Empty;
        }
        return Guid.TryParse(clsid, out var guid) ? guid.ToString("D") : clsid.Trim().ToLowerInvariant();
    }

    /// <summary>broker 报回非 0 退出：这个 CLSID 把 broker 搞死了。</summary>
    public static HandlerCrashReport RecordExternalCrash(string clsid, string? displayName)
    {
        var key = Normalize(clsid);
        lock (Gate)
        {
            var suspects = ReadSuspects();
            suspects.TryGetValue(key, out var prev);
            var consecutive = (prev?.Consecutive ?? 0) + 1;
            var display = string.IsNullOrWhiteSpace(displayName) ? prev?.DisplayName ?? key : displayName!;
            suspects[key] = new Suspect(key, display, consecutive, DateTimeOffset.Now);
            WriteSuspects(suspects);

            DiagnosticLog.Trace("shell.contextmenu",
                $"broker 归因 handler {display}（{key}）—— 连续 {consecutive}/{Threshold} 次");
            return new HandlerCrashReport(key, display, consecutive, consecutive >= Threshold);
        }
    }

    /// <summary>handler 这次正常工作 → 洗清嫌疑（只有"连续"崩溃才熔断）。</summary>
    public static void NoteSuccess(string clsid)
    {
        var key = Normalize(clsid);
        if (key.Length > 0)
        {
            RemoveSuspect(key);
        }
    }

    /// <summary>处置完毕：清零计数（下次需重新连续 3 次；用户手动恢复后也不会立刻被再停用）。</summary>
    public static void Acknowledge(string clsid)
    {
        var key = Normalize(clsid);
        if (key.Length > 0)
        {
            RemoveSuspect(key);
        }
    }

    /// <summary>已达阈值、尚未处置的 CLSID（宿主启动时消费）。</summary>
    public static IReadOnlyList<HandlerCrashReport> Pending()
    {
        lock (Gate)
        {
            return ReadSuspects().Values
                .Where(s => s.Consecutive >= Threshold)
                .OrderByDescending(s => s.Consecutive)
                .Select(s => new HandlerCrashReport(s.Clsid, s.DisplayName, s.Consecutive, true))
                .ToList();
        }
    }

    /// <summary>某 handler 当前连续崩溃计数（0 = 无嫌疑或已被处置）。诊断/UI 用。</summary>
    public static int ConsecutiveCount(string clsid)
    {
        var key = Normalize(clsid);
        if (key.Length == 0)
        {
            return 0;
        }
        lock (Gate)
        {
            var suspects = ReadSuspects();
            return suspects.TryGetValue(key, out var record) ? record.Consecutive : 0;
        }
    }

    private static void RemoveSuspect(string key)
    {
        lock (Gate)
        {
            var suspects = ReadSuspects();
            if (suspects.Remove(key))
            {
                WriteSuspects(suspects);
            }
        }
    }

    private static Dictionary<string, Suspect> ReadSuspects()
    {
        try
        {
            if (!File.Exists(SuspectsPath))
            {
                return new Dictionary<string, Suspect>(StringComparer.Ordinal);
            }
            var map = JsonSerializer.Deserialize<Dictionary<string, Suspect>>(File.ReadAllText(SuspectsPath));
            return map is null
                ? new Dictionary<string, Suspect>(StringComparer.Ordinal)
                : new Dictionary<string, Suspect>(map, StringComparer.Ordinal);
        }
        catch
        {
            // 状态文件损坏即重来（宁可漏熔断一次，不可误熔断）
            return new Dictionary<string, Suspect>(StringComparer.Ordinal);
        }
    }

    private static void WriteSuspects(Dictionary<string, Suspect> suspects)
    {
        try
        {
            Directory.CreateDirectory(StoreRoot);
            File.WriteAllText(SuspectsPath, JsonSerializer.Serialize(suspects));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.contextmenu", $"崩溃熔断状态落盘失败：{ex.Message}");
        }
    }

    internal sealed record Suspect(string Clsid, string DisplayName, int Consecutive, DateTimeOffset LastAt);
}
