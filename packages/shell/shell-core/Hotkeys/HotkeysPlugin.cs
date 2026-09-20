using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Hotkeys.Contracts;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Core.Hotkeys;

/// <summary>
/// 热键注册表插件：向内核 Provide <see cref="IHotkeyRegistryService"/>（shell 层基础设施）。
/// <para>
/// 注册表是热键侧板、设置中心热键管理、截图热键等一切热键面的唯一真相源；
/// 本插件在宿主（host）进程内提供，其余 shell 包经 <c>context.Get&lt;IHotkeyRegistryService&gt;()</c> 消费。
/// </para>
/// </summary>
public sealed class HotkeysPlugin : IPlugin
{
    /// <inheritdoc />
    public string Name => "hotkeys";

    /// <inheritdoc />
    public IReadOnlyList<Type> Inject => new[] { typeof(ISettingsService) };

    private HotkeyRegistryService? _service;

    /// <inheritdoc />
    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        var settings = context.Get<ISettingsService>() ?? new MemorySettingsService();
        HotkeyRegistryService? svc = null;
        // 中心窗口的分派回调须在服务构造前就绪（窗口先于服务创建）；经闭包延迟解引用，
        // WM_HOTKEY 只会出现在注册之后（此时 svc 已赋值）。
#pragma warning disable CA2000 // 生命周期由 UnloadAsync/宿主退出托管
        var window = new HotkeyCenterWindow(id => svc is not null && svc.DispatchSystemHotkey(id));
#pragma warning restore CA2000
        svc = new HotkeyRegistryService(settings, window);
        _service = svc;
        context.Provide<IHotkeyRegistryService>(svc);
        context.Logger.Info("[hotkeys] 热键注册表已就绪（HWND_MESSAGE 中心窗口 + 单钩子查表）");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        if (_service is not null)
        {
            _service.Dispose();
            _service = null;
        }

        return Task.CompletedTask;
    }

    /// <summary>设置服务缺失时的内存回退（不持久化；正常宿主不会走到）。</summary>
    private sealed class MemorySettingsService : ISettingsService
    {
        private readonly Dictionary<string, object?> _store = new(StringComparer.Ordinal);

        public T? Get<T>(string key, T? defaultValue = default)
            => _store.TryGetValue(key, out var v) && v is T t ? t : defaultValue;

        public void Set<T>(string key, T value) => _store[key] = value;
    }
}
