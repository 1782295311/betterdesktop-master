// BetterDesktop.Shell.MenuBar — 左区 Logo 快捷功能菜单（参照 CairoShell CairoMenu 裁剪为本程序快捷功能）
// 菜单分三组：
//   [本程序] 关于 / 设置（ISettingsWindowService）/ 应用提取器（IEventBus shell.appgrabber.show → shell.dock）
//   [系统入口] Windows 控制面板 / Windows 设置 / 运行 / 任务管理器
//   [电源与 session] 锁定 / 注销 / 重启 / 关机 / 退出 BetterDesktop
// 面板继承 MenuBarPopupWindow（失焦自动收起）；收起时经 Hidden 回调驱动左区图标反向动画回常态。

using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.MenuBar.Contracts;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>左区 Logo 快捷功能菜单。</summary>
internal sealed class LogoMenuWindow : MenuBarPopupWindow
{
    private const double DefaultWidth = 320;

    private readonly ISettingsWindowService? _settingsWindow;
    private readonly IEventBus? _events;

    /// <summary>true = 显示"关于"子视图（窗口内切换，替代系统 MessageBox——窗口属性统一走基类外观）。</summary>
    private bool _aboutMode;

    /// <summary>true = 在"关于"内显示"开源组件"二级子视图（第三方开源依赖清单）。</summary>
    private bool _licensesMode;

    /// <summary>面板隐藏（失焦/点击外部/Esc）后触发，供左区图标回常态动画。</summary>
    public event Action? Hidden;

