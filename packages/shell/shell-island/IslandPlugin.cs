// BetterDesktop.Shell.Island — 灵动岛插件（装配 + 生命周期）
//
// 角色：壳层视觉表面（活动呈现面）。归属见 docs/2026-09-11-resident-architecture.md：
// 岛不产生数据、只做"活动的呈现 + 仲裁消费"，因此随壳启动、随壳退出。
//
// 装配顺序（host/cordis.yml）：排在 clipboard-history 之后 —— 岛的三个来源分别是
// status 的 IMediaPlaybackService、convert 的四个事件、clipboard-history 的 IClipboardService。
// 任一来源缺失只降级该来源（M10），岛本体照常工作。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BetterDesktop.Activity.Contracts;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Clipboard.Contracts;
using BetterDesktop.Shell.Core;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Island.Rendering;
using BetterDesktop.Shell.Island.Sections;
using BetterDesktop.Shell.Island.Services;
using BetterDesktop.Shell.Island.Sources;
using BetterDesktop.Shell.Island.Windows;
using BetterDesktop.Shell.Music.Contracts;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Island;

/// <summary>灵动岛插件：装配岛表面并把既有消息源接到活动服务上。</summary>
public sealed class IslandPlugin : IPlugin
{
    private IKernelLogger? _logger;
    private ISettingsService? _settings;
    private IActivityService? _activity;
    private IEventBus? _events;
    private IVibrancyService? _vibrancy;
    private IAppearanceService? _appearance;
    private IMediaPlaybackService? _media;

    private IslandWindow? _window;
    private IslandController? _controller;
    private IslandDemoSequence? _demo;
    private PasteSessionActivitySource? _pasteSessionSource;
    private ConvertActivitySource? _convertSource;
    private MediaActivitySource? _mediaSource;
    private IslandOptions _options = new(
        Enabled: true,
        Tier: IslandMotionTier.Full,
        HoverExpand: true,
        IdleVisible: false,
        PasteSessionSource: true,
        ConvertSource: true,
        MediaSource: true,
        OffsetX: 0,
        OffsetY: 0);

    /// <inheritdoc />
    public string Name => "shell.island";

    /// <inheritdoc />
    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    /// <inheritdoc />
    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        _logger = context.Logger;
        _settings = context.Get<ISettingsService>();
        _activity = context.Get<IActivityService>();
        _events = context.Events;
        _vibrancy = context.Get<IVibrancyService>();
        _appearance = context.Get<IAppearanceService>();
        _media = context.Get<IMediaPlaybackService>();
        _options = IslandOptions.Read(_settings);

        // 设置分区：无论岛是否启用都注册（用户要能从设置里把它打开）
        context.Get<ISettingsSectionRegistry>()?.Register(new IslandSection(OnSettingsChanged));

        // 设置热更新：island.* 任一键变化 → 重读选项并即时落到运行中的表面
        context.Effect(() => context.Events.On<SettingsChangedEventArgs>(
            ShellEvents.SettingsChanged,
            (e, _) =>
            {
                if (e.Key.StartsWith("island.", StringComparison.Ordinal))
                {
                    OnSettingsChanged();
                }

                return Task.CompletedTask;
            }));

        if (_activity is null)
        {
            _logger?.Warn($"{Name}: IActivityService 未入图（shell-core 未加载？）——灵动岛不启动（M10 降级）");
            return Task.CompletedTask;
        }

        RunOnUi(Activate);

        // 开发期动画走查（仅 BD_ISLAND_DEMO 存在时；生产零副作用）
        if (IslandDemoSequence.Enabled && _activity is not null)
        {
            _demo = new IslandDemoSequence(_activity, _logger);
            _demo.Start();
            _logger?.Info($"{Name}: 已启动动画走查（BD_ISLAND_DEMO）——按 [demo] stage= 日志对齐形态");
        }

        _logger?.Info(
            $"{Name} 已加载：按序粘贴源={(_events is null ? "缺失" : "就绪")}、"
            + $"媒体源={(_media is null ? "缺失" : "就绪")}、转换源={(_events is null ? "缺失" : "就绪")}、"
            + $"启用={_options.Enabled}、动效={IslandOptions.TierText(_options.Tier)}");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        RunOnUi(Teardown);
        _logger?.Info($"{Name}: 已卸载（岛窗口与全部来源订阅已释放）");
        return Task.CompletedTask;
    }

    // ===================== 内部 =====================

    /// <summary>按当前选项起停（幂等）：禁用时拆除窗口与来源，启用时重建。</summary>
    private void Activate()
    {
        if (_activity is null)
        {
            return;
        }

        if (!_options.Enabled)
        {
            Teardown();
            return;
        }

        if (Application.Current is null)
        {
            _logger?.Warn($"{Name}: 无 WPF Application（无头环境）——灵动岛不创建窗口");
            return;
        }

        if (_window is null)
        {
            _window = new IslandWindow(
                _vibrancy ?? NullVibrancyService.Instance,
                _appearance,
                _events,
                _options,
                _logger);
            _window.Show();
            _logger?.Info($"{Name}: 岛窗口已创建（无焦点浮窗 + 轮廓命中测试 + 自绘液态轮廓）");
        }
        else
        {
            _window.ApplyOptions(_options);
        }

        if (_controller is null && _events is not null)
        {
            _controller = new IslandController(_activity, _window, _events, _options, _logger);
            _controller.Start();
        }
        else
        {
            _controller?.ApplyOptions(_options);
        }

        _pasteSessionSource ??= _events is null ? null : new PasteSessionActivitySource(_events, _activity, _logger);
        _convertSource ??= _events is null ? null : new ConvertActivitySource(_events, _activity, _logger);
        _mediaSource ??= _media is null ? null : new MediaActivitySource(_media, _activity, _logger);

        ApplySourceToggles();
    }

    /// <summary>来源开关：只影响"是否订阅"，不影响功能本身（例如关掉会话来源后按序粘贴照旧，只是不上岛）。</summary>
    private void ApplySourceToggles()
    {
        ToggleSource(_pasteSessionSource, _options.PasteSessionSource);
        ToggleSource(_convertSource, _options.ConvertSource);
        ToggleSource(_mediaSource, _options.MediaSource);
    }

    private static void ToggleSource(IActivitySource? source, bool enabled)
    {
        if (source is null)
        {
            return;
        }

        if (enabled)
        {
            source.Start();
        }
        else
        {
            source.Stop();
        }
    }

    private void Teardown()
    {
        _demo?.Dispose();
        _demo = null;

        _controller?.Dispose();
        _controller = null;

        _pasteSessionSource?.Dispose();
        _pasteSessionSource = null;
        _convertSource?.Dispose();
        _convertSource = null;
        _mediaSource?.Dispose();
        _mediaSource = null;

        _window?.Close();
        _window = null;
    }

    private void OnSettingsChanged()
    {
        _options = IslandOptions.Read(_settings);
        RunOnUi(Activate);
    }

    /// <summary>统一把窗口/定时器创建销毁 marshal 到 WPF UI 线程（LoadAsync 不保证在 UI 线程）。</summary>
    private void RunOnUi(Action action)
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
