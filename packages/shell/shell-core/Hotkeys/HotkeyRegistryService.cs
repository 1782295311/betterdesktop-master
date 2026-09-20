using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using BetterDesktop.Shell.Clipboard.Ipc;
using BetterDesktop.Shell.Core.Native;
using BetterDesktop.Shell.Hotkeys.Contracts;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Core.Hotkeys;

/// <summary>
/// 热键注册表服务（shell 层基础设施，全仓热键的**唯一真相源**）。
/// <para>
/// 模型：一条 <see cref="HotkeyBinding"/> 是"谁、什么键位、什么作用域、干什么"的声明；
/// 冲突判定与作用域过滤是纯函数（<see cref="HotkeyConflictPolicy"/>）；系统热键经
/// <see cref="HotkeyCenterWindow"/>（HWND_MESSAGE 中心窗口）统一注册，低级钩子键经
/// 单个 <see cref="KeyboardHook"/> 查表消费。
/// </para>
/// <para>
/// 两个正交维度（红线）：<c>Enabled</c>（停用 = 释放键位）与 <c>Visible</c>（隐藏 = 仅不显示，
/// 功能照常）。隐藏 ≠ 静音：冲突/被占用/被接管/注册失败的项无论是否隐藏都可见。
/// </para>
/// <para>
/// 持久化：<c>settings.json</c> 的 <c>hotkeys.&lt;Id&gt;</c> 键（值 = chord/enabled/visible，
/// 均为可空覆盖），本服务是唯一读写者；默认值在代码的 <c>DefaultChord</c>。
/// </para>
/// </summary>
public sealed class HotkeyRegistryService : IHotkeyRegistryService, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IHotkeyWindow _window;
    private readonly KeyboardHook _hook;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _systemIds = new(StringComparer.Ordinal); // 绑定 Id → 中心窗口热键 id
    private readonly object _gate = new();
    private HashSet<string> _activeScopes = new(StringComparer.Ordinal);
    private int _nextId = 1;
    private bool _disposed;

    /// <summary>
    /// 注册表内容或作用域变化（类级事件，ADR-002 D4：接口不声明跨程序集裸 event；
    /// 侧板/设置中心（P5）跨包消费届时走 IEventBus 桥接，同程序集内可直接订阅）。
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// 构造注册表。回调（onTrigger）在中心窗口泵线程或钩子线程执行——涉及 UI 的操作须自行编队回 UI。
    /// </summary>
    internal HotkeyRegistryService(ISettingsService settings, IHotkeyWindow window)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _hook = new KeyboardHook(OnKeyEvent);
    }

    /// <inheritdoc />
    public RegistrationResult Register(HotkeyBinding binding, Action<HotkeyBinding>? onTrigger = null)
    {
        if (binding is null)
        {
            return new RegistrationResult(false, null, "绑定不能为空");
        }

        if (string.IsNullOrWhiteSpace(binding.Id))
        {
            return new RegistrationResult(false, null, "绑定 Id 不能为空");
        }

        if (binding.Chord is null || string.IsNullOrWhiteSpace(binding.Chord.Spec))
        {
            return new RegistrationResult(false, null, "键位不能为空");
        }

        // 格式非法（无修饰键/双主键/无法解析）在注册期拒绝，不当作"OS 占用"（OsConflict 只留给系统占用）
        if (!HotkeySpec.TryParse(binding.Chord.Spec, out _, out _, out _))
        {
            return new RegistrationResult(false, null, $"键位格式非法（需至少一个修饰键，如 Ctrl+Shift+K）：{binding.Chord.Spec}");
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return new RegistrationResult(false, null, "注册表已释放");
            }

            if (_entries.ContainsKey(binding.Id))
            {
                return new RegistrationResult(false, null, $"绑定 Id 已存在：{binding.Id}");
            }

            // 用户持久化改键优先于代码默认值（重启后保持改键/隐藏/停用）
            var effective = ApplyPersistedOverrides(binding, out var persistedEnabled, out var persistedVisible);

            // 内置键（引擎三枚全局热键：Ctrl+Shift+V/P/Backspace 等，外部进程自注册）——
            // **Register（宿主真注册 OS 键）必须拒绝**：宿主与外部进程双注册必然 OS 冲突（0x581 红线）；
            // Declare 放行（登记展示，不注册键，见 Declare 内注释）。用户改键到内置键由 Rebind 拒绝。
            if (HotkeySpec.ConflictsWithBuiltIn(effective.Chord.Spec))
            {
                return new RegistrationResult(false, null, $"内置热键不可注册：{effective.Chord.Spec}");
            }

            foreach (var existing in _entries.Values)
            {
                if (HotkeyConflictPolicy.IsSameScopeDuplicate(effective, existing.Binding))
                {
                    return new RegistrationResult(false, existing.Binding.Id, $"同作用域已存在同键位绑定：{existing.Binding.Description}");
                }

                if (HotkeyConflictPolicy.IsUndeclaredCrossScopeConflict(effective, existing.Binding))
                {
                    return new RegistrationResult(false, existing.Binding.Id, $"跨作用域同键位需显式声明 Shadows 接管：{existing.Binding.Description}");
                }
            }

            var entry = new Entry(effective, onTrigger)
            {
                Enabled = persistedEnabled ?? true,
                Visible = persistedVisible ?? true,
            };
            _entries[binding.Id] = entry;
            EnsureRegistered(entry);
            RaiseChanged();
            return new RegistrationResult(true, null, null);
        }
    }

    /// <inheritdoc />
    public RegistrationResult Declare(HotkeyBinding binding)
    {
        if (binding is null)
        {
            return new RegistrationResult(false, null, "绑定不能为空");
        }

        if (string.IsNullOrWhiteSpace(binding.Id))
        {
            return new RegistrationResult(false, null, "绑定 Id 不能为空");
        }

        if (binding.Chord is null || string.IsNullOrWhiteSpace(binding.Chord.Spec))
        {
            return new RegistrationResult(false, null, "键位不能为空");
        }

        if (!HotkeySpec.TryParse(binding.Chord.Spec, out _, out _, out _))
        {
            return new RegistrationResult(false, null, $"键位格式非法（需至少一个修饰键，如 Ctrl+Shift+K）：{binding.Chord.Spec}");
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return new RegistrationResult(false, null, "注册表已释放");
            }

            if (_entries.ContainsKey(binding.Id))
            {
                return new RegistrationResult(false, null, $"绑定 Id 已存在：{binding.Id}");
            }

            var effective = ApplyPersistedOverrides(binding, out var persistedEnabled, out var persistedVisible);

            // 内置键（引擎三枚全局热键：Ctrl+Shift+V/P/Backspace）保护在 **Rebind/SetEnabled（用户操作）** 处生效
            // （改键到内置键被拒）；Declare 是"登记展示"（声明条目不注册 OS 键，键位由声明方进程自己注册，0x581）——
            // 引擎三枚内置键的**声明迁移**（HotkeyDeclarations.Build）必须放行登记，否则侧板看不到既有热键。

            foreach (var existing in _entries.Values)
            {
                if (HotkeyConflictPolicy.IsSameScopeDuplicate(effective, existing.Binding))
                {
                    return new RegistrationResult(false, existing.Binding.Id, $"同作用域已存在同键位绑定：{existing.Binding.Description}");
                }

                if (HotkeyConflictPolicy.IsUndeclaredCrossScopeConflict(effective, existing.Binding))
                {
                    return new RegistrationResult(false, existing.Binding.Id, $"跨作用域同键位需显式声明 Shadows 接管：{existing.Binding.Description}");
                }
            }

            _entries[binding.Id] = new Entry(effective, callback: null)
            {
                Enabled = persistedEnabled ?? true,
                Visible = persistedVisible ?? true,
                Declared = true,
            };
            // 声明条目不 EnsureRegistered：键位由声明方进程自己注册，宿主注册必然双占（0x581）。
            RaiseChanged();
            return new RegistrationResult(true, null, null);
        }
    }

    /// <inheritdoc />
    public void Unregister(string id)
    {
        lock (_gate)
        {
            if (!_entries.Remove(id, out var entry))
            {
                return;
            }

            ReleaseRegistered(entry);
            // 持久化改键/隐藏/停用保留：该 Id 重新注册时恢复用户选择（面板"粘贴回"热键随开随关即依赖此语义）
            RaiseChanged();
        }
    }

    /// <inheritdoc />
    public RegistrationResult Rebind(string id, HotkeyChord chord)
    {
        if (chord is null || string.IsNullOrWhiteSpace(chord.Spec))
        {
            return new RegistrationResult(false, null, "键位不能为空");
        }

        if (!HotkeySpec.TryParse(chord.Spec, out _, out _, out _))
        {
            return new RegistrationResult(false, null, $"键位格式非法（需至少一个修饰键，如 Ctrl+Shift+K）：{chord.Spec}");
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                return new RegistrationResult(false, null, $"绑定不存在：{id}");
            }

            var candidate = entry.Binding with { Chord = chord };
            if (HotkeyConflictPolicy.ConflictsWithBuiltIn(candidate))
            {
                return new RegistrationResult(false, null, $"与内置热键冲突：{HotkeySpec.Pretty(chord.Spec)}");
            }

            foreach (var other in _entries.Values)
            {
                if (other == entry)
                {
                    continue;
                }

                if (HotkeyConflictPolicy.IsSameScopeDuplicate(candidate, other.Binding))
                {
                    return new RegistrationResult(false, other.Binding.Id, $"改键后与「{other.Binding.Description}」冲突");
                }

                if (HotkeyConflictPolicy.IsUndeclaredCrossScopeConflict(candidate, other.Binding))
                {
                    return new RegistrationResult(false, other.Binding.Id, $"改键后跨作用域冲突（需声明 Shadows 接管）");
                }
            }

            ReleaseRegistered(entry);
            entry.Binding = candidate;
            if (!entry.Declared)
            {
                EnsureRegistered(entry); // 声明条目键位由外部进程注册，宿主不碰
            }

            // P1-2：声明条目的键位**唯一真相源在外部消费方配置**（如 paste-back 配置键），
            // 注册表不持久化 chord——否则"设置中心改配置键 → 重启 ApplyPersistedOverrides 用旧 override
            // 覆盖配置值"会长期显示旧键。非声明条目照旧落盘 chord。
            if (entry.Declared)
            {
                var persisted = _settings.Get<HotkeySettingsDto>("hotkeys." + id, null);
                if (persisted is not null)
                {
                    _settings.Set("hotkeys." + id, persisted with { Chord = null });
                }
            }
            else
            {
                SaveOverride(id, new HotkeySettingsDto(Chord: chord.Spec, Enabled: null, Visible: null));
            }

            RaiseChanged();
            return new RegistrationResult(true, null, null);
        }
    }

    /// <inheritdoc />
    public void SetEnabled(string id, bool enabled)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.Enabled == enabled)
            {
                return;
            }

            entry.Enabled = enabled;
            if (enabled)
            {
                if (!entry.Declared)
                {
                    EnsureRegistered(entry);
                }
            }
            else
            {
                if (!entry.Declared)
                {
                    ReleaseRegistered(entry); // 停用 = 释放键位（可被其他绑定占用）
                }
            }

            SaveOverride(id, new HotkeySettingsDto(Chord: null, Enabled: enabled, Visible: null));
            RaiseChanged();
        }
    }

    /// <inheritdoc />
    public void SetVisible(string id, bool visible)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.Visible == visible)
            {
                return;
            }

            entry.Visible = visible; // 隐藏只过滤显示，**不改键位注册状态**
            SaveOverride(id, new HotkeySettingsDto(Chord: null, Enabled: null, Visible: visible));
            RaiseChanged();
        }
    }

    /// <inheritdoc />
    public void ResetToDefault(string id)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                return;
            }

            ReleaseRegistered(entry);
            entry.Binding = entry.Binding with { Chord = entry.Binding.DefaultChord };
            entry.Enabled = true;
            entry.Visible = true;
            if (!entry.Declared)
            {
                EnsureRegistered(entry);
            }

            _settings.Set<HotkeySettingsDto?>("hotkeys." + id, null); // 清空该 Id 全部覆盖
            RaiseChanged();
        }
    }

    /// <inheritdoc />
    public void SetActiveScopes(IReadOnlyList<string> scopeIds)
    {
        lock (_gate)
        {
            _activeScopes = new HashSet<string>(scopeIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            RaiseChanged();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<HotkeyView> GetActive()
    {
        lock (_gate)
        {
            var views = new List<HotkeyView>(_entries.Count);
            foreach (var entry in _entries.Values)
            {
                if (!entry.Enabled || !HotkeyConflictPolicy.IsActive(entry.Binding, _activeScopes))
                {
                    continue;
                }

                var shadowed = FindShadowing(entry);
                var mustShow = entry.OsConflict || shadowed is not null;
                if (!entry.Visible && !mustShow)
                {
                    continue; // 用户隐藏（忽略）→ 侧板不显示；异常项强制显示（隐藏 ≠ 静音）
                }

                views.Add(new HotkeyView(
                    entry.Binding,
                    Enabled: true,
                    Visible: true,
                    ShadowedBy: shadowed is not null,
                    OsConflict: entry.OsConflict));
            }

            return views;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<HotkeyView> GetAll()
    {
        lock (_gate)
        {
            return _entries.Values.Select(e => new HotkeyView(
                e.Binding, e.Enabled, e.Visible,
                FindShadowing(e) is not null, e.OsConflict)).ToList();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<HotkeyConflict> GetConflicts()
    {
        lock (_gate)
        {
            var conflicts = new List<HotkeyConflict>();
            foreach (var entry in _entries.Values)
            {
                if (entry.OsConflict)
                {
                    conflicts.Add(new HotkeyConflict(
                        entry.Binding.Id, null, entry.Binding.Chord.Spec,
                        "被其他程序占用，热键未生效（请改键）"));
                }

                var shadowing = FindShadowing(entry);
                if (shadowing is not null)
                {
                    conflicts.Add(new HotkeyConflict(
                        entry.Binding.Id, shadowing.Id, entry.Binding.Chord.Spec,
                        $"被「{shadowing.Description}」接管（{shadowing.Scope.Id}）"));
                }
            }

            return conflicts;
        }
    }

    // ---- 内部实现 ----

    private sealed class Entry(HotkeyBinding binding, Action<HotkeyBinding>? callback)
    {
        public HotkeyBinding Binding = binding;
        public Action<HotkeyBinding>? Callback = callback;
        public bool Enabled = true;
        public bool Visible = true;
        public bool OsConflict;
        public bool Declared; // 声明条目（外部进程自注册，本服务不碰中心窗口/钩子）
    }

    /// <summary>用户持久化覆盖（chord/enabled/visible 均为可空，缺省用默认）。</summary>
    private sealed record HotkeySettingsDto(string? Chord = null, bool? Enabled = null, bool? Visible = null);

    private HotkeyBinding ApplyPersistedOverrides(
        HotkeyBinding binding, out bool? persistedEnabled, out bool? persistedVisible)
    {
        persistedEnabled = null;
        persistedVisible = null;
        var dto = _settings.Get<HotkeySettingsDto>("hotkeys." + binding.Id, null);
        if (dto is null)
        {
            return binding;
        }

        var result = binding;
        if (!string.IsNullOrWhiteSpace(dto.Chord))
        {
            result = result with { Chord = new HotkeyChord(dto.Chord) };
        }

        persistedEnabled = dto.Enabled;
        persistedVisible = dto.Visible;
        return result;
    }

    private void SaveOverride(string id, HotkeySettingsDto dto)
    {
        var current = _settings.Get<HotkeySettingsDto>("hotkeys." + id, null) ?? new HotkeySettingsDto();
        var merged = new HotkeySettingsDto(
            dto.Chord ?? current.Chord,
            dto.Enabled ?? current.Enabled,
            dto.Visible ?? current.Visible);
        _settings.Set("hotkeys." + id, merged);
    }

    private void EnsureRegistered(Entry entry)
    {
        if (!entry.Enabled)
        {
            return;
        }

        if (entry.Binding.Source == HotkeySource.SystemHotkey)
        {
            if (_systemIds.ContainsKey(entry.Binding.Id))
            {
                return;
            }

            if (!HotkeySpec.TryParse(entry.Binding.Chord.Spec, out var mods, out var mainKey, out _)
                || !Enum.TryParse<Key>(mainKey, ignoreCase: true, out var key) || key == Key.None)
            {
                entry.OsConflict = true; // 非法键位：冲突可见（不静默）
                return;
            }

            var vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0)
            {
                entry.OsConflict = true;
                return;
            }

            var id = _nextId++;
            if (_window.Register(id, mods, (uint)vk))
            {
                _systemIds[entry.Binding.Id] = id;
                entry.OsConflict = false;
            }
            else
            {
                entry.OsConflict = true; // 被其他程序占用 → 标记，不静默失败
            }
        }
        else
        {
            _hook.Start(); // LowLevelHook：确保单钩子已安装（幂等）
        }
    }

    private void ReleaseRegistered(Entry entry)
    {
        if (entry.Binding.Source == HotkeySource.SystemHotkey && _systemIds.Remove(entry.Binding.Id, out var id))
        {
            _ = _window.Unregister(id);
        }
    }

    private bool OnKeyEvent(int msg, NativeMethods.KBDLLHOOKSTRUCT info)
    {
        if (msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
        {
            return TryConsumeLowLevelKey(info.vkCode, CurrentModifierFlags());
        }

        return false;
    }

    /// <summary>当前修饰键状态位（与 HotkeySpec 的 MOD_* 位一致：Alt=1 / Ctrl=2 / Shift=4 / Win=8）。</summary>
    private static int CurrentModifierFlags()
    {
        var flags = 0;
        if (IsDown(NativeMethods.VK_CONTROL)) flags |= HotkeySpec.ModControl;
        if (IsDown(NativeMethods.VK_SHIFT)) flags |= HotkeySpec.ModShift;
        if (IsDown(NativeMethods.VK_MENU)) flags |= HotkeySpec.ModAlt;
        if (IsDown(NativeMethods.VK_LWIN) || IsDown(NativeMethods.VK_RWIN)) flags |= HotkeySpec.ModWin;
        return flags;
    }

    private static bool IsDown(int vk) => (NativeMethods.GetAsyncKeyState(vk) & NativeMethods.KeyStateDown) != 0;

    /// <summary>
    /// 键表查表消费（内部，供钩子回调调用；测试用假键事件直接驱动）。
    /// 命中（同键位 + 同修饰键 + 启用 + 作用域活跃）即消费（吞键）并触发回调。
    /// </summary>
    internal bool TryConsumeLowLevelKey(uint vk, int modifierFlags)
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                if (!entry.Enabled
                    || entry.Binding.Source != HotkeySource.LowLevelHook
                    || !HotkeyConflictPolicy.IsActive(entry.Binding, _activeScopes))
                {
                    continue;
                }

                if (!HotkeySpec.TryParse(entry.Binding.Chord.Spec, out var mods, out var mainKey, out _)
                    || !Enum.TryParse<Key>(mainKey, ignoreCase: true, out var key) || key == Key.None)
                {
                    continue;
                }

                if ((uint)KeyInterop.VirtualKeyFromKey(key) != vk)
                {
                    continue;
                }

                if ((mods & 0xF) != modifierFlags)
                {
                    continue; // 忽略 MOD_NOREPEAT 位，只比四个修饰键
                }

                entry.Callback?.Invoke(entry.Binding);
                return true; // 命中且作用域活跃 → 消费
            }

            return false;
        }
    }

    private HotkeyBinding? FindShadowing(Entry entry)
        => HotkeyConflictPolicy.FindShadowing(entry.Binding, _entries.Values.Select(e => e.Binding));

    /// <summary>
    /// 中心窗口 WM_HOTKEY 分派（由 <see cref="HotkeyCenterWindow"/> 回调；wParam = 窗口热键 id）。
    /// 命中且启用 + 作用域活跃才触发回调；作用域不活跃时忽略（键已被系统消费，无法放行）。
    /// </summary>
    internal bool DispatchSystemHotkey(int windowHotkeyId)
    {
        lock (_gate)
        {
            foreach (var (bindingId, registeredId) in _systemIds)
            {
                if (registeredId != windowHotkeyId)
                {
                    continue;
                }

                if (!_entries.TryGetValue(bindingId, out var entry))
                {
                    return false;
                }

                if (!entry.Enabled || !HotkeyConflictPolicy.IsActive(entry.Binding, _activeScopes))
                {
                    return false; // 已停用或作用域不活跃：不触发（注册表不静默改变状态，仅不消费）
                }

                entry.Callback?.Invoke(entry.Binding);
                return true;
            }

            return false;
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // 订阅方异常不得破坏注册表状态机
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var entry in _entries.Values)
            {
                ReleaseRegistered(entry);
            }

            _entries.Clear();
            _systemIds.Clear();
        }

        _hook.Dispose();
        _window.Dispose();
    }
}
