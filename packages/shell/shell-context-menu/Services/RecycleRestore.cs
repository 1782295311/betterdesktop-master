// BetterDesktop.Shell.ContextMenus — 回收站还原（消费 FileCapabilities.Restore）
// 模型（$I/$R 双文件；2026-09-04 审查 S1 修正——旧实现 0x14 头是 v1/老格式，与 v2 路径长度混搭，
// 导致路径前混入 FILETIME 高 32 位两个乱码字符且尾部少读 4 字节 → 还原必然失败被吞）：
//   $R<xxx> = 被删除文件的本体；$I<xxx> = 元数据头。
//   v2（Win10/11，544 字节）：version(8B) + 原大小(8B) + 删除时间 FILETIME(8B) = 24 字节头，
//   路径从 0x18 (24) 起 520 字节 UTF-16（260 字符）。
//   v1（280 字节）暂不支持：拒收 + 诊断（还原项自动隐藏；完整支持登记 deferred）。
// 还原 = 读 $I 得原路径 → 把 $R 移回原路径 → 删 $I。
// 【隔离红线】解析/移动任一步失败即返回 false 且不留下半成品（先移到临时名成功再删 $I）。

using System;
using System.IO;
using System.Text;
using BetterDesktop.Kernel.Core;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>回收站项还原。</summary>
public static class RecycleRestore
{
    private const int HeaderSize = 0x18;
    private const int PathBytes = 520; // 260 个 Unicode 字符
    private const int V2TotalBytes = HeaderSize + PathBytes; // 544

    /// <summary>由回收站内的 $R 文件路径解析出原始路径；失败返回 null。</summary>
    public static string? ParseOriginalPath(string recycleFilePath)
    {
        try
        {
            var iPath = InfoPathOf(recycleFilePath);
            if (iPath is null || !File.Exists(iPath))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(iPath);
            if (bytes.Length < V2TotalBytes)
            {
                // v1（280B）等旧格式：拒收 + 诊断（还原项自动隐藏，绝不弹坏路径）
                DiagnosticLog.Trace("shell.contextmenu",
                    $"$I 格式不支持（len={bytes.Length}，v2 需 {V2TotalBytes}）: {iPath}");
                return null;
            }

            var original = Encoding.Unicode.GetString(bytes, HeaderSize, PathBytes).Trim('\0');
            return string.IsNullOrWhiteSpace(original) ? null : original;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.contextmenu", $"$I 解析失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>还原回收站内的 $R 文件到原始位置；成功返回还原后的路径。</summary>
    public static string? Restore(string recycleFilePath)
    {
        var original = ParseOriginalPath(recycleFilePath);
        if (original is null || !File.Exists(recycleFilePath))
        {
            return null;
        }

        try
        {
            var dir = Path.GetDirectoryName(original);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var target = ZipOps.UniquePath(original);
            File.Move(recycleFilePath, target); // 本体先落地

            var iPath = InfoPathOf(recycleFilePath);
            if (iPath is not null)
            {
                TryDelete(iPath); // 再清元数据
            }
            return target;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.contextmenu", $"还原失败（不留下半成品）: {ex.Message}");
            return null;
        }
    }

    /// <summary>$R 路径 → 对应 $I 路径；输入不是 $R 文件时返回 null。</summary>
    private static string? InfoPathOf(string recycleFilePath)
    {
        var dir = Path.GetDirectoryName(recycleFilePath);
        var name = Path.GetFileName(recycleFilePath);
        if (string.IsNullOrEmpty(dir) || !name.StartsWith("$R", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return Path.Combine(dir, "$I" + name[2..]);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 元数据清理失败静默（不影响已还原的本体，M10）
        }
    }
}
