using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Settings;

/// <summary>
/// 设置窗口：macOS 系统设置风（左侧分区导航 + 右侧卡片内容）。
/// 继承统一窗口基类 <see cref="ShellWindow"/>，跟随壳面的无边框 + 毛玻璃材质 + 圆角外观。
/// 视觉架构对齐 AppGrabberWindow：根 Border 透明、透出基类毛玻璃、极简视觉树，
/// 避免多层半透明托盘嵌套与 ContentControl 动态重建导致的卡顿 / 双重窗口感。
/// </summary>
public partial class SettingsWindow : ShellWindow
{
    private readonly ISettingsSectionRegistry _registry;
    private readonly ISettingsService _settings;
    private readonly IThemeTokens _tokens;

    // 每个分区一次性构建的内容容器（缓存复用，切换只改 Visibility，不重建视觉树）
    private readonly Dictionary<ISettingsSection, Border> _sectionBodies = new();
    // 导航栏绑定源：可观察集合，增量追加分区时自动刷新 UI（不依赖加载时序）。
    private readonly System.Collections.ObjectModel.ObservableCollection<ISettingsSection> _sectionItems = new();

    public SettingsWindow(ISettingsSectionRegistry registry, ISettingsService settings, IThemeTokens tokens, IVibrancyService? vibrancy = null)
    {
        _registry = registry;
        _settings = settings;
        _tokens = tokens;
        // 外观服务交给统一基类：基类据此订阅 Changed 并自动重绘（背景/色调/字号/材质/描边/圆角）。
        AppearanceService = tokens as IAppearanceService;

        InitializeComponent();

        // 根 Border 接入基类外观令牌：描边由主题页统一驱动（ApplySurfaceChrome 处理）。
        ChromeBorder = RootBorder;

        // 跟随统一窗口基类：毛玻璃服务交给基类，Loaded 时应用与壳面一致的材质。
        VibrancyService = vibrancy;

        // 应用级对话框行为（置顶/激活/任务栏/可缩放）通过重写虚属性提供，由基类构造期统一应用。
        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
        Activated += OnWindowActivated;
    }

