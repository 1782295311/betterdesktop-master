// BetterDesktop 启动器 —— 设置快照读取（%APPDATA%\BetterDesktop\settings.json）。
//
// 【为什么要"快照"而不是逐键读文件】一次启动要对十几个开关做判断，逐键读会反复打盘；
// 更要紧的是**读盘瞬间可能正被宿主原子替换**（SettingsService 是写临时文件再 Move 覆盖）——
// 那时候读到的可能是空/半截内容。快照把"一次读取的全部结论"固定下来，并提供
// <see cref="Readable"/> 让调用方区分"文件不存在（= 全部取默认值，可判断）"与
// "读到了但解析失败（= 当前值不可信，任何写入都不该发生）"。
//
// 【为什么开关不由本进程直接写】SettingsService 没有文件监视：外部进程写盘，已经开着的
// 宿主**不会重载**（真机教训原话："点了没反应"）。所以写入一律走 CLI --toggle-key
// （宿主在线时管道转发 → 宿主进程内改 + 广播 → 立即生效）。本类只负责**读**。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BetterDesktop.Launcher.Services;

/// <summary>settings.json 的一次性只读快照。</summary>
internal sealed class SettingsSnapshot
{
    private readonly Dictionary<string, bool> _values;

    private SettingsSnapshot(bool readable, Dictionary<string, bool> values)
    {
        Readable = readable;
        _values = values;
    }

    /// <summary>
    /// 当前值是否可信。
    /// <para>false = 文件存在但解析失败（或读盘异常）：此时**任何**"当前值"判断都不可信，
    /// 调用方必须放弃写入，而不是按默认值去翻开关（翻错方向 = 用户看到"点了反而变了"）。</para>
    /// </summary>
    public bool Readable { get; }

    /// <summary>文件是否不存在（不存在 = 从未改过设置 → 全部等于各自默认值，可以安全判断）。</summary>
    public bool FileMissing { get; private init; }

    /// <summary>取键的当前值；键不存在返回 null（调用方用目录默认值）。</summary>
    public bool? Get(string key)
        => _values.TryGetValue(key, out var value) ? value : null;

    public static SettingsSnapshot Load()
    {
        var path = ComponentPaths.SettingsFile;
        try
        {
            if (!File.Exists(path))
            {
                return new SettingsSnapshot(true, new Dictionary<string, bool>(StringComparer.Ordinal))
                {
                    FileMissing = true,
                };
            }

            // 允许注释与尾逗号：设置文件是"用户可手改"的（仓库其余读取端同宽容度）。
            using var doc = JsonDocument.Parse(
                File.ReadAllText(path),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });

            var values = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.True)
                    {
                        values[property.Name] = true;
                    }
                    else if (property.Value.ValueKind == JsonValueKind.False)
                    {
                        values[property.Name] = false;
                    }

                    // 非布尔值（字符串/数字/null）一律忽略 = 该键"当前值不可判断"。
                }
            }

            return new SettingsSnapshot(true, values);
        }
        catch (Exception ex)
        {
            LauncherLog.Error($"读取设置快照（{path}）", ex);
            return new SettingsSnapshot(false, new Dictionary<string, bool>(StringComparer.Ordinal));
        }
    }
}
