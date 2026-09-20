// BetterDesktop.Shell.ContextMenus — 注册表状态的备份 / 回滚（S4-2 第 2 步的**前置**）
//
// 【为什么它必须排在"自动写注册表"之前】写 HKCU\Software\Classes 不是改代码，是改**系统状态**：
//   · 开发机在 D6 事故里已有残留（注册表指向打包目录），改之前先拍照；
//   · 真机验收要求"从零到有再到零"里的"到零"是**可验证**的，而不是"应该删干净了"；
//   · 用户机器上出问题时，回滚是唯一能立刻恢复的手段 —— 手工清理注册表该是最后一招，不是第一招。
//
// 【范围】只覆盖本扩展**拥有**的键（CLSID + InprocServer32 + 4 个场景键），
// 路径由 ComShellExtensionRegistrar.GetOwnedKeyPaths() 给出 —— 与注册/注销同源，不另立清单。
// 注册表里其它任何东西都不碰。
//
// 【失败语义】全部方法不抛：失败返回 false 且 error 带原因。恢复时**逐键收集**问题，
// 不因为一个键失败就放弃其余 —— 回滚场景下"尽量多还原"比"要么全对要么全不动"更有用。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Kernel.Deployment;
using Microsoft.Win32;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>一个值在备份时刻的样子（<see cref="Kind"/> 用 <see cref="RegistryValueKind"/> 的名字，跨机可读）。</summary>
public sealed record RegistryValueSnapshot
{
    /// <summary>值名；空串 = 默认值。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>注册表值类型名（如 <c>String</c> / <c>DWord</c>）。</summary>
    public string Kind { get; init; } = nameof(RegistryValueKind.String);

    /// <summary>数据（字符串 / 数字 / 字符串数组）。不可还原的类型为 null。</summary>
    public JsonElement? Data { get; init; }
}

/// <summary>一个键在备份时刻的状态。</summary>
/// <remarks>
/// <see cref="Exists"/> 为 <c>false</c> 的记录**必须**保留：回滚要能把"备份之后才出现的键"删掉，
/// 否则"从零到零"永远验证不了（零状态是"键不存在"，而不是"键存在但值为空"）。
/// </remarks>
public sealed record RegistryKeySnapshot
{
    public string Path { get; init; } = string.Empty;

    public bool Exists { get; init; }

    public List<RegistryValueSnapshot> Values { get; init; } = [];
}

/// <summary>一次备份的完整内容（落成 JSON）。</summary>
public sealed record ShellMenuBackup
{
    public int Version { get; init; } = 1;

    public string CreatedAt { get; init; } = string.Empty;

    /// <summary>拍照时扩展是否已注册（回滚后可据此核对"回到原样"）。</summary>
    public bool WasRegistered { get; init; }

    /// <summary>拍照时注册表里的 DLL 路径（诊断；<c>null</c> = 当时没有）。</summary>
    public string? RegisteredDllPath { get; init; }

    /// <summary>键快照（父键在前，与 <see cref="ComShellExtensionRegistrar.GetOwnedKeyPaths"/> 同序）。</summary>
    public List<RegistryKeySnapshot> Keys { get; init; } = [];
}

/// <summary>恢复结果（逐键统计，便于把"部分成功"如实报出来）。</summary>
public sealed record RestoreReport
{
    /// <summary>重建/覆盖的键数。</summary>
    public int Written { get; init; }

    /// <summary>按备份删除的键数（备份里 <c>Exists=false</c> 而现在存在的）。</summary>
    public int Removed { get; init; }

    /// <summary>遇到的问题（空 = 全部成功）。**不吞**：部分成功必须看得见。</summary>
    public List<string> Problems { get; init; } = [];

    public bool Ok => Problems.Count == 0;
}

/// <summary>注册表备份 / 回滚（范围 = <see cref="ComShellExtensionRegistrar.GetOwnedKeyPaths"/>）。</summary>
public static class ShellMenuRegistryBackup
{
    /// <summary>默认备份文件名（落在 <c>%LOCALAPPDATA%\BetterDesktop\</c>，与本产品其它留痕同处）。</summary>
    public const string DefaultFileName = "shellmenu-backup.json";

