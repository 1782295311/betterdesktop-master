using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BetterDesktop.Shell.Clipboard.Ipc;

namespace BetterDesktop.Shell.HotkeyPanel;

/// <summary>某款第三方软件的一条已知全局热键（默认键位 + 功能）。</summary>
internal sealed record ThirdPartyHotkeyBinding(string Chord, string Description);

/// <summary>一款第三方软件及其已知全局热键 + 进程探活名 + 改键入口指引。</summary>
/// <param name="SettingsPath">在该软件里改热键的路径（中文界面习惯叫法），用于直接告诉用户"去哪改"。</param>
internal sealed record ThirdPartyHotkeyApp(
    string App,
    IReadOnlyList<string> ProcessNames,
    IReadOnlyList<ThirdPartyHotkeyBinding> Bindings,
    IReadOnlyList<string> Notes,
    string? SettingsPath = null);

/// <summary>
/// 常见第三方软件的**已知全局热键**知识库（进程活性标注 + 键位反查）。
/// <para>
/// 【为什么需要它】<see cref="HotkeyScanner"/> 只能发现"某个键被占用了"，**无法知道是谁占的**
/// （Windows 不公开 RegisterHotKey 的占用者，这是硬边界）。本库把"键位 → 软件 + 功能"钉成索引：
/// 扫描结果命中即给出**疑似来源与功能**，命中软件正在运行时提高可信度；未命中则如实标注"归属未知"。
/// </para>
/// <para>
/// 【诚实边界】键位是**各家默认值**，用户可能在软件内改过；版本更新也可能变化；另外大量软件用
/// <c>WH_KEYBOARD_LL</c> 钩子实现热键，系统层探测不到，只能靠本库兜底。UI 必须写明"疑似/参考"，
/// 不得当作事实断言。</para>
/// </summary>
internal static class ThirdPartyHotkeyCatalog
{
    /// <summary>应用清单（UI 可把运行中的排前面）。</summary>
    public static IReadOnlyList<ThirdPartyHotkeyApp> All { get; } = new List<ThirdPartyHotkeyApp>
    {
        new("微信", new[] { "WeChat", "Weixin" },
            new[]
            {
                new ThirdPartyHotkeyBinding("Alt+A", "截图（默认）"),
                new ThirdPartyHotkeyBinding("Ctrl+Alt+W", "唤起微信（默认）"),
            },
            Array.Empty<string>(),
            "微信 → 左下角「≡」→ 设置 → 快捷键"),

        new("QQ", new[] { "QQ", "QQScLauncher", "QQEX" },
            new[]
            {
                new ThirdPartyHotkeyBinding("Ctrl+Alt+A", "截图（默认）"),
                new ThirdPartyHotkeyBinding("Ctrl+Alt+Z", "提取消息（默认）"),
            },
            Array.Empty<string>(),
            "QQ → 主菜单 → 设置 → 热键"),

        new("钉钉", new[] { "DingTalk" },
            new[] { new ThirdPartyHotkeyBinding("Ctrl+Shift+D", "唤起钉钉（默认）") },
            Array.Empty<string>(),
            "钉钉 → 头像 → 设置 → 快捷键"),

        new("NVIDIA 录制（ShadowPlay）", new[] { "NVIDIA Overlay", "NVIDIA Share", "NVIDIA GeForce Experience" },
            new[]
            {
                new ThirdPartyHotkeyBinding("Alt+Z", "打开 / 关闭 NVIDIA 面板（默认）"),
                new ThirdPartyHotkeyBinding("Alt+F9", "手动开始 / 停止录制（默认）"),
                new ThirdPartyHotkeyBinding("Alt+F10", "开始 / 停止广播（默认）"),
            },
            Array.Empty<string>(),
            "按 Alt+Z 呼出面板 → 齿轮「设置」→「键盘快捷键」可改；官方无配置文件可直改"),

        new("Snipaste", new[] { "Snipaste" },
            new[]
            {
                new ThirdPartyHotkeyBinding("F1", "截图（默认，裸键）"),
                new ThirdPartyHotkeyBinding("F3", "贴图（默认，裸键）"),
            },
            Array.Empty<string>(),
            "托盘图标右键 → 首选项 → 控制（配置存 config.ini，明文可改）"),

        new("PowerToys", new[] { "PowerToys.PowerLauncher", "PowerToys" },
            new[]
            {
                new ThirdPartyHotkeyBinding("Alt+Space", "运行 PowerToys Run（默认）"),
                new ThirdPartyHotkeyBinding("Win+Shift+T", "唤醒保持（可改）"),
            },
            Array.Empty<string>(),
            "PowerToys 设置 → PowerToys Run → 激活快捷键（配置为明文 json）"),

        new("输入法（搜狗 / 微软拼音 / 微信输入法）", new[] { "SogouImeBroker", "SGTool", "ChsIME", "WeChatInput" },
            new[]
            {
                new ThirdPartyHotkeyBinding("Ctrl+Shift", "切换输入法（系统级）"),
                new ThirdPartyHotkeyBinding("Win+Space", "切换输入法 / 键盘布局（系统级）"),
            },
            new[] { "皮肤 / 符号面板 / 快捷短语等由输入法自定义（钩子实现，系统层无法探测）" },
            "输入法状态栏 → 属性设置 → 按键"),

        new("向日葵远程控制", new[] { "SunloginClient", "SunloginClientService" },
            Array.Empty<ThirdPartyHotkeyBinding>(),
            new[] { "远控类软件的全局热键由使用者自定义（官方未提供默认值，需在其设置中查看）" },
            "向日葵 → 设置 → 快捷键"),

        new("360 安全卫士", new[] { "360tray", "360safe", "ZhuDongFangYu" },
            Array.Empty<ThirdPartyHotkeyBinding>(),
            new[] { "安全类软件的热键因版本而异，需在其设置中查看" },
            "360 安全卫士 → 设置 → 快捷键"),

        new("豆包 / AI 助手类", new[] { "Doubao", "DoubaoDesktop" },
            Array.Empty<ThirdPartyHotkeyBinding>(),
            new[] { "AI 助手类软件常注册「唤起面板 / 截图问答」类全局热键，具体键位以其设置为准" },
            "豆包 → 设置 → 快捷键"),
    };

