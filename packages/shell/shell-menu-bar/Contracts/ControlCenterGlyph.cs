// BetterDesktop.Shell.MenuBar — 控制中心图标库（问题 6：布局升级 + 功能对接）
//
// 【为什么要重写】控制中心此前所有图标都写死 Segoe MDL2 Assets 字体码位：
//   Wi‑Fi \uE701、蓝牙 \uE702、专注助手 \uE7C2、台前调度 \uE81E、热点 \uEB0B、投影 \uE7B4、
//   麦克风 \uE720、音乐 \uEC4F、上一首/播放/下一首 \uE100/\uE102/\uE101
// 其中只有极少数能确认对得上，其余全是凭印象填的，实际渲染出来的字形与语义不符；
// 字体缺失时更是直接显示方块。这与问题 2（Wi‑Fi 图标）、问题 5（电源方案图标）是同一个病根。
//
// 【本实现】24×24 栅格纯自绘，与 WifiGlyph / PowerGlyph 同一套规范；
// Wi‑Fi 直接复用 WifiGlyph，保证菜单栏、NETWORK 面板、控制中心三处完全一致。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BetterDesktop.Shell.MenuBar.Contracts;

/// <summary>控制中心用到的图标（与 ControlCenterFeatureCatalog 的声明顺序一一对应）。</summary>
internal enum ControlCenterIcon
{
    Wifi,
    Bluetooth,
    Hotspot,
    Focus,
    Project,
    Power,
    Display,
    Volume,
    Microphone,
    Music
}

/// <summary>控制中心图标（纯自绘，24×24 栅格）。</summary>
internal static class ControlCenterGlyph
{
    public const double GridSize = 24;

    private const double StrokeWidth = 1.8;

    /// <summary>构造图标画布（24×24），由调用方用 Viewbox 缩放到目标尺寸。</summary>
    public static Canvas Create(ControlCenterIcon icon, Brush brush)
    {
        var canvas = new Canvas { Width = GridSize, Height = GridSize };
        switch (icon)
        {
            case ControlCenterIcon.Bluetooth: BuildBluetooth(canvas, brush); break;
            case ControlCenterIcon.Hotspot: BuildHotspot(canvas, brush); break;
            case ControlCenterIcon.Focus: BuildMoon(canvas, brush); break;
            case ControlCenterIcon.Project: BuildProject(canvas, brush); break;
            case ControlCenterIcon.Power: BuildBattery(canvas, brush); break;
            case ControlCenterIcon.Display: BuildDisplay(canvas, brush); break;
            case ControlCenterIcon.Volume: BuildSpeaker(canvas, brush); break;
            case ControlCenterIcon.Microphone: BuildMicrophone(canvas, brush); break;
            case ControlCenterIcon.Music: BuildMusic(canvas, brush); break;
            default:
                // Wi‑Fi：复用同一份扇形实现（满格），与菜单栏状态条/NETWORK 面板保持完全一致
                WifiGlyph.BuildFan(brush, 3, out var fan, out _, out _);
                fan.Width = GridSize; fan.Height = GridSize;
                canvas.Children.Add(fan);
                break;
        }
        return canvas;
    }

