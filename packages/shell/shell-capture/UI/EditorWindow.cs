using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BetterDesktop.Capture.Contracts;
using BetterDesktop.Shell.Capture.Core;
using BetterDesktop.Shell.Core.Surface;

// CA2000（局部禁用）：UndoItem 托管位图快照的释放由 Push(入栈淘汰)/OnClosed(收尾) 统一负责，
// 快照绝不与活动位图别名（RestoreBitmap 复制恢复）——所有权转移模式，分析器无法跨集合跟踪。
#pragma warning disable CA2000

namespace BetterDesktop.Shell.Capture.UI;

/// <summary>
/// 编辑器：标注（画笔/箭头/矩形/文字）+ 马赛克/模糊（破坏性像素编辑，位图快照撤销）+ 撤销/重做。
/// 完成时：RTB 合成（基底图 + 形状层）→ PNG 落盘 → 回调写剪贴板。
/// </summary>
public sealed class EditorWindow : Window
{
    private enum Tool { Pen, Arrow, Rect, Text, Mosaic, Blur }

    private readonly string _pngPath;
    private readonly PixelRect _region;
    private readonly Action<string, string?> _onFinished; // (newPngPath, error)

    private RawFrame _bitmap;          // 工作位图（马赛克/模糊就地修改）
    private WriteableBitmap _display = null!;  // 显示位图（与 _bitmap 同内容）；解码失败路径不创建窗口
    private Canvas _canvas = null!;
    private ScrollViewer _scroller = null!;
    private readonly List<UIElement> _shapes = new(); // 形状层（撤销单元）
    private readonly Stack<UndoItem> _undo = new();
    private readonly Stack<UndoItem> _redo = new();

    private Tool _tool = Tool.Pen;
    private Color _color = Colors.Red;
    private double _thickness = 4;
    private bool _drawing;
    private Point _start;
    private Polyline? _penLine;
    private Shape? _preview;
    private TextBox? _textBox;
    private RawFrame? _bitmapSnapshot;

    private sealed class UndoItem : IDisposable
    {
        public UndoItem(Action undo, Action redo)
        {
            Undo = undo;
            Redo = redo;
        }

        public Action Undo { get; }

        public Action Redo { get; }

        /// <summary>随条目托管的位图快照（撤销/重做恢复时复制，条目丢弃/窗口关闭时释放）。</summary>
        public RawFrame? HeldBefore;
        public RawFrame? HeldAfter;

        public void Dispose()
        {
            HeldBefore?.Dispose();
            HeldAfter?.Dispose();
            HeldBefore = null;
            HeldAfter = null;
        }
    }

    public EditorWindow(string pngPath, PixelRect region, Action<string, string?> onFinished)
    {
        _pngPath = pngPath;
        _region = region;
        _onFinished = onFinished;

        if (!PngCodec.TryDecodeBgra(File.ReadAllBytes(pngPath), out _bitmap, out string decodeError))
        {
            Close();
            _onFinished(string.Empty, $"编辑器打不开图片：{decodeError}");
            return;
        }
        _display = ToWriteable(_bitmap);

        Title = "截图标注";
        Width = Math.Min(1200, _bitmap.Width + 40);
        Height = Math.Min(800, _bitmap.Height + 120);
        // 工具条是单行（左侧可换行）——窄窗口会让按钮被裁掉，故给一个下限宽度
        MinWidth = 720;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // 覆盖层（全屏 Topmost）之上显示，否则 Show 后落在覆盖层下面不可见（2026-09-15 真机）
        Topmost = true;
        // 普通窗口（无 DWM 亚克力）→ 用实色内容底色（ThemePanelBackground 带 0.2 不透明度，
        // 在非透明窗口上会叠成近黑）
        Background = ThemeBrushes.Get("ThemeContentBackground");

        var root = new DockPanel();

        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, System.Windows.Controls.Dock.Top);
        root.Children.Add(toolbar);

