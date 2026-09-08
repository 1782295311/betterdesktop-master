using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.WindowTracker;

namespace BetterDesktop.Shell.WindowTracker.Thumbnail;

/// <summary>
/// DWM 实时缩略图控件。
/// 本实现严格对齐 cairoshell 的 DwmThumbnail.xaml.cs（原版可运行路径），
/// 不自行添加注册闸门 / 重注册时序 / DPI 取值分支：
/// - SourceWindowHandle setter 内联完成 DwmRegisterThumbnail + 首帧 Refresh；
/// - DPI 实时取自 PresentationSource.CompositionTarget.TransformToDevice.M11
///   （对齐 cairoshell WindowPreviewService.RefreshElementPreviews），不依赖 Loaded
///   时机，也不使用 VisualTreeHelper.GetDpi——后者在 SizeToContent 分层窗下取值
///   时机不可靠，会把矩形算出控件框外→缩略图空白/全黑。DpiScale 字段仅作兜底。
/// - SizeChanged + LayoutUpdated 触发刷新；Unloaded 注销。
/// - 缩略图质量（system.thumbnailQuality）由构造时传入：low 降级缩放+降透明度省资源，
///   high 满透明度+高质缩放更清晰，medium 取当前默认。
/// 自 shell-dock 下沉（步骤5），供 Dock 预览、未来任务栏/开始菜单共用。
/// </summary>
internal sealed class DwmThumbnail : UserControl
{
    public byte ThumbnailOpacity = 255;
    public double DpiScale = 1.0;

    // 缩略图质量策略：影响渲染缩放模式与默认透明度。
    // low  → LowQuality 缩放 + 降透明度（省 GPU/内存）；
    // high → HighQuality 缩放 + 满透明度（清晰）；
    // medium → HighQuality 缩放 + 略降透明度（平衡，当前默认）。
    private readonly ThumbnailQuality _quality = ThumbnailQuality.Medium;

    private IntPtr _sourceWindowHandle = IntPtr.Zero;
    private IntPtr _thumbHandle;

    // 脏标记：布局/尺寸变化只置位，真实 DWM 刷新统一在渲染帧前执行（每帧至多一次）。
    // 避免 SizeToContent 打开瞬间 LayoutUpdated 连环触发多次 DwmUpdateThumbnailProperties 导致卡顿。
    private bool _dirty;
    private bool _frameSubscribed;
    private readonly object _frameLock = new();

    public DwmThumbnail(ThumbnailQuality quality = ThumbnailQuality.Medium)
    {
        _quality = quality;
        SnapsToDevicePixels = true;

        // 质量策略决定缩放模式：low 用 LowQuality（省资源），其余用 HighQuality（清晰）。
        RenderOptions.SetBitmapScalingMode(this,
            _quality == ThumbnailQuality.Low
                ? BitmapScalingMode.LowQuality
                : BitmapScalingMode.HighQuality);

        // 质量策略决定默认透明度：low 降到 200（半透更省合成开销），high 满 255，medium 238。
        ThumbnailOpacity = _quality switch
        {
            ThumbnailQuality.Low => 200,
            ThumbnailQuality.High => 255,
            _ => 238
        };

        // 布局变化只标记脏；首帧有真实变化才在渲染帧前统一刷新。
        SizeChanged += (_, _) => MarkDirty();
        LayoutUpdated += (_, _) => MarkDirty();
        Unloaded += (_, _) =>
        {
            UnsubscribeFrame();
            if (_thumbHandle != IntPtr.Zero)
            {
                DwmThumbnailInterop.DwmUnregisterThumbnail(_thumbHandle);
                _thumbHandle = IntPtr.Zero;
            }
        };
    }

