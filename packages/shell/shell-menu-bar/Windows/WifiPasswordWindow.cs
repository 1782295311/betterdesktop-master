// BetterDesktop.Shell.MenuBar — WiFi 密码输入弹窗（自绘，不跳转系统设置）
// 输入密码 → WifiEnumerator.ConnectWithPassword 写入 WLAN profile 并发起连接；
// 连接请求发出后回调通知宿主刷新 WiFi 列表面板。继承 MenuBarPopupWindow，走统一基类/主题。

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;

namespace BetterDesktop.Shell.MenuBar.Windows;

/// <summary>WiFi 密码输入弹窗：自绘界面，输入后直接发起连接，不跳转系统设置。</summary>
internal sealed class WifiPasswordWindow : MenuBarPopupWindow
{
    // 含密码输入框：禁用 WS_EX_NOACTIVATE，否则点击后窗口不获焦点、键盘输入落不进 PasswordBox。
    protected override bool UseNoActivateWindowStyle => false;

    private readonly string _ssid;
    private readonly Action<bool> _onResult; // true=连接请求已发出，false=取消/失败
    private PasswordBox? _passwordBox;
    private TextBox? _plainBox;
    private TextBlock? _errorText;
    private System.Windows.Controls.Button? _connectBtn;
    // S4：窗口关闭/取消后置位——轮询退出 UI 交互，但后台清理仍执行一次（connectionMode=auto
    // 红线：错误 profile 不删会导致系统反复自动重连）。
    private volatile bool _pollCancelled;

    public WifiPasswordWindow(
        string ssid,
        Action<bool> onResult,
        IVibrancyService vibrancy,
        IAppearanceService? appearance = null)
        : base(vibrancy, appearance)
    {
        Width = 300;
        MinWidth = 300;
        SizeToContent = SizeToContent.Height;
        _ssid = ssid;
        _onResult = onResult;
    }

    public FrameworkElement BuildPreviewContent() => BuildContent();

