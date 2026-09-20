using System;
using System.Windows.Forms;

namespace BetterDesktop.Shell.Capture;

/// <summary>托盘图标（截图/退出）。WinForms NotifyIcon（仓库已有先例：tray/ 与 BetterDesktop.Cli）。</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Action _onCapture;
    private readonly Action _onExit;

    public TrayIcon(Action onCapture, Action onExit)
    {
        _onCapture = onCapture;
        _onExit = onExit;
        _icon = new NotifyIcon
        {
            Text = "BetterDesktop 截图",
            Visible = true,
        };
        _icon.Icon = LoadIcon();

        var menu = new ContextMenuStrip();
        menu.Items.Add("截图", null, (_, _) => _onCapture());
        menu.Items.Add(new ToolStripSeparator());
        // 贴图默认置顶：用户可在托盘直接设置（写 settings.json，下次贴图生效）
        var stickerTop = new ToolStripMenuItem("贴图默认置顶")
        {
            CheckOnClick = true,
            Checked = Core.CaptureSettings.StickerTopmost,
        };
        stickerTop.CheckedChanged += (_, _) => Core.CaptureSettings.SetStickerTopmost(stickerTop.Checked);
        menu.Items.Add(stickerTop);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => _onExit());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => _onCapture();
    }

    public void ShowBalloon(string title, string text, int timeoutMs)
    {
        try
        {
            _icon.ShowBalloonTip(timeoutMs, title, text, ToolTipIcon.Info);
        }
        catch
        {
            // 通知区域不可用等场景静默
        }
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream(
                "BetterDesktop.Shell.Capture.Assets.capture.ico");
            if (stream is not null)
            {
                return new System.Drawing.Icon(stream);
            }
        }
        catch
        {
            // 回退到系统图标
        }
        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
