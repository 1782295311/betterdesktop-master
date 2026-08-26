using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.AppSource.Contracts;
using BetterDesktop.Shell.AppSource.Models;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.Core.Vibrancy;
using BetterDesktop.Shell.Pinning.Contracts;
using BetterDesktop.Shell.Recent.Contracts;
using BetterDesktop.Shell.Search.Contracts;
using BetterDesktop.Shell.Settings.Contracts;
using BetterDesktop.Shell.StartMenu.Contracts;
using BetterDesktop.Shell.StartMenu.Windows;
using BetterDesktop.Shell.WindowTracker.Contracts;

namespace BetterDesktop.Shell.StartMenu.Services;

/// <summary>
/// 开始菜单服务（自绘唯一后端）：菜单状态、数据聚合（程序树 / 全应用 / 搜索 / 最近）、
/// 布局与栏目扩展点、应用动作（固定/管理员/位置/卸载）。
/// Win 键钩子收到单独 Win 键时切换菜单；窗口单例 Show/Hide。
/// </summary>
public sealed class StartMenuService : IStartMenuService, IDisposable
{
    private readonly IAppSourceService _appSource;
    private readonly IWindowTrackerService _windowTracker;
    private readonly IStartMenuSearchService _search;
    private readonly IRecentItemsService _recent;
    private readonly IPinningService? _pinning;
    private readonly IVibrancyService? _vibrancy;
    private readonly IAppearanceService? _appearance;
    private readonly ISettingsService _settings;
    private readonly IKernelLogger _logger;
    private readonly StartKeyHook _keyHook;
    private IAppIconService? _appIcon;
    private ISettingsWindowService? _settingsWindow;
    private readonly Dictionary<string, IStartMenuLayoutProvider> _layouts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IStartMenuSectionProvider> _sections = new();
    private IStartMenuLayoutProvider _activeLayout;

    private StartMenuWindow? _window;
    private bool _hookInstalled;

    public event EventHandler? OpenStateChanged;

    /// <summary>活动布局切换通知（窗口据此重建内容）。</summary>
    public event Action? LayoutChanged;

    /// <summary>数据刷新请求（如卸载完成后，窗口据此重建程序列表）。</summary>
    public event Action? RequestRefresh;

    public StartMenuService(
        IAppSourceService appSource,
        IWindowTrackerService windowTracker,
        IStartMenuSearchService search,
        IRecentItemsService recent,
        IPinningService? pinning,
        IVibrancyService? vibrancy,
        IAppearanceService? appearance,
        ISettingsService settings,
        ISettingsWindowService? settingsWindow,
        IAppIconService? appIcon,
        IKernelLogger logger,
        IStartMenuLayoutProvider layout)
    {
        _appSource = appSource ?? throw new ArgumentNullException(nameof(appSource));
        _windowTracker = windowTracker ?? throw new ArgumentNullException(nameof(windowTracker));
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _recent = recent ?? throw new ArgumentNullException(nameof(recent));
        _pinning = pinning;
        _vibrancy = vibrancy;
        _appearance = appearance;
        _appIcon = appIcon;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settingsWindow = settingsWindow;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _activeLayout = layout ?? throw new ArgumentNullException(nameof(layout));

        _keyHook = new StartKeyHook();
        _keyHook.WinKeyPressed += OnWinKeyPressed;

        // 设置分区热更新：win-key 即时重挂钩子；其余 startmenu.* 触发菜单重建（宽度/栏目/搜索）。
        _settings.Changed += OnSettingChanged;
    }

    /// <summary>设置服务（供设置分区 / 栏目扩展点读取 startmenu.* 配置）。</summary>
    public ISettingsService Settings => _settings;

    /// <summary>
    /// 主题语义令牌（圆角/字号/颜色），由全局外观服务 <see cref="IAppearanceService"/> 承载；
    /// 布局据以免去硬编码圆角/字号（M6）。未注入时返回 null（调用方回退默认值）。
    /// </summary>
    internal IThemeTokens? ThemeTokens => _appearance as IThemeTokens;

    /// <summary>打开 BetterDesktop 设置窗口（Places 栏目「设置」入口；未注入时静默）。</summary>
    public void OpenSettings()
    {
        try
        {
            _settingsWindow?.Show();
        }
        catch
        {
            // 设置窗口打开失败静默（M10）。
        }
    }

    /// <summary>应用图标（磁贴 / 网格布局用；未注入时返回 null）。</summary>
    public System.Threading.Tasks.Task<System.Windows.Media.ImageSource?> GetIconAsync(AppItem app, System.Threading.CancellationToken ct = default)
        => _appIcon is null
            ? System.Threading.Tasks.Task.FromResult<System.Windows.Media.ImageSource?>(null)
            : _appIcon.GetIconAsync(app, ct);