    protected override FrameworkElement BuildContent()
    {
        var root = new Border
        {
            Padding = new Thickness(14),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        var col = new StackPanel { Orientation = Orientation.Vertical };

        // 标题：连接到 SSID
        col.Children.Add(new TextBlock
        {
            Text = $"连接到 {_ssid}",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        // 密码输入区：密码框（密文黑点）+ 眼睛图标按钮（切换显示/隐藏），同行布局
        var pwdRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        pwdRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pwdRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _passwordBox = new PasswordBox
        {
            Height = 28,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 0, 6, 0)
        };
        _plainBox = new TextBox
        {
            Height = 28,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 0, 6, 0),
            Visibility = Visibility.Collapsed
        };
        var pwdContainer = new Grid();
        pwdContainer.Children.Add(_passwordBox);
        pwdContainer.Children.Add(_plainBox);
        Grid.SetColumn(pwdContainer, 0);
        pwdRow.Children.Add(pwdContainer);

        // 眼睛图标按钮：显示/隐藏密码
        var eyeIcon = new TextBlock
        {
            Text = "\uE7B3", // 眼睛（显示）
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var eyeBtn = new Button
        {
            Width = 28,
            Height = 28,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Content = eyeIcon
        };
        bool showing = false;
        eyeBtn.Click += (_, _) =>
        {
            showing = !showing;
            if (showing)
            {
                if (_passwordBox is not null && _plainBox is not null)
                {
                    _plainBox.Text = _passwordBox.Password;
                    _plainBox.Visibility = Visibility.Visible;
                    _passwordBox.Visibility = Visibility.Collapsed;
                    _plainBox.Focus();
                }
                eyeIcon.Text = "\uE7B1"; // 眼睛划线（隐藏）
            }
            else
            {
                if (_passwordBox is not null && _plainBox is not null)
                {
                    _passwordBox.Password = _plainBox.Text;
                    _passwordBox.Visibility = Visibility.Visible;
                    _plainBox.Visibility = Visibility.Collapsed;
                    _passwordBox.Focus();
                }
                eyeIcon.Text = "\uE7B3"; // 眼睛（显示）
            }
        };
        Grid.SetColumn(eyeBtn, 1);
        pwdRow.Children.Add(eyeBtn);
        col.Children.Add(pwdRow);

        // 错误提示
        _errorText = new TextBlock
        {
            FontSize = 11,
            Foreground = ThemeBrushes.Get("StatusDanger"),
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap
        };
        col.Children.Add(_errorText);

        // 按钮行：连接 / 取消
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelBtn = new Button
        {
            Content = "取消",
            Width = 72,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        cancelBtn.Click += (_, _) =>
        {
            _onResult(false);
            Close();
        };
        var connectBtn = new Button
        {
            Content = "连接",
            Width = 72,
            Height = 28,
            Cursor = System.Windows.Input.Cursors.Hand,
            IsDefault = true
        };
        _connectBtn = connectBtn;
        connectBtn.Click += OnConnect;
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(connectBtn);
        col.Children.Add(btnRow);

        root.Child = col;
        return root;
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        // 当前显示明文 TextBox 时读明文，否则读 PasswordBox 密文
        bool plainVisible = _plainBox is { Visibility: Visibility.Visible };
        string pwd = plainVisible ? (_plainBox?.Text ?? "") : (_passwordBox?.Password ?? "");

        if (string.IsNullOrEmpty(pwd))
        {
            ShowError("请输入 WiFi 密码。");
            return;
        }

        // 2026-09-04 回归修复：ConnectWithPassword 是同步 wlanapi（含 W5 的认证探测 + 最多两次
        // WlanSetProfile/DPAPI），适配器忙时可达数秒——必须在后台执行，否则 UI 线程冻结（剧烈卡顿根因）。
        // G5：连接期间按钮保持禁用直到轮询结束（防轮询期间再次点击并发写 profile）。
        if (_connectBtn is not null)
        {
            _connectBtn.IsEnabled = false;
        }
        bool ok;
        try
        {
            ok = await System.Threading.Tasks.Task.Run(() => Services.WifiEnumerator.ConnectWithPassword(_ssid, pwd));
        }
        catch (Exception ex)
        {
            BetterDesktop.Kernel.Core.DiagnosticLog.Trace("menu-bar.wifi", $"ConnectWithPassword 异常（{_ssid}）: {ex.Message}");
            ok = false;
        }

        if (!ok)
        {
            ShowError("连接请求失败，请检查密码或网络状态后重试。");
            ResetPasswordForRetry();
            if (_connectBtn is not null)
            {
                _connectBtn.IsEnabled = true;
            }
            return;
        }

        // 连接请求已发出：统一等待器确认连接成功后才关闭；失败/超时则清理错误 profile。
        _ = PollConnectionAsync();
    }

    /// <summary>连接失败后清空密码框并聚焦，方便用户直接重试。</summary>
    private void ResetPasswordForRetry()
    {
        if (_passwordBox is not null)
        {
            _passwordBox.Password = string.Empty;
            _passwordBox.Visibility = Visibility.Visible;
        }
        if (_plainBox is not null)
        {
            _plainBox.Text = string.Empty;
            _plainBox.Visibility = Visibility.Collapsed;
        }
        _passwordBox?.Focus();
    }

    private void RestoreConnectButton()
    {
        if (_connectBtn is not null)
        {
            _connectBtn.IsEnabled = true;
        }
    }

    /// <summary>S4：窗口关闭（取消/X/成功后 Close）置位取消标志——轮询停止触碰 UI，但后台清理仍执行。</summary>
    protected override void OnClosed(EventArgs e)
    {
        _pollCancelled = true;
        base.OnClosed(e);
    }

    /// <summary>
    /// 统一等待连接结果（G4：走 WifiEnumerator.WaitForConnectionAsync，含 SSID 核对）。
    /// 成功：回调 + 关窗；失败/超时：后台清理错误 profile（先断开再删，connectionMode=auto 红线）+ 报错。
    /// 窗口已关闭（_pollCancelled）时只做清理，不触碰任何 UI（S4）。
    /// </summary>
    private async System.Threading.Tasks.Task PollConnectionAsync()
    {
        try
        {
            bool connected = await Services.WifiEnumerator.WaitForConnectionAsync(_ssid).ConfigureAwait(false);
            if (!connected)
            {
                await System.Threading.Tasks.Task.Run(() => Services.WifiEnumerator.CleanupFailedConnection(_ssid)).ConfigureAwait(false);
            }
            if (_pollCancelled)
            {
                return; // 窗口已关：清理已做，UI 交互全部跳过
            }
            await Dispatcher.InvokeAsync(() =>
            {
                if (connected)
                {
                    _onResult(true);
                    Close();
                }
                else
                {
                    ShowError("无法连接到此网络，密码可能错误或信号不佳，请重试。");
                    ResetPasswordForRetry();
                    RestoreConnectButton();
                }
            });
        }
        catch (Exception ex)
        {
            BetterDesktop.Kernel.Core.DiagnosticLog.Trace("menu-bar.wifi", $"连接轮询异常（{_ssid}）: {ex.Message}");
            // 兜底清理：防错误 profile 残留导致系统反复重连
            try { await System.Threading.Tasks.Task.Run(() => Services.WifiEnumerator.CleanupFailedConnection(_ssid)).ConfigureAwait(false); }
            catch (Exception ex2) { BetterDesktop.Kernel.Core.DiagnosticLog.Trace("menu-bar.wifi", $"兜底清理失败（{_ssid}）: {ex2.Message}"); }
            if (!_pollCancelled)
            {
                await Dispatcher.InvokeAsync(RestoreConnectButton);
            }
        }
    }

    private void ShowError(string message)
    {
        if (_errorText is not null)
        {
            _errorText.Text = message;
            _errorText.Visibility = Visibility.Visible;
        }
    }
}