        var image = new Image { Source = _display, Stretch = Stretch.None };
        _canvas = new Canvas { Width = _bitmap.Width, Height = _bitmap.Height, Background = Brushes.Transparent };
        _canvas.Children.Add(image);
        // 画布镶一条细边并在视口内居中：截图内容常是纯白/纯黑，没有边界就"浮"在窗口底色上分不清范围。
        // 注意：这层 Border 在 _canvas **之外**，故不会被 Finish() 的 RenderTargetBitmap 合成进导出图。
        var canvasFrame = new Border
        {
            BorderBrush = ThemeBrushes.Get("BorderStrokeSubtle"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _canvas,
        };
        _scroller = new ScrollViewer
        {
            Content = canvasFrame,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        root.Children.Add(_scroller);
        Content = root;

        _canvas.MouseLeftButtonDown += OnCanvasDown;
        _canvas.MouseMove += OnCanvasMove;
        _canvas.MouseLeftButtonUp += OnCanvasUp;
        _canvas.MouseRightButtonUp += (_, _) => { };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _drawing)
            {
                _drawing = false;
                _canvas.ReleaseMouseCapture();
                RemovePreview();
                // 马赛克/模糊拖拽中取消：回滚本次涂抹（快照恢复）
                if (_tool is Tool.Mosaic or Tool.Blur && _bitmapSnapshot is not null)
                {
                    RestoreBitmap(_bitmapSnapshot);
                    _bitmapSnapshot = null;
                }
                e.Handled = true;
            }
        };
    }

    private WriteableBitmap ToWriteable(RawFrame frame)
    {
        var wb = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Stride * frame.Height, frame.Stride);
        wb.Freeze();
        return wb;
    }

    private void RefreshDisplay()
    {
        var wb = new WriteableBitmap(_bitmap.Width, _bitmap.Height, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, _bitmap.Width, _bitmap.Height), _bitmap.Pixels, _bitmap.Stride * _bitmap.Height, _bitmap.Stride);
        wb.Freeze();
        _display = wb;
        // 更新基底 Image
        for (int i = 0; i < _canvas.Children.Count; i++)
        {
            if (_canvas.Children[i] is Image img)
            {
                img.Source = wb;
                break;
            }
        }
    }

    private readonly Dictionary<Tool, Button> _toolButtons = new();
    private readonly List<(Button Button, Border Dot, Color Color)> _swatches = new();
    private Button? _undoBtn;
    private Button? _redoBtn;
    private TextBlock? _thicknessValue;

    /// <summary>
    /// 工具条：左侧 = 工具单选组 + 颜色 + 粗细 + 撤销/重做（**可换行**，见下），右侧 = 完成/取消。
    /// <para>
    /// 【为什么左侧用 WrapPanel】窗口宽度随截图尺寸变化（小截图 → 窗口只有几百像素宽），
    /// 单行工具条会被裁掉右半截（按钮点不到）。WrapPanel 让它在窄窗口下折行而不是消失。
    /// </para>
    /// </summary>
    private Border BuildToolbar()
    {
        var row = new DockPanel { LastChildFill = false };

        // ---- 左区：可换行 ----
        var left = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };

        foreach (var tool in s_toolOrder)
        {
            var captured = tool;
            var btn = CaptureUi.PillButton(
                ToolLabel(tool), () => SelectTool(captured), CaptureUi.Face.Surface, compact: true);
            // 工具之间留 ButtonGap（4px 时"一排工具"读起来是一整块，分不清边界）
            btn.Margin = new Thickness(
                CaptureUi.ButtonGap / 2, 0, CaptureUi.ButtonGap / 2, EntryTheme.Scale.SpaceXS);
            _toolButtons[tool] = btn;
            left.Children.Add(btn);
        }

        left.Children.Add(Separator());

        foreach (var (color, name) in s_palette)
        {
            left.Children.Add(ColorSwatch(color, name));
        }

        left.Children.Add(Separator());

        // 粗细：滑块 + **实时数值**（原先只有滑块没有读数，用户无法知道当前是几像素）
        var thicknessGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, EntryTheme.Scale.SpaceS, 0),
        };
        thicknessGroup.Children.Add(CaptureUi.Muted("粗细", hud: false));
        var slider = new Slider
        {
            Minimum = 1,
            Maximum = 24,
            Value = _thickness,
            Width = 110,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(EntryTheme.Scale.SpaceS, 0, EntryTheme.Scale.SpaceS, 0),
            // 点轨道即跳到位（默认只能拖拽，细调到目标值很费劲）
            IsMoveToPointEnabled = true,
            Foreground = new SolidColorBrush(EntryTheme.AccentColor),
        };
        _thicknessValue = new TextBlock
        {
            FontFamily = CaptureUi.MonoFont,
            FontSize = EntryTheme.Scale.FontSmall,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 40,
            Text = $"{_thickness:0} px",
        };
        _thicknessValue.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMutedForeground");
        slider.ValueChanged += (_, e) =>
        {
            _thickness = e.NewValue;
            if (_thicknessValue is not null)
            {
                _thicknessValue.Text = $"{e.NewValue:0} px";
            }
        };
        thicknessGroup.Children.Add(slider);
        thicknessGroup.Children.Add(_thicknessValue);
        left.Children.Add(thicknessGroup);

        left.Children.Add(Separator());

        _undoBtn = CaptureUi.IconButton("\uE7A7", "撤销（Ctrl+Z）", Undo);
        _redoBtn = CaptureUi.IconButton("\uE7A6", "重做（Ctrl+Y）", Redo);
        _undoBtn.IsEnabled = false;
        _redoBtn.IsEnabled = false;
        left.Children.Add(_undoBtn);
        left.Children.Add(_redoBtn);

        DockPanel.SetDock(left, System.Windows.Controls.Dock.Left);
        row.Children.Add(left);

        // ---- 右区：主次分明的完成/取消 ----
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(EntryTheme.Scale.SpaceS, 0, 0, 0),
        };
        var done = CaptureUi.PillButton("完成", Finish, CaptureUi.Face.Primary);
        done.Margin = new Thickness(0, 0, CaptureUi.ButtonGap / 2, 0);
        var cancel = CaptureUi.PillButton("取消", () =>
        {
            Close();
            _onFinished(string.Empty, null);
        });
        cancel.Margin = new Thickness(CaptureUi.ButtonGap / 2, 0, 0, 0);
        right.Children.Add(done);
        right.Children.Add(cancel);
        DockPanel.SetDock(right, System.Windows.Controls.Dock.Right);
        row.Children.Add(right);

        SelectTool(_tool); // 初始化工具单选组的外观

        return new Border
        {
            Background = ThemeBrushes.Get("ControlBackground"),
            BorderBrush = ThemeBrushes.Get("BorderStrokeSubtle"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(
                EntryTheme.Scale.SpaceS, EntryTheme.Scale.SpaceXS,
                EntryTheme.Scale.SpaceS, EntryTheme.Scale.SpaceXS),
            Child = row,
        };
    }

    private static readonly Tool[] s_toolOrder =
    {
        Tool.Pen, Tool.Arrow, Tool.Rect, Tool.Text, Tool.Mosaic, Tool.Blur,
    };

    /// <summary>标注用色板（颜色 + 中文名：颜色按钮必须有无障碍名与 ToolTip，否则"七个小方块"说不出是什么）。</summary>
    private static readonly (Color Color, string Name)[] s_palette =
    {
        (Colors.Red, "红"),
        (Colors.Orange, "橙"),
        (Colors.Yellow, "黄"),
        (Colors.Lime, "绿"),
        (Colors.Cyan, "青"),
        (Colors.White, "白"),
        (Colors.Black, "黑"),
    };

    private static string ToolLabel(Tool tool) => tool switch
    {
        Tool.Pen => "画笔",
        Tool.Arrow => "箭头",
        Tool.Rect => "矩形",
        Tool.Text => "文字",
        Tool.Mosaic => "马赛克",
        _ => "模糊",
    };

    /// <summary>工具条上的细分隔（浅色低透明，只做视觉分组，不抢注意力）。</summary>
    private static Border Separator() => new()
    {
        Width = 1,
        Margin = new Thickness(
            CaptureUi.ButtonGap / 2, EntryTheme.Scale.SpaceS,
            CaptureUi.ButtonGap / 2, EntryTheme.Scale.SpaceS),
        Background = ThemeBrushes.Get("BorderStrokeSubtle"),
    };

    /// <summary>
    /// 颜色按钮：圆形色块 + 32px 点击目标 + 选中强调环 + hover 预选环。
    /// （原实现是 18px 的方按钮，点击目标太小、且白/黑色块在工具条上几乎看不见边界。）
    /// </summary>
    private Button ColorSwatch(Color color, string name)
    {
        var dot = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(color),
            // 默认给一圈中性描边：白/黑色块在深浅工具条上都要看得见边界
            BorderBrush = ThemeBrushes.Get("BorderStroke"),
            BorderThickness = new Thickness(2),
        };
        var btn = new Button
        {
            Content = dot,
            Width = EntryTheme.Scale.TapTarget,
            Height = EntryTheme.Scale.TapTarget,
            Margin = new Thickness(
                CaptureUi.ButtonGap / 2, 0, CaptureUi.ButtonGap / 2, EntryTheme.Scale.SpaceXS),
            Cursor = Cursors.Hand,
            ToolTip = $"线条 / 文字颜色：{name}",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = TransparentHostTemplate(),
            Tag = color,
        };
        AutomationProperties.SetName(btn, $"颜色 {name}");

        var entry = (Button: btn, Dot: dot, Color: color);
        _swatches.Add(entry);
        btn.MouseEnter += (_, _) => HoverSwatch(entry, true);
        btn.MouseLeave += (_, _) => HoverSwatch(entry, false);
        btn.Click += (_, _) => SelectColor(color);
        return btn;
    }

    private void HoverSwatch((Button Button, Border Dot, Color Color) entry, bool enter)
    {
        if (_color == entry.Color)
        {
            return; // 已选中：环必须是强调色，不能被 hover 覆盖
        }
        entry.Dot.BorderBrush = enter
            ? ThemeBrushes.AccentTint(0.5)
            : ThemeBrushes.Get("BorderStroke");
    }

    private void SelectColor(Color color)
    {
        _color = color;
        foreach (var entry in _swatches)
        {
            entry.Dot.BorderBrush = entry.Color == color
                ? new SolidColorBrush(EntryTheme.AccentColor)
                : ThemeBrushes.Get("BorderStroke");
        }
    }

    private static ControlTemplate TransparentHostTemplate()
    {
        var host = new FrameworkElementFactory(typeof(ContentPresenter));
        host.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        host.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        return new ControlTemplate(typeof(Button)) { VisualTree = host };
    }

    private void SelectTool(Tool tool)
    {
        _tool = tool;
        _canvas.Cursor = tool switch
        {
            Tool.Pen => Cursors.Pen,
            Tool.Text => Cursors.IBeam,
            _ => Cursors.Cross,
        };
        foreach (var (t, btn) in _toolButtons)
        {
            CaptureUi.ApplyFace(btn, t == tool ? CaptureUi.Face.Selected : CaptureUi.Face.Surface);
        }
    }

    /// <summary>撤销/重做按钮随栈深度启停（空栈时点它没有任何反应，看起来像坏了）。</summary>
    private void RefreshHistoryButtons()
    {
        if (_undoBtn is not null)
        {
            _undoBtn.IsEnabled = _undo.Count > 0;
        }
        if (_redoBtn is not null)
        {
            _redoBtn.IsEnabled = _redo.Count > 0;
        }
    }

    // ---------------- 鼠标交互 ----------------

    private void OnCanvasDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        _start = e.GetPosition(_canvas);
        _drawing = true;
        _canvas.CaptureMouse();

        switch (_tool)
        {
            case Tool.Pen:
                _penLine = new Polyline
                {
                    Stroke = new SolidColorBrush(_color),
                    StrokeThickness = _thickness,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                _penLine.Points.Add(_start);
                _canvas.Children.Add(_penLine);
                break;
            case Tool.Arrow:
                _preview = new Line
                {
                    Stroke = new SolidColorBrush(_color),
                    StrokeThickness = _thickness,
                    X1 = _start.X,
                    Y1 = _start.Y,
                    X2 = _start.X,
                    Y2 = _start.Y,
                };
                _canvas.Children.Add(_preview);
                break;
            case Tool.Rect:
                _preview = new Rectangle
                {
                    Stroke = new SolidColorBrush(_color),
                    StrokeThickness = _thickness,
                    Fill = new SolidColorBrush(Color.FromArgb(0x20, _color.R, _color.G, _color.B)),
                };
                Canvas.SetLeft(_preview, _start.X);
                Canvas.SetTop(_preview, _start.Y);
                _preview.Width = 0;
                _preview.Height = 0;
                _canvas.Children.Add(_preview);
                break;
            case Tool.Text:
                _drawing = false;
                _canvas.ReleaseMouseCapture();
                AddText(_start);
                break;
            case Tool.Mosaic:
            case Tool.Blur:
                _bitmapSnapshot = Snapshot(_bitmap);
                ApplyEffectAt(_start, _start);
                break;
        }
        e.Handled = true;
    }

    private void OnCanvasMove(object sender, MouseEventArgs e)
    {
        if (!_drawing || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        var p = e.GetPosition(_canvas);
        switch (_tool)
        {
            case Tool.Pen:
                if (_penLine is not null)
                {
                    _penLine.Points.Add(p);
                }
                break;
            case Tool.Arrow:
                if (_preview is Line line)
                {
                    line.X2 = p.X;
                    line.Y2 = p.Y;
                }
                break;
            case Tool.Rect:
                if (_preview is Rectangle rect)
                {
                    var x = Math.Min(_start.X, p.X);
                    var y = Math.Min(_start.Y, p.Y);
                    Canvas.SetLeft(rect, x);
                    Canvas.SetTop(rect, y);
                    rect.Width = Math.Abs(p.X - _start.X);
                    rect.Height = Math.Abs(p.Y - _start.Y);
                }
                break;
            case Tool.Mosaic:
            case Tool.Blur:
                ApplyEffectAt(_start, p); // 拖动连续涂抹
                break;
        }
        e.Handled = true;
    }

    private void OnCanvasUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawing)
        {
            return;
        }
        _drawing = false;
        _canvas.ReleaseMouseCapture();

        switch (_tool)
        {
            case Tool.Pen:
                if (_penLine is { } line && line.Points.Count >= 2)
                {
                    _shapes.Add(line);
                    Push(new UndoItem(
                        () => RemoveShape(line),
                        () => AddShape(line)));
                }
                else if (_penLine is not null)
                {
                    _canvas.Children.Remove(_penLine);
                }
                _penLine = null;
                break;
            case Tool.Arrow:
                CommitPreview();
                break;
            case Tool.Rect:
                CommitPreview();
                break;
            case Tool.Mosaic:
            case Tool.Blur:
                if (_bitmapSnapshot is not null)
                {
                    var before = _bitmapSnapshot;
                    _bitmapSnapshot = null;
                    var after = Snapshot(_bitmap);
                    var item = new UndoItem(
                        () => RestoreBitmap(before),
                        () => RestoreBitmap(after))
                    {
                        HeldBefore = before,
                        HeldAfter = after,
                    };
                    Push(item);
                }
                break;
        }
        e.Handled = true;
    }

    private void CommitPreview()
    {
        if (_preview is null)
        {
            return;
        }
        _shapes.Add(_preview);
        var shape = _preview;
        Push(new UndoItem(
            () => RemoveShape(shape),
            () => AddShape(shape)));
        _preview = null;
    }

    private void RemovePreview()
    {
        if (_preview is not null)
        {
            _canvas.Children.Remove(_preview);
            _preview = null;
        }
        if (_penLine is not null)
        {
            _canvas.Children.Remove(_penLine);
            _penLine = null;
        }
    }

    private void AddText(Point p)
    {
        _textBox = new TextBox
        {
            Text = string.Empty,
            Foreground = new SolidColorBrush(_color),
            FontSize = _thickness * 3.5,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            MinWidth = 40,
        };
        Canvas.SetLeft(_textBox, p.X);
        Canvas.SetTop(_textBox, p.Y);
        _canvas.Children.Add(_textBox);
        _textBox.Focus();
        _textBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitText();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _canvas.Children.Remove(_textBox);
                _textBox = null;
                e.Handled = true;
            }
        };
        _textBox.LostFocus += (_, _) => CommitText();
    }

    private void CommitText()
    {
        if (_textBox is null)
        {
            return;
        }
        var tb = _textBox;
        _textBox = null;
        if (string.IsNullOrEmpty(tb.Text))
        {
            _canvas.Children.Remove(tb);
            return;
        }
        var block = new TextBlock
        {
            Text = tb.Text,
            Foreground = new SolidColorBrush(_color),
            FontSize = tb.FontSize,
            FontFamily = new FontFamily("Microsoft YaHei"),
        };
        Canvas.SetLeft(block, Canvas.GetLeft(tb));
        Canvas.SetTop(block, Canvas.GetTop(tb));
        _canvas.Children.Remove(tb);
        _canvas.Children.Add(block);
        _shapes.Add(block);
        Push(new UndoItem(
            () => RemoveShape(block),
            () => AddShape(block)));
    }

    // ---------------- 像素效果（马赛克/模糊） ----------------

    private unsafe void ApplyEffectAt(Point a, Point end)
    {
        var rect = NormalizeRect(a, end, _bitmap.Width, _bitmap.Height);
        if (rect.Width < 4 || rect.Height < 4)
        {
            return;
        }

        int block = 16;
        byte* px = (byte*)_bitmap.Pixels;
        int stride = _bitmap.Stride;
        int bpp = 4;

        if (_tool == Tool.Mosaic)
        {
            for (int y = rect.Y; y < rect.Bottom; y += block)
            {
                for (int x = rect.X; x < rect.Right; x += block)
                {
                    int bx = Math.Min(x, rect.Right - block);
                    int by = Math.Min(y, rect.Bottom - block);
                    int bw = Math.Min(block, rect.Right - bx);
                    int bh = Math.Min(block, rect.Bottom - by);
                    long r = 0, g = 0, b = 0;
                    int n = 0;
                    for (int yy = by; yy < by + bh; yy++)
                    {
                        byte* row = px + (nint)yy * stride + bx * bpp;
                        for (int xx = 0; xx < bw; xx++)
                        {
                            r += row[xx * 4 + 2];
                            g += row[xx * 4 + 1];
                            b += row[xx * 4];
                            n++;
                        }
                    }
                    if (n == 0)
                    {
                        continue;
                    }
                    byte R = (byte)(r / n), G = (byte)(g / n), B = (byte)(b / n);
                    for (int yy = by; yy < by + bh; yy++)
                    {
                        byte* row = px + (nint)yy * stride + bx * bpp;
                        for (int xx = 0; xx < bw; xx++)
                        {
                            row[xx * 4] = B;
                            row[xx * 4 + 1] = G;
                            row[xx * 4 + 2] = R;
                        }
                    }
                }
            }
        }
        else // 模糊：3×3 box blur（两遍近似高斯）
        {
            var copy = new byte[rect.Width * rect.Height * 4];
            fixed (byte* dst = copy)
            {
                byte* src = px;
                for (int pass = 0; pass < 2; pass++)
                {
                    // 拷贝当前矩形到临时缓冲
                    for (int y = rect.Y; y < rect.Bottom; y++)
                    {
                        byte* s = src + (nint)y * stride + rect.X * bpp;
                        byte* d = dst + (nint)(y - rect.Y) * rect.Width * bpp;
                        new Span<byte>(s, rect.Width * bpp).CopyTo(new Span<byte>(d, rect.Width * bpp));
                    }
                    for (int y = 0; y < rect.Height; y++)
                    {
                        byte* d = dst + (nint)y * rect.Width * bpp;
                        for (int x = 0; x < rect.Width; x++)
                        {
                            for (int c = 0; c < 3; c++)
                            {
                                int acc = 0;
                                for (int dy = -1; dy <= 1; dy++)
                                {
                                    for (int dx = -1; dx <= 1; dx++)
                                    {
                                        int sy = Math.Clamp(y + dy, 0, rect.Height - 1);
                                        int sx = Math.Clamp(x + dx, 0, rect.Width - 1);
                                        acc += d[sy * rect.Width * bpp + sx * bpp + c];
                                    }
                                }
                                int srcX = x + rect.X, srcY = y + rect.Y;
                                byte* t = src + (nint)srcY * stride + srcX * bpp;
                                t[c] = (byte)(acc / 9);
                            }
                        }
                    }
                }
            }
        }
        RefreshDisplay();
    }

    private static System.Drawing.Rectangle NormalizeRect(Point a, Point b, int w, int h)
    {
        int x = Math.Clamp((int)Math.Min(a.X, b.X), 0, w - 1);
        int y = Math.Clamp((int)Math.Min(a.Y, b.Y), 0, h - 1);
        int x2 = Math.Clamp((int)Math.Max(a.X, b.X), 0, w - 1);
        int y2 = Math.Clamp((int)Math.Max(a.Y, b.Y), 0, h - 1);
        return new System.Drawing.Rectangle(x, y, x2 - x + 1, y2 - y + 1);
    }

    private static unsafe RawFrame Snapshot(RawFrame frame)
    {
        var copy = RawFrame.Allocate(frame.Rect, frame.Format);
        Buffer.MemoryCopy((void*)frame.Pixels, (void*)copy.Pixels, (long)frame.Stride * frame.Height, (long)frame.Stride * frame.Height);
        return copy;
    }

    private void RestoreBitmap(RawFrame snapshot)
    {
        // 复制恢复：快照所有权始终归 UndoItem，绝不与活动位图别名。
        var copy = Snapshot(snapshot);
        _bitmap.Dispose();
        _bitmap = copy;
        RefreshDisplay();
    }

    // ---------------- 撤销 / 重做 ----------------

    private void Push(UndoItem item)
    {
        _undo.Push(item);
        // 新动作使重做栈失效：释放其托管快照
        while (_redo.Count > 0)
        {
            _redo.Pop().Dispose();
        }
        RefreshHistoryButtons();
    }

    private void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }
        var item = _undo.Pop();
        item.Undo();
        _redo.Push(item);
        RefreshHistoryButtons();
    }

    private void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }
        var item = _redo.Pop();
        item.Redo();
        _undo.Push(item);
        RefreshHistoryButtons();
    }

    private void AddShape(UIElement el)
    {
        _shapes.Add(el);
        _canvas.Children.Add(el);
    }

    private void RemoveShape(UIElement el)
    {
        _shapes.Remove(el);
        _canvas.Children.Remove(el);
    }

    // ---------------- 完成 ----------------

    private void Finish()
    {
        try
        {
            CommitText();
            // 合成：RTB 捕获画布（基底位图已含马赛克/模糊 + 形状层）
            var rtb = new RenderTargetBitmap(_bitmap.Width, _bitmap.Height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(_canvas);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            string path = TempFileManager.WriteTempPng(ms.ToArray());
            Close();
            _onFinished(path, null);
        }
        catch (Exception ex)
        {
            Close();
            _onFinished(string.Empty, $"导出失败：{ex.Message}");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _bitmap.Dispose();
        while (_undo.Count > 0)
        {
            _undo.Pop().Dispose();
        }
        while (_redo.Count > 0)
        {
            _redo.Pop().Dispose();
        }
        base.OnClosed(e);
    }
}
