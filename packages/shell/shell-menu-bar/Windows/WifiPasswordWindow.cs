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
    private readonly string _ssid;
    private readonly Action<bool> _onResult; // true=连接请求已发出，false=取消/失败
    private PasswordBox? _passwordBox;
    private TextBox? _plainBox;
    private TextBlock? _errorText;

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
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6A, 0x6A)),
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
        connectBtn.Click += OnConnect;
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(connectBtn);
        col.Children.Add(btnRow);

        root.Child = col;
        return root;
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        // 当前显示明文 TextBox 时读明文，否则读 PasswordBox 密文
        bool plainVisible = _plainBox is { Visibility: Visibility.Visible };
        string pwd = plainVisible ? (_plainBox?.Text ?? "") : (_passwordBox?.Password ?? "");

        if (string.IsNullOrEmpty(pwd))
        {
            ShowError("请输入 WiFi 密码。");
            return;
        }

        bool ok = Services.WifiEnumerator.ConnectWithPassword(_ssid, pwd);
        if (!ok)
        {
            ShowError("连接请求失败，请检查密码或网络状态后重试。");
            ResetPasswordForRetry();
            return;
        }

        // 连接请求已发出：轮询接口状态，确认连接成功后才关闭；
        // 失败/超时则删除错误 profile 并显示错误，避免系统反复重试导致适配器繁忙。
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

    /// <summary>轮询无线接口状态：最多 10 秒（密码正确连接很快，超时即判定失败），每 1 秒查一次。
    /// 状态 1=已连接，0/6=连接失败。失败/超时后先 WlanDisconnect 释放适配器再删 profile，保证列表不崩溃、下次点击秒弹密码窗。</summary>
    private async System.Threading.Tasks.Task PollConnectionAsync()
    {
        for (int i = 0; i < 10; i++)
        {
            await System.Threading.Tasks.Task.Delay(1000);
            int state = Services.WifiEnumerator.GetInterfaceState();
            if (state == 1) // Connected
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _onResult(true);
                    Close();
                });
                return;
            }
            if (state == 0 || state == 6) // Disconnected / AdHocNetworkFormed = 系统已反馈连接失败
            {
                // 先断开释放适配器，再删除错误 profile，保证 WiFi 列表不崩溃、下次点击秒弹
                await System.Threading.Tasks.Task.Run(() =>
                {
                    Services.WifiEnumerator.Disconnect();
                    Services.WifiEnumerator.DeleteProfile(_ssid);
                });
                await Dispatcher.InvokeAsync(() =>
                {
                    ShowError("无法连接到此网络，密码可能错误，请重试。");
                    ResetPasswordForRetry();
                });
                return;
            }
            // 其他状态（关联中/搜索中/认证中/漫游中/断开中）继续等待系统反馈
        }
        // 超时（30 秒系统仍未反馈）：主动断开释放适配器 + 删 profile
        await System.Threading.Tasks.Task.Run(() =>
        {
            Services.WifiEnumerator.Disconnect();
            Services.WifiEnumerator.DeleteProfile(_ssid);
        });
        await Dispatcher.InvokeAsync(() =>
        {
            ShowError("连接超时，请检查密码或网络信号后重试。");
            ResetPasswordForRetry();
        });
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