    private bool _built;

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // 外观统一由基类 ShellWindow 经 AppearanceService 驱动（背景/色调/字号/材质/描边/圆角），
        // 侧栏/标题/导航等颜色已由本窗口 XAML 直接绑定主题令牌 DynamicResource，随模式一键切换，
        // 故此处不再持有任何补色逻辑（消除"特例独行"）。
        BuildAllSections();
        _built = true;
        // 订阅分区注册表变更：窗口打开后其他插件注册的分区（如任务栏外观）即时出现在导航栏，
        // 不依赖插件加载时序（SettingsPlugin 先于后续插件打开窗口的场景）。
        _registry.SectionsChanged -= OnSectionsChanged;
        _registry.SectionsChanged += OnSectionsChanged;
    }

    private void OnSectionsChanged(object? sender, EventArgs e)
    {
        // 注册可能发生在非 UI 线程（插件 LoadAsync 线程池），统一回 UI 线程增量刷新。
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnSectionsChanged(sender, e));
            return;
        }
        if (!_built) return;

        // 找出尚未加入导航栏的新分区（标题去重，覆盖同名不影响列表）。
        var existing = new HashSet<string>(
            _sectionItems.Select(s => s.Title));
        foreach (var section in _registry.Sections)
        {
            if (existing.Contains(section.Title)) continue;
            AddSection(section);
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // 反订阅，避免 registry 持有窗口强引用导致无法回收（每次 Show 新建窗口）。
        _registry.SectionsChanged -= OnSectionsChanged;
    }

    /// <summary>每次窗口激活/打开时与注册表对齐，补齐任何晚注册的分区（如开始菜单外观）。</summary>
    private void OnWindowActivated(object? sender, EventArgs e) => SyncSections();

    /// <summary>向导航栏与内容区增量追加一个分区（保持当前选中项）。</summary>
    private void AddSection(ISettingsSection section)
    {
        try
        {
            var body = new Border
            {
                Padding = new Thickness(30, 22, 30, 30),
                Visibility = Visibility.Collapsed
            };
            body.Child = section.Build(_settings, _tokens);
            _sectionBodies[section] = body;
            ContentPanel.Children.Add(body);

            // 加入可观察集合，导航栏自动刷新（SelectedIndex 保持不变）。
            _sectionItems.Add(section);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("Settings", $"分区 \"{section.Title}\" 构建失败：{ex.Message}");
        }
    }

    // 设置窗口：应用级对话框——不置顶、打开激活、在任务栏显示、可缩放。
    protected override bool DefaultTopmost => false;
    protected override bool DefaultShowActivated => true;
    protected override bool ShowInTaskbarDefault => true;
    protected override ResizeMode DefaultResizeMode => ResizeMode.CanResize;

    /// <summary>
    /// 一次性构建所有分区内容并加入 ContentPanel，默认全部折叠，仅显示选中项。
    /// 缓存到 <see cref="_sectionBodies"/>，切换导航只改 Visibility，杜绝重复 Build 卡顿。
    /// </summary>
    private void BuildAllSections()
    {
        var sections = _registry.Sections;
        SectionList.SelectionChanged -= OnSectionSelectionChanged;

        DiagnosticLog.Trace("Settings", $"BuildAllSections 分区数={sections.Count}：{string.Join("、", sections.Select(s => s.Title))}");

        // 导航栏绑定到可观察集合：后续增量 AddSection 会自动刷新 UI。
        _sectionItems.Clear();
        foreach (var s in sections) _sectionItems.Add(s);
        SectionList.ItemsSource = _sectionItems;
        // 导航项模板：图标 + 标题（复用全局 MacCombo 风格由 XAML 资源提供，这里用简约内联）
        SectionList.ItemTemplate = (DataTemplate)FindResource("SectionItemTemplate");

        foreach (var section in sections)
        {
            try
            {
                var body = new Border
                {
                    Padding = new Thickness(30, 22, 30, 30),
                    Visibility = Visibility.Collapsed
                };
                body.Child = section.Build(_settings, _tokens);
                _sectionBodies[section] = body;
                ContentPanel.Children.Add(body);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Trace("Settings", $"分区 \"{section.Title}\" 构建失败：{ex.Message}");
            }
        }

        SectionList.SelectedIndex = 0;
        SectionList.SelectionChanged += OnSectionSelectionChanged;
        ShowSection(sections.Count > 0 ? sections[0] : null);
    }

    /// <summary>
    /// 每次打开设置窗口时，从注册表补齐任何尚未入导航的分区。
    /// 消除「启动即开设置窗口」与「后续插件（如开始菜单/任务栏外观）注册分区」之间的时序竞态：
    /// 无论分区是早于还是晚于窗口首次加载注册，用户打开设置时都能看到。
    /// </summary>
    private void SyncSections()
    {
        if (!_built) return;
        var existing = new HashSet<string>(_sectionItems.Select(s => s.Title));
        foreach (var section in _registry.Sections)
        {
            if (existing.Contains(section.Title)) continue;
            DiagnosticLog.Trace("Settings", $"SyncSections 补齐分区：{section.Title}");
            AddSection(section);
        }
    }

    private void OnSectionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowSection(SectionList.SelectedItem as ISettingsSection);
    }

    private void ShowSection(ISettingsSection? section)
    {
        foreach (var (sec, body) in _sectionBodies)
        {
            body.Visibility = sec == section ? Visibility.Visible : Visibility.Collapsed;
        }
        TitleText.Text = section?.Title ?? "设置";
    }

    // ---- 标题栏拖拽（无 WindowChrome，自管） ----
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 重新定位到指定分区（供 <see cref="ISettingsWindowService.ShowSection"/> 调用）。
    /// </summary>
    public void SelectSection(string? sectionTitle)
    {
        // 打开/切换时先与注册表对齐，补齐任何晚注册的分区（如开始菜单外观）。
        SyncSections();

        if (string.IsNullOrWhiteSpace(sectionTitle)) return;
        foreach (var sec in _sectionBodies.Keys)
        {
            if (sec.Title == sectionTitle)
            {
                SectionList.SelectedItem = sec;
                return;
            }
        }
    }
}