    /// <summary>默认备份路径。覆盖式（"最近一次已知良好状态"）——回滚最需要的就是它。</summary>
    public static string DefaultPath => Path.Combine(DeploymentInfo.DirectoryPath, DefaultFileName);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // 路径与值里含中文/反斜杠时别转成 \uXXXX：这份文件是给人看的
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>给当前注册表状态拍照。</summary>
    public static ShellMenuBackup Capture()
    {
        var keys = new List<RegistryKeySnapshot>();
        foreach (var path in ComShellExtensionRegistrar.GetOwnedKeyPaths())
        {
            keys.Add(CaptureKey(path));
        }

        return new ShellMenuBackup
        {
            CreatedAt = DateTimeOffset.Now.ToString("O"),
            WasRegistered = ComShellExtensionRegistrar.IsRegistered(),
            RegisteredDllPath = ComShellExtensionRegistrar.GetRegisteredDllPath(),
            Keys = keys,
        };
    }

    private static RegistryKeySnapshot CaptureKey(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        if (key is null)
        {
            return new RegistryKeySnapshot { Path = path, Exists = false };
        }

        var values = new List<RegistryValueSnapshot>();
        foreach (var name in key.GetValueNames())
        {
            var kind = key.GetValueKind(name);
            // 展开环境变量：备份存的是**当时看得见的字符串**，回滚写回同一字符串即可
            var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            values.Add(new RegistryValueSnapshot
            {
                Name = name,
                Kind = kind.ToString(),
                Data = ToJson(raw, kind),
            });
        }

        return new RegistryKeySnapshot { Path = path, Exists = true, Values = values };
    }

    /// <summary>把注册表数据转成 JSON（不可还原的类型记 <c>null</c>，由恢复侧如实报告）。</summary>
    private static JsonElement? ToJson(object? raw, RegistryValueKind kind)
    {
        object? shape = kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => raw as string ?? string.Empty,
            // DWord 存成 int（写回时类型也对得上）；QWord 存 long
            // 【必须写全名 System.Convert】本项目有 `BetterDesktop.Shell.Convert` 命名空间，
            // 裸 `Convert` 会被它抢走（编译期报"命名空间里没有 ToInt64"）。同类坑见 `global::Windows`。
            RegistryValueKind.DWord => (int)System.Convert.ToInt64(raw ?? 0L),
            RegistryValueKind.QWord => System.Convert.ToInt64(raw ?? 0L),
            RegistryValueKind.MultiString => raw as string[] ?? [],
            RegistryValueKind.Binary => raw as byte[] ?? [],
            // 其余类型（None / Unknown）不记录数据 —— 恢复侧会如实报"不可还原"，而不是写回垃圾
            _ => null,
        };

