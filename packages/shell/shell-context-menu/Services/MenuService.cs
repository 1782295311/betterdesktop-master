using System.Windows;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>
/// 统一右键菜单服务（五区块骨架在此组装，第一菜单原则固化于此）：
/// 模板 + 贡献项 → 组排序（Common→Manage→Contribution→System→Dynamic，组间分隔线）
/// → 能力过滤（隐藏优先，递归）→ Opening 注入点 → MenuHost 渲染（独立弹层窗口）。
/// </summary>
public sealed class MenuService : IMenuService, IDisposable
{
    private sealed record Contribution(IContextMenuContributor Contributor, int Seq);

    // 区块渲染顺序恒定；MenuGroup 枚举顺序与之对齐，此处显式声明防漂移
    private static readonly MenuGroup[] GroupOrder =
        [MenuGroup.Common, MenuGroup.Manage, MenuGroup.Contribution, MenuGroup.System, MenuGroup.Dynamic];

    private sealed class SeparatorMarker
    {
        public static readonly SeparatorMarker Instance = new();
        private SeparatorMarker() { }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose()
        {
            var d = Interlocked.Exchange(ref _dispose, null);
            d?.Invoke();
        }
    }

    private readonly object _gate = new();
    private readonly Dictionary<MenuScope, IMenuTemplate> _templates = [];
    private readonly List<Contribution> _contributions = [];
    private readonly IAppearanceService? _appearance;
    private readonly IVibrancyService? _vibrancy;
    private readonly ISettingsService? _settings;
    private int _seq;

    /// <summary>面板不透明度设置键（0.3~1.0；用户可在「设置 → 右键菜单」调节）。</summary>
    public const string OpacityKey = "context-menu.opacity";

    /// <summary>Shift 扩展项是否常驻（true = 普通右键也显示 Extended 项）。</summary>
    public const string ExtendedAlwaysKey = "context-menu.extended.always";

    private MenuHostSession? _active;

    public event EventHandler<MenuOpeningArgs>? Opening;

    /// <inheritdoc />
    public bool IsOpen => _active is not null;

    /// <inheritdoc />
    public event EventHandler? Closed;

    public MenuService(IAppearanceService? appearance = null, IVibrancyService? vibrancy = null, ISettingsService? settings = null)
    {
        _appearance = appearance;
        _vibrancy = vibrancy;
        _settings = settings;
    }

    /// <summary>读取用户设置的面板不透明度（每次展示时读，设置页改动即时生效）。</summary>
    private double PanelOpacity =>
        Math.Clamp(_settings?.Get(OpacityKey, 0.82) ?? 0.82, 0.3, 1.0);

    public IDisposable RegisterContributor(IContextMenuContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(contributor);
        lock (_gate)
        {
            var entry = new Contribution(contributor, _seq++);
            _contributions.Add(entry);
            return new Subscription(() =>
            {
                lock (_gate) _contributions.Remove(entry);
            });
        }
    }

    public IDisposable RegisterTemplate(IMenuTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        lock (_gate) _templates[template.Scope] = template;
        return new Subscription(() =>
        {
            lock (_gate)
            {
                if (_templates.TryGetValue(template.Scope, out var current) && ReferenceEquals(current, template))
                    _templates.Remove(template.Scope);
            }
        });
    }

    public Task<MenuResult> ShowAsync(MenuRequest request, CancellationToken cancellationToken = default)
    {
        var dispatcher = Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
        if (!dispatcher.CheckAccess())
        {
            // 编队回 UI 线程（契约：ShowAsync 必须 UI 线程；宽容处理调用方疏漏）
            return dispatcher.InvokeAsync(() => ShowAsync(request, cancellationToken)).Task.Unwrap();
        }

        DismissInternal();

        var items = BuildItems(request);
        DiagnosticLog.Trace("context-menu", $"Show scope={request.Scope} items={items.Count} file={request.File?.Kind.ToString() ?? "-"}");

        if (items.Count == 0)
            return Task.FromResult(new MenuResult(MenuResultKind.None));

        // 独立弹层窗口承载（ShellWindow 统一基类；不依赖调用方视觉元素）：Target 仅作业务载荷
        _active = MenuHost.Show(items, request.ScreenPosition, _appearance, _vibrancy, PanelOpacity);
        NotifyClosedOnCompletion(_active);
        return _active.Completion;
    }

