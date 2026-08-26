using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.Settings.Sections;
using BetterDesktop.Shell.Settings.Services;

namespace BetterDesktop.Shell.Settings;

/// <summary>
/// 设置中心插件：Provide 设置服务 / 分区注册表 / 设置窗口服务。
/// 各插件在 LoadAsync 里经 <see cref="ISettingsSectionRegistry.Register"/> 贡献自己的设置分区。
/// </summary>
public sealed class SettingsPlugin : IPlugin
{
    /// <inheritdoc />
    public string Name => "shell.settings";

    /// <inheritdoc />
    public IReadOnlyList<Type> Inject => Array.Empty<Type>();

    /// <inheritdoc />
    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        // 复用 Bootstrap 提前 Provide 的实例（保证 Dock 等插件能可靠取到设置），不重复创建。
        // 生命周期说明：SettingsService 自注册 AppDomain.ProcessExit 兜底 flush/释放，
        // 此处不手动 Dispose（分析器 CA2000 压制）：
#pragma warning disable CA2000
        var settings = context.Get<ISettingsService>() ?? new SettingsService();
#pragma warning restore CA2000
        var registry = context.Get<ISettingsSectionRegistry>() ?? new SettingsSectionRegistry();
        // 全局外观服务：同时实现 IThemeTokens（兼容现有消费者）与 IAppearanceService（主题板块/皮肤）。
        var appearance = new AppearanceService(settings);
        // 启动即把已保存的外观模式（控件画刷 + 主题令牌画刷）推入 App 资源，首屏即应用上次模式。
        appearance.Initialize();
        // 跟随统一窗口基类：注入毛玻璃服务，使设置窗口获得与壳面一致的材质外观。
        var vibrancy = context.Get<IVibrancyService>();

        // 绑定内核桥接：让设置分区（仅接 ISettingsService/IThemeTokens）能取回 IHmrManager
        // 以即时改内存治理阈值（设置界面→治理器，不重建治理器生命周期）。
        SettingsKernelBridge.Bind(context);

        // 设置主分区（合并原"通用"+"系统管理"）：桌面环境自身设置项（常规/启动/性能/维护/组件）。
        // 主题分区：统一管控程序一切外观（强调色/透明度/毛玻璃/字号/圆角/皮肤）。
        // 导航栏显示两个板块；后续其他插件可经 ISettingsSectionRegistry.Register 贡献独立分区。
        registry.Register(new SystemSection());
        registry.Register(new ThemeSection());
        registry.Register(new LeftDockSection());

        context.Provide<ISettingsService>(settings);
        context.Provide<ISettingsSectionRegistry>(registry);
        context.Provide<IThemeTokens>(appearance);
        context.Provide<IAppearanceService>(appearance);
        context.Provide<ISettingsWindowService>(new SettingsWindowService(
            () => new SettingsWindow(registry, settings, appearance, vibrancy)));

        // 启动即打开设置窗口（与应用提取器一致的启动行为；后续改为配置开关/菜单入口）
        context.Get<ISettingsWindowService>()?.Show();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
