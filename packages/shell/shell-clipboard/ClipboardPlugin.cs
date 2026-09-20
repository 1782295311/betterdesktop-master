using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Clipboard.Contracts;
using BetterDesktop.Shell.Clipboard.Ipc;
using BetterDesktop.Shell.Core;
using BetterDesktop.Shell.Core.Contracts;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Clipboard;

/// <summary>
/// 剪贴板历史扩展插件（quick-note 观察者模式范式）。
/// 注册点：host/Bootstrap.cs「clipboard-history」；cordis id/name：clipboard-history；程序集内 Name：shell.clipboard。
///
/// **唯一后端 = engine**（2026-09-14 删除 legacy）：宿主只做 IPC 客户端——
/// 数据面（监听/捕获/存储/热键）在独立 Rust 引擎进程，入口面在面板 exe，宿主只持
/// <see cref="ClipboardIpcClient"/> 代理。
///
/// 宿主内实现（legacy `ClipboardManager`）已**整层删除**：它与引擎写同一份
/// %LOCALAPPDATA%\BetterDesktop\clipboard_history.json，而引擎是 800ms 无条件整体重写，
/// 二者并存必然互相覆盖（旧注释把它当"回退"是低估了代价）。
/// </summary>
public sealed class ClipboardPlugin : IPlugin
{
    public string Name => "shell.clipboard";

    private const string EnabledKey = "extensions.clipboard-history.enabled";
    private const string CapacityKey = "extensions.clipboard-history.capacity";
    private const string PinnedLimitKey = "extensions.clipboard-history.pinned-limit";
    private const string MaxImageMbKey = "extensions.clipboard-history.max-image-mb";
    private const string MaxTotalMbKey = "extensions.clipboard-history.max-total-mb";
    private const string MaxTotalImageMbKey = "extensions.clipboard-history.max-total-image-mb";
    private const string RetentionDaysKey = "extensions.clipboard-history.retention-days";
    private const string StorageModeKey = "extensions.clipboard-history.storage-mode";
    private const string FileCopyMaxMbKey = "extensions.clipboard-history.file-copy-max-mb";
    private const string EntryStyleKey = "extensions.clipboard-history.entry-style";
    // 【2026-09-12 补齐】以下 6 项在引擎 Settings 里早已存在（engine/src/settings.rs），
    // 但此前既没进设置界面、也没进 apply_settings patch 与 WatchKeys ——
    // 结果是"设置里改了也传不到引擎"（引擎只在自己启动时读一次 settings.json，运行中靠 apply_settings 热更新）。
    private const string MaxImagePixelsKey = "extensions.clipboard-history.max-image-pixels";
    private const string ThumbWidthKey = "extensions.clipboard-history.thumb-width";
    private const string MaxHtmlImageMbKey = "extensions.clipboard-history.max-html-image-mb";
    private const string MaxTextBytesKey = "extensions.clipboard-history.max-text-bytes";
    private const string MaxContentTotalMbKey = "extensions.clipboard-history.max-content-total-mb";
    private const string PushEventsKey = "extensions.clipboard-history.push-events";

    // 【2026-09-13 TieZ 对标 P1-4/P1-5/P2】8 项新增设置（引擎 settings.rs 已定义，四处同步）：
    // ① P1-5 按应用清洗规则（JSON 数组字符串）② P1-4 命名格式透传（开关 + 三重上限）
    // ③ P2-2 敏感识别（开关 + 遮罩前后可见位数）。
    private const string AppRulesKey = "extensions.clipboard-history.app-rules";
    private const string NamedFormatPassthroughKey = "extensions.clipboard-history.named-format-passthrough";
    private const string NamedFormatMaxCountKey = "extensions.clipboard-history.named-format-max-count";
    private const string NamedFormatMaxKbKey = "extensions.clipboard-history.named-format-max-kb";
    private const string NamedFormatTotalKbKey = "extensions.clipboard-history.named-format-total-kb";
    private const string SensitiveDetectionKey = "extensions.clipboard-history.sensitive-detection";
    private const string SensitiveMaskLeadingKey = "extensions.clipboard-history.sensitive-mask-leading";
    private const string SensitiveMaskTrailingKey = "extensions.clipboard-history.sensitive-mask-trailing";

    private static readonly string[] WatchKeys =
    {
        EnabledKey, CapacityKey, PinnedLimitKey, MaxImageMbKey, MaxTotalMbKey,
        MaxTotalImageMbKey, RetentionDaysKey, StorageModeKey, FileCopyMaxMbKey,
        MaxImagePixelsKey, ThumbWidthKey, MaxHtmlImageMbKey, MaxTextBytesKey,
        MaxContentTotalMbKey, PushEventsKey,
        // 【2026-09-13】P1-4/P1-5/P2 新增
        AppRulesKey, NamedFormatPassthroughKey, NamedFormatMaxCountKey, NamedFormatMaxKbKey,
        NamedFormatTotalKbKey, SensitiveDetectionKey, SensitiveMaskLeadingKey, SensitiveMaskTrailingKey,
    };

