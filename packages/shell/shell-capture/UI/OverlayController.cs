using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BetterDesktop.Capture.Contracts;
using BetterDesktop.Shell.Capture.Core;
using BetterDesktop.Shell.Capture.Native;

namespace BetterDesktop.Shell.Capture.UI;

/// <summary>
/// 覆盖层控制器：每显示器一个 <see cref="OverlayWindow"/>（混合 DPI 下各自按本屏缩放渲染，
/// 选区在物理像素空间统一计算，DoD T2）。共享一个 SelectionController 与全屏 PNG。
/// </summary>
public sealed class OverlayController
{
    private readonly List<OverlayWindow> _windows = new();
    private readonly SelectionController _selection;
    private readonly BitmapImage _fullImage;

    public event Action<PixelRect>? Selected;
    public event Action? Confirmed;
    public event Action? Annotate;
    public event Action? Ocr;
    public event Action? Sticker;
    public event Action? Cancelled;

    public OverlayController(
        string pngPath,
        Action<PixelRect>? selected,
        Action? confirmed,
        Action? annotate,
        Action? ocr,
        Action? sticker,
        Action? cancelled)
    {
        Selected = selected;
        Confirmed = confirmed;
        Annotate = annotate;
        Ocr = ocr;
        Sticker = sticker;
        Cancelled = cancelled;
        // OnLoad 立即解码并释放文件句柄（默认延迟加载会持续锁住临时 PNG，
        // 导致会话结束时 CleanupSession 删除失败——真机日志多次 WARN）
        _fullImage = new BitmapImage
        {
            CacheOption = BitmapCacheOption.OnLoad,
            CreateOptions = BitmapCreateOptions.IgnoreImageCache,
        };
        _fullImage.BeginInit();
        _fullImage.UriSource = new Uri(pngPath);
        _fullImage.EndInit();
        _fullImage.Freeze();

        var screen = VirtualScreenInfo.Query();
        _selection = new SelectionController(screen.Bounds);
        var all = _windows;
        foreach (var monitor in screen.Monitors)
        {
            var w = new OverlayWindow(monitor, _fullImage, _selection, SamplePixel, RenderAll);
            w.Wire(OnSelected, OnConfirmed, OnAnnotate, OnOcr, OnSticker, OnCancelled);
            all.Add(w);
        }
    }

    public void Show()
    {
        foreach (var w in _windows)
        {
            w.Show();
        }
        foreach (var w in _windows)
        {
            w.FocusForKeyboard();
        }
    }

    private void RenderAll()
    {
        foreach (var w in _windows)
        {
            w.Render();
        }
    }

    public void CloseAll()
    {
        foreach (var w in _windows)
        {
            w.Close();
        }
    }

    private void OnSelected(PixelRect region) => Selected?.Invoke(region);

    private void OnConfirmed() => Confirmed?.Invoke();

    private void OnAnnotate() => Annotate?.Invoke();

    private void OnOcr() => Ocr?.Invoke();

    private void OnSticker() => Sticker?.Invoke();

    private void OnCancelled() => Cancelled?.Invoke();

    /// <summary>取物理像素颜色（BGRA8 假定；PNG 截图为 BGRA）。</summary>
    private (byte R, byte G, byte B) SamplePixel(System.Drawing.Point physical)
    {
        int x = physical.X - 0;
        int y = physical.Y - 0;
        if (x < 0 || y < 0 || x >= _fullImage.PixelWidth || y >= _fullImage.PixelHeight)
        {
            return (0, 0, 0);
        }
        // BitmapImage 原点 = 虚拟屏原点（PNG 为整虚拟屏）。
        var rect = new Int32Rect(x, y, 1, 1);
        var buf = new byte[4];
        try
        {
            _fullImage.CopyPixels(rect, buf, 4, 0);
            return (buf[2], buf[1], buf[0]); // BGRA → RGB
        }
        catch
        {
            return (0, 0, 0);
        }
    }
}
