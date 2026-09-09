using System;
using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Shell.Core;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Dock.Services;

/// <summary>
/// Dock 视觉配置：集中管理左侧 dock 栏的可调视觉参数（图标大小/间距/距底高度/名称显示/
/// 倒影/材质清晰模糊/用户图标文件夹），统一从 <see cref="ISettingsService"/> 读取并广播变更。
/// 渲染层（DockWindow）与设置层（LeftDockSection）共享同一组键，避免散落字符串。
/// 所有键以 <c>dock.</c> 为前缀。
/// </summary>
public sealed class DockVisualSettings
{
    private readonly ISettingsService? _settings;
    private readonly IEventBus? _events;
    private IDisposable? _settingsSub;

    public DockVisualSettings(ISettingsService? settings = null, IEventBus? events = null)
    {
        _settings = settings;
        _events = events;
        if (_settings is not null && _events is not null)
        {
            _settingsSub = _events.On<SettingsChangedEventArgs>(
                ShellEvents.SettingsChanged,
                (e, _) =>
                {
                    OnSettingsChanged(e);
                    return Task.CompletedTask;
                });
        }
    }

    public void Dispose() => _settingsSub?.Dispose();

    /// <summary>任意 dock 视觉配置键变更时触发（渲染层据此重建面板/重新定位）。</summary>
    public event EventHandler? Changed;

