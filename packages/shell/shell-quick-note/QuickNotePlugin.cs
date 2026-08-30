// BetterDesktop.Shell.QuickNote — 外部扩展功能插件（IPlugin）
// 角色：`shell.quick-note` — 一键便签速记。
// 与「扩展中心」的关系：扩展中心只负责把开关意图持久化到 ISettingsService
// （extensions.quick-note.enabled）；本插件是该键的**观察者**：
//   - LoadAsync 时按当前键位决定是否激活浮窗；
//   - 订阅 ISettingsService.Changed，用户在任何地方（含扩展中心）切换开关即实时启停；
//   - 因此扩展中心从「记忆开关」变成「真正启停」的端到端闭环。

using System;
using System.Threading;
using System.Windows;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Core;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.QuickNote;

/// <summary>快速笔记插件：由扩展中心开关（extensions.quick-note.enabled）驱动启停的常驻浮窗。</summary>
public sealed class QuickNotePlugin : IPlugin
{
    public string Name => "shell.quick-note";
    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    private ISettingsService? _settings;
    private IAppearanceService? _appearance;
    private IVibrancyService? _vibrancy;
    private QuickNoteLauncher? _launcher;
    private QuickNoteWindow? _note;
    private bool _subscribed;

    private const string EnabledKey = "extensions.quick-note.enabled";
    private const string TextKey = "extensions.quick-note.text";

    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        // 依赖可能为 null（M10 降级）：本插件仅消费，缺失时静默不激活，不抛。
        _settings = context.Get<ISettingsService>();
        _appearance = context.Get<IAppearanceService>();
        _vibrancy = context.Get<IVibrancyService>() ?? new NullVibrancy();

        if (_settings is not null)
        {
            _settings.Changed += OnSettingsChanged;
            _subscribed = true;
            // 启动即按持久化意图决定浮窗是否存在（默认启用，与扩展中心显示一致）。
            if (_settings.Get(EnabledKey, true) == true)
            {
                Activate();
            }
        }

        context.Logger.Info($"{Name} 已加载：扩展中心开关 observer 就绪 (enabled={_settings?.Get(EnabledKey, true) == true})");
        return Task.CompletedTask;
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        if (_subscribed && _settings is not null)
        {
            _settings.Changed -= OnSettingsChanged;
        }
        _subscribed = false;

        RunOnUi(() =>
        {
            _note?.Close();
            _note = null;
            _launcher?.Close();
            _launcher = null;
        });
        return Task.CompletedTask;
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (!string.Equals(e.Key, EnabledKey, StringComparison.Ordinal))
        {
            return;
        }
        if (e.Value is bool enabled)
        {
            RunOnUi(enabled ? Activate : Deactivate);
        }
    }

    private void Activate()
    {
        RunOnUi(() =>
        {
            if (_launcher is not null)
            {
                return; // 已激活，幂等
            }
            _launcher = new QuickNoteLauncher(_appearance, _vibrancy);
            _launcher.NoteToggle += ToggleNote;
            _launcher.RequestDisable += DisableSelf;
            _launcher.Show();
        });
    }

    private void Deactivate()
    {
        RunOnUi(() =>
        {
            _note?.Hide();
            _note = null;
            _launcher?.Close();
            _launcher = null;
        });
    }

    private void ToggleNote()
    {
        RunOnUi(() =>
        {
            if (_note is null)
            {
                _note = new QuickNoteWindow(_appearance, _vibrancy, _settings);
                _note.Show();
            }
            else if (_note.IsVisible)
            {
                _note.Hide();
            }
            else
            {
                _note.Show();
            }
        });
    }

    /// <summary>由 launcher 右键触发：把扩展中心开关置 false，经 Changed 回到 Deactivate，闭环一致。</summary>
    private void DisableSelf()
    {
        _settings?.Set(EnabledKey, false);
    }

    /// <summary>统一把窗口创建/销毁marshal到 WPF UI 线程（LoadAsync 可能因 ConfigureAwait(false) 不在 UI 线程）。</summary>
    private static void RunOnUi(Action action)
    {
        var app = Application.Current;
        if (app?.Dispatcher is null)
        {
            return;
        }
        if (app.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            app.Dispatcher.Invoke(action);
        }
    }
}

/// <summary>兜底：IVibrancyService 不存在时降级为空壳（窗口不变毛玻璃，但不崩溃）。</summary>
internal sealed class NullVibrancy : IVibrancyService
{
    public void Apply(IntPtr hwnd, VibrancyStyle style, bool roundCorners, bool smallRadius) { }
    public void Disable(IntPtr hwnd) { }
}