    /// <summary>应用是否正在运行（进程探活；探活失败按"运行中"处理，不误报为未运行）。</summary>
    public static bool IsRunning(ThirdPartyHotkeyApp app)
    {
        foreach (var name in app.ProcessNames)
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                int count = procs.Length;
                foreach (var p in procs)
                {
                    p.Dispose();
                }
                if (count > 0)
                {
                    return true;
                }
            }
            catch (Exception)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>运行中的应用（UI 首选展示），保持清单原顺序。</summary>
    public static IReadOnlyList<ThirdPartyHotkeyApp> Running()
        => All.Where(IsRunning).ToList();

    /// <summary>
    /// 按键位反查归属（canonical 归一；命中返回软件 + 功能说明，未命中返回 null）。
    /// <para>用于把扫描出的"某个键被占了"变成"疑似谁占的、干什么用"——这是本库存在的主要理由。</para>
    /// </summary>
    public static (ThirdPartyHotkeyApp App, ThirdPartyHotkeyBinding Binding)? MatchByChord(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return null;
        }

        var canonical = Canonical(spec);
        foreach (var app in All)
        {
            foreach (var binding in app.Bindings)
            {
                var bindingCanonical = Canonical(binding.Chord);
                if (canonical.Length > 0 && bindingCanonical.Length > 0)
                {
                    if (string.Equals(bindingCanonical, canonical, StringComparison.OrdinalIgnoreCase))
                    {
                        return (app, binding);
                    }
                }
                else if (string.Equals(binding.Chord, spec, StringComparison.OrdinalIgnoreCase))
                {
                    return (app, binding); // 裸键（F1/F3）走原样比较
                }
            }
        }
        return null;
    }

    private static string Canonical(string spec)
        => HotkeySpec.TryParse(spec, out _, out _, out var canonical) ? canonical : string.Empty;
}