    /// <inheritdoc />
    public Task<MenuResult> ShowAsync(IReadOnlyList<MenuItemDef> items, Point screenPos)
    {
        var dispatcher = Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
        if (!dispatcher.CheckAccess())
        {
            return dispatcher.InvokeAsync(() => ShowAsync(items, screenPos)).Task.Unwrap();
        }

        DismissInternal();

        // 低层入口：调用方自带菜单项，跳过模板/贡献项管线；渲染与样式仍由 MenuHost 统一驱动。
        if (items.Count == 0)
            return Task.FromResult(new MenuResult(MenuResultKind.None));

        DiagnosticLog.Trace("context-menu", $"Show(items={items.Count})");
        _active = MenuHost.Show(items, screenPos, _appearance, _vibrancy, PanelOpacity);
        NotifyClosedOnCompletion(_active);
        return _active.Completion;
    }

    /// <summary>
    /// 菜单关闭（会话完成）时在 UI 线程：清空 _active（否则窗口已关而 IsOpen 恒 true，
    /// 失焦自毁型宿主的豁免逻辑会被永久卡住）+ 广播 Closed（供宿主补收尾）。
    /// </summary>
    private void NotifyClosedOnCompletion(MenuHostSession session)
    {
        _ = session.Completion.ContinueWith(
            _ => Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                Interlocked.CompareExchange(ref _active, null, session);
                Closed?.Invoke(this, EventArgs.Empty);
            }),
            TaskScheduler.Default);
    }

    public void Dismiss()
    {
        // 与 DismissInternal 同口径：Interlocked 取走会话（裸读字段与置空者竞态，审查 P3-1）
        var session = Interlocked.Exchange(ref _active, null);
        if (session is null) return;
        session.Window.Dispatcher.BeginInvoke(session.Dismiss);
    }

    private void DismissInternal()
    {
        var session = Interlocked.Exchange(ref _active, null);
        if (session is null) return;
        session.Dismiss();
        DiagnosticLog.Trace("context-menu", "Dismiss(重入)");
    }

    /// <summary>组装菜单项：模板 → 贡献项 → 组排序 → 能力过滤 → Opening。</summary>
    private List<MenuItemDef> BuildItems(MenuRequest request)
    {
        var byGroup = new Dictionary<MenuGroup, List<object>>();
        List<object> Group(MenuGroup g) => byGroup.TryGetValue(g, out var l) ? l : byGroup[g] = [];

        IMenuTemplate? template;
        Contribution[] contributions;
        lock (_gate)
        {
            _templates.TryGetValue(request.Scope, out template);
            contributions = [.. _contributions
                .Where(c => c.Contributor.Scope == request.Scope)
                .OrderByDescending(c => c.Contributor.Priority)
                .ThenBy(c => c.Seq)];
        }

        // 1) 模板项（①②④⑤ 区块内容）
        template?.Build(new TemplateBuilder(Group), request);

        // 2) 贡献项 → 各自声明的区块（失败贡献者仅跳过，不影响整体）
        foreach (var c in contributions)
        {
            try
            {
                foreach (var item in c.Contributor.Build(request))
                {
                    if (item is null) continue;
                    Group(item.Group).Add(item);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Trace("context-menu", $"贡献项构建失败 {c.Contributor.GetType().Name}: {ex.Message}");
            }
        }

        // 3) 展平：区块顺序恒定，区块间分隔线，区块内 marker 原位（首尾悬空由 TrimEdges 兜底）
        static MenuItemDef MakeSep(MenuGroup group) => new()
        {
            Id = $"__sep_{group}",
            Text = string.Empty,
            Kind = MenuItemKind.Separator,
            Group = group,
        };

        var flat = new List<MenuItemDef>();
        foreach (var group in GroupOrder)
        {
            if (!byGroup.TryGetValue(group, out var list) || list.Count == 0) continue;
            if (flat.Count > 0) flat.Add(MakeSep(group));
            foreach (var entry in list)
                flat.Add(entry is SeparatorMarker ? MakeSep(group) : (MenuItemDef)entry);
        }

        // 4) 能力过滤（隐藏优先；子菜单全滤则父项隐藏）
        var filtered = FilterCapabilities(flat, request.File);

        // 4.5) Shift 扩展项过滤（Win10 语义）：非 Shift 右键且未设"常驻" → 隐藏 Extended 项。
        //      低频/危险项的正确归宿是 Shift 扩展，不是 Win11 式二级收纳。
        var showExtended = request.ShiftPressed
            || (_settings?.Get(ExtendedAlwaysKey, false) ?? false);
        if (!showExtended)
        {
            filtered = StripExtended(filtered);
        }

        // 5) 展示前注入点（订阅者异常隔离）
        IList<MenuItemDef> final = filtered;
        try
        {
            var args = new MenuOpeningArgs { Request = request, Items = final };
            Opening?.Invoke(this, args);
            final = args.Items;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("context-menu", $"Opening 订阅者异常(忽略): {ex.Message}");
        }

        return [.. final.OfType<MenuItemDef>()];
    }

    private static List<MenuItemDef> FilterCapabilities(IReadOnlyList<MenuItemDef> items, FileIdentity? identity)
    {
        // 无身份也走 TrimEdges：与有身份分支对称，防御未来悬空分隔线（审查 P3-5）
        if (identity is null) return TrimEdges([.. items]);
        var result = new List<MenuItemDef>(items.Count);
        foreach (var item in items)
        {
            if (item.Kind == MenuItemKind.Separator)
            {
                result.Add(item);
                continue;
            }
            if (item.RequiredCapability != FileCapabilities.None && !identity.Caps.HasFlag(item.RequiredCapability))
                continue; // 隐藏优先：能力不满足不渲染

            if (item.Children is { Count: > 0 })
            {
                var children = FilterCapabilities(item.Children, identity);
                if (item.Kind == MenuItemKind.Submenu && children.Count == 0)
                    continue;
                result.Add(item with { Children = children });
            }
            else
            {
                result.Add(item);
            }
        }
        return TrimEdges(result);
    }

    /// <summary>移除 Shift 扩展项（递归；子菜单被清空则父项一并移除）。</summary>
    private static List<MenuItemDef> StripExtended(List<MenuItemDef> items)
    {
        var result = new List<MenuItemDef>(items.Count);
        foreach (var item in items)
        {
            if (item.Extended)
            {
                continue;
            }

            if (item.Kind == MenuItemKind.Submenu && item.Children is { Count: > 0 })
            {
                var children = StripExtended(item.Children.ToList());
                if (children.Count == 0)
                {
                    continue;
                }
                result.Add(item with { Children = children });
                continue;
            }

            result.Add(item);
        }
        return TrimEdges(result);
    }

    /// <summary>去掉首尾与连续重复的分隔线（过滤后可能暴露悬空分隔线）。</summary>
    private static List<MenuItemDef> TrimEdges(List<MenuItemDef> items)
    {
        var list = new List<MenuItemDef>(items.Count);
        foreach (var item in items)
        {
            var isSep = item.Kind == MenuItemKind.Separator;
            if (isSep && (list.Count == 0 || list[^1].Kind == MenuItemKind.Separator)) continue;
            list.Add(item);
        }
        while (list.Count > 0 && list[^1].Kind == MenuItemKind.Separator) list.RemoveAt(list.Count - 1);
        return list;
    }

    public void Dispose() => DismissInternal();

    /// <summary>模板填充器实现（把模板产出收集到各区块）。</summary>
    private sealed class TemplateBuilder(Func<MenuGroup, List<object>> group) : IMenuTemplateBuilder
    {
        public void AddItem(MenuItemDef item)
        {
            ArgumentNullException.ThrowIfNull(item);
            group(item.Group).Add(item);
        }

        public void AddSeparator(MenuGroup targetGroup) => group(targetGroup).Add(SeparatorMarker.Instance);
    }
}
