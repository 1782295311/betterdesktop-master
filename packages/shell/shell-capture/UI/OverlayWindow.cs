using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BetterDesktop.Capture.Contracts;
using BetterDesktop.Shell.Capture.Core;
using BetterDesktop.Shell.Capture.Native;
using BetterDesktop.Shell.Core.Surface;

namespace BetterDesktop.Shell.Capture.UI;

/// <summary>
/// 单个显示器的覆盖层窗口：暗化本屏（挖孔选区）+ 本屏截图切片 + 选区边框（跨窗口拼接）。
/// 选区/坐标全在物理像素空间；本窗口内部换算到本屏 DIP（混合 DPI 各自缩放，DoD T2）。
/// <para>
/// 【视觉 2026-09-15】本窗口及其功能栏/信息条/放大镜属于 <b>HUD 表面</b>（恒深色，见
/// <see cref="CaptureUi"/> 类注释）：它叠在用户任意内容之上，深色才能在任何底图上保持对比度；
/// 只有**选区描边与主按钮**跟随主题强调色 —— 让"这是我们家的工具"与"跟主题一致"同时成立。
/// </para>
/// </summary>
public sealed class OverlayWindow : Window
{
    private static readonly Brush DimBrush = new SolidColorBrush(Color.FromArgb(0x59, 0x00, 0x00, 0x00));

    /// <summary>选区描边（主题强调色；由 Render 每帧取，主题改了重启即跟随）。</summary>
    private static Brush SelectionBrush => new SolidColorBrush(EntryTheme.AccentColor);

    private readonly MonitorEntry _monitor;
    private readonly double _scale; // 物理像素 / DIP
    private readonly SelectionController _selection;
    private readonly Func<System.Drawing.Point, (byte R, byte G, byte B)> _samplePixel;
    private readonly Action _renderAll;
    private readonly Canvas _canvas;
    private readonly Border _info;      // 圆角 HUD 信息条（定位/测量对象）
    private readonly TextBlock _infoText; // 尺寸/坐标/取色文本
    private readonly Border _magnifier;
    private readonly Image _magnifierImage;
    private readonly BitmapImage _fullImage;

    private bool _dragging;
    private bool _fullscreenSelected; // 全屏截图（点击/完成按钮全屏）：任意点击=完成保存

