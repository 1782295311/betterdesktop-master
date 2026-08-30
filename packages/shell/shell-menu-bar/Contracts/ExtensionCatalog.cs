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

/// <summary>扩展中心插件目录（单一真相源）。顺序即面板展示顺序。</summary>
internal static class ExtensionCatalog
{
    public static IReadOnlyList<ExtensionDescriptor> All { get; } = new[]
    {
        // —— 菜单栏内建功能模块（可实时显隐）——
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

        // —— 外部扩展功能插件（持久化管理；需内核支持的待后续接入）——
        new ExtensionDescriptor("programs-menu", "程序菜单", "左区程序菜单（分组/拖放/Win 键）", "单", External: true),
        new ExtensionDescriptor("weather", "天气", "桌面天气卡片（和风天气）", "天", External: true),
        new ExtensionDescriptor("search", "搜索", "全局搜索（UWP SearchPane）", "搜", External: true),
        new ExtensionDescriptor("stage-manager", "台前调度", "窗口总览与平铺（Mission Control 式）", "窗", External: true),
        new ExtensionDescriptor("quick-note", "快速笔记", "一键便签速记", "记", External: true),
        new ExtensionDescriptor("screenshot", "截屏工具", "区域/全屏截屏与标注", "截", External: true),
        new ExtensionDescriptor("dynamic-desktop", "动态桌面", "动态壁纸桌面", "动", External: true),
    };
}
