// BetterDesktop.Shell.MenuBar — 扩展中心插件目录（单一真相源）
// 「+」（扩展中心）按钮打开的面板以此目录为准：列出可管理的外部扩展功能插件与菜单栏内建模块，
// 每个条目带启用态（持久化于 ISettingsService 的 extensions.<id>.enabled），映射到菜单栏按钮的项可实时显隐。

using BetterDesktop.Shell.MenuBar.Status;

namespace BetterDesktop.Shell.MenuBar.Contracts;

/// <summary>扩展中心管理的条目描述符：一个外部扩展功能插件，或一个菜单栏内建模块。</summary>
internal sealed record ExtensionDescriptor(
    /// <summary>稳定标识（同时用于设置键 extensions.&lt;Id&gt;.enabled）。</summary>
    string Id,
    /// <summary>显示名。</summary>
    string Name,
    /// <summary>一句话描述。</summary>
    string Description,
    /// <summary>图标字符（单字，避免依赖外部图标资源）。</summary>
    string Glyph,
    /// <summary>若映射到菜单栏按钮，则启用态可直接控制该按钮显隐；否则仅持久化意图。</summary>
    MenuBarStatusButtonId? MenuBarButton = null,
    /// <summary>是否为外部扩展功能插件（true=需内核支持，当前仅记忆开关；false=菜单栏内建模块，可实时生效）。</summary>
    bool External = true)
{
    /// <summary>设置键：extensions.&lt;Id&gt;.enabled。</summary>
    public string SettingsKey => $"extensions.{Id}.enabled";
}

/// <summary>
/// 扩展中心插件目录（单一真相源）。顺序即面板展示顺序。
///
/// 【2026-08-30 职责拆分】原来一张表里混了「菜单栏系统功能」与「外部扩展插件」两类，
/// 导致「+」按钮既管系统功能又管外部扩展，职责不清。现按用户定义拆成两张表：
///   <list type="bullet">
///     <item><see cref="External"/> —— **外部扩展功能插件**，由「+」扩展中心管理（启动/停用）。</item>
///     <item><see cref="SystemFeatures"/> —— **菜单栏系统功能**，改由「设置 → 菜单栏」统一管理显隐。</item>
///   </list>
/// <see cref="All"/> 仍返回两者合集，供菜单栏启动时一次性应用持久化显隐。
/// </summary>
internal static class ExtensionCatalog
{
    /// <summary>
    /// 外部扩展功能插件：由菜单栏「+」（扩展中心）管理。
    /// 与系统功能的区别在于——这些是**可选的、独立的扩展能力**，不是菜单栏运行的必要组成。
    /// </summary>
    public static IReadOnlyList<ExtensionDescriptor> External { get; } = new[]
    {
        new ExtensionDescriptor("quick-note", "快速笔记", "一键便签速记（常驻浮窗）", "记"),
        new ExtensionDescriptor("programs-menu", "程序菜单", "左区程序菜单（分组/拖放/Win 键）", "单"),
        new ExtensionDescriptor("weather", "天气", "桌面天气卡片（和风天气）", "天"),
        new ExtensionDescriptor("screenshot", "截屏工具", "区域/全屏截屏与标注", "截"),
        new ExtensionDescriptor("dynamic-desktop", "动态桌面", "动态壁纸桌面", "动"),
        new ExtensionDescriptor("clipboard-history", "剪贴板历史", "记录剪贴板历史，搜索/收藏/一键粘贴（Ctrl+Shift+V）", "剪"),
    };

    /// <summary>
    /// 菜单栏系统功能：由「设置 → 菜单栏」管理显隐。
    /// 每一项都映射到菜单栏右区的具体按钮，切换即实时显隐。
    /// </summary>
    public static IReadOnlyList<ExtensionDescriptor> SystemFeatures { get; } = new[]
    {
        new ExtensionDescriptor("system-tray", "系统托盘", "接管并展示系统托盘图标", "托", MenuBarStatusButtonId.SystemTray, External: false),
        new ExtensionDescriptor("fps", "帧率", "实时显示桌面帧率", "帧", MenuBarStatusButtonId.Fps, External: false),
        new ExtensionDescriptor("cpu", "CPU 利用率", "实时显示 CPU 占用", "芯", MenuBarStatusButtonId.Cpu, External: false),
        new ExtensionDescriptor("memory", "内存利用率", "实时显示内存占用与走势", "存", MenuBarStatusButtonId.Memory, External: false),
        new ExtensionDescriptor("wifi", "WiFi 信号", "显示 WiFi 信号与开关", "网", MenuBarStatusButtonId.Wifi, External: false),
        new ExtensionDescriptor("network-traffic", "网络流量", "显示实时上传/下载速率", "流", MenuBarStatusButtonId.NetworkTraffic, External: false),
        new ExtensionDescriptor("brightness", "亮度", "显示并调节屏幕亮度", "亮", MenuBarStatusButtonId.Brightness, External: false),
        new ExtensionDescriptor("ime", "输入法", "显示并切换输入法", "文", MenuBarStatusButtonId.Ime, External: false),
        new ExtensionDescriptor("bluetooth", "蓝牙", "显示蓝牙状态与已连接设备", "蓝", MenuBarStatusButtonId.Bluetooth, External: false),
        new ExtensionDescriptor("volume", "音量", "显示并调节系统音量", "音", MenuBarStatusButtonId.Volume, External: false),
        new ExtensionDescriptor("microphone", "麦克风", "显示麦克风状态与静音", "麦", MenuBarStatusButtonId.Microphone, External: false),
        new ExtensionDescriptor("battery", "电池", "显示电量与续航", "电", MenuBarStatusButtonId.Battery, External: false),
        new ExtensionDescriptor("notification", "通知中心", "打开系统通知中心", "铃", MenuBarStatusButtonId.Notification, External: false),
        new ExtensionDescriptor("datetime", "日期时间", "显示日期时间并打开日历", "时", MenuBarStatusButtonId.DateTime, External: false),
        new ExtensionDescriptor("desktop", "桌面覆盖", "一键显示桌面", "幕", MenuBarStatusButtonId.Desktop, External: false),
        new ExtensionDescriptor("search", "搜索", "全局搜索（程序/设置/文件）", "搜", MenuBarStatusButtonId.Search, External: false),
    };

    /// <summary>
    /// 全部条目（外部扩展 + 系统功能）。**仅供菜单栏启动时一次性应用持久化显隐**；
    /// 展示用途请分别取 <see cref="External"/> / <see cref="SystemFeatures"/>。
    /// </summary>
    public static IReadOnlyList<ExtensionDescriptor> All { get; } = External.Concat(SystemFeatures).ToArray();
}