    /// <summary>
    /// 标记需要刷新；同一渲染帧内的多次标记合并为一次真实 DWM 刷新。
    /// </summary>
    private void MarkDirty()
    {
        if (_thumbHandle == IntPtr.Zero)
        {
            return;
        }

        _dirty = true;
        if (_frameSubscribed)
        {
            return;
        }

        lock (_frameLock)
        {
            if (_frameSubscribed)
            {
                return;
            }

            _frameSubscribed = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void UnsubscribeFrame()
    {
        if (!_frameSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        _frameSubscribed = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_dirty)
        {
            return;
        }

        _dirty = false;

        // 渲染帧前执行一次真实刷新；若布局尚未稳定（如 SizeToContent 首帧），
        // 刷新后仍会因后续布局事件再次置脏，最终以稳定尺寸收尾，不会漏刷。
        Refresh();

        // 本帧处理完即退订，下一轮布局变化再订阅。避免永久每帧订阅（无变化时空转）。
        UnsubscribeFrame();
    }

    /// <summary>
    /// 当前控件自身的 WPF 宿主窗口句柄（Destination）。
    /// </summary>
    private IntPtr Handle
    {
        get
        {
            var source = PresentationSource.FromVisual(this) as HwndSource;
            return source?.Handle ?? IntPtr.Zero;
        }
    }

    /// <summary>
    /// 设置要显示缩略图的源窗口句柄；切换源窗口会自动注销旧缩略图。
    /// 对齐 cairoshell：在 destination 句柄有效且注册返回 0 (S_OK) 时立即 Refresh。
    /// 调用方应在控件已挂载到已显示窗口后再设置本属性（参考 TaskThumbnail.UserControl_Loaded）。
    /// </summary>
    public IntPtr SourceWindowHandle
    {
        get => _sourceWindowHandle;
        set
        {
            if (_sourceWindowHandle != IntPtr.Zero)
            {
                DwmThumbnailInterop.DwmUnregisterThumbnail(_thumbHandle);
                _thumbHandle = IntPtr.Zero;
            }

            _sourceWindowHandle = value;
            int hr = unchecked((int)0xE0000000); // 未执行注册时的占位（E_FAIL 风格），实际值见各 trace
            bool compEnabled = DwmThumbnailInterop.DwmIsCompositionEnabled();
            bool ok = _sourceWindowHandle != IntPtr.Zero &&
                      Handle != IntPtr.Zero &&
                      compEnabled &&
                      DwmThumbnailInterop.DwmRegisterThumbnail(Handle, _sourceWindowHandle, out _thumbHandle, out hr);
            if (ok)
            {
                DebugLog.Trace("DwmReg",
                    $"src={_sourceWindowHandle} dest={Handle} ok=True thumb={(long)_thumbHandle}");
                Refresh();
            }
            else
            {
                DebugLog.Trace("DwmReg",
                    $"src={_sourceWindowHandle} dest={Handle} ok=False comp={compEnabled} hr=0x{hr:X8} (注册被拒)");
            }
        }
    }

    /// <summary>
    /// 当前控件在顶层窗口内的像素矩形（DWM 使用物理像素），对齐 cairoshell 的 Rect。
    /// DPI 实时取自 CompositionTarget.TransformToDevice（与真源一致），失败才退 DpiScale 字段。
    /// </summary>
    private NativeRect DestinationRect
    {
        get
        {
            double dpi = GetEffectiveDpi();
            try
            {
                var ancestor = Window.GetWindow(this);
                if (ancestor is null)
                {
                    return default;
                }

                var transform = TransformToAncestor(ancestor);
                var point = transform.Transform(new Point(0, 0));
                return new NativeRect(
                    (int)(point.X * dpi),
                    (int)(point.Y * dpi),
                    (int)((point.X + ActualWidth) * dpi),
                    (int)((point.Y + ActualHeight) * dpi));
            }
            catch
            {
                return default;
            }
        }
    }

    /// <summary>
    /// 当前控件所在 HwndSource 的 DPI 缩放（物理像素/逻辑像素）。
    /// 对齐 cairoshell WindowPreviewService.RefreshElementPreviews：优先
    /// CompositionTarget.TransformToDevice.M11；取不到时回退外部 DpiScale 字段（默认 1.0）。
    /// </summary>
    private double GetEffectiveDpi()
    {
        try
        {
            var source = PresentationSource.FromVisual(this) as HwndSource;
            if (source?.CompositionTarget != null)
            {
                double m11 = source.CompositionTarget.TransformToDevice.M11;
                if (m11 > 0)
                {
                    return m11;
                }
            }
        }
        catch
        {
            // 回退到字段
        }

        return DpiScale > 0 ? DpiScale : 1.0;
    }

    /// <summary>
    /// 根据源窗口宽高比，把缩略图等比缩放到控件矩形内并居中（沿用 cairoshell 算法）。
    /// </summary>
    public void Refresh()
    {
        if (_thumbHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (!DwmThumbnailInterop.DwmQueryThumbnailSourceSize(_thumbHandle, out var size) ||
                size.x <= 0 || size.y <= 0)
            {
                DebugLog.Trace("DwmUpd",
                    $"src={_sourceWindowHandle} thumb={(long)_thumbHandle} QUERY_FAIL/zero size");
                return;
            }

            var rect = DestinationRect;
            if (rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            {
                DebugLog.Trace("DwmUpd",
                    $"src={_sourceWindowHandle} BAD_RECT L={rect.Left} T={rect.Top} R={rect.Right} B={rect.Bottom} aw={ActualWidth} ah={ActualHeight} scale={DpiScale}");
                return;
            }

            double sourceAspect = (double)size.x / size.y;
            double controlAspect = (double)rect.Width / rect.Height;

            var props = new NativeMethods.DwmThumbnailProperties
            {
                fVisible = true,
                dwFlags = DwmThumbnailInterop.DwmTnpVisible |
                          DwmThumbnailInterop.DwmTnpRectDestination |
                          DwmThumbnailInterop.DwmTnpOpacity,
                opacity = ThumbnailOpacity,
                rcDestination = new NativeMethods.RECT { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom }
            };

            if (sourceAspect > controlAspect)
            {
                // 源更宽：按宽度平铺，垂直居中
                var height = (int)(rect.Width / sourceAspect);
                props.rcDestination.Top += (rect.Height - height) / 2;
                props.rcDestination.Bottom = props.rcDestination.Top + height;
            }
            else if (sourceAspect < controlAspect)
            {
                // 源更高：按高度平铺，水平居中
                var width = (int)(rect.Height * sourceAspect);
                props.rcDestination.Left += (rect.Width - width) / 2;
                props.rcDestination.Right = props.rcDestination.Left + width;
            }

            DwmThumbnailInterop.DwmUpdateThumbnailProperties(_thumbHandle, ref props);
            DebugLog.Trace("DwmUpd",
                $"src={_sourceWindowHandle} srcSize=({size.x},{size.y}) finalRect L={props.rcDestination.Left} T={props.rcDestination.Top} R={props.rcDestination.Right} B={props.rcDestination.Bottom} aw={ActualWidth} ah={ActualHeight} scale={DpiScale}");
        }
        catch (Exception ex)
        {
            DebugLog.Trace("DwmUpd", $"src={_sourceWindowHandle} EX {ex.GetType().Name}: {ex.Message}");
            // 刷新失败保持静默，不影响标题列表展示。
        }
    }
}

/// <summary>
/// 缩略图质量档位（对应设置 system.thumbnailQuality）。
/// 仅描述"质量意图"，具体渲染参数由 <see cref="DwmThumbnail"/> 解释。
/// </summary>
public enum ThumbnailQuality
{
    Low,
    Medium,
    High
}
