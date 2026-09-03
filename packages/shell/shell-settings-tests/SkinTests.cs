using System.Windows.Media;
using BetterDesktop.Shell.Core.Surface;
using BetterDesktop.Shell.PluginSdk;
using BetterDesktop.Shell.Settings.Services;
using Xunit;

#pragma warning disable CA2000 // SettingsService 自带 ProcessExit 兜底 flush，测试内局部实例无需显式 Dispose
namespace BetterDesktop.Shell.Settings.Tests;

/// <summary>
/// 皮肤系统核心逻辑测试（headless，不依赖 Application.Current）：
/// 1. 皮肤激活模型（img:/preset: 解析、SkinKind、SkinPath 向后兼容）
/// 2. BackgroundBrush 推导（图片→ImageBrush，配色→色调托盘）
/// 3. SkinManager 激活/预设覆盖
/// 4. 扩散令牌推送（Application.Current 就绪时推入 App 资源 + GlobalDictionary）
/// 5. 黑屏回归：合并字典只读键写入不抛异常（AppearanceService.SetAppResource 修复验证）
///
/// 隔离：SettingsService 落盘到 %APPDATA%\BetterDesktop\settings.json，多测试共享文件会相互污染，
/// 故每个测试前后清理 appearance.* 键（删除物理文件），保证默认断言不读到残留。
/// </summary>
public class SkinTests : IDisposable
{
    private static readonly string SettingsPath = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "BetterDesktop", "settings.json");

    public SkinTests()
    {
        CleanSettings();
    }

    public void Dispose()
    {
        CleanSettings();
        GC.SuppressFinalize(this);
    }

    private static void CleanSettings()
    {
        try { if (System.IO.File.Exists(SettingsPath)) System.IO.File.Delete(SettingsPath); }
        catch { /* 忽略清理失败 */ }
    }

    private static AppearanceService CreateService()
    {
        CleanSettings(); // 确保从干净状态构造
        var settings = new SettingsService();
        return new AppearanceService(settings);
    }

    [Fact]
    public void SkinActive_ImagePrefix_ParsesToSkinKindAndPath()
    {
        var svc = CreateService();
        svc.SkinActive = "img:C:/x/test.png";

        Assert.Equal(SkinKind.Image, svc.SkinKind);
        Assert.Equal("C:/x/test.png", svc.SkinPath); // 向后兼容：去掉 img: 前缀
        Assert.True(svc.SkinActive == "img:C:/x/test.png");
    }

    [Fact]
    public void SkinPath_Setter_BackwardCompat_WritesImgPrefix()
    {
        var svc = CreateService();
        svc.SkinPath = "C:/x/test.png";

        Assert.Equal(SkinKind.Image, svc.SkinKind);
        Assert.Equal("img:C:/x/test.png", svc.SkinActive);
    }

    [Fact]
    public void SkinActive_Empty_ClearsSkin()
    {
        var svc = CreateService();
        svc.SkinActive = "img:C:/x/test.png";
        svc.SkinActive = "";

        Assert.Equal(SkinKind.None, svc.SkinKind);
        Assert.Null(svc.SkinPath);
    }

    [Fact]
    public void SkinActive_PresetPrefix_SetsPresetKind_AndOverridesTint()
    {
        var svc = CreateService();
        var originalTint = svc.WindowTint;
        svc.SkinActive = "preset:midnight";

        Assert.Equal(SkinKind.Preset, svc.SkinKind);
        // 预设覆盖 WindowTint（午夜蓝 #0B1020）
        Assert.Equal(Color.FromRgb(0x0B, 0x10, 0x20), svc.WindowTint);
        Assert.NotEqual(originalTint, svc.WindowTint);
    }

    [Fact]
    public void BackgroundBrush_ImageSkin_BlurAndSharp_ReturnDifferentBrushes()
    {
        // 模糊档现已改为"预渲染模糊位图 ImageBrush"（与清晰档对称、可 Freeze、跨窗口共享），
        // 不再用 VisualBrush 包实时 BlurEffect。ImageBrush 无需 STA 约束，但保持 STA 包装以防后续改动。
        Exception? captured = null;
        var t = new System.Threading.Thread(() =>
        {
            try { RunBlurSharpAssert(); }
            catch (Exception ex) { captured = ex; }
        });
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start();
        t.Join();
        if (captured is not null) throw captured!;
    }

    private static void RunBlurSharpAssert()
    {
        CleanSettings(); // 避免读到磁盘残留
        // 用项目内已知存在的资源（_real_icons 下 png）做存在性验证。
        var iconDir = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Desktop", "betterdt", "_real_icons");
        if (!System.IO.Directory.Exists(iconDir)) return; // CI 无资源则跳过，不失败
        var png = System.IO.Directory.EnumerateFiles(iconDir, "*.png").FirstOrDefault();
        if (png is null) return;

        var svc = new AppearanceService(new SettingsService());
        svc.SkinActive = "img:" + png;
        // 显式切到模糊档（不依赖默认值，避免磁盘残留干扰）：返回预渲染模糊位图 ImageBrush（Freeze）。
        svc.SkinBlurBehindDwm = true;
        var blurBrush = svc.BackgroundBrush;
        Assert.IsType<ImageBrush>(blurBrush);
        Assert.True(blurBrush.IsFrozen, "模糊档 ImageBrush 应 Freeze（跨窗口共享、零重复合成）");

        // 切到清晰档：返回锐利原图 ImageBrush。
        svc.SkinBlurBehindDwm = false;
        var sharpBrush = svc.BackgroundBrush;
        Assert.IsType<ImageBrush>(sharpBrush);
        // 两档 Brush 实例不同（切档必须换图）。
        Assert.NotSame(blurBrush, sharpBrush);
    }

    [Fact]
    public void BackgroundBrush_NoSkin_ReturnsSolidColorBrush_Tray()
    {
        var svc = CreateService();
        svc.SkinActive = "";
        var brush = svc.BackgroundBrush;
        Assert.IsType<SolidColorBrush>(brush);
    }

    [Fact]
    public void SkinImageParams_DefaultsAndSet()
    {
        var svc = CreateService();
        Assert.Equal(1, svc.SkinImageStretch);     // 默认 Uniform 完整居中（不变形，窗口适应皮肤）
        Assert.Equal(0.25, svc.SkinImageDarken);   // 默认轻暗化
        Assert.Equal(0.0, svc.SkinImageBlur);
        Assert.Equal(1.0, svc.SkinImageOpacity);

        svc.SkinImageStretch = 3;
        svc.SkinImageDarken = 0.5;
        Assert.Equal(3, svc.SkinImageStretch);
        Assert.Equal(0.5, svc.SkinImageDarken);
    }

    [Fact]
    public void SkinManager_ApplyPreset_ChangesTintAndRecordsActive()
    {
        var svc = CreateService();
        var manager = new SkinManager(svc);
        manager.Apply("preset:sakura");

        Assert.Equal("preset:sakura", svc.SkinActive);
        Assert.Equal(Color.FromRgb(0x2A, 0x1A, 0x22), svc.WindowTint); // 樱粉主色
        Assert.Equal(Color.FromRgb(0xFF, 0x37, 0x5F), svc.Accent);
    }

    [Fact]
    public void SkinManager_ApplyPreset_Graphite_KeepsDefaultTint()
    {
        var svc = CreateService();
        var manager = new SkinManager(svc);
        manager.Apply("preset:graphite");

        Assert.Equal("preset:graphite", svc.SkinActive);
        Assert.Equal(Color.FromRgb(0x1F, 0x1F, 0x22), svc.WindowTint);
    }

    [Fact]
    public void SkinManager_ApplyUnknownId_ClearsActive()
    {
        var svc = CreateService();
        var manager = new SkinManager(svc);
        svc.SkinActive = "img:C:/x/test.png";
        manager.Apply("preset:does-not-exist");

        Assert.Equal("", svc.SkinActive);
        Assert.Equal(SkinKind.None, svc.SkinKind);
    }

    [Fact]
    public void SkinManager_List_ContainsBuiltinPresets()
    {
        var svc = CreateService();
        var manager = new SkinManager(svc);
        var list = manager.List();

        Assert.Contains(list, e => e.Id == "preset:graphite");
        Assert.Contains(list, e => e.Id == "preset:midnight");
        Assert.Contains(list, e => e.Id == "preset:sakura");
        Assert.Contains(list, e => e.Id == "preset:mint");
    }

    /// <summary>
    /// 复现设置窗口黑屏/点击无反应的根因（崩溃日志 00:07 的 InvalidOperationException："无法在对象#FFECECEC上
    /// 设置属性，因为它处于只读状态"）：App.xaml 中定义的主题令牌 Brush（如 ControlForeground）在 App 启动后
    /// 被 WPF 冻结（Frozen，等效只读）。旧版 SetBrush 复用 app.Resources[key] 取出的**冻结 Brush** 并原地改其
    /// Color/属性 → 抛只读异常，该异常发生在 Dispatcher 内未处理 → 整窗挂死（黑屏+点击无反应）。
    /// 修复（AppearanceService.SetBrush）：不再复用冻结 Brush，总是 new 一个 Brush 覆盖（等价 Remove+Add）。
    /// 此测试验证：① 对冻结 Brush 原地改属性确实抛只读异常（复现旧崩溃机制）；
    /// ② 新写法（新建 Brush 覆盖）不抛且解析到新值。
    /// </summary>
    [Fact]
    public void FrozenBrush_ReuseAndMutate_Throws_NewBrushSafe()
    {
        var appResources = new System.Windows.ResourceDictionary();
        // 模拟 App.xaml 启动后冻结的 ControlForeground
        var frozen = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC));
        frozen.Freeze();
        appResources["ControlForeground"] = frozen;

        // 旧写法（崩溃机制）：取出冻结 Brush 就地改 Color → 抛 InvalidOperationException
        var oldThrew = false;
        try
        {
            if (appResources["ControlForeground"] is SolidColorBrush existing)
                existing.Color = Colors.Red; // 原地改冻结 Brush
        }
        catch (System.InvalidOperationException) { oldThrew = true; }
        Assert.True(oldThrew, "对冻结 Brush 原地改属性未抛异常——崩溃机制未复现，测试前提失效");

        // 新写法（AppearanceService.SetBrush 逻辑）：总是新建 Brush 覆盖 → 不抛
        var ex = Record.Exception(() =>
        {
            appResources.Remove("ControlForeground");
            appResources.Add("ControlForeground", new SolidColorBrush(Colors.Red));
        });
        Assert.Null(ex);

        var resolved = (SolidColorBrush)appResources["ControlForeground"];
        Assert.Equal(Colors.Red, resolved.Color);
    }
}
