using System.Collections.Generic;

namespace BetterDesktop.Shell.Settings.Contracts;

/// <summary>
/// 设置分区注册表（内核同类型服务只存单实例，故分区用注册表收集而非多次 Provide）。
/// 由 settings 插件 Provide；各插件在 LoadAsync 里经
/// <c>context.Get&lt;ISettingsSectionRegistry&gt;()?.Register(new MySection(...))</c> 贡献分区。
/// </summary>
public interface ISettingsSectionRegistry
{
    /// <summary>注册一个设置分区；标题重复时覆盖（后注册生效）。</summary>
    void Register(ISettingsSection section);

    /// <summary>当前已注册分区的只读快照（注册顺序）。</summary>
    IReadOnlyList<ISettingsSection> Sections { get; }

    /// <summary>
    /// 分区集合发生变化（新增/覆盖）时触发。设置窗口订阅此事件以增量刷新左侧导航栏，
    /// 使在窗口已打开后才注册的分区也能即时出现，不依赖插件加载时序。
    /// </summary>
    event System.EventHandler? SectionsChanged;
}