    public LogoMenuWindow(ISettingsWindowService? settingsWindow, IVibrancyService vibrancy, IAppearanceService? appearance = null, IEventBus? events = null)
        : base(vibrancy, appearance)
    {
        _settingsWindow = settingsWindow;
        _events = events;
        Width = DefaultWidth;
        MinWidth = DefaultWidth;
        SizeToContent = SizeToContent.Height;
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue)
            {
                _aboutMode = false; // 复位子视图：下次打开显示主菜单
                _licensesMode = false;
                Hidden?.Invoke();
            }
        };
    }

    public FrameworkElement BuildPreviewContent() => BuildContent();

    protected override FrameworkElement BuildContent()
    {
        if (_aboutMode)
        {
            return _licensesMode ? BuildLicensesContent() : BuildAboutContent();
        }

        var root = new Border
        {
            Padding = new Thickness(4, 4, 4, 6),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        var column = new StackPanel { Orientation = Orientation.Vertical };

        // —— 本程序 ——
        // "关于"是窗口内子视图切换：hideOnClick=false，点击后就地切到关于页（不收起面板）。
        column.Children.Add(CreateItem("关于 BetterDesktop", () =>
        {
            _aboutMode = true;
            RebuildContent();
        }, hideOnClick: false));
        column.Children.Add(CreateItem("设置", () => _settingsWindow?.Show()));
        // 应用提取器在 shell.dock 包内（跨包不经类型引用）：经 IEventBus 契约由 DockPlugin 打开。
        column.Children.Add(CreateItem("应用提取器", () =>
        {
            try { _ = _events?.EmitAsync<string>("shell.appgrabber.show", string.Empty); }
            catch { /* 事件发送失败静默（M10） */ }
        }));

        column.Children.Add(CreateSeparator());

        // —— 系统入口 ——
        column.Children.Add(CreateItem("Windows 控制面板", () => Start("control.exe")));
        column.Children.Add(CreateItem("Windows 设置", () => Start("ms-settings:")));
        column.Children.Add(CreateItem("运行……", () => Start("shell:::{2559a1f3-21d7-11d4-bdaf-00c04f60b9f0}")));
        column.Children.Add(CreateItem("任务管理器", () => Start("taskmgr.exe")));

        column.Children.Add(CreateSeparator());

        // —— 电源与会话 ——
        column.Children.Add(CreateItem("锁定", () => Start("rundll32.exe", "user32.dll,LockWorkStation")));
        column.Children.Add(CreateItem("注销……", () => Start("shutdown.exe", "/l")));
        column.Children.Add(CreateItem("重启……", () => Start("shutdown.exe", "/r /t 0")));
        column.Children.Add(CreateItem("关机……", () => Start("shutdown.exe", "/s /t 0")));

        column.Children.Add(CreateSeparator());

        // 【2026-09-17 修复】退出前必须写**看门狗豁免标记**：
        // 看门狗（watchdog/Program.cs：3s 轮询 + 8s 宽限）把"壳消失"一律当成崩溃 → 不写标记的话，
        // 用户点了"退出"十几秒后壳会自己回来（用户实测"退不掉"）。语义与托盘"停止主程序"一致：
        // 标记存在 = 不守护，直到用户下次显式启动（tray/ProcessBridge.StartHost 会清标记）。
        // 标记名/路径的单点定义见 kernel/Core/ResidentFlags（托盘与看门狗是零依赖进程，各持一份字面量）。
        column.Children.Add(CreateItem("退出 BetterDesktop……", () =>
        {
            BetterDesktop.Kernel.Core.ResidentFlags.Set(BetterDesktop.Kernel.Core.ResidentFlags.HostStopped);
            Application.Current.Shutdown();
        }));

        root.Child = column;
        return root;
    }

    /// <summary>按当前模式重建内容并应用统一主题外观（背景/描边/前景走基类）。</summary>
    private void RebuildContent()
    {
        ApplyContent(BuildContent());
    }

    /// <summary>
    /// "关于"子视图（窗口内切换，走统一基类外观——不用系统 MessageBox）：
    /// 返回栏 + 程序名 + 版本/构建 + 简介 + 功能特性 + 许可证。
    /// 功能手册（使用说明）后续版本在此追加入口。
    /// </summary>
    private FrameworkElement BuildAboutContent()
    {
        var root = new Border
        {
            Padding = new Thickness(6, 6, 6, 10),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        var column = new StackPanel { Orientation = Orientation.Vertical };

        // 顶栏：返回 + 标题
        var header = new Grid { Height = 32, Margin = new Thickness(2, 0, 2, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var back = CreateItem("‹ 返回", () =>
        {
            _aboutMode = false;
            RebuildContent();
        }, hideOnClick: false);
        back.Height = 28;
        back.Margin = new Thickness(0);
        Grid.SetColumn(back, 0);
        header.Children.Add(back);
        var title = new TextBlock
        {
            Text = "关于",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        column.Children.Add(header);

        column.Children.Add(CreateSeparator());

        // 程序名 + 版本 / 构建
        var name = new TextBlock
        {
            Text = "Better Desktop Cordis",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(10, 10, 0, 2)
        };
        column.Children.Add(name);

        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        string displayVersion = "1.0";
        string buildStamp = "";
        try
        {
            var fv = FileVersionInfo.GetVersionInfo(assembly.Location);
            if (!string.IsNullOrWhiteSpace(fv.ProductVersion))
            {
                var dash = fv.ProductVersion.IndexOf('-');
                displayVersion = dash > 0 ? fv.ProductVersion.Substring(0, dash) : fv.ProductVersion;
            }
            if (!string.IsNullOrWhiteSpace(fv.FileVersion))
            {
                buildStamp = fv.FileVersion;
            }
        }
        catch
        {
            // 版本信息不可读时保持默认（M10）
        }

        var ver = new TextBlock
        {
            Text = $"版本 {displayVersion}" + (string.IsNullOrEmpty(buildStamp) ? "" : $" · 构建 {buildStamp}"),
            FontSize = 11.5,
            Margin = new Thickness(10, 0, 0, 8)
        };
        SetThemeBinding(ver, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        column.Children.Add(ver);

        // 简介
        var desc = new TextBlock
        {
            Text = "Cordis 风格插件内核的 Windows 桌面外壳套件：自绘桌面、顶部菜单栏、底部 Dock、全局搜索、剪贴板历史、截屏、灵动岛、热键侧板与系统右键菜单接管；界面层 C#（WPF），数据与性能敏感层使用 Rust 常驻引擎。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 0, 10, 6)
        };
        column.Children.Add(desc);

        column.Children.Add(CreateSeparator());

        // 功能特性
        var featTitle = new TextBlock
        {
            Text = "功能特性",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(10, 4, 0, 4)
        };
        column.Children.Add(featTitle);

        string[] features =
        {
            "自绘桌面：壁纸 / 图标网格 / 桌面控制（独立进程）",
            "顶部菜单栏：系统状态区 / 搜索 / 扩展中心",
            "底部 Dock：应用图标 / 悬停预览 / 拖放固定",
            "全局搜索：程序 / 设置 / 文件，支持中文路径",
            "剪贴板历史：记录 / 搜索 / 收藏 / 贴图 / OCR",
            "截屏：区域 / 全屏 / 标注，默认热键 Win+Shift+B",
            "灵动岛：媒体 / 剪贴板 / 格式转换活动浮层",
            "热键侧板：可操作热键面板，默认 Ctrl+Alt+H",
            "系统右键菜单接管：第三方扩展完整保留",
            "格式转换：右键级联菜单，Rust 引擎 + 第三方工具",
            "托盘控制面与看门狗：组件启停 / 异常自动拉起",
        };
        foreach (var feature in features)
        {
            var line = new TextBlock
            {
                Text = "· " + feature,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10, 2, 10, 2)
            };
            column.Children.Add(line);
        }

        column.Children.Add(CreateSeparator());

        // 许可证 + 版权
        var license = new TextBlock
        {
            Text = "许可证：CC BY-NC 4.0（署名-非商业使用）",
            FontSize = 11.5,
            Margin = new Thickness(10, 6, 0, 2)
        };
        SetThemeBinding(license, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        column.Children.Add(license);

        var copyright = new TextBlock
        {
            Text = "Copyright (C) 2026 Better Desktop Cordis contributors",
            FontSize = 11,
            Margin = new Thickness(10, 0, 0, 2)
        };
        SetThemeBinding(copyright, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        column.Children.Add(copyright);

        // 开源组件（第三方开源依赖清单）：二级子视图，hideOnClick=false 就地切换。
        column.Children.Add(CreateSeparator());
        column.Children.Add(CreateItem("开源组件 ›", () =>
        {
            _licensesMode = true;
            RebuildContent();
        }, hideOnClick: false));

        root.Child = column;
        return root;
    }

    /// <summary>
    /// "开源组件"二级子视图：随包分发的第三方引擎 + .NET（NuGet）依赖 + Rust 引擎依赖。
    /// 许可证信息来自实际安装包元数据（nuget.org nuspec / crates.io Cargo.toml），不臆写。
    /// 完整许可证文本见发布包根 THIRD-PARTY-NOTICES.md 及各组件自带 LICENSE。
    /// </summary>
    private FrameworkElement BuildLicensesContent()
    {
        var root = new Border
        {
            Padding = new Thickness(6, 6, 6, 10),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        var column = new StackPanel { Orientation = Orientation.Vertical };

        // 顶栏：返回（回"关于"）+ 标题
        var header = new Grid { Height = 32, Margin = new Thickness(2, 0, 2, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var back = CreateItem("‹ 返回", () =>
        {
            _licensesMode = false;
            RebuildContent();
        }, hideOnClick: false);
        back.Height = 28;
        back.Margin = new Thickness(0);
        Grid.SetColumn(back, 0);
        header.Children.Add(back);
        var title = new TextBlock
        {
            Text = "开源组件",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        column.Children.Add(header);

        column.Children.Add(CreateSeparator());

        // 清单较长：滚动容器限制高度，避免面板超高。
        var scroll = new ScrollViewer
        {
            MaxHeight = 430,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 2, 0, 0)
        };
        var list = new StackPanel { Orientation = Orientation.Vertical };

        // 设计参考（源码级借鉴）：直接借鉴/移植了以下开源项目的代码实现。
        // 排除仅参考功能形态、自行重新设计的项目。
        list.Children.Add(CreateSectionTitle("设计参考（源码级借鉴）"));
        AddLicenseLine(list, "CairoDesktop (CairoShell)", "Apache-2.0", "菜单栏：Logo 菜单 / Stacks 弹层");
        AddLicenseLine(list, "TranslucentTB", "GPL-3.0", "任务栏外观方案 / ExplorerTAP.dll（GPL 组件随包分发）");
        AddLicenseLine(list, "Open-Shell", "MIT", "高清图标提取 / GDI 资源审计（思路参考）");
        AddLicenseLine(list, "ContextMenuManager", "GPL-3.0", "右键菜单：路径解析 / CLSID 反查（思路参考重写）");
        AddLicenseLine(list, "EarTrumpet", "MIT", "系统强调色读取");
        AddLicenseLine(list, "Mineradio", "待核实", "酷狗音乐 API 契约移植");

        list.Children.Add(CreateSectionTitle("第三方引擎（随包分发 / 按需下载）"));
        AddLicenseLine(list, "calibre", "GPL-3.0", "电子书（epub/mobi 等）转换");
        AddLicenseLine(list, "LibreOffice", "MPL-2.0", "Office 文档转 PDF");
        AddLicenseLine(list, "FFmpeg", "LGPL-2.1 / GPL-2.0", "音视频转码");
        AddLicenseLine(list, "Tesseract OCR", "Apache-2.0", "图片 / 截图文字识别");
        AddLicenseLine(list, "Pandoc", "GPL-2.0+", "文档互转（按需下载）");

        list.Children.Add(CreateSectionTitle(".NET 依赖（NuGet）"));
        AddLicenseLine(list, "ManagedShell", "Apache-2.0", "Windows Shell 互操作");
        AddLicenseLine(list, "SkiaSharp", "MIT", "2D 图形渲染");
        AddLicenseLine(list, "PDFsharp", "MIT", "PDF 生成");
        AddLicenseLine(list, "YamlDotNet", "MIT", "YAML 解析");
        AddLicenseLine(list, "Markdig", "BSD-2-Clause", "Markdown 解析");
        AddLicenseLine(list, "ReverseMarkdown", "MIT", "Markdown 转换");
        AddLicenseLine(list, "TinyPinyin.Net", "未声明（核心源自 Apache-2.0 的 TinyPinyin）", "汉字转拼音");
        AddLicenseLine(list, "LibreHardwareMonitorLib", "MPL-2.0", "硬件状态监测");
        AddLicenseLine(list, "System.Drawing.Common", "MIT", ".NET 平台（微软）");

        list.Children.Add(CreateSectionTitle("Rust 依赖（引擎）"));
        AddLicenseLine(list, "windows", "MIT / Apache-2.0", "Windows API（微软）");
        AddLicenseLine(list, "serde / serde_json", "MIT / Apache-2.0", "序列化");
        AddLicenseLine(list, "sha2 / md-5 / regex / base64 / flate2 / image / serde_yaml", "MIT / Apache-2.0", "哈希 / 正则 / 图像等");
        AddLicenseLine(list, "pulldown-cmark / quick-xml / lopdf / zip", "MIT", "Markdown / XML / PDF / ZIP");
        AddLicenseLine(list, "csv", "Unlicense / MIT", "CSV 解析");

        var note = new TextBlock
        {
            Text = "完整许可证文本见发布包根 THIRD-PARTY-NOTICES.md 及各组件自带 LICENSE 文件。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 8, 10, 2)
        };
        SetThemeBinding(note, TextBlock.ForegroundProperty, "ThemeMutedForeground");
        list.Children.Add(note);

        scroll.Content = list;
        column.Children.Add(scroll);

        root.Child = column;
        return root;
    }

    /// <summary>开源组件清单里的分区标题行。</summary>
    private static TextBlock CreateSectionTitle(string text)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(10, 8, 0, 2)
        };
        return t;
    }

    /// <summary>开源组件清单里的一行：名称 — 许可证（用途）。</summary>
    private static void AddLicenseLine(StackPanel list, string name, string license, string purpose)
    {
        var line = new TextBlock
        {
            Text = $"· {name} — {license}（{purpose}）",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 1, 10, 1)
        };
        list.Children.Add(line);
    }

    /// <summary>一条菜单行：紧凑行 + hover 高亮。默认点击后收起面板（子视图内导航项传 hideOnClick=false）。</summary>
    private FrameworkElement CreateItem(string label, Action onClick, bool hideOnClick = true)
    {
        var row = new Border
        {
            Height = 30,
            Margin = new Thickness(2, 1, 2, 1),
            Padding = new Thickness(10, 0, 8, 0),
            CornerRadius = new CornerRadius(6),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent
        };
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        row.Child = text;

        row.MouseEnter += (_, _) => row.Background = MenuBarTheme.Hover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonDown += (_, _) => row.Background = MenuBarTheme.Pressed;
        row.MouseLeftButtonUp += (_, _) =>
        {
            if (hideOnClick)
            {
                Hide();
            }
            try { onClick(); }
            catch { /* 命令执行失败静默（M10） */ }
        };
        return row;
    }

    private static Border CreateSeparator()
    {
        var sep = new Border { Height = 1, Margin = new Thickness(8, 5, 8, 5) };
        SetThemeBinding(sep, Border.BackgroundProperty, "ThemeSeparator");
        return sep;
    }

    /// <summary>ShellExecute 启动（程序/URI/桌面协议）。失败静默（M10）。</summary>
    private static void Start(string fileName, string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName, arguments ?? string.Empty) { UseShellExecute = true });
        }
        catch
        {
            // 启动失败静默，不打断菜单
        }
    }
}