    private void OnSettingsChanged(SettingsChangedEventArgs e)
    {
        if (e.Key.StartsWith("dock.", StringComparison.Ordinal))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---- 图标尺寸 ----
    // 默认值 = 2026-09-01 用户实测调优值固化（设置分区滑块同默认，新环境开箱即此观感）。
    /// <summary>固定/运行图标边长（px）。默认 32.71。</summary>
    public double IconSize => Clamp(_settings?.Get("dock.iconSize", 32.71d) ?? 32.71d, 24, 96);

    /// <summary>图标项之间的水平间距（px）。默认 6.54。</summary>
    public double IconSpacing => Clamp(_settings?.Get("dock.iconSpacing", 6.54d) ?? 6.54d, 0, 48);

    /// <summary>Dock 距屏幕底部的高度（px）。默认 10。</summary>
    public double BottomMargin => Clamp(_settings?.Get("dock.bottomMargin", 10d) ?? 10d, 0, 200);

    /// <summary>是否在图标下方显示软件名称。默认 false。</summary>
    public bool ShowLabel => _settings?.Get("dock.showLabel", false) ?? false;

    // ---- 运行区 ----
    /// <summary>是否在右半区显示「运行中的应用」(从 RunningAppDetector 枚举的窗口去重图标)。
    /// 默认 true——关闭后整个 running 区+分隔线都不渲染,dock 退化为单区(仅左半 fixed 应用)。</summary>
    public bool ShowRunning => _settings?.Get("dock.showRunning", true) ?? true;

    // ---- 倒影 ----
    /// <summary>是否开启图标倒影。默认 true。</summary>
    public bool ReflectionEnabled => _settings?.Get("dock.reflection.enabled", true) ?? true;

    /// <summary>倒影强度（0-1，影响倒影的模糊/暗化混合，越大越实）。默认 0.77。</summary>
    public double ReflectionIntensity => Clamp01(_settings?.Get("dock.reflection.intensity", 0.77d) ?? 0.77d);

    /// <summary>倒影整体不透明度（0-1）。默认 0.87。</summary>
    public double ReflectionOpacity => Clamp01(_settings?.Get("dock.reflection.opacity", 0.87d) ?? 0.87d);

    /// <summary>倒影与图标之间的距离（px）。默认 2.59。</summary>
    public double ReflectionDistance => Clamp(_settings?.Get("dock.reflection.distance", 2.59d) ?? 2.59d, 0, 40);

    /// <summary>倒影渐变消失距离（px，从倒影顶端向下渐隐的长度）。默认 48.47。</summary>
    public double ReflectionGradientDistance => Clamp(_settings?.Get("dock.reflection.gradientDistance", 48.47d) ?? 48.47d, 4, 120);

    /// <summary>倒影倾斜角度（度，-30 左倾到 +30 右倾,0 为垂直）。默认 0。</summary>
    public double ReflectionSkew => Clamp(_settings?.Get("dock.reflection.skew", 0d) ?? 0d, -30, 30);

    /// <summary>模拟太阳日升日落：倒影倾斜角度与方向随时间平滑变化（全天 24h 正弦周期：
    /// 6:00 日出→右倾 +30°、12:00 正午→垂直 0°、18:00 日落→左倾 -30°、
    /// 18→24 反向回正、24:00/0:00→垂直 0°、再衔接次日日出，无缝循环）。
    /// 开启后忽略手动 <see cref="ReflectionSkew"/>。默认 true。</summary>
    public bool SunSync => _settings?.Get("dock.reflection.sunSync", true) ?? true;

    // ---- 空闲自动隐藏 ----
    /// <summary>
    /// 用户无任何输入（鼠标/键盘，GetLastInputInfo 系统级空闲）达到该分钟数后，
    /// dock 与菜单栏自动隐藏；期间任何输入立即恢复显示，操作中绝不隐藏。
    /// 默认 20 分钟（shell.idleHideMinutes，dock 与菜单栏共用同一阈值）。
    /// </summary>
    public double IdleHideMinutes => Clamp(_settings?.Get("shell.idleHideMinutes", 20d) ?? 20d, 1, 240);

    // ---- 材质（dock 窗口自身清晰/模糊） ----
    /// <summary>Dock 窗口材质：<c>blur</c>=透亮模糊（BlurBehind，高斯模糊不叠暗色调），
    /// <c>clear</c>=清晰（无模糊透明）。默认 blur。</summary>
    public string DockMaterial => _settings?.Get("dock.material", "blur") ?? "blur";

    // ---- 用户图标文件夹 ----
    /// <summary>用户自定义图标文件夹路径；为空表示不使用。该目录下与程序 exe 同名（去扩展名）的
    /// .png/.ico 文件会优先替换 dock 图标。</summary>
    public string CustomIconFolder => _settings?.Get("dock.customIconFolder", "") ?? "";

    // ---- 写入（供设置界面调用） ----
    public void SetIconSize(double v) => _settings?.Set("dock.iconSize", Clamp(v, 24, 96));
    public void SetIconSpacing(double v) => _settings?.Set("dock.iconSpacing", Clamp(v, 0, 48));
    public void SetBottomMargin(double v) => _settings?.Set("dock.bottomMargin", Clamp(v, 0, 200));
    public void SetShowLabel(bool v) => _settings?.Set("dock.showLabel", v);
    public void SetShowRunning(bool v) => _settings?.Set("dock.showRunning", v);
    public void SetReflectionEnabled(bool v) => _settings?.Set("dock.reflection.enabled", v);
    public void SetReflectionIntensity(double v) => _settings?.Set("dock.reflection.intensity", Clamp01(v));
    public void SetReflectionOpacity(double v) => _settings?.Set("dock.reflection.opacity", Clamp01(v));
    public void SetReflectionDistance(double v) => _settings?.Set("dock.reflection.distance", Clamp(v, 0, 40));
    public void SetReflectionGradientDistance(double v) => _settings?.Set("dock.reflection.gradientDistance", Clamp(v, 4, 120));
    public void SetReflectionSkew(double v) => _settings?.Set("dock.reflection.skew", Clamp(v, -30, 30));
    public void SetSunSync(bool v) => _settings?.Set("dock.reflection.sunSync", v);
    public void SetDockMaterial(string v) => _settings?.Set("dock.material", v is "clear" ? "clear" : "blur");
    public void SetCustomIconFolder(string v) => _settings?.Set("dock.customIconFolder", v ?? "");

    private static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;
    private static double Clamp01(double v) => Clamp(v, 0, 1);
}
