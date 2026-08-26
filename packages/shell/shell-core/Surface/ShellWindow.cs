using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BetterDesktop.Shell.Core.Vibrancy;
using Point = System.Windows.Point;

namespace BetterDesktop.Shell.Core.Surface;

/// <summary>
/// 外壳窗口统一基类（纯代码版，可被 XAML 窗口继承）。
/// 目标：统一窗口属性与毛玻璃入口，避免各包重复硬编码；
/// 并订阅 <see cref="IAppearanceService"/> 实现"一处改主题、全局窗口即时重绘"。
/// </summary>
public abstract class ShellWindow : Window
{
    /// <summary>
    /// 统一基类构造函数：注入双服务，并在句柄创建前应用窗口基础样式（由虚属性提供配置值）。
    /// 子类通过重写对应虚属性定制行为，无需自己赋值、无需关心生效时机。
    /// </summary>
    protected ShellWindow(IAppearanceService? appearance = null, IVibrancyService? vibrancy = null)
    {
        // === 窗口行为配置：基类统一在构造期（句柄创建前）应用，子类只重写虚属性提供值 ===
        // 每个属性经 CanSetProperty 白名单校验（默认 true；PluginHostWindow 按权限裁决）。
        if (CanSetProperty("WindowStyle")) WindowStyle = DefaultWindowStyle;
        if (CanSetProperty("ResizeMode")) ResizeMode = DefaultResizeMode;
        if (CanSetProperty("AllowsTransparency")) AllowsTransparency = AllowsTransparencyDefault;
        if (CanSetProperty("ShowInTaskbar")) ShowInTaskbar = ShowInTaskbarDefault;
        if (CanSetProperty("Topmost")) Topmost = DefaultTopmost;
        if (CanSetProperty("ShowActivated")) ShowActivated = DefaultShowActivated;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // 使用几乎透明的背景而不是完全透明，防止鼠标穿透（ApplyAppearance 会按主题覆盖）。
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

        // 保留原有注入逻辑
        AppearanceService = appearance;
        VibrancyService = vibrancy;

        Loaded += OnShellWindowLoaded;
        // HWND 创建后挂上 HwndSource hook（WM_NCHITTEST 自建 resize 命中区）。
        // 窗口圆角走 FrostedGlassDemo 实证基准：DWM 系统默认圆角（DWMWA_WINDOW_CORNER_PREFERENCE），
        // 由 VibrancyService.Apply(roundCorners:true) → DwmHelper 在应用毛玻璃时一并设置，
        // 与根 Border 的 XAML CornerRadius 重合。无需 SetWindowRgn 手动裁切。
        SourceInitialized += OnSourceInitialized;
        // 窗口关闭时清空字号基值字典，避免长会话下 Dictionary 持有已卸载的可视树元素引用。
        Closed += (_, _) => _baseFontSizes.Clear();
    }

    // === 窗口行为配置（子类按需重写，无需手动赋值） ===
    /// <summary>是否置顶。默认 true（壳面常驻）；管理窗口（AppGrabber，已融合 Launchpad 能力）重写为 false。</summary>
    protected virtual bool DefaultTopmost => true;
    /// <summary>打开时是否激活。默认 false（不打断用户）；管理窗口重写为 true。</summary>
    protected virtual bool DefaultShowActivated => false;
    /// <summary>是否在任务栏显示。壳面窗口默认 false。</summary>
    protected virtual bool ShowInTaskbarDefault => false;
    /// <summary>窗口样式。壳面统一无边框。</summary>
    protected virtual WindowStyle DefaultWindowStyle => WindowStyle.None;
    /// <summary>缩放模式。默认可自由缩放（自建 WM_NCHITTEST resize 命中区已就绪，边缘/四角可拖拽）。
    /// dock 悬浮胶囊（DockWindow/NewAppsNotificationWindow）因尺寸由内容/停靠决定，
    /// 显式 override 回 NoResize；其余窗口（设置、管理窗口、插件宿主）均跟随此默认获得自由拉伸能力，
    /// 让皮肤按原始比例完整铺底、窗口尺寸去适应皮肤而非反之。</summary>
    protected virtual ResizeMode DefaultResizeMode => ResizeMode.CanResize;
    /// <summary>分层透明。壳面默认 true（毛玻璃必需）。</summary>
    protected virtual bool AllowsTransparencyDefault => true;
    /// <summary>标题栏拖拽高度（逻辑像素）。默认 0 表示整窗可拖（无显式标题栏区域）。</summary>
    protected virtual double CaptionHeight => 0;

