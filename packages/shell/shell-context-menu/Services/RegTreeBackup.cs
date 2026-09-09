// BetterDesktop.Shell.ContextMenus — 注册表键树备份/恢复（M1 管理器安全网）
// 纪律：任何写操作（启停/删除）前必须先 Snapshot 到本地 JSON；Restore 可整体还原。
// 备份目录：%LocalAppData%\BetterDesktop\MenuManager\backup\

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BetterDesktop.Kernel.Core;
using Microsoft.Win32;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>注册表键树备份（JSON 快照）与恢复。</summary>
public static class RegTreeBackup
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public sealed class KeySnapshot
    {
        public string Path { get; set; } = string.Empty;
        public List<ValueEntry> Values { get; set; } = [];
        public List<KeySnapshot> SubKeys { get; set; } = [];
    }

    public sealed class ValueEntry
    {
        public string Name { get; set; } = string.Empty;
        public int Kind { get; set; }
        public string? StringValue { get; set; }
        public byte[]? BytesValue { get; set; }
        public int IntValue { get; set; }
        public long LongValue { get; set; }
    }

    public static string BackupDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BetterDesktop", "MenuManager", "backup");

    /// <summary>快照键树（regPath 形如 "HKLM\SOFTWARE\Classes\..."）。返回备份文件路径；失败 null。</summary>
    public static string? Snapshot(string regPath)
    {
        try
        {
            var snapshot = ReadTree(regPath);
            if (snapshot is null)
            {
                DiagnosticLog.Trace("menu-manager", $"备份失败（键不存在）: {regPath}");
                return null;
            }
            Directory.CreateDirectory(BackupDirectory);
            var file = Path.Combine(BackupDirectory,
                $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{Sanitize(regPath)}.json");
            File.WriteAllText(file, JsonSerializer.Serialize(snapshot, JsonOptions));
            DiagnosticLog.Trace("menu-manager", $"已备份: {regPath} → {Path.GetFileName(file)}");
            return file;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("menu-manager", $"备份异常: {regPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>从备份文件恢复键树（不存在则重建）。</summary>
    public static bool Restore(string backupFile)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<KeySnapshot>(File.ReadAllText(backupFile), JsonOptions);
            if (snapshot is null)
            {
                return false;
            }
            WriteTree(snapshot);
            DiagnosticLog.Trace("menu-manager", $"已恢复备份: {Path.GetFileName(backupFile)}");
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("menu-manager", $"恢复失败: {backupFile}: {ex.Message}");
            return false;
        }
    }

    /// <summary>备份目录内全部备份文件（新→旧）。</summary>
    public static IReadOnlyList<string> ListBackups()
    {
        return Directory.Exists(BackupDirectory)
            ? Directory.GetFiles(BackupDirectory, "*.json").OrderByDescending(File.GetLastWriteTime).ToList()
            : [];
    }

    private static string Sanitize(string regPath) =>
        regPath.Replace('\\', '_').Replace(':', '_').Replace('*', '#');

    private static KeySnapshot? ReadTree(string regPath)
    {
        var (root, sub) = RegTakeover.SplitPath(regPath);
        using var baseKey = RegistryKey.OpenBaseKey(root, RegistryView.Default);
        using var key = baseKey.OpenSubKey(sub);
        return key is null ? null : ReadTreeRecursive(key, regPath);
    }

    private static KeySnapshot ReadTreeRecursive(RegistryKey key, string path)
    {
        var node = new KeySnapshot { Path = path };
        foreach (var name in key.GetValueNames())
        {
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var entry = new ValueEntry { Name = name, Kind = (int)key.GetValueKind(name) };
            switch (value)
            {
                case int i: entry.IntValue = i; break;
                case long l: entry.LongValue = l; break;
                case byte[] bytes: entry.BytesValue = bytes; break;
                case string[] arr: entry.StringValue = string.Join('\0', arr); entry.Kind = (int)RegistryValueKind.MultiString; break;
                case string s: entry.StringValue = s; break;
            }
            node.Values.Add(entry);
        }
        foreach (var subName in key.GetSubKeyNames())
        {
            try
            {
                using var sub = key.OpenSubKey(subName);
                if (sub is not null)
                {
                    node.SubKeys.Add(ReadTreeRecursive(sub, $"{path}\\{subName}"));
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Trace("menu-manager", $"备份子键跳过: {path}\\{subName}: {ex.Message}");
            }
        }
        return node;
    }

    private static void WriteTree(KeySnapshot node)
    {
        var (root, sub) = RegTakeover.SplitPath(node.Path);
        using var baseKey = RegistryKey.OpenBaseKey(root, RegistryView.Default);
        using var key = baseKey.CreateSubKey(sub, writable: true);
        if (key is null)
        {
            return;
        }
        foreach (var v in node.Values)
        {
            var kind = (RegistryValueKind)v.Kind;
            object value = kind switch
            {
                RegistryValueKind.DWord => v.IntValue,
                RegistryValueKind.QWord => v.LongValue,
                RegistryValueKind.Binary => v.BytesValue ?? [],
                RegistryValueKind.MultiString => (v.StringValue ?? string.Empty).Split('\0'),
                RegistryValueKind.ExpandString => v.StringValue ?? string.Empty,
                _ => v.StringValue ?? string.Empty,
            };
            key.SetValue(v.Name.Length == 0 ? string.Empty : v.Name, value, kind);
        }
        foreach (var child in node.SubKeys)
        {
            WriteTree(child);
        }
    }
}
