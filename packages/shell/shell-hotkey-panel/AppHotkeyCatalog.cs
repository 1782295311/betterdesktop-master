using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterDesktop.Shell.HotkeyPanel;

/// <summary>某个应用内的一条功能键（键位是**原始写法**，可以是裸键 / 多键并列 / 鼠标操作）。</summary>
internal sealed record AppHotkey(string Chord, string Description);

/// <summary>应用内热键分组（按功能域，如"变换""视图""导航"）。</summary>
internal sealed record AppHotkeyGroup(string Title, IReadOnlyList<AppHotkey> Items);

/// <summary>一个应用的场景热键档案（进程名 → 分组热键）。</summary>
internal sealed record AppHotkeyProfile(string App, IReadOnlyList<string> ProcessNames, IReadOnlyList<AppHotkeyGroup> Groups)
{
    public int Count => Groups.Sum(g => g.Items.Count);
}

/// <summary>
/// **应用场景热键目录**：按前台应用显示"这个软件此刻能按什么键"（用户需求：
/// "打开 Blender 后显示的功能按键，这些需要优先显示"）。
/// <para>
/// 【为什么不进热键注册表】这些是**应用内**键位（Blender 的 <c>G</c>/<c>R</c>/<c>S</c>/<c>Tab</c> 多为裸键），
/// 既不由 <c>RegisterHotKey</c> 注册、也不参与全局冲突，更不该被我们的"必须含修饰键"模型卡住；
/// 它们是**只读参考**，所以单独一条目录通道，侧板按场景置顶展示。
/// </para>
/// <para>【诚实边界】键位以**默认设置**为准，用户改过或版本更新后可能不同；且各应用键位极多，
/// 这里只收录高频核心项（按功能域分组），不是全量手册。</para>
/// </summary>
internal static class AppHotkeyCatalog
{
    public static IReadOnlyList<AppHotkeyProfile> All { get; } = new List<AppHotkeyProfile>
    {
        new("Blender", new[] { "blender" },
            new[]
            {
                new AppHotkeyGroup("变换", new[]
                {
                    new AppHotkey("G", "移动"),
                    new AppHotkey("R", "旋转"),
                    new AppHotkey("S", "缩放"),
                    new AppHotkey("Shift+D", "复制（副本跟随鼠标）"),
                    new AppHotkey("Alt+G / Alt+R / Alt+S", "重置 位置 / 旋转 / 缩放"),
                    new AppHotkey("Ctrl+A", "应用变换"),
                }),
                new AppHotkeyGroup("模式与建模", new[]
                {
                    new AppHotkey("Tab", "切换 物体 / 编辑 模式"),
                    new AppHotkey("E", "挤出（编辑模式）"),
                    new AppHotkey("I", "内插面"),
                    new AppHotkey("Ctrl+R", "环切（Loop Cut）"),
                    new AppHotkey("K", "切割（Knife）"),
                    new AppHotkey("M", "合并 / 移动到集合"),
                    new AppHotkey("P", "分离（Separate）"),
                    new AppHotkey("Ctrl+J", "合并物体（Join）"),
                    new AppHotkey("X", "删除（弹菜单）"),
                    new AppHotkey("Shift+A", "添加物体 / 节点"),
                }),
                new AppHotkeyGroup("选择", new[]
                {
                    new AppHotkey("A / Alt+A", "全选 / 取消全选"),
                    new AppHotkey("Ctrl+I", "反选"),
                    new AppHotkey("B / C", "框选 / 刷选"),
                    new AppHotkey("L", "选择相连元素"),
                }),
                new AppHotkeyGroup("视图", new[]
                {
                    new AppHotkey("数字键盘 1 / 3 / 7", "前 / 右 / 顶 视图"),
                    new AppHotkey("数字键盘 5", "正交 / 透视切换"),
                    new AppHotkey("数字键盘 .", "聚焦选中物体"),
                    new AppHotkey("Home", "全览视图"),
                    new AppHotkey("Z", "着色方式轮盘"),
                    new AppHotkey("Shift+Z", "线框 / 实体切换"),
                    new AppHotkey("Ctrl+Space", "最大化当前视图"),
                    new AppHotkey("T / N", "左侧工具栏 / 右侧属性栏"),
                    new AppHotkey("Shift+C", "重置视图"),
                }),
                new AppHotkeyGroup("动画与播放", new[]
                {
                    new AppHotkey("Space", "播放 / 暂停"),
                    new AppHotkey("← / →", "上一帧 / 下一帧"),
                    new AppHotkey("Shift+← / Shift+→", "跳首帧 / 末帧"),
                    new AppHotkey("F9", "调整上一步操作"),
                }),
            }),

        new("Visual Studio Code", new[] { "Code" },
            new[]
            {
                new AppHotkeyGroup("导航", new[]
                {
                    new AppHotkey("Ctrl+P", "快速打开文件"),
                    new AppHotkey("Ctrl+Shift+P", "命令面板"),
                    new AppHotkey("Ctrl+Shift+O", "转到符号"),
                    new AppHotkey("Ctrl+G", "跳转到行"),
                    new AppHotkey("F12", "转到定义"),
                    new AppHotkey("Alt+← / Alt+→", "后退 / 前进"),
                }),
                new AppHotkeyGroup("编辑", new[]
                {
                    new AppHotkey("Alt+↑ / Alt+↓", "上下移动当前行"),
                    new AppHotkey("Shift+Alt+↑ / ↓", "复制当前行"),
                    new AppHotkey("Ctrl+/", "行注释"),
                    new AppHotkey("Ctrl+D", "选中下一个相同项"),
                    new AppHotkey("Ctrl+Shift+L", "选中所有相同项"),
                    new AppHotkey("Ctrl+Shift+K", "删除整行"),
                    new AppHotkey("F2", "重命名符号"),
                    new AppHotkey("Alt+Click", "多光标"),
                }),
                new AppHotkeyGroup("视图与面板", new[]
                {
                    new AppHotkey("Ctrl+B", "显示 / 隐藏侧边栏"),
                    new AppHotkey("Ctrl+`", "打开 / 关闭终端"),
                    new AppHotkey("Ctrl+Shift+E", "资源管理器"),
                    new AppHotkey("Ctrl+Shift+F", "全局搜索"),
                    new AppHotkey("Ctrl+Shift+G", "源代码管理"),
                    new AppHotkey("Ctrl+\\", "拆分编辑器"),
                    new AppHotkey("F5 / Shift+F5", "开始 / 停止调试"),
                }),
            }),
    };

    /// <summary>按前台进程名匹配档案（大小写不敏感；未收录返回 null）。</summary>
    public static AppHotkeyProfile? Match(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        foreach (var profile in All)
        {
            foreach (var name in profile.ProcessNames)
            {
                if (string.Equals(name, processName, StringComparison.OrdinalIgnoreCase))
                {
                    return profile;
                }
            }
        }
        return null;
    }
}
