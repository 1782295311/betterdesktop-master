using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Core.Surface;

namespace BetterDesktop.Clipboard.Panel;

/// <summary>
/// 侧边栏收纳手柄（默认且唯一入口）：常驻屏幕右缘的细窄胶囊条，显示「›」收纳形态，
/// **点击**滑出完整面板（贴右缘，<see cref="PanelApp.ShowMainWindowRight"/>）。
///
/// 交互（2026-09-12 用户实测后重定）：
///   - **点击展开** —— 原实现 hover 即展开，鼠标扫过屏幕右缘就弹面板（用户："太大也太敏感"），现已改为点击；
///   - **拖动移动** —— 按住可上下拖动，松手后位置持久化；水平锁右缘（保持"侧边栏"语义），
///     垂直夹在当前工作区内（换分辨率/显示器不会跑出屏幕）；
///   - hover 只做**视觉反馈**（箭头提亮），不再触发任何动作。
///
/// 【统一窗口基类】继承 <see cref="ShellWindow"/>：无边框 / 置顶 / 不抢焦点 / 不进任务栏 / 材质 /
/// 主题传导全部由基类给，本类只描述内容、尺寸与交互策略。
/// </summary>
public sealed class EdgeHandleWindow : ShellWindow
{
    private const double HandleWidth = 14;

    /// <summary>高度（原 180 → 120）：原尺寸视觉占位偏大（用户反馈"侧边栏太大"）。</summary>
    private const double HandleHeight = 120;

    /// <summary>判定"拖动"的最小位移（像素）—— 小于它视为单击。</summary>
    private const double DragThreshold = 3;

    private readonly Border _chrome;
    private readonly TextBlock _glyph;

    // 拖动状态（自实现而非 WPF DragMove：DragMove 会吞掉后续 MouseLeftButtonUp，
    // 且无法锁定水平方向 —— 这正是悬浮球时代遗留未解的问题，见下方 MouseMove 注释）
    private bool _dragging;
    private bool _moved;
    private double _grabOffsetY;
    private double _dragStartTop;

    /// <summary>尺寸由设计决定（贴边细窄条），禁止拉伸。</summary>
    protected override ResizeMode DefaultResizeMode => ResizeMode.NoResize;

    /// <summary>
    /// 常驻浮窗：不抢焦点、不进任务栏 —— 基类据此补 WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW。
    /// 必要性（2026-09-12 真机）：侧边栏曾被 dock 的「运行中应用」当成普通应用窗口，
    /// 而 WPF 的 ShowInTaskbar=false **不足以保证** TOOLWINDOW 扩展样式，必须显式设置。
    /// </summary>
    protected override bool UseNoActivateWindowStyle => true;

    public EdgeHandleWindow()
    {
        Title = "剪贴板侧边栏";
        Width = HandleWidth;
        Height = HandleHeight;

        _chrome = new Border
        {
            CornerRadius = new CornerRadius(7, 0, 0, 7),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Cursor = Cursors.Hand,
            SnapsToDevicePixels = true,
            Margin = new Thickness(0)
        };
        _chrome.SetResourceReference(Border.BackgroundProperty, "ThemePanelBackground");
        _chrome.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");

        // 【显眼】原 muted 色 + 14px 太淡（用户："这个标志需要更加显眼"）→ 强调色 + 22px + 加粗。
        // AccentBrush 由 PanelTheme.ApplyToAppResources 写入 App.Resources（与宿主外观设置同源）。
        _glyph = new TextBlock
        {
            Text = "›",
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Opacity = 0.85,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _glyph.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
        _chrome.Child = _glyph;
        Content = _chrome;

        // 交给基类做统一外观（描边/圆角/阴影/字号随主题刷新）。
        // 【必须】ShellWindow 在 DEBUG 下对未挂 ChromeBorder 的窗口 FailFast —— 2026-09-12
        // 本窗从裸 Window 迁到统一基类时正是被这条纪律拦下，属"接对了才有资格被统一管理"。
        ChromeBorder = _chrome;

        // hover 仅视觉反馈（提亮），**不再触发展开**
        _chrome.MouseEnter += (_, _) => _glyph.Opacity = 1.0;
        _chrome.MouseLeave += (_, _) =>
        {
            if (!_dragging)
            {
                _glyph.Opacity = 0.85;
            }
        };

        _chrome.MouseLeftButtonDown += OnHandleMouseDown;
        _chrome.MouseMove += OnHandleMouseMove;
        _chrome.MouseLeftButtonUp += OnHandleMouseUp;
        // 【不要订阅 LostMouseCapture 来清 _dragging】踩过的坑：鼠标捕获若在按下时即丢失，
        // 该回调会立刻把 _dragging 清成 false，随后 MouseUp 走进 "if (!_dragging) return;"
        // 直接返回 —— **点击语义被吃掉**（真机症状：点手柄不展开面板，且 popup-trace 无任何记录）。
        // 位置被写坏的问题由 OnHandleMouseMove 内的"左键已松开则自愈"单独承担，足够且可预测。
    }

    /// <summary>结束拖动态（不触发任何动作）。幂等。</summary>
    private void EndDrag()
    {
        _dragging = false;
        _moved = false;
        _glyph.Opacity = 0.85;
    }

    /// <summary>
    /// 点手柄那一刻的前台窗口（= 用户真正在用的窗口）。面板收起时据此**把前台还回去**。
    /// <para>
    /// 记录点定在 MouseDown：手柄是 `WS_EX_NOACTIVATE`、点击不改变前台，所以此刻拿到的
    /// 就是用户窗口。不记这个的话，面板 `Hide()` 会把焦点交给同进程的手柄，
    /// 之后按序粘贴的 Ctrl+V 打在自己身上、第一条丢失（2026-09-12 真机）。
    /// </para>
    /// </summary>
    internal static IntPtr ForegroundBeforeOpen;

    private void OnHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        ForegroundBeforeOpen = NativeMethods.GetForegroundWindow();
        _dragging = true;
        _moved = false;
        _dragStartTop = Top;
        _grabOffsetY = e.GetPosition(this).Y; // 抓取点相对窗口顶部的偏移（拖动时保持该偏移，手感自然）
        _chrome.CaptureMouse();
    }

