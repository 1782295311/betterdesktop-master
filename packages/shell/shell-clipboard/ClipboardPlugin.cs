using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Clipboard.Contracts;
using BetterDesktop.Shell.Core;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Clipboard;

/// <summary>
/// 剪贴板历史扩展插件（quick-note 观察者模式范式）。
/// 注册点：host/Bootstrap.cs「clipboard-history」；cordis id/name：clipboard-history；程序集内 Name：shell.clipboard。
/// 职责：把 <see cref="ClipboardManager"/> 作为 <see cref="IClipboardService"/> Provide 到内核服务图；
/// 设置键 extensions.clipboard-history.enabled 控制监听活性（服务本体常驻，开关只控制 Start/Stop）。
/// </summary>
public sealed class ClipboardPlugin : IPlugin
{
    public string Name => "shell.clipboard";
    private const string EnabledKey = "extensions.clipboard-history.enabled";

    private readonly object _sync = new();
    private ISettingsService? _settings;
    private ClipboardManager? _manager;
    private IDisposable? _provideHandle;
    private IDisposable? _settingsSub;
    private bool _activated;

    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        _settings = context.Get<ISettingsService>();
        _manager = new ClipboardManager(context.Logger);

        // 服务注册托管清理：插件卸载时自动撤销（Effect 生命周期）。
        _provideHandle = context.Effect(() => context.Provide<IClipboardService>(_manager));

        if (_settings is not null)
        {
            _settingsSub = context.Effect(() => context.Events.On<SettingsChangedEventArgs>(
                ShellEvents.SettingsChanged,
                (e, _) =>
                {
                    if (string.Equals(e.Key, EnabledKey, StringComparison.Ordinal))
                    {
                        ApplyEnabledState();
                    }

                    return Task.CompletedTask;
                }));
            ApplyEnabledState();
        }

        bool enabledInitial = _settings?.Get(EnabledKey, false) ?? false;
        context.Logger.Info($"{Name} 已加载：IClipboardService 已注册（{EnabledKey}={enabledInitial}）");
        return Task.CompletedTask;
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        Deactivate();
        _settingsSub?.Dispose();
        _settingsSub = null;
        _provideHandle?.Dispose();
        _provideHandle = null;
        _manager = null;
        _settings = null;
        return Task.CompletedTask;
    }

    private void ApplyEnabledState()
    {
        bool enabled = _settings?.Get(EnabledKey, false) ?? false;
        if (enabled)
        {
            Activate();
        }
        else
        {
            Deactivate();
        }
    }

    private void Activate()
    {
        RunOnUi(() =>
        {
            lock (_sync)
            {
                if (_activated || _manager is null)
                {
                    return;
                }

                _manager.Start();
                _activated = true;
            }
        });
    }

    private void Deactivate()
    {
        RunOnUi(() =>
        {
            lock (_sync)
            {
                if (!_activated || _manager is null)
                {
                    return;
                }

                _manager.Stop();
                _activated = false;
            }
        });
    }

    /// <summary>统一把监听窗口创建/销毁 marshal 到 WPF UI 线程（LoadAsync 可能因 ConfigureAwait(false) 不在 UI 线程，否则 HwndSource 收不到 WM_CLIPBOARDUPDATE）。</summary>
    private static void RunOnUi(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher is null)
        {
            action();
        }
        else
        {
            app.Dispatcher.Invoke(action);
        }
    }
}
