// BetterDesktop.Shell.ContextMenus — 回收站还原（消费 FileCapabilities.Restore）
// 模型（技术库：拆析-ContextMenuManager，$I/$R 双文件）：
//   $R<xxx> = 被删除文件的本体；$I<xxx> = 元数据头，0x14 起 520 字节 Unicode = 原完整路径。
// 还原 = 读 $I 得原路径 → 把 $R 移回原路径 → 删 $I。
// 【隔离红线】解析/移动任一步失败即返回 false 且不留下半成品（先移到临时名成功再删 $I）；
//   真机若验证不通过，本模块整体下掉（菜单项隐藏），不拖累批次。

using System;
using System.IO;
using System.Text;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>回收站项还原。</summary>
public static class RecycleRestore
{
    private const int HeaderSize = 0x14;
    private const int PathBytes = 520; // 260 个 Unicode 字符

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
            if (bytes.Length < HeaderSize + PathBytes)
            {
                return null;
            }

            var original = Encoding.Unicode.GetString(bytes, HeaderSize, PathBytes).TrimEnd('\0');
            return string.IsNullOrWhiteSpace(original) ? null : original;
        }
        catch
        {
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
        catch
        {
            return null; // 失败不留下半成品（M10）
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
