using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace BetterDesktop.Host.Views;

/// <summary>
/// 初始化弹窗:播放品牌启动动画(splash-anim-v5.mp4)。
/// 动画播完 / 点击跳过 / 解码失败 / 超时兜底 任一发生时关闭,
/// 由 App.OnStartup 在 Closed 后继续装配主程序(Bootstrap.Build)。
/// </summary>
public partial class SplashWindow : Window
{
    /// <summary>动画最长等待:5s 动画 + 解码/缓冲余量,超时强制进入主程序,避免卡死启动。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly DispatcherTimer _timer;

    public SplashWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = Timeout };
        _timer.Tick += (_, _) => Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 资源由 csproj 以 Resource 嵌入程序集,pack URI 读取
        Player.Source = new Uri("pack://application:,,,/Assets/splash-anim-v5.mp4");
        _timer.Start();
    }

    private void CardClip_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // MediaElement 按矩形渲染,用圆角几何裁剪使其贴合卡片圆角
        CardClip.Clip = new RectangleGeometry(
            new Rect(0, 0, CardClip.ActualWidth, CardClip.ActualHeight), 18, 18);
    }

    private void Player_OnMediaEnded(object sender, RoutedEventArgs e) => Close();

    /// <summary>解码/资源加载失败:不阻塞启动,直接进入主程序。</summary>
    private void Player_OnMediaFailed(object sender, ExceptionRoutedEventArgs e) => Close();

    /// <summary>点击任意处跳过动画,立即进入主程序。</summary>
    private void Card_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