    private IKernelLogger? _logger;
    private ISettingsService? _settings;
    private ClipboardIpcClient? _ipcClient;
    private Action? _onReconnected;
    private IDisposable? _provideHandle;
    private IDisposable? _settingsSub;

    /// <summary>生命周期动作的串行锁与任务链（见 <see cref="RunOffUiThread"/>）。</summary>
    private readonly object _lifecycleLock = new();
    private Task _lifecycleChain = Task.CompletedTask;

    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        _logger = context.Logger;
        _settings = context.Get<ISettingsService>();

        // 【2026-09-17 用户拍板 · 开关语义】enabled=false 时**不起引擎、不起面板**（不留进程）；
        // 开启时起引擎 + 起入口 + 顺手打开侧边面板（见 ApplyEnabledState）。
        bool enabledAtLoad = _settings?.Get(EnabledKey, true) ?? true;

        // 引擎 + IPC 代理。宿主不监听、不存储、不建面板。
        if (enabledAtLoad)
        {
            ClipboardEngineLauncher.EnsureEngine(msg => context.Logger.Info($"{Name}: {msg}"));
        }
        else
        {
            context.Logger.Info($"{Name}: {EnabledKey}=false → 不启动引擎与面板（不留进程）");
        }

        // CA2000：transport 生命周期转交 ClipboardIpcClient（其 Dispose 释放 transport）。
#pragma warning disable CA2000
        _ipcClient = new ClipboardIpcClient(new NamedPipeTransport());
#pragma warning restore CA2000
        _ipcClient.Connect();

        _onReconnected = () => ApplyConfiguration();
        _ipcClient.Reconnected += _onReconnected;

        _provideHandle = context.Effect(() => context.Provide<IClipboardService>(_ipcClient!));

        // 【2026-09-18 形态统一】「剪贴板历史」不再走独立的静态注册表项（其语义是"打开面板"，
        // 与其它开关形态不一致，且只出现在经典菜单），改为快照「桌面控制」子菜单里的**功能开关**
        // （带图标 + 勾选框，见 ShellMenuContentBuilder.BuildControlToggles）。
        // 这里改为清理历史静态项（幂等），避免经典菜单与新版菜单各出现一个同名但语义不同的入口。
        // 打开面板的入口仍在：托盘「打开剪贴板历史」+ 侧边手柄 + 全局热键。
        ClipboardShellMenuRegistrar.Unregister();

        // 入口面常驻：面板 exe 承载侧边栏「›」手柄（O5）。宿主启动即拉起，
        // 使"桌面跑起来就有侧边栏"，而不是等用户按热键/点右键才第一次出现。
        // entry-style=off（用户在设置里显式关闭入口）时不拉起。
        string entryStyle = _settings?.Get(EntryStyleKey, "sidebar") ?? "sidebar";
        if (!enabledAtLoad)
        {
            context.Logger.Info($"{Name}: 功能已关闭，不装配面板入口面");
        }
        else if (!string.Equals(entryStyle, "off", StringComparison.OrdinalIgnoreCase))
        {
            ClipboardEngineLauncher.EnsurePanelEntry(msg => context.Logger.Info($"{Name}: {msg}"));
        }
        else
        {
            context.Logger.Info($"{Name}: entry-style=off，不装配面板入口面");
        }

        IClipboardService service = _ipcClient;

        // G6/K5 配置化：向设置中心贡献"剪贴板"分区。
        context.Get<ISettingsSectionRegistry>()?.Register(new Sections.ClipboardSection());

        // I6 菜单栏按钮：右区「📋」直开面板（经契约 → engine 模式下发 open_panel 拉起面板 exe）。
        context.Get<IMenuBarExtensionRegistry>()?.Register(new ClipboardMenuBarExtension(service));

        if (_settings is not null)
        {
            _settingsSub = context.Effect(() => context.Events.On<SettingsChangedEventArgs>(
                ShellEvents.SettingsChanged,
                (e, _) =>
                {
                    // 【2026-09-17 用户拍板】总开关是"生命周期"级动作：起/停进程与入口，见 ApplyEnabledState。
                    // 其余键只推配置（引擎热更新）。
                    if (string.Equals(e.Key, EnabledKey, StringComparison.Ordinal))
                    {
                        ApplyEnabledState(e.Value is bool b ? b : (_settings?.Get(EnabledKey, true) ?? true));
                        return Task.CompletedTask;
                    }

                    if (Array.IndexOf(WatchKeys, e.Key) >= 0)
                    {
                        // 推配置是**同步 IPC**（引擎半死时最坏秒级）：同样不能留在 UI 线程上，见 RunOffUiThread。
                        RunOffUiThread("推送引擎配置", ApplyConfiguration);
                    }
                    return Task.CompletedTask;
                }));
            ApplyConfiguration();
        }