    /// <summary>菜单宽度（设置 startmenu.width，默认 460）。</summary>
    public double GetMenuWidth() => _settings.Get("startmenu.width", 460.0);

    /// <summary>开始菜单样式（startmenu.style：win7 / win10 / win11）。</summary>
    public string GetMenuStyle() => _settings.Get("startmenu.style", "win11") switch
    {
        "win7" => "win7",
        "win10" => "win10",
        _ => "win11"
    };

    /// <summary>样式 → 布局名映射（win11 用专属 Win11 布局，其余同名）。</summary>
    public static string StyleToLayoutName(string style) => style switch
    {
        "win7" => "win7",
        "win10" => "win10",
        _ => "win11"
    };

    /// <summary>最近程序数量上限（startmenu.recent-count，默认 8）。</summary>
    public int GetRecentCount() => Math.Clamp(_settings.Get("startmenu.recent-count", 8), 0, 30);

    /// <summary>菜单打开动画（startmenu.menu-animation：none / fade / slide）。</summary>
    public string GetMenuAnimation() => _settings.Get("startmenu.menu-animation", "fade") switch
    {
        "none" => "none",
        "slide" => "slide",
        _ => "fade"
    };

    /// <summary>菜单垂直偏移像素（startmenu.vertical-offset，默认 8，贴左下角边距）。</summary>
    public double GetVerticalOffset() => Math.Clamp(_settings.Get("startmenu.vertical-offset", 8.0), 0, 120);

    /// <summary>系统用户名（Win11 底部栏 / Win7 用户头显示）。</summary>
    public string GetUserName()
    {
        try
        {
            return string.IsNullOrWhiteSpace(Environment.UserName) ? "User" : Environment.UserName;
        }
        catch
        {
            return "User";
        }
    }

    /// <summary>
    /// 开始菜单固定应用（zone="startmenu"）：取固定快照；默认未固定时回退全应用前若干项，
    /// 保证 Win11 "已固定 / Win10 磁贴" 恒有内容可展示。按固定顺序返回。
    /// </summary>
    public IReadOnlyList<AppItem> GetPinnedStartMenuApps(int cap)
    {
        var max = Math.Max(1, cap);
        var list = new List<AppItem>();
        try
        {
            if (_pinning is not null)
            {
                var pinned = _pinning.GetPinned("startmenu");
                foreach (var p in pinned)
                {
                    if (p?.AppItem is { } app && !app.Id.IsEmpty)
                    {
                        list.Add(app);
                    }

                    if (list.Count >= max)
                    {
                        break;
                    }
                }
            }
        }
        catch
        {
            // 固定读取失败回退全应用
        }

        if (list.Count < max)
        {
            foreach (var app in GetAllApps())
            {
                if (!list.Contains(app))
                {
                    list.Add(app);
                }

                if (list.Count >= max)
                {
                    break;
                }
            }
        }

        return list;
    }

    private void OnSettingChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (string.Equals(e.Key, "startmenu.win-key", StringComparison.OrdinalIgnoreCase))
        {
            ApplyWinKeySetting();
            return;
        }

        if (string.Equals(e.Key, "startmenu.style", StringComparison.OrdinalIgnoreCase))
        {
            // 样式切换：映射到布局（win11→classic）并重建。
            var style = GetMenuStyle();
            ShowLayout(StyleToLayoutName(style));
            return;
        }

