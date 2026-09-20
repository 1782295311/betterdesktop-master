// BetterDesktop.Host — settings.json 的唯一安全写入口（原子 + 滚动备份 + 防清空守卫）
//
// 【为什么需要它 · 2026-09-16 真机事故】用户配置被清空成两个键，热键随之失效。
// 根因：`--toggle-key` / `--toggle-desktop` 这两个**一次性短命进程**各自
// "读出整个 JSON → 改一个键 → File.WriteAllText 整文件覆盖"，与宿主的设置服务并发写同一文件：
// 只要有一次读到别的写者写了一半（或刚被清空）的内容，就把那份不完整内容原样写回，
// 其余键**永久丢失**（文件里只剩最后那个写者手里的键）。
//
// 【本类的三条保证】
//   ① 原子落盘：先写 .tmp 再 File.Replace，进程中途死掉也不会留下半截文件；
//   ② 滚动备份：覆盖前留一份 .bak（出事可人工/自动恢复）；
//   ③ 防清空守卫：这两个命令只会"设一个键"，键数**只增不减**；若新内容明显少于磁盘现状，
//      判定为丢键事故征兆 → **拒绝写入**并留证（宁可这一次开关没生效，也不能清空用户配置）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Host;

/// <summary>settings.json 安全写入口（仅供 host 的一次性命令进程使用）。</summary>
internal static class SettingsFileWriter
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// 原子写回设置文件。返回 false = 被守卫拒绝或写入失败（调用方据此提示/放弃）。
    /// </summary>
    public static bool TryWrite(string path, JsonObject root)
    {
        try
        {
            var existingKeys = CountKeys(path);
            if (existingKeys >= 5 && root.Count < existingKeys)
            {
                // 键数只增不减：变少即"读到了不完整内容"的强信号 → fail-closed
                CopyAside(path, ".rejected");
                DiagnosticLog.Trace(
                    "settings-guard",
                    $"拒绝写入 {Path.GetFileName(path)}：新键数 {root.Count} 少于磁盘现状 {existingKeys}（疑似丢键事故）");
                return false;
            }

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(WriteOptions));
            if (File.Exists(path))
            {
                CopyAside(path, ".bak");   // 覆盖前留一份可恢复的旧版本
                File.Replace(tmp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, path);
            }

            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("settings-guard", $"写入 {Path.GetFileName(path)} 失败（已放弃本次写入）：{ex.Message}");
            return false;
        }
    }

    /// <summary>磁盘现有键数；读不到（无文件/损坏）返回 0。</summary>
    private static int CountKeys(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return 0;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }

            var count = 0;
            foreach (var _ in doc.RootElement.EnumerateObject())
            {
                count++;
            }

            return count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>把文件复制成带后缀的旁路副本（失败不影响主流程）。</summary>
    private static void CopyAside(string path, string suffix)
    {
        try
        {
            File.Copy(path, path + suffix, overwrite: true);
        }
        catch
        {
            // 备份失败不阻断：原子写本身仍能保证不产生半截文件
        }
    }
}