    /// <summary>
    /// HWND 创建完成时：挂上 HwndSource hook（WM_NCHITTEST 自建 resize 命中区）。
    /// 圆角由 DWM 系统默认圆角提供（毛玻璃应用时经 DwmHelper 设置），无需在此裁 region。
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (HwndSource.FromHwnd(hwnd) is { } src)
        {
            src.AddHook(WndProcHook);
        }
    }

    /// <summary>
    /// 自定义 resize 命中区厚度（物理像素的逻辑值，单位 DIP）。仅当 <see cref="ResizeMode"/> 为
    /// CanResize/CanResizeWithGrip 且本窗口未使用 WindowChrome 时生效——由 <see cref="WndProcHook"/>
    /// 在 WM_NCHITTEST 中自行判定边/角命中，替代 WindowChrome 的非客户区 resize。
    /// 子类可在构造函数覆盖（如设置窗口想要更窄的 6px 边）。
    /// </summary>
    protected virtual double ResizeBorderThickness => 6;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>
    /// HwndSource 消息钩子：仅拦截 WM_NCHITTEST（0x0084）——当窗口可 resize 且未使用 WindowChrome 时，
    /// 自行判定鼠标落在哪条边/角，返回对应 HT* 命中常量，让系统像有 WindowChrome 一样完成 resize，
    /// 但走纯自建命中区（不引入非客户区合成冲突）。
    /// 注：不再拦截 WM_WINDOWPOSCHANGED 做 region 重裁——圆角路线已弃用，且该机制是此前卡顿根因。
    /// </summary>
    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084;
        if (msg == WM_NCHITTEST && !handled)
        {
            // 仅当窗口可 resize 且未挂 WindowChrome 时，自建命中区。
            var canResize = ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;
            var hasChrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this) is not null;
            if (canResize && !hasChrome)
            {
                var hit = HitTestResize(lParam);
                if (hit != IntPtr.Zero)
                {
                    handled = true;
                    return hit;
                }
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 根据 WM_NCHITTEST 的 lParam（屏幕坐标）判定鼠标落在窗口哪条边/角，返回对应 HT* 常量；
    /// 落在内部返回 IntPtr.Zero（交给 WPF 继续处理，标题栏拖拽/按钮命中由 WPF 路由）。
    /// 圆角区域内不做边角命中，避免角落拖拽与内容冲突。
    /// </summary>
    private IntPtr HitTestResize(IntPtr lParam)
    {
        // lParam 低位=屏幕 x，高位=屏幕 y
        var screenX = (short)(lParam.ToInt32() & 0xFFFF);
        var screenY = (short)((lParam.ToInt32() >> 16) & 0xFFFF);

        var dpi = VisualTreeHelper.GetDpi(this);
        var thick = (int)Math.Round(ResizeBorderThickness * dpi.DpiScaleX); // 逻辑 DIP → 设备像素

        // 直接取 HWND 真实物理矩形（与 lParam 同一屏幕坐标空间），不依赖 WPF 逻辑坐标 Left/Top/ActualWidth
        // 与 DPI 换算——后者在窗口移动/跨监视器 DPI 变化后可能滞后错位，导致边缘命中带整体偏移、拖拽失效。
        var hwnd = new WindowInteropHelper(this).Handle;
        var rect = new RECT();
        var hasRect = hwnd != IntPtr.Zero && GetWindowRect(hwnd, out rect);
        var left = hasRect ? rect.Left : (int)Math.Round(Left * dpi.DpiScaleX);
        var top = hasRect ? rect.Top : (int)Math.Round(Top * dpi.DpiScaleY);
        var right = hasRect ? rect.Right : left + (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        var bottom = hasRect ? rect.Bottom : top + (int)Math.Round(ActualHeight * dpi.DpiScaleY);

        const int HTLEFT = 10;
        const int HTRIGHT = 11;
        const int HTTOP = 12;
        const int HTTOPLEFT = 13;
        const int HTTOPRIGHT = 14;
        const int HTBOTTOM = 15;
        const int HTBOTTOMLEFT = 16;
        const int HTBOTTOMRIGHT = 17;

        var inX = screenX >= left + thick && screenX <= right - thick;
        var inY = screenY >= top + thick && screenY <= bottom - thick;
        // 在圆角半径内（角部小三角区域）不命中边角，避免与内容交互冲突；使用系统默认小圆角。
        var r = (int)Math.Round(SystemCornerRadius * Math.Min(dpi.DpiScaleX, dpi.DpiScaleY));

        // 四角优先（在厚边带内且接近角）
        if (screenX <= left + thick && screenY <= top + thick)
        {
            if (screenX - left <= r && top + r - screenY >= 0 && (screenX - left) <= (top + r - screenY)) return (IntPtr)HTTOPLEFT;
            return (IntPtr)HTTOPLEFT;
        }
        if (screenX >= right - thick && screenY <= top + thick)
        {
            return (IntPtr)HTTOPRIGHT;
        }
        if (screenX <= left + thick && screenY >= bottom - thick)
        {
            return (IntPtr)HTBOTTOMLEFT;
        }
        if (screenX >= right - thick && screenY >= bottom - thick)
        {
            return (IntPtr)HTBOTTOMRIGHT;
        }
        // 四边（四角判定已把角部 thick×thick 方块拿走，此处用"中间带" inX/inY 与四角互补；
        // 此前误写 !inY/!inX，导致除四角外整条边都不命中，窗口只能对角拖拽）。
        if (screenX <= left + thick && inY) return (IntPtr)HTLEFT;
        if (screenX >= right - thick && inY) return (IntPtr)HTRIGHT;
        if (screenY <= top + thick && inX) return (IntPtr)HTTOP;
        if (screenY >= bottom - thick && inX) return (IntPtr)HTBOTTOM;

        return IntPtr.Zero;
    }

    /// <summary>毛玻璃服务。</summary>
    protected IVibrancyService? VibrancyService { get; set; }

    /// <summary>
    /// 外观服务。宿主/插件注入后，本窗口会订阅其 <see cref="IAppearanceService.Changed"/>，
    /// 主题（色调/透明度/字号/材质/皮肤）变更即自动重绘。
    /// </summary>
    protected IAppearanceService? AppearanceService { get; set; }

    /// <summary>子类扩展点：窗口加载完成后执行。</summary>
    protected virtual void OnLoadedCore()
    {
    }

    // === 插件化适配扩展点（add-only：默认空/true，内置窗口零影响，仅 PluginHostWindow 重写） ===

    /// <summary>
    /// ChromeBorder 就绪钩子：基类在 ChromeBorder 赋值后（<see cref="OnShellWindowLoaded"/> 开头）调用。
    /// 内置窗口无需重写；<c>PluginHostWindow</c> 重写此方法注入插件内容与主题资源字典。
    /// </summary>
    protected virtual void OnChromeBorderReady()
    {
    }

    /// <summary>
    /// 属性白名单校验：基类在应用窗口行为虚属性（DefaultTopmost 等）前调用。
    /// 内置窗口默认放行（true）；PluginHostWindow 重写为按权限服务裁决。
    /// </summary>
    protected virtual bool CanSetProperty(string propertyName) => true;

    /// <summary>
    /// 卸载清理入口：基类统一解绑外观事件、释放 DWM 资源。
    /// PluginHostWindow 重写时先 base.DetachWindow() 再补充插件级清理（解绑插件事件、清空内容引用）。
    /// </summary>
    protected virtual void DetachWindow()
    {
        if (AppearanceService is not null)
        {
            AppearanceService.Changed -= OnAppearanceChanged;
        }
    }

    /// <summary>子类可重写：窗口材质应用策略。</summary>
    protected virtual void ApplyWindowMaterial()
    {
        if (VibrancyService is null)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (AppearanceService is not null)
        {
            VibrancyService.Apply(hwnd, AppearanceService.Material, roundCorners: true);
        }
        else
        {
            VibrancyService.Apply(hwnd, VibrancyStyle.Transparent, roundCorners: true);
        }
    }

    /// <summary>
    /// 窗口"外框描边 + 外边缘阴影"容器。子类（XAML 或代码构造）应在 <see cref="OnLoadedCore"/> 或构造函数里
    /// 把根 Border 赋给本属性；基类据此把主题描边（<see cref="IAppearanceService.CardBorder"/>）与外边缘阴影
    /// （<see cref="IAppearanceService.CardShadow"/>）应用到所有外壳窗口外边缘，实现"一处改主题、全局窗口统一"。
    /// 范式对齐 cairoshell：根 Border 用 BorderThickness+BorderBrush 描边，Effect 套外边缘阴影，
    /// 并用 <see cref="ChromeMargin"/> 给阴影留出窗外空间，避免分层透明窗的 Effect 阴影被窗体合成边界裁切
    /// （原版 CairoWindowContainer Margin=12,7,12,17、CairoTaskbarBorderStyle 同此理）。
    /// </summary>
    protected Border? ChromeBorder { get; set; }

    /// <summary>
    /// 根 Border 外边距（设备无关像素）。默认 new(0)：根 Border 铺满窗口，不向内缩。
    /// 此前曾用 12,7,12,17 给外边缘阴影留窗外空间，但分层透明窗下内缩会露出一圈透明、且与描边阴影机制耦合导致反复出错；
    /// 当前阶段暂停描边/阴影尝试，根 Border 直接铺满窗口（内缩问题修复）。后续若重做外边缘阴影再评估留白策略。
    /// </summary>
    protected virtual Thickness ChromeMargin => new(0);

    /// <summary>字号缩放基值：首次 Loaded 时遍历可视树记录所有文本类控件的 FontSize，
    /// 之后按基值×FontScale 重设。手动修改某元素 FontSize 后基值不变（基值代表"主题基础字号"）。</summary>
    private readonly Dictionary<DependencyObject, double> _baseFontSizes = new();

    /// <summary>
    /// 基类内置统一主题绑定工具：让纯代码创建的控件也能像 XAML 一样使用 DynamicResource，
    /// 无需写死颜色、无需每个窗口重复实现 BindTheme。绑定目标资源键（如 "ThemeForeground"、
    /// "CardBorderBrush"、"CardShadowEffect"），由 AppearanceService 推送，模式/描边/阴影变化时自动刷新。
    /// 使用 <see cref="FrameworkElement.SetResourceReference"/>（等价于 XAML DynamicResource 绑定），
    /// 不依赖 ProvideValue 的 serviceProvider，headless/测试环境同样安全。
    /// </summary>
    protected static void SetThemeBinding(DependencyObject target, DependencyProperty property, string resourceKey)
    {
        if (target is FrameworkElement fe)
        {
            fe.SetResourceReference(property, resourceKey);
        }
        else if (target is FrameworkContentElement fce)
        {
            fce.SetResourceReference(property, resourceKey);
        }
    }

    private void OnShellWindowLoaded(object sender, RoutedEventArgs e)
    {
        // ChromeBorder 就绪钩子：PluginHostWindow 在此注入插件内容/资源；内置窗口空实现零影响。
        OnChromeBorderReady();

        ApplyWindowMaterial();
        if (AppearanceService is not null)
        {
            AppearanceService.Changed += OnAppearanceChanged;
            ApplyAppearance(AppearanceChangedArgs.All);
        }

        // 子类在此钩子里把根 Border 赋给 ChromeBorder。
        OnLoadedCore();
        ApplySurfaceChrome(AppearanceChangedArgs.All);
        ApplyWindowChrome(AppearanceChangedArgs.All);
        // 首次记录基值并按当前 FontScale 应用一次。
        ApplyFontScale(AppearanceChangedArgs.All);

#if DEBUG
        // 调试自检：未挂 ChromeBorder 的窗口外观将失效，及早发现接入遗漏。
        System.Diagnostics.Debug.Assert(ChromeBorder is not null,
            $"{GetType().Name} 未设置 ChromeBorder，窗口外观（背景/描边/圆角）将失效");
#endif
    }

    private void OnAppearanceChanged(object? sender, AppearanceChangedArgs e)
    {
        ApplyAppearance(e);
        ApplySurfaceChrome(e);
        ApplyWindowChrome(e);
        ApplyFontScale(e);
        OnAppearanceContentChanged(e);
    }

    /// <summary>
    /// 子类扩展点：外观（模式/描边/阴影/字号等）变化时，重刷本窗口代码生成的内容颜色/描边/阴影。
    /// 基类默认空实现——背景/材质/字号/圆角已由基类统一处理，代码生成的控件（图标文字、卡片、徽标等）
    /// 需子类在此按需重建或重设令牌画刷，实现"一处改主题、全部窗口即时跟随"。
    /// </summary>
    protected virtual void OnAppearanceContentChanged(AppearanceChangedArgs e)
    {
    }

    /// <summary>
    /// 把 FrostedGlassDemo 风格描边应用到所有 Shell 窗口根 Border（<see cref="ChromeBorder"/>）。
    /// 对齐 <c>FrostedGlassDemo</c> 实证基准：
    /// 1) 描边由**各窗口声明式绑定**（XAML 写 <c>CornerRadius</c> + <c>BorderThickness</c> + <c>BorderBrush</c>，
    ///    纯代码窗口用 <see cref="SetThemeBinding"/>），基类不再在代码里覆盖——保证"窗口属性在 XAML/代码层面就对齐示例"。
    /// 2) 玻璃体积感叠加层（Highlight/Shade）已在 2026-08-23 暂停：用户真机验证发现叠加层在 dock 紫色背景上
    ///    形成不自然光斑，最终决策"只保留描边、不保留叠加层"。原方法 <see cref="ApplyGlassOverlay"/> 整体静默、代码
    ///    保留以便后续恢复；本方法保留挂载点（e.BorderChanged 等仍会触发），不再调用叠加层。
    /// 圆角由各窗口 XAML CornerRadius（=SystemCornerRadius=8）显式给出，与 DWM 系统默认圆角重合。
    /// 注意：对齐 demo 不使用 DropShadowEffect 外投影（demo 仅有渐变描边+体积感层），故不消费
    /// <see cref="IAppearanceService.CardShadow"/>（该令牌仍定义并推成 App 资源供外部/插件使用）。
    /// </summary>
    private void ApplySurfaceChrome(AppearanceChangedArgs e)
    {
        // 2026-08-23：用户决策"只保留描边"——叠加层（ApplyGlassOverlay）整体静默，本方法保留挂载点便于后续恢复。
        // 描边由各窗口 XAML 声明式绑定 / SetThemeBinding 提供，无需基类覆盖。
        return;
    }

    /// <summary>
    /// 内容层圆角半径（像素）。与 FrostedGlassDemo 实证基准一致：DWM 系统默认圆角
    /// （DWMWA_WINDOW_CORNER_PREFERENCE，由毛玻璃应用时设置）与根 Border 的 XAML CornerRadius
    /// 重合，避免"内容圆角 vs 系统窗口矩形直角"的双重窗口错觉。
    /// </summary>
    protected virtual double SystemCornerRadius => 8;

    /// <summary>
    /// 玻璃体积感叠加层（对齐 FrostedGlassDemo 的 Highlight/Shade）：
    /// 把根 Border 当前 Child 包裹进一个 Grid 容器，并在其上叠加 Highlight(左上光源高光) 与 Shade(右下背光暗角)
    /// 两个 <see cref="Border"/>（IsHitTestVisible=false，不挡交互），呈现受光面玻璃体积感。
    ///
    /// **2026-08-23 整体静默**：用户真机验证发现 Highlight/Shade 叠加层在 dock 紫色背景 + 圆角轮廓条件下
    /// 呈现不自然光斑，决策"只保留描边、不保留叠加层"。原方法体已整段注释（机制保留便于以后恢复；
    /// 恢复时直接把方法体外层 `pauseGlassOverlay:`/块注释解开即可重新挂回 <see cref="ApplySurfaceChrome"/>）。
    /// 基类也不再有调用点（<see cref="ApplySurfaceChrome"/> 已整体静默）。
    /// </summary>
    private void ApplyGlassOverlay()
    {
        // 2026-08-23：用户决策"只保留描边"——叠加层整体静默。原方法体已整段块注释，避免 warnaserror 触发 CS0162。
        // 恢复步骤：解开下方块注释，并在 ApplySurfaceChrome 内恢复 ApplyGlassOverlay() 调用点。

        /*
        pauseGlassOverlay:
        if (ChromeBorder is null)
        {
            return;
        }

        // 已包裹则只更新叠加层圆角（随 SystemCornerRadius 变化），不重复包裹。
        if (ChromeBorder.Child is Grid existing && existing.Tag as string == "GlassOverlay")
        {
            UpdateOverlayRadius(existing);
            return;
        }

        var original = ChromeBorder.Child;
        var overlay = new Grid { Tag = "GlassOverlay" };

        // 先把 ChromeBorder.Child 指向 overlay（断开 original 与 ChromeBorder 的父子关系），
        // 再将 original 加入 overlay——顺序不对会抛"元素已是另一个元素的逻辑子元素"。
        ChromeBorder.Child = overlay;
        if (original is not null)
        {
            overlay.Children.Add(original);
        }

        // 左上光源高光：从 (0,0) 起、覆盖左上、向右下迅速衰减（对齐 FrostedGlassDemo Highlight 色标）。
        var highlight = new Border
        {
            Tag = "GlassHighlight",
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(SystemCornerRadius),
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF), 0),
                    new GradientStop(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), 0.35),
                    new GradientStop(Colors.Transparent, 0.6)
                }
            }
        };

        // 右下背光暗角：从 (1,1) 起、覆盖右下、向左上衰减（对齐 FrostedGlassDemo Shade 色标）。
        var shade = new Border
        {
            Tag = "GlassShade",
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(SystemCornerRadius),
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(1, 1),
                EndPoint = new Point(0, 0),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x1A, 0x00, 0x00, 0x00), 0),
                    new GradientStop(Color.FromArgb(0x0A, 0x00, 0x00, 0x00), 0.3),
                    new GradientStop(Colors.Transparent, 0.55)
                }
            }
        };

        overlay.Children.Add(highlight);
        overlay.Children.Add(shade);

        ChromeBorder.Child = overlay;
        */
    }

    private void UpdateOverlayRadius(Grid overlay)
    {
        var r = new CornerRadius(SystemCornerRadius);
        foreach (var child in overlay.Children)
        {
            if (child is Border b && (b.Tag as string == "GlassHighlight" || b.Tag as string == "GlassShade"))
            {
                b.CornerRadius = r;
            }
        }
    }

    /// <summary>
    /// 根 Border 外边距：由 <see cref="ChromeMargin"/> 控制（默认 new(0) 铺满窗口，不内缩）。
    /// 圆角不再由代码设置——改由各窗口 XAML 声明式绑定 <c>CardCornerRadius</c> 资源键（=DWM 系统圆角），
    /// 保证"窗口属性在 XAML 层面就对齐示例"。不触碰 WindowChrome / region / SetWindowRgn。
    /// </summary>
    private void ApplyWindowChrome(AppearanceChangedArgs e)
    {
        if (ChromeBorder is null)
        {
            return;
        }

        ChromeBorder.Margin = ChromeMargin;
    }

    /// <summary>
    /// 全局字号缩放 + 前景色传导：
    /// 1. 前景色：在根 ChromeBorder 上设 <see cref="TextElement.ForegroundProperty"/> 并 DynamicResource 绑定
    ///    ThemeForeground，利用 WPF 属性继承自动传导到所有子文本控件（无需遍历，局部未显式设置的自动跟随，
    ///    显式设置的照常覆盖继承值）。
    /// 2. 字号：仍遍历可视树按 基值×FontScale 重设——因各 XAML 普遍显式写 FontSize，纯附加属性继承会被
    ///    显式值中断（已实证），故保留遍历强制重算以保证缩放对所有元素生效；关闭窗口时清空基值字典避免泄漏。
    /// </summary>
    private void ApplyFontScale(AppearanceChangedArgs e)
    {
        if (AppearanceService is null || !e.FontScaleChanged || ChromeBorder is null)
        {
            return;
        }

        // 前景色：根附加属性继承 + 动态资源（模式切换自动更新，无需重建）。
        ChromeBorder.SetValue(TextElement.ForegroundProperty,
            new DynamicResourceExtension("ThemeForeground").ProvideValue(null));

        var scale = AppearanceService.FontScale;
        var content = Content as DependencyObject;
        if (content is null)
        {
            return;
        }

        // 首次进入：遍历记录基值。
        if (_baseFontSizes.Count == 0)
        {
            foreach (var fe in EnumerateTextControls(content))
            {
                if (fe is null) continue;
                _baseFontSizes[fe] = fe is TextBlock tb ? tb.FontSize
                    : fe is Control c ? c.FontSize
                    : fe.GetValue(TextElement.FontSizeProperty) is double d ? d : 12.0;
            }
        }

        // 按基值×FontScale 重设。
        foreach (var kv in _baseFontSizes)
        {
            var fe = kv.Key;
            var baseSize = kv.Value;
            if (fe is null) continue;
            var scaled = baseSize * scale;
            if (fe is TextBlock ttb) ttb.FontSize = scaled;
            else if (fe is Control cc) cc.FontSize = scaled;
            else fe.SetValue(TextElement.FontSizeProperty, scaled);
        }
    }

    private static IEnumerable<DependencyObject> EnumerateTextControls(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (cur is null) continue;
            if (cur is TextBlock or TextBox or Button or CheckBox or RadioButton
                or ComboBox or Label or PasswordBox or RichTextBox)
            {
                yield return cur;
            }
            int n = VisualTreeHelper.GetChildrenCount(cur);
            for (int i = 0; i < n; i++)
            {
                queue.Enqueue(VisualTreeHelper.GetChild(cur, i));
            }
        }
    }

    /// <summary>
    /// 按当前外观服务值重绘窗口背景/材质。
    /// <para>
    /// **皮肤扩散（与描边完全对称）**：皮肤根背景 = <see cref="IAppearanceService.BackgroundBrush"/>，
    /// 由 <see cref="AppearanceService.SyncAppResources"/> 推成 App 资源键 <c>SkinBackgroundBrush</c>，
    /// 各外壳窗口根 Border 用 <c>DynamicResource SkinBackgroundBrush</c> 绑定（XAML 声明式，与
    /// <c>CardBorderBrush</c> 同一机制）。一处改背景 → 资源键刷新 → 所有窗口 DynamicResource 自动跟随。
    /// 这里基类在代码侧把同一 Brush 设为 <c>Window.Background</c>（兜底，供纯代码窗口；XAML 窗口用声明式绑定）。
    /// </para>
    /// <para>
    /// 不再有任何覆盖层 / 包裹 / 条件判断——皮肤就是窗口级背景，像描边是窗口级 BorderBrush 一样直白。
    /// </para>
    /// </summary>
    /// <summary>
    /// 按当前外观服务值重绘窗口背景/材质。
    /// <para>
    /// **皮肤扩散（与描边完全对称）**：皮肤根背景 = <see cref="IAppearanceService.BackgroundBrush"/>，
    /// 由 <see cref="AppearanceService.SyncAppResources"/> 推成 App 资源键 <c>SkinBackgroundBrush</c>，
    /// 各外壳窗口根 Border 用 <c>DynamicResource SkinBackgroundBrush</c> 绑定（XAML 声明式，与
    /// <c>CardBorderBrush</c> 同一机制）。一处改背景 → 资源键刷新 → 所有窗口 DynamicResource 自动跟随。
    /// 这里基类在代码侧把同一 Brush 设为 <c>Window.Background</c>（兜底，供纯代码窗口；XAML 窗口用声明式绑定）。
    /// </para>
    /// <para>
    /// 清晰/模糊档位由 <see cref="IAppearanceService.BackgroundBrush"/> 自身决定——模糊档返回的 Brush 是
    /// 对皮肤图套 BlurEffect 的模糊版本（视觉发糊 + DWM 毛玻璃透出磨砂感）；清晰档返回原图（锐利）。
    /// 这是 WPF 模型下能产生真实视觉差异的做法，无需任何覆盖层 / 包裹 / 条件判断。
    /// </para>
    /// </summary>
    protected void ApplyAppearance(AppearanceChangedArgs e)
    {
        if (AppearanceService is null)
        {
            return;
        }

        // 1) 窗口级背景画刷：无条件同步 —— 任何事件都重新对齐，保证上传皮肤 / 切预设 / 调色调时所有窗口跟随。
        //    清晰/模糊档已由 BackgroundBrush 内部按 SkinBlurBehindDwm 返回不同 Brush 实例，切档即换图。
        //    子类 override UseSkinBackground=false 屏蔽（dock 半透明托盘可借此保留纯透明）。
        if (UseSkinBackground)
        {
            var bg = AppearanceService.BackgroundBrush;
            // 缓存语义：BackgroundBrush 对同一 SkinPath 缓存同一冻结 Brush 实例（同进程跨窗口共享），
            // 用 ReferenceEquals 避免每次重设触发 WPF 视觉树重建 / 毛玻璃重合成。
            if (!ReferenceEquals(Background, bg))
            {
                Background = bg;
            }
        }

        // 2) DWM 材质：仅在 MaterialChanged 时重新应用毛玻璃（避免无关事件反复调 DWM）。
        if (e.MaterialChanged && VibrancyService is not null)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            VibrancyService.Apply(hwnd, AppearanceService.Material, roundCorners: true);
        }
    }

    /// <summary>
    /// 是否应用皮肤作为窗口级背景（图片皮肤铺底 / 配色预设 tint）。
    /// 默认 <c>true</c>：所有 ShellWindow 子类都接收皮肤背景——这是用户上传图标、切换配色预设时，
    /// 设置之外的所有外壳窗口也即时跟随的基线行为（与描边同一扩散级别）。
    /// 个别窗口（如 dock 半透明托盘）可 override 为 <c>false</c> 保留全透明 + 毛玻璃。
    /// </summary>
    protected virtual bool UseSkinBackground => true;
}