        if (e.Key is not null && e.Key.StartsWith("startmenu.", StringComparison.OrdinalIgnoreCase))
        {
            // 宽度 / 栏目显隐等：菜单重建即生效。
            RequestRefresh?.Invoke();
        }
    }

    /// <summary>按 startmenu.win-key 设置挂载/卸载 Win 键钩子。</summary>
    private void ApplyWinKeySetting()
    {
        var enabled = _settings.Get("startmenu.win-key", true);
        if (enabled)
        {
            if (!_hookInstalled)
            {
                _hookInstalled = _keyHook.Install();
            }
        }
        else
        {
            _keyHook.Dispose();
            _hookInstalled = false;
        }
    }

    /// <inheritdoc />
    public bool IsActive => _hookInstalled;

    /// <inheritdoc />
    public bool IsOpen => _window is { IsVisible: true };

    /// <summary>活动布局。</summary>
    public IStartMenuLayoutProvider ActiveLayout => _activeLayout;

    /// <summary>注册布局（同名单覆盖）。</summary>
    public void RegisterLayout(IStartMenuLayoutProvider provider)
    {
        if (provider is null)
        {
            return;
        }

        _layouts[provider.Name] = provider;
    }

    /// <summary>请求菜单重建内容（布局内部需刷新自身时调用，触发窗口 RebuildContent）。</summary>
    public void RefreshLayout()
    {
        try
        {
            RequestRefresh?.Invoke();
        }
        catch
        {
            // 刷新通知失败不阻断（M10）。
        }
    }

    /// <summary>切换活动布局并通知窗口重建（如 Dock 入口切到 "allapps"）。</summary>
    public void ShowLayout(string name)
    {
        if (_layouts.TryGetValue(name, out var layout) && layout != _activeLayout)
        {
            _activeLayout = layout;
            try
            {
                LayoutChanged?.Invoke();
            }
            catch
            {
                // 通知失败不阻断（M10）。
            }
        }
    }

    /// <summary>注册菜单栏目（右栏扩展点）。</summary>
    public void RegisterSection(IStartMenuSectionProvider provider)
    {
        if (provider is not null)
        {
            _sections.Add(provider);
        }
    }

    /// <summary>已注册栏目列表。</summary>
    public IReadOnlyList<IStartMenuSectionProvider> GetSectionProviders() => _sections.ToList();

    // ===== 数据聚合（供布局 / 窗口 / 栏目使用） =====

    /// <summary>开始菜单程序树（可展开文件夹层级）。</summary>
    public ProgramFolder GetProgramTree() => _appSource.GetProgramTree();

    /// <summary>全应用平铺列表（开始菜单 + 已安装，按 Id 去重，名称排序）。</summary>
    public IReadOnlyList<AppItem> GetAllApps()
    {
        var seen = new HashSet<AppItemId>();
        var list = new List<AppItem>();
        try
        {
            foreach (var a in _appSource.ScanStartMenu().Concat(_appSource.ScanInstalledApps()))
            {
                if (seen.Add(a.Id))
                {
                    list.Add(a);
                }
            }
        }
        catch
        {
            // 扫描失败返回已有部分
        }

        return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>搜索（程序 / 设置 / 文件）。</summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var results = await _search.SearchAsync(query, ct);

        // 搜索范围开关（startmenu.search-programs/settings/files）+ 结果上限（search-limit）。
        var enableApp = _settings.Get("startmenu.search-programs", true);
        var enableSettings = _settings.Get("startmenu.search-settings", true);
        var enableFiles = _settings.Get("startmenu.search-files", true);
        var limit = Math.Clamp(_settings.Get("startmenu.search-limit", 20), 5, 50);

        return results
            .Where(r => (string.Equals(r.Category, "App", StringComparison.OrdinalIgnoreCase) && enableApp)
                        || (string.Equals(r.Category, "Settings", StringComparison.OrdinalIgnoreCase) && enableSettings)
                        || (string.Equals(r.Category, "File", StringComparison.OrdinalIgnoreCase) && enableFiles))
            .Take(limit)
            .ToList();
    }

    /// <summary>最近程序。</summary>
    public IReadOnlyList<RecentItem> GetRecentPrograms(int count) => _recent.GetRecentPrograms(count);

    /// <summary>最近添加的应用（快照差集；CLASSIC_LAYOUTS.md Win10「最近添加」/ Win11「最近添加」）。</summary>
    public IReadOnlyList<AppItem> GetNewlyInstalledApps() => _appSource.GetNewlyInstalledApps();

    /// <summary>已运行则激活，否则启动应用。</summary>
    public void ActivateOrLaunch(AppItem app)
    {
        if (app is null || app.Id.IsEmpty)
        {
            return;
        }

        try
        {
            if (_windowTracker.IsRunning(app.Id))
            {
                _windowTracker.Activate(app.Id);
                return;
            }

            var path = ResolveLaunchPath(app);
            if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(app.AppUserModelId))
            {
                return;
            }

            // UWP / Store 应用走 AUMID（shell:AppsFolder），其余走路径。
            AppSource.Services.AppLauncher.Launch(app);
        }
        catch
        {
            // 启动失败静默（M10）。
        }
    }

    // ===== 应用动作（Step 8：右键菜单） =====

    /// <summary>固定到指定 zone（dock / startmenu / taskbar）。</summary>
    public void PinToZone(AppItem app, string zone)
    {
        if (app is null || app.Id.IsEmpty)
        {
            return;
        }

        try
        {
            _pinning?.Pin(zone, app);
        }
        catch
        {
            // 固定失败静默（M10）。
        }
    }

    /// <summary>以管理员身份运行（UAC 提权）。</summary>
    public void LaunchAsAdmin(AppItem app)
    {
        if (app is null || app.Id.IsEmpty)
        {
            return;
        }

        try
        {
            // UWP / Store 应用无文件路径，无法提权启动 → 回退普通启动。
            if (string.IsNullOrWhiteSpace(app.TargetPath) && string.IsNullOrWhiteSpace(app.ShortcutPath))
            {
                AppSource.Services.AppLauncher.Launch(app);
                return;
            }

            var path = ResolveLaunchPath(app);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" });
        }
        catch
        {
            // 提权启动被取消/失败静默（M10）。
        }
    }

    /// <summary>打开文件所在目录（explorer）。</summary>
    public void OpenFileLocation(AppItem app)
    {
        if (app is null)
        {
            return;
        }

        try
        {
            var path = !string.IsNullOrWhiteSpace(app.TargetPath) ? app.TargetPath : app.ShortcutPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                Process.Start("explorer.exe", dir);
            }
        }
        catch
        {
            // 打开目录失败静默（M10）。
        }
    }

    /// <summary>
    /// 卸载：调用卸载注册表 UninstallString（cmd /c，复刻原 AppGrabber 行为）。
    /// 卸载进程退出后失效扫描缓存并请求刷新程序列表（已卸载程序从列表消失）。
    /// </summary>
    public void Uninstall(AppItem app)
    {
        if (app is null || string.IsNullOrWhiteSpace(app.UninstallCommand))
        {
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                FileName = "cmd.exe",
                Arguments = "/c " + app.UninstallCommand.Trim()
            };
            var proc = Process.Start(psi);
            if (proc is null)
            {
                return;
            }

            var dispatcher = Application.Current?.Dispatcher;
            Task.Run(() =>
            {
                try
                {
                    proc.WaitForExit();
                }
                catch
                {
                    // 进程句柄异常不阻断
                }
            }).ContinueWith(_ =>
            {
                void Refresh()
                {
                    try
                    {
                        _appSource.InvalidateCache();
                        RequestRefresh?.Invoke();
                    }
                    catch
                    {
                        // 刷新失败不阻断（M10）。
                    }
                }

                if (dispatcher is not null && !dispatcher.CheckAccess())
                {
                    dispatcher.BeginInvoke((Action)Refresh);
                }
                else
                {
                    Refresh();
                }
            }, TaskScheduler.Default);
        }
        catch
        {
            // 卸载启动失败静默（M10）。
        }
    }

    private static string? ResolveLaunchPath(AppItem app)
    {
        return !string.IsNullOrWhiteSpace(app.ShortcutPath) ? app.ShortcutPath : app.TargetPath;
    }

    // ===== IStartMenuService =====

    /// <inheritdoc />
    public bool Enable()
    {
        if (_hookInstalled)
        {
            return true;
        }

        // Win 键开关：startmenu.win-key=false 时不挂钩子（仅保留程序化 Show）。
        if (!_settings.Get("startmenu.win-key", true))
        {
            return true;
        }

        _hookInstalled = _keyHook.Install();
        return _hookInstalled;
    }

    /// <inheritdoc />
    public bool Disable()
    {
        _keyHook.Dispose();
        _hookInstalled = false;
        Hide();
        return true;
    }

    /// <inheritdoc />
    public bool ToggleMenu()
    {
        Toggle();
        return true;
    }

    /// <inheritdoc />
    public void Show()
    {
        if (!_hookInstalled)
        {
            _hookInstalled = _keyHook.Install();
        }

        var window = EnsureWindow();
        if (window is null)
        {
            return; // 熔断已切 TTB（EnsureWindow 内部处理）
        }

        window.Show();
        window.Activate();
        RaiseOpenStateChanged();
    }

    /// <inheritdoc />
    public void Hide()
    {
        if (_window is { IsVisible: true })
        {
            _window.Hide();
            RaiseOpenStateChanged();
        }
    }

    /// <inheritdoc />
    public void Toggle()
    {
        if (IsOpen)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }

    private void OnWinKeyPressed(object? sender, EventArgs e)
    {
        try
        {
            Toggle();
        }
        catch
        {
            // 钩子回调异常绝不冒泡（M10）。
        }
    }

    /// <summary>
    /// 确保单例窗口存在；创建失败记录日志并返回 null。
    /// </summary>
    private StartMenuWindow? EnsureWindow()
    {
        if (_window is not null)
        {
            return _window;
        }

        if (_vibrancy is null)
        {
            _logger.Error("[StartMenu] IVibrancyService 不可用，无法创建自绘窗口。");
            return null;
        }

        try
        {
            var window = new StartMenuWindow(this, _vibrancy, _appearance, _logger);
            window.Closed += (_, _) =>
            {
                _window = null;
                RaiseOpenStateChanged();
            };
            _window = window;
            return window;
        }
        catch (Exception ex)
        {
            _logger.Error($"[StartMenu] 自绘窗口创建失败：{ex.Message}");
            return null;
        }
    }

    private void RaiseOpenStateChanged()
    {
        try
        {
            OpenStateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // 通知失败不阻断（M10）。
        }
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingChanged;
        _keyHook.WinKeyPressed -= OnWinKeyPressed;
        _keyHook.Dispose();
        _window?.Close();
        _window = null;
    }
}