    public OverlayWindow(
        MonitorEntry monitor,
        BitmapImage fullImage,
        SelectionController selection,
        Func<System.Drawing.Point, (byte R, byte G, byte B)> samplePixel,
        Action renderAll)
    {
        _monitor = monitor;
        _scale = monitor.DpiX / 96.0;
        _selection = selection;
        _samplePixel = samplePixel;
        _renderAll = renderAll;
        _fullImage = fullImage;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        Focusable = true;
        Cursor = Cursors.Cross;

        Left = monitor.Bounds.X / _scale;
        Top = monitor.Bounds.Y / _scale;
        Width = monitor.Bounds.Width / _scale;
        Height = monitor.Bounds.Height / _scale;

        _canvas = new Canvas();
        Content = _canvas;

        // 截图切片（本屏物理矩形 → CroppedBitmap，1:1 物理像素显示）
        int sx = Math.Max(0, monitor.Bounds.X);
        int sy = Math.Max(0, monitor.Bounds.Y);
        int sw = Math.Min(fullImage.PixelWidth - sx, monitor.Bounds.Width);
        int sh = Math.Min(fullImage.PixelHeight - sy, monitor.Bounds.Height);
        if (sw > 0 && sh > 0)
        {
            var slice = new CroppedBitmap(fullImage, new Int32Rect(sx, sy, sw, sh));
            slice.Freeze();
            var image = new Image
            {
                Source = slice,
                Width = sw / _scale,
                Height = sh / _scale,
                Stretch = Stretch.Fill,
            };
            Canvas.SetLeft(image, (sx - monitor.Bounds.X) / _scale);
            Canvas.SetTop(image, (sy - monitor.Bounds.Y) / _scale);
            _canvas.Children.Add(image);
        }

        // 信息条 = HUD 胶囊 + 等宽字体（尺寸/坐标/色值都是"精确数据"，等宽读起来更稳）。
        // 初始操作提示；拖选开始/移动后由 Render/UpdateMagnifier 覆盖为尺寸/取色。
        _infoText = new TextBlock
        {
            Foreground = new SolidColorBrush(CaptureUi.HudForeground),
            FontFamily = CaptureUi.MonoFont,
            FontSize = EntryTheme.Scale.FontSmall,
            Text = "拖选截图 · 松开后底部选功能 · Esc 取消",
        };
        _info = new Border
        {
            Background = new SolidColorBrush(CaptureUi.HudSurface),
            CornerRadius = new CornerRadius(EntryTheme.Scale.RadiusPill),
            // 留白口径与功能栏一致：横向 12、纵向 8（原先 8/5 让信息条又扁又挤）
            Padding = new Thickness(
                EntryTheme.Scale.SpaceM, EntryTheme.Scale.SpaceS,
                EntryTheme.Scale.SpaceM, EntryTheme.Scale.SpaceS),
            Child = _infoText,
            Visibility = Visibility.Visible,
        };
        _canvas.Children.Add(_info);
        Panel.SetZIndex(_info, 990); // 信息条在最上层（暗化矩形之上，避免被遮挡/拦截点击）

        _magnifier = new Border
        {
            Width = 96,
            Height = 96,
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x00, 0x00, 0x00)),
            // 2px 亮环 + 深底：放大镜要"压得住"任意底图（1px 细边在亮内容上会糊掉）
            BorderBrush = new SolidColorBrush(CaptureUi.HudForeground),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(48),
            ClipToBounds = true,
            Visibility = Visibility.Collapsed,
        };
        _magnifierImage = new Image { Stretch = Stretch.Fill, Width = 96, Height = 96 };
        _magnifier.Child = _magnifierImage;
        _canvas.Children.Add(_magnifier);
        Panel.SetZIndex(_magnifier, 980); // 放大镜在暗化层之上

        BuildActionBar();

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _selection.Reset();
                _renderAll();
                _cancelRequested?.Invoke();
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (_selectedState && _confirmed is not null)
                {
                    _confirmed();
                }
            }
        };
    }

    private Action? _confirmed;
    private Action? _cancelRequested;
    private Action? _annotate;
    private Action? _ocr;
    private Action? _sticker;
    private Action<PixelRect>? _selected;
    private bool _selectedState; // 已截图状态：覆盖层保留，底部功能栏可见，点空白=完成

    /// <summary>
    /// 绑定回调（控制器统一接线）：
    /// selected=拖选定稿（region）；confirmed=完成（写剪贴板退出）；annotate/ocr/sticker=功能；
    /// cancelled=取消。点空白（已截图状态、选区外）= confirmed。
    /// </summary>
    public void Wire(
        Action<PixelRect> selected,
        Action confirmed,
        Action annotate,
        Action ocr,
        Action sticker,
        Action cancelled)
    {
        _selected = selected;
        _confirmed = confirmed;
        _annotate = annotate;
        _ocr = ocr;
        _sticker = sticker;
        _cancelRequested = cancelled;
    }

    public void FocusForKeyboard()
    {
        ShowActivated = true;
        Activate();
        Focus();
    }

    private System.Drawing.Point ToPhysical(Point dip)
    {
        int x = _monitor.Bounds.X + (int)Math.Round(dip.X * _scale);
        int y = _monitor.Bounds.Y + (int)Math.Round(dip.Y * _scale);
        return new System.Drawing.Point(x, y);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        // 点击落在功能栏/按钮上 → 放行给按钮（绝对不触发完成/拖选，防"点功能=保存"误触）
        if (IsOnActionBar(e.OriginalSource as DependencyObject))
        {
            return;
        }
        var pt = ToPhysical(e.GetPosition(this));
        if (_selectedState)
        {
            // 全屏已截图：不响应点击（无"空白"可言），保存只走"完成 ✓"按钮/Enter
            if (_fullscreenSelected)
            {
                return;
            }
            // 已截图（区域）状态下点选区外空白 = 完成并退出
            if (_selection.Current is { } sel && !sel.Contains(pt.X, pt.Y))
            {
                e.Handled = true;
                _confirmed?.Invoke();
                return;
            }
            // 选区内按下 = 重新拖选
            _selectedState = false;
            _fullscreenSelected = false;
            _dragging = true;
            _selection.Begin(pt);
            CaptureMouse();
            _renderAll();
            e.Handled = true;
            return;
        }
        // 初始状态按下 = 开始拖选
        _fullscreenSelected = false;
        _dragging = true;
        _selection.Begin(pt);
        CaptureMouse();
        _renderAll();
        e.Handled = true;
    }

    /// <summary>点击源是否在底部功能栏（按钮）内——是则完全放行，不参与完成/拖选。</summary>
    private bool IsOnActionBar(DependencyObject? src)
    {
        for (var n = src; n is not null; n = System.Windows.Media.VisualTreeHelper.GetParent(n))
        {
            if (ReferenceEquals(n, _actions))
            {
                return true;
            }
        }
        return false;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = ToPhysical(e.GetPosition(this));
        if (_dragging)
        {
            _selection.Update(p);
            _renderAll();
        }
        else if (!_selectedState)
        {
            UpdateMagnifier(p);
        }
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }
        _dragging = false;
        ReleaseMouseCapture();
        if (!_selection.IsMeaningful)
        {
            // 点击未拖动 → 全屏
            _selection.Begin(new System.Drawing.Point(_selection.VirtualBounds.X, _selection.VirtualBounds.Y));
            _selection.Update(new System.Drawing.Point(_selection.VirtualBounds.Right - 1, _selection.VirtualBounds.Bottom - 1));
            _fullscreenSelected = true;
        }
        _renderAll();
        e.Handled = true;
        // 【2026-09-15 真机交互定稿】拖选完成 → 覆盖层保持，进入"已截图"状态：
        // 底部显示功能栏（完成/标注/OCR/贴图/取消），点空白处 = 完成保存并退出；
        // Esc = 取消。不再"松开即关覆盖层"。
        EnterSelectedState();
        if (_selection.Current is { } region)
        {
            _selected?.Invoke(region);
        }
    }

    /// <summary>进入已截图状态：提示信息更新。覆盖层与底部功能栏保持。</summary>
    private void EnterSelectedState()
    {
        _selectedState = true;
        _infoText.Text = _fullscreenSelected
            ? "已全屏截图 ✓  点『完成』保存 · Esc 取消"
            : $"已截图 {_selection.Current?.Width}×{_selection.Current?.Height} ✓  点空白处完成 · Esc 取消";
        _info.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
    }

    private Border? _actions;

    /// <summary>
    /// 底部功能栏：**常驻可见**（2026-09-15 真机：原实现 Collapsed 态测量 → DesiredSize=0 →
    /// 按钮栏定位到窗口底缘外不可见）。拖选前点"完成"= 全屏截图；拖选后= 保存选区。
    /// <para>
    /// 【视觉 2026-09-15】五个按钮从"五块并列的白底方块"改为 **HUD 胶囊条**：
    /// 图标（Segoe MDL2）+ 文字 + 统一 32px 点击目标 + hover 过渡；主次分明（完成 = 强调色实底，
    /// 取消 = 独立在分隔线之后）。整条工具浮在选区下方，形状与信息条同族。
    /// </para>
    /// </summary>
    private void BuildActionBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(CaptureUi.IconTextButton("\uE73E", "完成", CompleteOrFullscreen, CaptureUi.Face.HudPrimary));
        row.Children.Add(CaptureUi.IconTextButton("\uE70F", "标注", () => _annotate?.Invoke()));
        row.Children.Add(CaptureUi.IconTextButton("\uE8A5", "OCR", () => _ocr?.Invoke()));
        row.Children.Add(CaptureUi.IconTextButton("\uE718", "贴图", () => _sticker?.Invoke()));
        row.Children.Add(CaptureUi.HudSeparator());
        row.Children.Add(CaptureUi.IconTextButton("\uE711", "取消", () => _cancelRequested?.Invoke()));

        _actions = CaptureUi.Capsule(row);
        _actions.Margin = new Thickness(0, 0, 0, 0);
        // 先可见再 Measure（Collapsed 态 DesiredSize=0 导致定位失效），定位后保持常驻。
        _actions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double aw = _actions.DesiredSize.Width;
        double ah = _actions.DesiredSize.Height;
        Canvas.SetLeft(_actions, Math.Max(0, (Width - aw) / 2));
        Canvas.SetTop(_actions, Math.Max(0, Height - ah - 12));
        _canvas.Children.Add(_actions);
        // 【2026-09-15 真机根因】Render 追加的暗化矩形/选区边框 z 序在功能栏之上 →
        // 功能栏被盖在"弹窗层下面"：看得见但点击被矩形拦截、冒泡触发完成退出。
        // 功能栏设最高 ZIndex，永远最上层、可点。
        Panel.SetZIndex(_actions, 1000);
    }

    /// <summary>
    /// 完成：无选区（未拖选点"完成"）= 全屏截图 → **弹窗保留、显示全屏内容**并进入已截图状态
    /// （2026-09-15 用户要求：全屏截图要显示在弹窗内部，与拖选一致，点空白/再点完成才保存退出）；
    /// 有选区直接确认保存。
    /// </summary>
    private void CompleteOrFullscreen()
    {
        if (_selection.Current is null)
        {
            _selection.Begin(new System.Drawing.Point(_selection.VirtualBounds.X, _selection.VirtualBounds.Y));
            _selection.Update(new System.Drawing.Point(_selection.VirtualBounds.Right - 1, _selection.VirtualBounds.Bottom - 1));
            _fullscreenSelected = true;
            _renderAll();
            EnterSelectedState();
            _selected?.Invoke(_selection.Current!.Value);
            return;
        }
        _confirmed?.Invoke();
    }

    /// <summary>重绘本窗口（暗化挖孔 + 选区边框 + 信息条）。由 renderAll 触发。</summary>
    public void Render()
    {
        // 移除上一帧动态图形（Rectangle 均为暗化/边框层；Image/TextBlock/Border 常驻）
        for (int i = _canvas.Children.Count - 1; i >= 0; i--)
        {
            if (_canvas.Children[i] is Rectangle)
            {
                _canvas.Children.RemoveAt(i);
            }
        }

        var sel = _selection.Current;
        if (!sel.HasValue)
        {
            return;
        }
        var s = sel.Value;

        // MonitorEntry.Bounds 是 System.Drawing.Rectangle → 转 PixelRect 再求交
        var monitorRect = new PixelRect(_monitor.Bounds.X, _monitor.Bounds.Y, _monitor.Bounds.Width, _monitor.Bounds.Height);
        var inter = s.Intersect(monitorRect);
        if (inter.IsEmpty)
        {
            return;
        }

        double l = (inter.X - _monitor.Bounds.X) / _scale;
        double t = (inter.Y - _monitor.Bounds.Y) / _scale;
        double w = inter.Width / _scale;
        double h = inter.Height / _scale;

        // 四块暗化矩形（挖孔选区）
        foreach (var (x, y, ww, hh) in DimQuads(l, t, w, h))
        {
            var r = new Rectangle { Fill = DimBrush, Width = ww, Height = hh };
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, y);
            _canvas.Children.Add(r);
        }

        // 选区边框 + 半透明填充（强调色 2px：1.5px 在 125% 缩放下会因半像素取样发虚）
        var accent = EntryTheme.AccentColor;
        var frame = new Rectangle
        {
            Stroke = SelectionBrush,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(0x18, accent.R, accent.G, accent.B)),
        };
        Canvas.SetLeft(frame, l);
        Canvas.SetTop(frame, t);
        frame.Width = w;
        frame.Height = h;
        _canvas.Children.Add(frame);

        // 信息条（尺寸 + 坐标；已截图状态显示完成提示，紧跟选区下沿）
        if (_selectedState)
        {
            _infoText.Text = _fullscreenSelected
                ? "已全屏截图 ✓  点『完成』保存 · Esc 取消"
                : $"已截图 {inter.Width}×{inter.Height} ✓  点空白处完成 · Esc 取消";
        }
        else
        {
            _infoText.Text = $"{inter.Width} × {inter.Height}  ({inter.X}, {inter.Y})";
        }
        _info.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double ix = Math.Clamp(l, 4, Width - _info.DesiredSize.Width - 8);
        double iy = Math.Clamp(t + h + 8, 4, Height - _info.DesiredSize.Height - 8);
        Canvas.SetLeft(_info, ix);
        Canvas.SetTop(_info, iy);
        _info.Visibility = Visibility.Visible;

        // 功能栏**跟随选区**（选区下方、信息条之下）：选区/内容变化时一起移动，与弹窗绑定。
        // 全屏选区 → 贴窗口底（无"下方"空间）；区域选区 → 紧跟选区下沿浮动。
        if (_actions is not null)
        {
            double aw2 = _actions.DesiredSize.Width;
            double ah2 = _actions.DesiredSize.Height;
            double ay = t + h + 8 + _info.DesiredSize.Height + 8;
            Canvas.SetLeft(_actions, Math.Clamp(l + w / 2 - aw2 / 2, 0, Math.Max(0, Width - aw2)));
            Canvas.SetTop(_actions, Math.Clamp(ay, 0, Math.Max(0, Height - ah2 - 8)));
        }
    }

    private (double X, double Y, double W, double H)[] DimQuads(double l, double t, double w, double h)
    {
        double right = Width;
        double bottom = Height;
        return new[]
        {
            (0.0, 0.0, right, Math.Clamp(t, 0, bottom)),
            (0.0, Math.Clamp(t + h, 0, bottom), right, Math.Max(0, bottom - Math.Clamp(t + h, 0, bottom))),
            (0.0, Math.Clamp(t, 0, bottom), Math.Clamp(l, 0, right), Math.Max(0, Math.Min(h, bottom - Math.Clamp(t, 0, bottom)))),
            (Math.Clamp(l + w, 0, right), Math.Clamp(t, 0, bottom), Math.Max(0, right - Math.Clamp(l + w, 0, right)), Math.Max(0, Math.Min(h, bottom - Math.Clamp(t, 0, bottom)))),
        };
    }

    /// <summary>放大镜 + 取色（光标物理坐标）。</summary>
    private void UpdateMagnifier(System.Drawing.Point p)
    {
        var (r, g, b) = _samplePixel(p);
        _infoText.Text = $"#{r:X2}{g:X2}{b:X2}  ({p.X}, {p.Y})";
        _info.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        double cx = (p.X - _monitor.Bounds.X) / _scale;
        double cy = (p.Y - _monitor.Bounds.Y) / _scale;
        Canvas.SetLeft(_magnifier, Math.Clamp(cx + 16, 0, Width - _magnifier.Width - 8));
        Canvas.SetTop(_magnifier, Math.Clamp(cy + 16, 0, Height - _magnifier.Height - 8));
        Canvas.SetLeft(_info, Math.Clamp(cx + 16, 0, Width - _info.DesiredSize.Width - 8));
        Canvas.SetTop(_info, Math.Clamp(cy - _info.DesiredSize.Height - 10, 0, Height - _info.DesiredSize.Height - 8));

        // 放大镜内容：光标周围 12×12 物理像素 → 8 倍
        int px = p.X - 6, py = p.Y - 6;
        var src = new Int32Rect(Math.Clamp(px, 0, _fullImage.PixelWidth - 12), Math.Clamp(py, 0, _fullImage.PixelHeight - 12), 12, 12);
        var crop = new CroppedBitmap(_fullImage, src);
        _magnifierImage.Source = crop;
        _magnifier.Visibility = Visibility.Visible;
        _info.Visibility = Visibility.Visible;
    }
}
