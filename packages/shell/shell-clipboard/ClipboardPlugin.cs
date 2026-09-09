using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Clipboard.Contracts;
using BetterDesktop.Shell.Core;
using BetterDesktop.Shell.Core.Contracts;
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
    private const string CapacityKey = "extensions.clipboard-history.capacity";
    private const string PinnedLimitKey = "extensions.clipboard-history.pinned-limit";
    private const string MaxImageMbKey = "extensions.clipboard-history.max-image-mb";
    private const string MaxTotalImageMbKey = "extensions.clipboard-history.max-total-image-mb";
    private const string RetentionDaysKey = "extensions.clipboard-history.retention-days";

    private static readonly string[] WatchKeys =
    {
        EnabledKey, CapacityKey, PinnedLimitKey, MaxImageMbKey, MaxTotalImageMbKey, RetentionDaysKey,
    };

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

        // G6/K5 配置化：向设置中心贡献"剪贴板"分区（容量/收藏/图片预算/保留天数）。
        context.Get<ISettingsSectionRegistry>()?.Register(new Sections.ClipboardSection());

        // I6 菜单栏按钮：右区「📋」直开面板（轻量扩展，不重复实现面板）。
        context.Get<IMenuBarExtensionRegistry>()?.Register(new ClipboardMenuBarExtension(_manager));

        // I10 系统右键「剪贴板历史…」：4 场景 HKCU 注册（幂等避让），命令经宿主单实例管道转发。
        ClipboardShellMenuRegistrar.EnsureRegistered();

        if (_settings is not null)
        {
            _settingsSub = context.Effect(() => context.Events.On<SettingsChangedEventArgs>(
                ShellEvents.SettingsChanged,
                (e, _) =>
                {
                    if (Array.IndexOf(WatchKeys, e.Key) >= 0)
                    {
                        ApplyConfiguration();
                        if (string.Equals(e.Key, EnabledKey, StringComparison.Ordinal))
                        {
                            ApplyEnabledState();
                        }
                    }

                    return Task.CompletedTask;
                }));
            ApplyConfiguration();
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

    /// <summary>读取容量/过期等运行时配置（G6/K5 配置化；键缺省时保持 Manager 默认）。</summary>
    private void ApplyConfiguration()
    {
        var s = _settings;
        var m = _manager;
        if (s is null || m is null)
        {
            return;
        }

        int capacity = s.Get(CapacityKey, 0);
        int pinned = s.Get(PinnedLimitKey, 0);
        int imageMb = s.Get(MaxImageMbKey, 0);
        int totalImageMb = s.Get(MaxTotalImageMbKey, 0);
        int retentionDays = s.Get(RetentionDaysKey, 0);

        m.ApplyRuntimeSettings(
            capacity: capacity > 0 ? capacity : null,
            pinnedLimit: pinned > 0 ? pinned : null,
            maxImageBytes: imageMb > 0 ? (long)imageMb * 1024 * 1024 : null,
            maxTotalImageBytes: totalImageMb > 0 ? (long)totalImageMb * 1024 * 1024 : null,
            expirationAge: retentionDays > 0 ? TimeSpan.FromDays(retentionDays) : null);
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
                _manager.CloseHistoryWindow();
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
