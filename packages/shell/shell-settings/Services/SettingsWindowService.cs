using System;
using System.Windows;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Settings.Services;

/// <summary>
/// 设置窗口服务实现：单实例管理，Show/ShowSection 幂等（已开则激活/切分区）。
/// </summary>
public sealed class SettingsWindowService : ISettingsWindowService
{
    private readonly Func<SettingsWindow> _windowFactory;
    private SettingsWindow? _window;

    public SettingsWindowService(Func<SettingsWindow> windowFactory)
    {
        _windowFactory = windowFactory;
    }

    /// <inheritdoc />
    public void Show()
    {
        ShowSection(null);
    }

    /// <inheritdoc />
    public void ShowSection(string? sectionTitle)
    {
        if (_window is null)
        {
            _window = _windowFactory();
            _window.Closed += (_, _) => _window = null;
        }

        _window.SelectSection(sectionTitle);
        if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }
}
