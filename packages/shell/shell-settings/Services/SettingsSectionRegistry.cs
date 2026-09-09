using System.Collections.Generic;
using System.Linq;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Settings.Services;

/// <summary>
/// 设置分区注册表实现：线程安全收集，标题去重（后注册覆盖）。
/// </summary>
public sealed class SettingsSectionRegistry : ISettingsSectionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ISettingsSection> _sections = new();

    /// <inheritdoc />
    public event System.EventHandler? SectionsChanged;

    /// <inheritdoc />
    public void Register(ISettingsSection section)
    {
        if (section is null)
        {
            return;
        }

        bool changed;
        lock (_gate)
        {
            changed = !_sections.ContainsKey(section.Title);
            _sections[section.Title] = section;
        }

        // 仅在确实新增（而非覆盖同名）时通知，避免无谓刷新。
        if (changed)
        {
            SectionsChanged?.Invoke(this, System.EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ISettingsSection> Sections
    {
        get
        {
            lock (_gate)
            {
                return _sections.Values.ToList();
            }
        }
    }
}
