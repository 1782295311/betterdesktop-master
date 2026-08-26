using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using BetterDesktop.Shell.Search.Contracts;

namespace BetterDesktop.Shell.Search.Services;

/// <summary>
/// 设置搜索：硬编码 Windows 设置页清单（ms-settings: canonical URI），
/// 对显示名 + 关键字做子序列匹配（复用 <see cref="ProgramSearchProvider.Score"/>）。
/// 结果可直接用 LaunchPath（ms-settings:…）经 ShellExecute 打开。
/// </summary>
public sealed class SettingsSearchProvider : ISearchResultProvider
{
    public string Name => "settings";

    private static readonly SettingsEntry[] Entries =
    {
        new("系统", "ms-settings:", "system about"),
        new("显示", "ms-settings:display", "display screen 显示器"),
        new("通知和操作", "ms-settings:notifications", "notifications 通知"),
        new("声音", "ms-settings:sound", "sound audio 声音"),
        new("专注助手", "ms-settings:quietmomentshome", "focus assist quiet 专注"),
        new("电源和睡眠", "ms-settings:powersleep", "power sleep battery 电源 睡眠"),
        new("存储", "ms-settings:storagesense", "storage disk 存储"),
        new("平板电脑模式", "ms-settings:tabletmode", "tablet mode 平板"),
        new("多任务处理", "ms-settings:multitasking", "multitasking snap 多任务"),
        new("投影到此电脑", "ms-settings:project", "project 投影"),
        new("共享体验", "ms-settings:sharing", "sharing 共享"),
        new("剪贴板", "ms-settings:clipboard", "clipboard 剪贴板"),
        new("远程桌面", "ms-settings:remotedesktop", "remote desktop 远程"),
        new("关于", "ms-settings:about", "about pc info 关于"),
        new("蓝牙和其他设备", "ms-settings:bluetooth", "bluetooth devices 蓝牙"),
        new("打印机和扫描仪", "ms-settings:printers", "printers scanner 打印机 扫描"),
        new("鼠标", "ms-settings:mousetouchpad", "mouse pointer 鼠标"),
        new("触摸板", "ms-settings:devices-touchpad", "touchpad 触摸板"),
        new("输入", "ms-settings:typing", "typing keyboard 输入"),
        new("笔和 Windows Ink", "ms-settings:pen", "pen ink 笔"),
        new("自动播放", "ms-settings:autoplay", "autoplay 自动播放"),
        new("USB", "ms-settings:usb", "usb"),
        new("网络和 Internet", "ms-settings:network", "network internet 网络"),
        new("Wi-Fi", "ms-settings:network-wifi", "wifi wireless 无线"),
        new("以太网", "ms-settings:network-ethernet", "ethernet lan 以太网"),
        new("拨号", "ms-settings:network-dialup", "dialup 拨号"),
        new("VPN", "ms-settings:network-vpn", "vpn"),
        new("飞行模式", "ms-settings:network-airplanemode", "airplane flight 飞行"),
        new("移动热点", "ms-settings:network-mobilehotspot", "mobile hotspot 热点"),
        new("数据使用量", "ms-settings:network-datausage", "data usage 数据"),
        new("代理", "ms-settings:network-proxy", "proxy 代理"),
        new("个性化", "ms-settings:personalization", "personalize 个性化"),
        new("背景", "ms-settings:personalization-background", "background wallpaper 背景"),
        new("颜色", "ms-settings:personalization-colors", "colors theme accent 颜色"),
        new("锁屏界面", "ms-settings:lockscreen", "lock screen 锁屏"),
        new("主题", "ms-settings:personalization-themes", "themes 主题"),
        new("开始", "ms-settings:personalization-start", "start 开始"),
        new("任务栏", "ms-settings:taskbar", "taskbar 任务栏"),
        new("应用", "ms-settings:apps", "apps 应用"),
        new("应用和功能", "ms-settings:appsfeatures", "apps features uninstall 应用 卸载"),
        new("默认应用", "ms-settings:defaultapps", "default apps 默认应用"),
        new("可选功能", "ms-settings:optionalfeatures", "optional features 可选功能"),
        new("启动", "ms-settings:startupapps", "startup apps 启动"),
        new("账户", "ms-settings:accounts", "accounts 账户"),
        new("你的信息", "ms-settings:yourinfo", "your info profile 信息"),
        new("电子邮件和账户", "ms-settings:emailandaccounts", "email accounts 邮件"),
        new("登录选项", "ms-settings:signinoptions", "sign in password pin 登录"),
        new("家庭和其他用户", "ms-settings:otherusers", "family users 家庭 用户"),
        new("同步你的设置", "ms-settings:sync", "sync 同步"),
        new("日期和时间", "ms-settings:dateandtime", "date time clock 日期 时间"),
        new("区域", "ms-settings:region", "region format 区域"),
        new("语言", "ms-settings:regionlanguage", "language 语言"),
        new("语音", "ms-settings:speech", "speech 语音"),
        new("游戏", "ms-settings:gaming", "game 游戏"),
        new("Xbox 网络", "ms-settings:gaming-gamebar", "xbox game bar"),
        new("游戏模式", "ms-settings:gaming-gamemode", "game mode 游戏模式"),
        new("辅助功能", "ms-settings:easeofaccess", "accessibility ease 辅助 轻松"),
        new("辅助功能-显示", "ms-settings:easeofaccess-display", "accessibility display 显示"),
        new("辅助功能-鼠标指针", "ms-settings:easeofaccess-mousepointer", "mouse pointer accessibility 指针"),
        new("辅助功能-文本大小", "ms-settings:easeofaccess-textsize", "text size 文本"),
        new("辅助功能-高对比度", "ms-settings:easeofaccess-highcontrast", "high contrast 对比度"),
        new("辅助功能-键盘", "ms-settings:easeofaccess-keyboard", "keyboard 键盘"),
        new("辅助功能-讲述人", "ms-settings:easeofaccess-narrator", "narrator 讲述人"),
        new("搜索", "ms-settings:search", "search 搜索"),
        new("搜索权限", "ms-settings:search-permissions", "search permissions 权限"),
        new("隐私", "ms-settings:privacy", "privacy 隐私"),
        new("隐私-位置", "ms-settings:privacy-location", "location 位置"),
        new("隐私-相机", "ms-settings:privacy-webcam", "camera 相机"),
        new("隐私-麦克风", "ms-settings:privacy-microphone", "microphone 麦克风"),
        new("隐私-后台应用", "ms-settings:privacy-backgroundapps", "background apps 后台"),
        new("隐私-活动历史", "ms-settings:privacy-activityhistory", "activity history 活动"),
        new("隐私-诊断和反馈", "ms-settings:privacy-diagnostics", "diagnostics feedback 诊断 反馈"),
        new("Windows 更新", "ms-settings:windowsupdate", "update windows 更新"),
        new("传递优化", "ms-settings:delivery-optimization", "delivery optimization 传递"),
        new("激活", "ms-settings:activation", "activate 激活"),
        new("备份", "ms-settings:backup", "backup 备份"),
        new("疑难解答", "ms-settings:troubleshoot", "troubleshoot 疑难"),
        new("恢复", "ms-settings:recovery", "recovery reset 恢复"),
        new("开发人员选项", "ms-settings:developers", "developer 开发"),
        new("Windows 预览体验计划", "ms-settings:windowsinsider", "insider preview 预览体验")
    };

    public IReadOnlyList<SearchResult> Search(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<SearchResult>();
        }

        var results = new List<SearchResult>();
        foreach (var entry in Entries)
        {
            ct.ThrowIfCancellationRequested();
            var score = ProgramSearchProvider.Score(query, entry.Title + " " + entry.Keywords);
            if (score <= 0)
            {
                continue;
            }

            results.Add(new SearchResult
            {
                Title = entry.Title,
                Subtitle = "设置",
                Category = "Settings",
                LaunchPath = entry.Uri,
                IconPath = entry.Uri,
                Score = score + 1,
                Execute = () =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(entry.Uri) { UseShellExecute = true });
                    }
                    catch
                    {
                        // 打开设置页失败静默（M10）。
                    }
                }
            });
        }

        return results
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
    }

    private sealed record SettingsEntry(string Title, string Uri, string Keywords);
}
