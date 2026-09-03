// BetterDesktop.Shell.ContextMenus — 系统字形图标（sys: 协议）
// 用 Segoe MDL2 Assets / Segoe Fluent Icons 字体渲染矢量图标，零资源、零磁盘 IO、随 DPI 缩放。
// 【红线】只认调用方给出的**十六进制码位**（如 sys:E8A7）；解析失败或码位不在白名单 → 返回 null
// （宁可留空图标位，也不猜码位——Win11 式错误图标比没有图标更糟）。

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>系统字形图标工厂（sys:&lt;hex&gt; → Segoe 字体 TextBlock）。</summary>
public static class SystemGlyphs
{
    /// <summary>已核对的码位白名单（Segoe MDL2 Assets）。未列出的码位一律不渲染。</summary>
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // 文档
        "E8A5", // Document
        "E7C3", // Page
        "E8E5", // Folder
        "E7B8", // FolderOpen
        // 编辑
        "E70F", // Edit
        "E74D", // Delete
        "E8C8", // Copy
        "E77F", // Cut
        // 视图/工具
        "E713", // More
        "E7B3", // Zoom
        "E7A7", // Print
        "E8B9", // Slideshow(Preview)
        // 系统
        "E7E8", // Info
        "E946", // Help
        "E7BA", // Refresh
        "E72E", // Save
        "E894", // OpenFile
        "E8A7", // OpenWith
        "E7AD", // Folder(alt)
        "E838", // Repair
        "E7EF", // Admin(Shield)
        "E730", // Pictures
        "E8B2", // Zip/Folder
    };

    /// <summary>按码位创建字形图标；未命中白名单/解析失败返回 null（调用方留空图标位）。</summary>
    public static FrameworkElement? Create(string? hexCode)
    {
        if (string.IsNullOrWhiteSpace(hexCode) || !Known.Contains(hexCode!))
        {
            return null;
        }

        if (!int.TryParse(hexCode, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)
            || code <= 0 || code > 0x10FFFF)
        {
            return null;
        }

        return new TextBlock
        {
            Text = char.ConvertFromUtf32(code),
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            SnapsToDevicePixels = true,
        };
    }
}