        return shape is null ? null : JsonSerializer.SerializeToElement(shape);
    }

    /// <summary>写入备份文件（原子：先写临时文件再 Move，读方永远看到完整内容）。</summary>
    public static bool Save(ShellMenuBackup backup, string path, out string? error)
    {
        error = null;
        ArgumentNullException.ThrowIfNull(backup);

        var temp = path + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                temp,
                JsonSerializer.Serialize(backup, WriteOptions),
                new System.Text.UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            DiagnosticLog.Trace("shell.context-menu",
                $"注册表备份已写入 {path}（{backup.Keys.Count} 键，registered={backup.WasRegistered}）");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // 清理失败不影响结论
            }
            return false;
        }
    }

    /// <summary>读取备份文件。</summary>
    public static bool TryLoad(string path, out ShellMenuBackup? backup, out string? error)
    {
        backup = null;
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                error = $"备份文件不存在：{path}";
                return false;
            }

            backup = JsonSerializer.Deserialize<ShellMenuBackup>(File.ReadAllText(path), ReadOptions);
            if (backup is null || backup.Keys.Count == 0)
            {
                backup = null;
                error = $"备份文件内容为空或没有键记录：{path}";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 按备份回滚。**逐键进行、逐键收集问题** —— 一个键失败不放弃其余
    /// （回滚场景下"尽量多还原"比"要么全对要么全不动"有用）。
    /// </summary>
    public static RestoreReport Restore(ShellMenuBackup backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        var written = 0;
        var removed = 0;
        var problems = new List<string>();

        // 先删后建，且**父键在前**（CLSID 在 InprocServer32 之前）：删父键会连带删掉子键，
        // 若顺序反过来，先重建的子键会被随后删父键的操作带走 —— 静默少一个键。
        foreach (var snapshot in backup.Keys)
        {
            try
            {
                if (!snapshot.Exists)
                {
                    if (DeleteTree(snapshot.Path))
                    {
                        removed++;
                        DiagnosticLog.Trace("shell.context-menu", $"回滚：删除备份中不存在的键 {snapshot.Path}");
                    }
                    continue;
                }

                DeleteTree(snapshot.Path); // 覆盖式：先清空再按快照重建
                using var key = Registry.CurrentUser.CreateSubKey(snapshot.Path);
                if (key is null)
                {
                    problems.Add($"{snapshot.Path}: 无法创建键");
                    continue;
                }

                foreach (var value in snapshot.Values)
                {
                    if (!TryWrite(key, value, out var problem))
                    {
                        problems.Add($"{snapshot.Path}\\{value.Name}: {problem}");
                    }
                }
                written++;
            }
            catch (Exception ex)
            {
                problems.Add($"{snapshot.Path}: {ex.Message}");
            }
        }

        return new RestoreReport { Written = written, Removed = removed, Problems = problems };
    }

    private static bool TryWrite(RegistryKey key, RegistryValueSnapshot value, out string? problem)
    {
        problem = null;
        if (!Enum.TryParse<RegistryValueKind>(value.Kind, out var kind))
        {
            problem = $"未知的值类型 '{value.Kind}'";
            return false;
        }

        if (value.Data is null)
        {
            // 不可还原的类型：**如实报出来**而不是跳过 —— 静默少还原一个值，
            // 症状是"回滚说成功了但行为还是不对"，那是最难查的一类。
            problem = $"值类型 '{value.Kind}' 不可还原（备份中未记录数据）";
            return false;
        }

        var data = value.Data.Value;
        object? payload = kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => data.GetString(),
            RegistryValueKind.DWord => (int)data.GetInt64(),
            RegistryValueKind.QWord => data.GetInt64(),
            RegistryValueKind.MultiString =>
                data.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray(),
            RegistryValueKind.Binary => data.EnumerateArray().Select(e => e.GetByte()).ToArray(),
            _ => null,
        };

        if (payload is null)
        {
            problem = $"值类型 '{value.Kind}' 不支持写入";
            return false;
        }

        key.SetValue(value.Name, payload, kind);
        return true;
    }

    /// <summary>删除整棵键树（<c>DeleteSubKeyTree</c> 语义）；键不存在返回 false 不报错。</summary>
    /// <remarks>
    /// **必须先探测再删、且探测句柄当场释放**。两个坑叠在一起会得到一类间歇性失败：
    /// <list type="number">
    /// <item>把 <c>OpenSubKey(leaf)</c> 的返回值直接用在 <c>is null</c> 判断里 → 句柄泄漏；</item>
    /// <item>句柄还开着就删掉那个键 → 该键进入"标记为删除"状态，**后续**对同一路径的操作
    /// （比如回滚里的"删掉再重建"）会报"试图在标记为删除的注册表项上进行不合法的操作"。</item>
    /// </list>
    /// 因为依赖 GC 何时回收那个泄漏句柄，症状是**时而复现**——最难查的一类。
    /// 故这里用 <c>using</c> 把探测句柄立刻释放，再用**完整路径**直接删（不再持有父键句柄）。
    /// </remarks>
    private static bool DeleteTree(string path)
    {
        using (var probe = Registry.CurrentUser.OpenSubKey(path))
        {
            if (probe is null)
            {
                return false;
            }
        }

        Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        return true;
    }
}