        // 默认开启：剪贴板历史是核心功能，开箱即用；用户可在设置里显式关闭。
        bool enabledInitial = _settings?.Get(EnabledKey, true) ?? true;
        context.Logger.Info(
            $"{Name} 已加载：IClipboardService 经引擎 IPC 注册（{EnabledKey}={enabledInitial}）");
        return Task.CompletedTask;
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        _settingsSub?.Dispose();
        _settingsSub = null;
        _provideHandle?.Dispose();
        _provideHandle = null;

        if (_ipcClient is not null)
        {
            if (_onReconnected is not null)
            {
                _ipcClient.Reconnected -= _onReconnected;
                _onReconnected = null;
            }
            // 注意：引擎是独立常驻进程，宿主卸载不关停引擎（这是常驻架构的设计目标）。
            _ipcClient.Dispose();
            _ipcClient = null;
        }

        _settings = null;
        _logger = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 剪贴板历史的"开关"落地（<c>extensions.clipboard-history.enabled</c> 变化时）。
    /// <para>
    /// 【2026-09-17 用户拍板】开关必须管到**进程与入口**，不能只推配置：
    ///   · 开 → 起引擎 + 按 entry-style 起面板入口 + **顺手打开侧边面板**（原话："开启时顺便打开侧边面板"）；
    ///   · 关 → 停面板（连带侧边入口手柄）+ 停引擎，**不留进程**（原话："关闭时把侧边的入口也关掉，
    ///     功能也关掉，不留进程了"）。
    /// </para>
    /// <para>
    /// 关键约束（用户原话"但是下一次开启时要可以正常工作"）：关闭必须是"进程退场"而不是"标记停用"，
    /// 且**不残留半开状态**；再次开启走的是与宿主启动完全相同的 Ensure* 路径，因此行为一致。
    /// </para>
    /// <para>看门狗侧另有 StopWhenDisabled 兜底（用于"关掉时进程恰好不在守护视野内"等漏杀场景）。</para>
    /// </summary>
    private void ApplyEnabledState(bool enabled)
        => RunOffUiThread(
            enabled ? "启用剪贴板历史" : "停用剪贴板历史",
            () => ApplyEnabledStateCore(enabled));