    /// <summary>蓝牙：Material Design 的蓝牙字形（24 栅格，填充）。</summary>
    private static void BuildBluetooth(Canvas canvas, Brush brush)
    {
        // 主轮廓
        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse("M17.71 7.71L12 2h-1v7.59L6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 11 14.41V22h1l5.71-5.71-4.3-4.29 4.3-4.29z"),
            Fill = brush
        });
        // 中间两个小三角（字形缺口，补全后才是完整蓝牙标志）
        canvas.Children.Add(new Path { Data = Geometry.Parse("M13 5.83l1.88 1.88L13 9.59V5.83z"), Fill = brush });
        canvas.Children.Add(new Path { Data = Geometry.Parse("M13 18.17v-3.76l1.88 1.88z"), Fill = brush });
    }

    /// <summary>热点：中心圆点 + 左右各两道广播弧（((•)) 造型）。</summary>
    private static void BuildHotspot(Canvas canvas, Brush brush)
    {
        var dot = new Ellipse { Width = 4, Height = 4, Fill = brush };
        Canvas.SetLeft(dot, 10); Canvas.SetTop(dot, 10);
        canvas.Children.Add(dot);

        // 左两道（sweep=0 → 向左凸）
        canvas.Children.Add(Arc("M 7.76 7.76 A 6 6 0 0 0 7.76 16.24", brush));
        canvas.Children.Add(Arc("M 4.93 4.93 A 10 10 0 0 0 4.93 19.07", brush));
        // 右两道（sweep=1 → 向右凸）
        canvas.Children.Add(Arc("M 16.24 7.76 A 6 6 0 0 1 16.24 16.24", brush));
        canvas.Children.Add(Arc("M 19.07 4.93 A 10 10 0 0 1 19.07 19.07", brush));
    }

    /// <summary>专注助手 = 弯月（免打扰）。</summary>
    private static void BuildMoon(Canvas canvas, Brush brush)
    {
        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse("M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z"),
            Fill = brush
        });
    }

    /// <summary>台前调度 = 两个叠放的窗口。</summary>
    private static void BuildWindows(Canvas canvas, Brush brush)
    {
        canvas.Children.Add(RoundedRect(2.5, 4, 12, 11, 2, brush));
        canvas.Children.Add(RoundedRect(9.5, 9, 12, 11, 2, brush));
    }

    /// <summary>投影 = 屏幕 + 底座 + 向右发出的两道投射弧。</summary>
    private static void BuildProject(Canvas canvas, Brush brush)
    {
        canvas.Children.Add(RoundedRect(2.5, 4, 12.5, 10, 1.6, brush));
        canvas.Children.Add(Line(8.75, 14, 8.75, 17.5, brush));
        canvas.Children.Add(Line(5.5, 17.5, 12, 17.5, brush));
        canvas.Children.Add(Arc("M 17.5 7.5 A 4.5 4.5 0 0 1 17.5 14.5", brush));
        canvas.Children.Add(Arc("M 20.5 5 A 8.5 8.5 0 0 1 20.5 17", brush));
    }

    /// <summary>电源 = 电池（外壳 + 正极凸起 + 内部两格电量）。</summary>
    private static void BuildBattery(Canvas canvas, Brush brush)
    {
        // 正极凸起
        var cap = new Rectangle { Width = 2.2, Height = 4.4, RadiusX = 0.6, RadiusY = 0.6, Fill = brush };
        Canvas.SetLeft(cap, 21.2); Canvas.SetTop(cap, 9.8);
        canvas.Children.Add(cap);
        // 外壳
        canvas.Children.Add(RoundedRect(2, 7, 19, 10, 2, brush));
        // 电量格
        foreach (var x in new[] { 5.5, 10.0, 14.5 })
        {
            var cell = new Rectangle { Width = 2.6, Height = 5.4, RadiusX = 0.5, RadiusY = 0.5, Fill = brush };
            Canvas.SetLeft(cell, x); Canvas.SetTop(cell, 9.3);
            canvas.Children.Add(cell);
        }
    }

    /// <summary>屏幕（显示器）：屏幕框 + 底座。</summary>
    private static void BuildDisplay(Canvas canvas, Brush brush)
    {
        canvas.Children.Add(RoundedRect(2.5, 4, 19, 12, 2, brush));
        canvas.Children.Add(Line(12, 16, 12, 19, brush));
        canvas.Children.Add(Line(7.5, 19, 16.5, 19, brush));
    }

    /// <summary>扬声器：喇叭梯形 + 两道声波。</summary>
    private static void BuildSpeaker(Canvas canvas, Brush brush)
    {
        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse("M 4 9.5 H 7.5 L 12.5 5.5 V 18.5 L 7.5 14.5 H 4 Z"),
            Fill = brush
        });
        canvas.Children.Add(Arc("M 15.5 9 A 4.5 4.5 0 0 1 15.5 15", brush));
        canvas.Children.Add(Arc("M 18.2 6.6 A 8 8 0 0 1 18.2 17.4", brush));
    }

    /// <summary>麦克风：胶囊话筒 + U 形支架 + 立柱底座。</summary>
    private static void BuildMicrophone(Canvas canvas, Brush brush)
    {
        canvas.Children.Add(RoundedRect(9, 3, 6, 10.5, 3, brush));
        // U 形支架：sweep=0 走下方，正好兜住话筒
        canvas.Children.Add(Arc("M 6.5 11 A 5.5 5.5 0 0 0 17.5 11", brush));
        canvas.Children.Add(Line(12, 16.5, 12, 20.5, brush));
        canvas.Children.Add(Line(8.5, 20.5, 15.5, 20.5, brush));
    }

    /// <summary>音乐：八分音符（符头 + 符干 + 符尾）。</summary>
    private static void BuildMusic(Canvas canvas, Brush brush)
    {
        var head = new Ellipse { Width = 6.2, Height = 5, Fill = brush };
        Canvas.SetLeft(head, 5.4); Canvas.SetTop(head, 14.4);
        canvas.Children.Add(head);
        canvas.Children.Add(Line(11, 16, 11, 4.5, brush));
        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse("M 11 4.5 C 14.6 5.2 17.4 7 17.4 9.8 L 15.2 9.2 C 15.2 7.4 13.6 6.3 11 6 Z"),
            Fill = brush
        });
    }

    // ==================== 媒体播放控制（独立尺寸，不走 24 栅格语义） ====================

    /// <summary>播放三角几何。控制中心复用同一个 Path 实例，切状态时只改 Data（不重建按钮）。</summary>
    public static Geometry PlayGeometry => Geometry.Parse("M 7 4.5 L 18.5 12 L 7 19.5 Z");

    /// <summary>暂停双竖杠几何。同上，与 <see cref="PlayGeometry"/> 成对切换。</summary>
    public static Geometry PauseGeometry => Geometry.Parse("M 6.5 4.5 H 10 V 19.5 H 6.5 Z M 14 4.5 H 17.5 V 19.5 H 14 Z");

    /// <summary>上一首：竖条 + 左向三角。</summary>
    public static Path CreatePrev(Brush brush) => new()
    {
        Data = Geometry.Parse("M 5 5 H 7 V 19 H 5 Z M 17 5 V 19 L 8.5 12 Z"),
        Fill = brush
    };

    /// <summary>下一首：右向三角 + 竖条。</summary>
    public static Path CreateNext(Brush brush) => new()
    {
        Data = Geometry.Parse("M 17 5 H 19 V 19 H 17 Z M 7 5 V 19 L 15.5 12 Z"),
        Fill = brush
    };

    /// <summary>播放：右向三角。</summary>
    public static Path CreatePlay(Brush brush) => new()
    {
        Data = PlayGeometry,
        Fill = brush
    };

    /// <summary>暂停：两条竖杠。</summary>
    public static Path CreatePause(Brush brush) => new()
    {
        Data = PauseGeometry,
        Fill = brush
    };

    // ==================== 内部工具 ====================

    private static Path Arc(string data, Brush brush) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = brush,
        StrokeThickness = StrokeWidth,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round
    };

    private static Line Line(double x1, double y1, double x2, double y2, Brush brush) => new()
    {
        X1 = x1,
        Y1 = y1,
        X2 = x2,
        Y2 = y2,
        Stroke = brush,
        StrokeThickness = StrokeWidth,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round
    };

    /// <summary>圆角矩形（描边，左上角坐标 + 宽高）。</summary>
    private static Rectangle RoundedRect(double left, double top, double width, double height, double radius, Brush brush)
    {
        var r = new Rectangle
        {
            Width = width,
            Height = height,
            RadiusX = radius,
            RadiusY = radius,
            Stroke = brush,
            StrokeThickness = StrokeWidth
        };
        Canvas.SetLeft(r, left);
        Canvas.SetTop(r, top);
        return r;
    }
}
