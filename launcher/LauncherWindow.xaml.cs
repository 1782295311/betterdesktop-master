// BetterDesktop 启动器 —— 唯一一屏 UI 的后台逻辑。
//
// 【线程纪律】启动流程（拉起进程、调 CLI、轮询落盘）全部在**后台线程**跑，UI 只收进度：
// 这是仓库反复写进注释的铁律——任何"可能不存在或卡住"的外部调用都不得占用 UI 线程
// （托盘曾因同步子进程调用把界面挂死过，见 tray/ProcessBridge.RunCliCapture 的修法）。
//
// 【生命周期】本窗口关闭 = 启动器退出。它**不常驻、不注册自启**，之后是托盘的职责。

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using BetterDesktop.Launcher.Services;

namespace BetterDesktop.Launcher;

/// <summary>
/// 启动器主窗口。
/// <para>
/// 刻意是 <c>internal</c>（XAML 侧用 <c>x:ClassModifier="internal"</c>）：启动器是应用而非库，
/// 行模型（StepRow/ToggleRow）与启动服务都保持 internal，不该为了"公开属性"把整条类型链放大成 public。
/// </para>
/// </summary>
internal partial class LauncherWindow : Window, INotifyPropertyChanged
{
    private readonly ComponentBootstrapper _bootstrapper;

    private IntegrationStatus _integration = IntegrationStatus.Unknown;
    private string _headline = "正在启动组件…";
    private string _footer = "启动器会拉起托盘、常驻服务与主程序，并检查系统右键菜单是否可用。";
    private string _hint = "开关改动立即生效；主程序未运行时于下次启动生效。启动器退出后由托盘接管，它自己不常驻。";

    public LauncherWindow()
    {
        _bootstrapper = new ComponentBootstrapper(OnStep);
        InitializeComponent();

        SubTitle = $"版本 {ReadVersion()} · 一次性启动器（跑完即退，不常驻）";
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<StepRow> Steps { get; } = new();

    public ObservableCollection<ToggleRow> Toggles { get; } = new();

    public string SubTitle { get; }

    public string Headline
    {
        get => _headline;
        private set => Set(ref _headline, value, nameof(Headline));
    }

    public string Footer
    {
        get => _footer;
        private set => Set(ref _footer, value, nameof(Footer));
    }

    public string Hint
    {
        get => _hint;
        private set => Set(ref _hint, value, nameof(Hint));
    }

    private static string ReadVersion()
    {
        try
        {
            var assembly = typeof(LauncherWindow).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                return informational;
            }

            var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
            return string.IsNullOrWhiteSpace(fileVersion)
                ? assembly.GetName().Version?.ToString() ?? "?"
                : fileVersion;
        }
        catch (Exception)
        {
            return "?";
        }
    }

    // ---------------- 启动流程 ----------------

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        StepList.ItemTemplate = (DataTemplate)FindResource("StepTemplate");
        ToggleList.ItemTemplate = (DataTemplate)FindResource("ToggleTemplate");
        StepList.ItemsSource = Steps;
        ToggleList.ItemsSource = Toggles;

        SetBusy(true);
        _ = Task.Run(RunBootstrap);
    }

    /// <summary>后台线程：跑完整启动流程。</summary>
    private void RunBootstrap()
    {
        try
        {
            var report = _bootstrapper.Run();
            Dispatcher.Invoke(() => CompleteBootstrap(report));
        }
        catch (Exception ex)
        {
            LauncherLog.Error("启动流程", ex);
            Dispatcher.Invoke(() =>
            {
                Steps.Add(StepRow.From(new BootStep(StepState.Fail, "启动流程异常中断", ex.Message)));
                Headline = "启动未完成";
                Footer = $"出现未预期错误：{ex.Message}（详见 {LauncherLog.FilePath}）";
                SetBusy(false);
            });
        }
    }

    /// <summary>UI 线程：收进度（每步实时出现，绝不静默）。</summary>
    private void OnStep(BootStep step) => Dispatcher.Invoke(() => Steps.Add(StepRow.From(step)));

    private void CompleteBootstrap(BootReport report)
    {
        _integration = _bootstrapper.Integration;

        var snapshot = SettingsSnapshot.Load();
        foreach (var toggle in FeatureToggleCatalog.All)
        {
            // 初始态 = 当前真实值：系统整合类以实际注册态为准（设置键可能陈旧），其余读设置文件，
            // 键缺失则取目录默认值（与 DesktopToggleCatalog / 托盘菜单同一套默认）。
            bool current;
            if (toggle.EnableArgs is not null && _integration.ComRegistered.HasValue)
            {
                current = _integration.ComRegistered.Value;
            }
            else
            {
                current = snapshot.Get(toggle.Key) ?? toggle.Default;
            }

            Toggles.Add(new ToggleRow(toggle, current));
        }

        Headline = report.Failures == 0
            ? (report.Warnings == 0 ? "启动完成，功能已就绪" : $"启动完成（{report.Warnings} 处需留意）")
            : $"启动完成，但有 {report.Failures} 项不可用";

        Footer = report.Failures == 0
            ? "下方开关可以随时调整；点「完成」应用并退出。"
            : "红色步骤表示功能缺失，请按提示处理后重试（下方仍可调整开关）。";

        SetBusy(false);
    }

    // ---------------- 开关区动作 ----------------

    private void OnEnableAll(object sender, RoutedEventArgs e) => SetAllToggles(static _ => true);

    private void OnRecommended(object sender, RoutedEventArgs e) => SetAllToggles(static toggle => toggle.Default);

    private void SetAllToggles(Func<FeatureToggle, bool> selector)
    {
        foreach (var row in Toggles)
        {
            row.IsOn = selector(row.Toggle);
        }
    }

    private async void OnDone(object sender, RoutedEventArgs e)
    {
        var desired = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var row in Toggles)
        {
            desired[row.Toggle.Key] = row.IsOn;
        }

        SetBusy(true);
        Footer = "正在应用开关…";

        IReadOnlyList<ToggleOutcome> outcomes;
        try
        {
            var integration = _integration;
            outcomes = await Task.Run(() => ToggleApplier.Apply(desired, integration));
        }
        catch (Exception ex)
        {
            LauncherLog.Error("应用开关", ex);
            Footer = $"应用开关时出错：{ex.Message}";
            SetBusy(false);
            return;
        }

        foreach (var outcome in outcomes)
        {
            var row = Toggles.FirstOrDefault(r => string.Equals(r.Label, outcome.Label, StringComparison.Ordinal));
            row?.SetOutcome(outcome);
        }

        var failed = outcomes.Count(o => !o.Applied);
        if (failed > 0)
        {
            // 有未确认项就把窗口留着：用户必须能看到"哪一项没成"，不能让启动器一退了之。
            Headline = "有开关未能确认";
            Footer = $"{failed} 项未能确认（见红色标注）。修好后可再点「完成」，或直接关闭。";
            SetBusy(false);
            return;
        }

        Headline = "全部完成";
        Footer = "组件已在运行、开关已生效。启动器即将退出，之后由托盘常驻接管。";
        await Task.Delay(1200);
        Close();
    }

    // ---------------- 基础 ----------------

    private void SetBusy(bool busy)
    {
        // 忙时禁用动作按钮：启动/落地过程中再点一次会并发改写设置（结果不可预期）。
        BtnDone.IsEnabled = !busy;
        BtnEnableAll.IsEnabled = !busy;
        BtnRecommended.IsEnabled = !busy;
    }

    private void Set(ref string field, string value, string propertyName)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