    /// <summary>
    /// 把"启停外部进程 / 同步 IPC"这类与 UI 无关的重活挪出 **UI 线程**，并**串行**执行。
    /// <para>
    /// 【2026-09-18 真机："点功能开关时 BetterDesktop.Host 未响应"】
    /// 设置变更事件在宿主 **UI 线程**上派发（命令桥与设置中心都在 UI 线程改设置），而下游动作是：
    ///   · <c>StopAll()</c> → 逐进程 Kill + <c>WaitForExit(3000)</c>（面板 + 引擎，最坏数秒）
    ///   · <c>EnsureEngine / EnsurePanelEntry / OpenPanel</c> → Process.Start
    ///   · <c>ApplySettings()</c> → 同步 IPC 往返
    /// 于是每点一次开关，界面就被钉住，Windows 直接弹"未响应"，用户看到的是"点一下卡半天"。
    /// </para>
    /// <para>
    /// 为什么用**任务链**而不是各自 Task.Run：顺序即语义 —— "先关再开"必须按序落地，
    /// 并发执行会留下"关了又开一半"的半开状态（引擎/面板互相抢管道，正是历史上修过的坑）。
    /// </para>
    /// </summary>
    private void RunOffUiThread(string what, Action work)
    {
        lock (_lifecycleLock)
        {
            _lifecycleChain = _lifecycleChain.ContinueWith(
                _ =>
                {
                    try
                    {
                        work();
                    }
                    catch (Exception ex)
                    {
                        // 关停/拉起失败必须留痕：静默失败会表现为"开关点了没反应"。
                        _logger?.Warn($"{Name}: {what} 失败：{ex.Message}");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    private void ApplyEnabledStateCore(bool enabled)
    {
        var log = new Action<string>(msg => _logger?.Info($"{Name}: {msg}"));

        if (!enabled)
        {
            ClipboardEngineLauncher.StopAll(log);
            return;
        }

        // 【2026-09-17 修复"重新开启后没有侧边入口"】先把设置**立即落盘**，再拉起面板：
        // 面板是独立进程，它启动时自己读 settings.json 决定"要不要装配侧边「›」手柄"
        //（扩展中心 enabled=false → 故意不装配）。而 SettingsService 落盘有 debounce，
        // 不 flush 的话面板读到的仍是旧值 false → 手柄不出现（真机面板日志：
        // "[panel] 扩展中心 clipboard-history 未启用（enabled=false），面板不装配入口"）。
        // FlushNow() 忽略 debounce（其本意就是"进程退出前别丢改动"，此处复用为跨进程可见性保证）。
        (_settings as BetterDesktop.Shell.Settings.Services.SettingsService)?.FlushNow();

        ClipboardEngineLauncher.EnsureEngine(log);

        var entryStyle = _settings?.Get(EntryStyleKey, "sidebar") ?? "sidebar";
        if (!string.Equals(entryStyle, "off", StringComparison.OrdinalIgnoreCase))
        {
            ClipboardEngineLauncher.EnsurePanelEntry(log);
        }

        // "开启时顺便打开侧边面板"：用户要的是"开了即可用"，而不是"开了还得自己去找入口"。
        // 注意只在**开关翻转**时打开（LoadAsync 启动路径不打开），否则每次宿主启动都会弹面板。
        ClipboardEngineLauncher.OpenPanel(log);
    }

    /// <summary>把设置变更推给引擎（apply_settings 增量）。</summary>
    private void ApplyConfiguration()
    {
        var s = _settings;
        if (s is null)
        {
            return;
        }

        var client = _ipcClient;
        if (client is null)
        {
            return;
        }

        // 键名 = 引擎 Settings 的 kebab-case 序列化（engine/src/settings.rs）；
        // apply_settings 为部分字段合并，缺失键保持引擎现值。
        // 注意：patch 必须覆盖"设置界面里可改的全部键"，否则用户改了设置界面却不生效
        //（引擎仅在自身启动时读一次 settings.json）。新增设置项时这里要同步补。
        var patch = new Dictionary<string, object?>
        {
            ["enabled"] = s.Get(EnabledKey, true),
            ["capacity"] = s.Get(CapacityKey, 10000),
            ["pinned-limit"] = s.Get(PinnedLimitKey, 200),
            ["retention-days"] = s.Get(RetentionDaysKey, 90),
            ["max-image-mb"] = s.Get(MaxImageMbKey, 64),
            ["max-total-mb"] = s.Get(MaxTotalMbKey, 1024),
            ["file-copy-max-mb"] = s.Get(FileCopyMaxMbKey, 64),
            // 【2026-09-12 补】此前缺失的 6 项
            ["max-image-pixels"] = s.Get(MaxImagePixelsKey, 100_000_000),
            ["thumb-width"] = s.Get(ThumbWidthKey, 480),
            ["max-html-image-mb"] = s.Get(MaxHtmlImageMbKey, 2),
            ["max-text-bytes"] = s.Get(MaxTextBytesKey, 10 * 1024 * 1024),
            ["max-content-total-mb"] = s.Get(MaxContentTotalMbKey, 100),
            ["push-events"] = s.Get(PushEventsKey, true),
            // 【2026-09-13 TieZ 对标 P1-4/P1-5/P2】新增项（缺一即"设置改了引擎收不到"）
            ["app-rules"] = ParseAppRules(s.Get(AppRulesKey, string.Empty)),
            ["named-format-passthrough"] = s.Get(NamedFormatPassthroughKey, true),
            ["named-format-max-count"] = s.Get(NamedFormatMaxCountKey, 8),
            ["named-format-max-kb"] = s.Get(NamedFormatMaxKbKey, 1024),
            ["named-format-total-kb"] = s.Get(NamedFormatTotalKbKey, 4096),
            ["sensitive-detection"] = s.Get(SensitiveDetectionKey, true),
            ["sensitive-mask-leading"] = s.Get(SensitiveMaskLeadingKey, 3),
            ["sensitive-mask-trailing"] = s.Get(SensitiveMaskTrailingKey, 2),
        };
        string storageMode = s.Get(StorageModeKey, "full") ?? "full";
        if (!string.IsNullOrWhiteSpace(storageMode))
        {
            patch["storage-mode"] = storageMode;
        }

        try
        {
            client.ApplySettings(patch);
        }
        catch (ClipboardIpcException ex)
        {
            // 引擎未连接（首次启动竞态/引擎重启中）：Reconnected 回调会重推全量配置。
            _logger?.Warn($"{Name}: 设置推送引擎失败（重连后自动重推）：{ex.Message}");
        }
    }

    /// <summary>
    /// 解析设置里的规则 JSON（非法 JSON → 空规则 + 告警）。
    /// **绝不因规则 JSON 损坏而中止设置推送** —— 其余设置仍须生效，用户改回正常 JSON 即恢复。
    /// </summary>
    private List<ClipboardAppRule> ParseAppRules(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<ClipboardAppRule>();
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<ClipboardAppRule>>(json)
                ?? new List<ClipboardAppRule>();
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger?.Warn($"{Name}: app-rules JSON 非法（{ex.Message}），本次按空规则推送");
            return new List<ClipboardAppRule>();
        }
    }
}
