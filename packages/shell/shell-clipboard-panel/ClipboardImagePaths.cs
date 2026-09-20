// BetterDesktop.Clipboard.Panel — 剪贴板图片文件路径解析
//
// 【为什么需要它 · 2026-09-12 真机】引擎写入条目的 imagePath 是**相对存储根**的路径
//（engine/src/engine.rs: `format!("clipboard\\images\\{id}.png")`；model.rs 字段注释亦标明"相对存储根"）。
// 面板早期直接用 `File.Exists(entry.ImagePath)` 判断 → 按进程当前工作目录解析 → 恒为 false
// → 图片条目**永远走回退分支**，只显示占位图标与尺寸比例（用户实测现象）。
//
// 本类把相对路径统一解析为绝对路径，并优先返回引擎生成的缩略图（thumbs\{id}.{jpg|png}，480px）：
//   · 缩略图更小、解码更快，且路径与扩展名有约定可查，不依赖条目字段；
//   · 原图（images\{id}.png）作为回退；
//   · 都不存在 → 返回 null，调用方显示占位/尺寸文本。

using System;
using System.IO;
using BetterDesktop.Shell.Clipboard.Contracts;

namespace BetterDesktop.Clipboard.Panel;

/// <summary>剪贴板存储目录下的图片文件解析（相对路径 → 绝对路径；缩略图优先）。</summary>
internal static class ClipboardImagePaths
{
    /// <summary>存储根目录（引擎与面板约定的同一位置）。</summary>
    internal static string BaseDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterDesktop");

    /// <summary>解析引擎给出的相对/绝对路径；文件不存在返回 null。</summary>
    internal static string? Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        if (Path.IsPathRooted(path))
        {
            return File.Exists(path) ? path : null;
        }
        string full = Path.Combine(BaseDirectory, path);
        return File.Exists(full) ? full : null;
    }

    /// <summary>缩略图绝对路径（有 alpha 存 PNG、无 alpha 存 JPEG，两种扩展名都试）；不存在返回 null。</summary>
    internal static string? ResolveThumbnail(string? entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId))
        {
            return null;
        }
        string dir = Path.Combine(BaseDirectory, "clipboard", "thumbs");
        foreach (var extension in new[] { ".jpg", ".png" })
        {
            string candidate = Path.Combine(dir, entryId + extension);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>图片条目用于展示的最佳文件：缩略图 → 原图 → null（调用方据此回退占位/尺寸文本）。</summary>
    internal static string? BestDisplayImage(ClipboardEntry entry)
    {
        // 【表情包 · 2026-09-12】表情包的 `file_paths[0]` 就是**珍藏的原文件**（动图），
        // 但它可能是几 MB 的 GIF —— 展示优先用引擎生成的**首帧缩略图**（thumbs\{id}.{png|jpg}），
        // 只有缩略图缺失时才回退原文件（面板显示小尺寸，限幅解码）。
        // 【2026-09-13】判据由"分类是表情包"改为**标记**（标记可打在任意类型条目上）。
        if (entry.IsSticker)
        {
            return ResolveThumbnail(entry.Id) ?? Resolve(StickerSourcePath(entry));
        }
        return ResolveThumbnail(entry.Id) ?? Resolve(entry.ImagePath);
    }

    /// <summary>表情包珍藏文件路径（条目 file_paths[0]；绝对路径，不存在返回 null）。</summary>
    internal static string? StickerSourcePath(ClipboardEntry entry)
        => entry.FilePaths.Length > 0 ? entry.FilePaths[0] : null;
}