    private void OnHandleMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        // 【自愈 · 2026-09-12 真机】左键若已松开却仍处于拖动态（快速点击时 MouseUp 未被收到 /
        // 捕获丢失），必须在此复位 —— 否则"点击后把鼠标移开"会被当成拖动，把窗口挪到鼠标处并
        // 持久化。真机实测症状：手柄位置被莫名改成 handleTop=281.6（用户并未拖动）。
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _chrome.ReleaseMouseCapture();
            EndDrag();
            return;
        }

        // 不用 PointToScreen：它返回**物理像素**，而 Top/Left 是 DIP，缩放屏（125%/150%）下会漂移。
        // 窗口内坐标 + 当前 Top 即为屏幕 DIP 坐标，两者单位一致。
        var cursorY = Top + e.GetPosition(this).Y;
        var targetTop = cursorY - _grabOffsetY;

        var wa = SystemParameters.WorkArea;
        Top = Math.Max(wa.Top, Math.Min(targetTop, wa.Bottom - Height)); // 垂直自由，夹在工作区内
        Left = wa.Right - HandleWidth + 1;                               // 水平锁右缘（侧边栏语义）

        if (Math.Abs(Top - _dragStartTop) > DragThreshold)
        {
            _moved = true;
        }
    }

    private void OnHandleMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        // 先取判定结果：ReleaseMouseCapture 会同步触发 LostMouseCapture → EndDrag → 清 _moved。
        var moved = _moved;
        _chrome.ReleaseMouseCapture();
        EndDrag();

        if (moved)
        {
            SaveTop(Top); // 拖动结束 → 持久化新位置
        }
        else
        {
            OpenPanel(); // 未移动 = 单击 → 展开面板
        }
    }

    private static void OpenPanel()
    {
        PanelApp.ShowMainWindowRight();
    }

    public void ShowHandle()
    {
        if (!IsVisible)
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - HandleWidth + 1; // 贴右缘（右缘外 1px 让圆角完整显示）
            // 位置优先取上次拖动保存值；缺省 = 右缘垂直居中。
            // 无论来源，一律夹紧到当前工作区 —— 否则换分辨率/拔掉外接屏后手柄会留在屏幕外，
            // 表现为"入口凭空消失"。
            var top = LoadSavedTop() ?? (wa.Top + (wa.Height - HandleHeight) / 2);
            Top = Math.Max(wa.Top, Math.Min(top, wa.Bottom - Height));
        }
        Show();
    }

    // ---- 位置持久化 ----
    // 刻意写**面板私有状态文件**而非宿主 settings.json：后者由宿主进程频繁读写，
    // 双写方必然互相覆盖（宿主写主题、面板写位置 → 先写的一方丢失）。

    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BetterDesktop",
        "clipboard-panel-state.json");

    private static double? LoadSavedTop()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return null;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
            return doc.RootElement.TryGetProperty("handleTop", out var v) && v.TryGetDouble(out var top)
                ? top
                : null;
        }
        catch (Exception e)
        {
            PanelLog.Trace($"读取面板状态失败（忽略，用默认位置）: {e.Message}");
            return null;
        }
    }

    private static void SaveTop(double top)
    {
        try
        {
            var dir = Path.GetDirectoryName(StatePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new { handleTop = Math.Round(top, 1) }));
        }
        catch (Exception e)
        {
            // 存不下只是下次回到默认位置，不影响使用
            PanelLog.Trace($"保存手柄位置失败（不影响使用）: {e.Message}");
        }
    }
}
