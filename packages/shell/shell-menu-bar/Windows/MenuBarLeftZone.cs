// BetterDesktop.Shell.MenuBar — 菜单栏左区
//
// 【背景】此前左区只是一个 Text="  " 的占位 TextBlock，README 的验收清单里"左区逐项"全未勾选。
// 左区承担的是**导航入口**（程序菜单 + 常用位置），与右区"状态展示"职责正交，因此独立成文件。
//
// 【设计约定】
//   - 程序菜单（Cairo 图标）复用已存在的 IStartMenuService（shell-start-menu 提供），
//     菜单栏不自己实现菜单；服务未注入时按 M10 降级——**不呈现该入口**，而不是放个点了没反应的按钮。
//   - 位置/下载/文档交给 explorer 打开，菜单栏不自己实现文件浏览。
//   - 外观（尺寸、悬停反馈、前景色）与右区共用 MenuBarTheme，保证左右两区手感一致。

using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BetterDesktop.Shell.MenuBar.Contracts;
using BetterDesktop.Shell.StartMenu.Contracts;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>菜单栏左区：程序菜单入口 + 常用位置（位置/下载/文档）。</summary>
internal sealed class MenuBarLeftZone : StackPanel
{
    /// <summary>左区按钮高度（与右区 MenuBarStatusStrip.CreateButton 一致，保持两区对齐）。</summary>
    private const double ButtonHeight = 14.0;

    public MenuBarLeftZone(IStartMenuService? startMenu)
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;

        // 1) Cairo 图标 → 打开/关闭程序菜单（开始菜单）
        //    服务缺失时整个入口不添加：宁可没有，也不放一个点击无反应的按钮。
        if (startMenu is not null)
        {
            Children.Add(CreateGlyphButton("◈", "程序菜单", startMenu.Toggle));
        }

        // 2) 常用位置：位置（此电脑）/ 下载 / 文档。交给 explorer，菜单栏不重复实现文件浏览。
        AddPlace("位置", "打开“此电脑”", "shell:MyComputerFolder");
        AddPlace("下载", "打开下载文件夹", ResolveDownloads());
        AddPlace("文档", "打开文档文件夹", SafeGetFolderPath(Environment.SpecialFolder.Personal));
    }

    private void AddPlace(string label, string tooltip, string? target)
    {
        if (string.IsNullOrEmpty(target))
        {
            // 目录解析失败（权限/特殊环境）：不添加入口，避免点了抛异常
            return;
        }

        Children.Add(CreateTextButton(label, tooltip, () => OpenInExplorer(target!)));
    }

    /// <summary>用资源管理器打开目标（目录路径或 shell: 协议）。失败静默（M10 降级：菜单栏不弹错误框）。</summary>
    private static void OpenInExplorer(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
        }
        catch
        {
            // 目标不存在/被策略拦截：静默忽略，不影响菜单栏其余部分
        }
    }

    private static string? ResolveDownloads()
    {
        // .NET 的 Environment.SpecialFolder 没有 Downloads 项，但用户目录在中文 Windows 上已被本地化，
        // 直接拼 "Downloads" 会失效。因此优先用 shell: 协议交给 explorer 解析，零本地化问题。
        return "shell:Downloads";
    }

    private static string? SafeGetFolderPath(Environment.SpecialFolder folder)
    {
        try
        {
            var path = Environment.GetFolderPath(folder);
            return string.IsNullOrEmpty(path) || !Directory.Exists(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>字形按钮（Cairo 图标）：单字符图标 + 固定宽度。</summary>
    private static Border CreateGlyphButton(string glyph, string tooltip, Action onClick)
    {
        var text = new TextBlock
        {
            Text = glyph,
            Foreground = MenuBarTheme.Foreground,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        return Build(18, tooltip, text, onClick);
    }

    /// <summary>文字按钮（位置/下载/文档）：菜单栏仅 16px 高，字号取 9 并压缩内边距。</summary>
    private static Border CreateTextButton(string label, string tooltip, Action onClick)
    {
        var text = new TextBlock
        {
            Text = label,
            Foreground = MenuBarTheme.Foreground,
            FontSize = 9,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        return Build(double.NaN, tooltip, text, onClick);
    }

    private static Border Build(double width, string tooltip, TextBlock content, Action onClick)
    {
        var text = content;
        if (double.IsNaN(width))
        {
            // 文字按钮按内容自适应宽度，左右各留 6px 呼吸
            text.Margin = new Thickness(6, 0, 6, 0);
        }

        var btn = new Border
        {
            Width = width,
            Height = ButtonHeight,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = text,
            ToolTip = tooltip,
            SnapsToDevicePixels = true,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        MenuBarTheme.AttachHoverFeedback(btn);
        btn.MouseLeftButtonUp += (_, _) =>
        {
            btn.Background = MenuBarTheme.Hover;
            onClick();
        };
        return btn;
    }
}
