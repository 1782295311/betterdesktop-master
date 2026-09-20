// BlurProbe — 毛玻璃"实拍"探针（诊断工具，不参与产品运行；见 csproj 头注释的缘由与用法）
//
// 【判定方法】在探针窗口后面放一块**黑白细条纹**背景，然后抓取探针窗口区域的真实屏幕像素：
//   · 模糊生效 → 条纹被糊掉，出现大量**中间灰**像素；
//   · 模糊没生效 → 像素仍是纯黑/纯白（或主题色），中间灰≈0。
// 这个判据与"配方/窗口样式/系统前提"都无关，是纯粹的"到底画没画出来"，所以能一次定案。
//
// 【测什么】五组对照：
//   ①分层 + BLURBEHIND        ②分层 + ACRYLIC(α=0)
//   ③非分层 + BLURBEHIND      ④非分层 + ACRYLIC(α=0)
//   ⑤对照组：不设任何 accent（预期中间灰 ≈ 0，作为测量基线）
// ①②③④ 中哪一组"中间灰"显著高于对照组，就是这台机器上真正可用的无色模糊配方。

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace BlurProbe;

internal static class Program
{
    // ---- accent 常量（与产品 DwmHelper 同源；未文档化 ABI，值不得臆改）----
    private const int WcaAccentPolicy = 19;
    private const int AccentDisabled = 0;
    private const int AccentEnableBlurBehind = 3;
    private const int AccentEnableAcrylicBlurBehind = 4;

    private const int WmDwmCompositionChanged = 0x031A;
    private const int SmRemoteSession = 0x1000;

    /// <summary>中间灰判据：既不是近黑也不是近白，即"被糊过"的像素。</summary>
    private const int GreyLow = 55;
    private const int GreyHigh = 200;

    private sealed record Variant(string Name, bool Layered, int AccentState, int AccentFlags, int AccentColor);

    private static readonly Variant[] Variants =
    {
        new("分层 + BLURBEHIND", true, AccentEnableBlurBehind, 0, 0),
        new("分层 + ACRYLIC(α=0)", true, AccentEnableAcrylicBlurBehind, 2, unchecked((int)0x00FFFFFF)),
        new("非分层 + BLURBEHIND", false, AccentEnableBlurBehind, 0, 0),
        new("非分层 + ACRYLIC(α=0)", false, AccentEnableAcrylicBlurBehind, 2, unchecked((int)0x00FFFFFF)),
        new("对照：无 accent", true, AccentDisabled, 0, 0),
    };

    [STAThread]
    public static void Main(string[] args)
    {
        // --quiet：不弹摘要窗（自动化/自检用；结果仍然写文件）。
        var quiet = Array.IndexOf(args, "--quiet") >= 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            var report = new StringBuilder();
            try
            {
                await RunAsync(report);
            }
            catch (Exception ex)
            {
                report.AppendLine("PROBE FAILED: " + ex);
            }
            finally
            {
                try
                {
                    var path = Path.Combine(AppContext.BaseDirectory, "blurprobe-result.txt");
                    File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
                    if (!quiet)
                    {
                        MessageBox.Show(report + "\n\n完整结果已写入:\n" + path, "BlurProbe", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                catch (Exception ex)
                {
                    if (!quiet)
                    {
                        MessageBox.Show(report + "\n\n写入文件失败: " + ex.Message, "BlurProbe", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }

                app.Shutdown();
            }
        };
        app.Run();
    }

    private static async Task RunAsync(StringBuilder report)
    {
        report.AppendLine("=== BetterDesktop 毛玻璃能力探针 (BlurProbe) ===");
        report.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        report.AppendLine();
        AppendEnvironment(report);
        report.AppendLine();
        report.AppendLine("--- 实拍结果（中间灰比例越高 = 模糊越明显）---");

        // 背景：黑白细条纹（6px），越细越容易看出"被糊掉"。
        var backdrop = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = false,
            Left = 80,
            Top = 80,
            Width = 1000,
            Height = 300,
            Background = CreateStripeBrush(),
        };
        backdrop.Show();

        var probes = new Window[Variants.Length];
        for (var i = 0; i < Variants.Length; i++)
        {
            probes[i] = CreateProbeWindow(Variants[i], 110 + (i * 190), 170, 170, 100);
            probes[i].Show();
        }

        // 等首帧 + DWM 应用材质（给足时间；这一步不需要精确，只要窗口画完）。
        await Task.Delay(900);

        for (var i = 0; i < Variants.Length; i++)
        {
            ApplyAccent(Variants[i], probes[i]);
        }

        await Task.Delay(900);

        var baseline = -1;
        for (var i = 0; i < Variants.Length; i++)
        {
            var m = Measure(probes[i]);
            report.AppendLine(FormatRow(Variants[i].Name, m, baseline));
            if (Variants[i].AccentState == AccentDisabled)
            {
                baseline = m.GreyPercent;
            }
        }

        report.AppendLine();
        report.AppendLine("--- 结论 ---");
        AppendConclusion(report, probes);

        foreach (var probe in probes)
        {
            probe.Close();
        }

        backdrop.Close();
    }

    /// <summary>一组测量结果：黑/白/中间灰 占比（三者相加 ≈ 100%）。</summary>
    private sealed record Measurement(int BlackPercent, int WhitePercent, int GreyPercent, string Verdict);

    private static string FormatRow(string name, Measurement m, int baseline)
    {
        var extra = baseline >= 0 ? $"，对照基线 {baseline}%" : string.Empty;
        return $"  {name,-22} 黑 {m.BlackPercent,3}%  白 {m.WhitePercent,3}%  灰 {m.GreyPercent,3}%   → {m.Verdict}{extra}";
    }

    private static void AppendConclusion(StringBuilder report, Window[] probes)
    {
        string? best = null;
        var bestScore = 0;
        for (var i = 0; i < Variants.Length; i++)
        {
            if (Variants[i].AccentState == AccentDisabled)
            {
                continue;
            }

            var score = Measure(probes[i]).GreyPercent;
            if (score > bestScore)
            {
                bestScore = score;
                best = Variants[i].Name;
            }
        }

        if (best is null || bestScore < 15)
        {
            report.AppendLine("  ❌ 五组里没有任何一组画出了模糊 → 问题在**系统前提**，不在配方：");
            report.AppendLine("     · 设置 → 个性化 → 颜色 → 透明效果 是否为「开」；");
            report.AppendLine("     · 是否处于省电模式（省电模式会强制关闭透明效果）；");
            report.AppendLine("     · 是否远程会话 / 虚拟机显卡驱动为基础显示适配器（DWM 合成受限）。");
            report.AppendLine("     → 程序内可用 BlurCapability 判据 + 设置页「一键开启透明效果」入口解决前者。");
            return;
        }

        report.AppendLine($"  ✅ 可用档位：{best}（中间灰 {bestScore}%）。");
        report.AppendLine("     → 把 settings.json 的 appearance.material.blur 设为对应档位（blurbehind）即可；");
        report.AppendLine("     → 若「分层 + BLURBEHIND」已生效，则无需开 appearance.material.glass（非分层玻璃窗）——多数机器都是这种情况；");
        report.AppendLine("     → 「ACRYLIC(α=0)」实测会把窗口刷成纯白/纯黑（不是无色），只作对比档用，不要选它。");
    }

    private static Window CreateProbeWindow(Variant variant, double left, double top, double width, double height)
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            UseLayoutRounding = true,

            // 关键：窗口层必须**没有任何颜色**，否则模糊被自己的底色压掉（这正是产品里"无色模式"
            // 曾经铺了 35%+40% 灰的教训）。分层走 Transparent，非分层也走 Transparent + 玻璃区。
            AllowsTransparency = variant.Layered,
            Background = Brushes.Transparent,
        };

        if (!variant.Layered)
        {
            // 非分层窗口：客户区是普通重定向位图，必须靠"铺满玻璃区"让透明像素由 DWM 补 backdrop。
            window.Loaded += (_, _) => ExtendGlass(new WindowInteropHelper(window).Handle);
        }

        return window;
    }

    private static void ApplyAccent(Variant variant, Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!variant.Layered)
        {
            ExtendGlass(hwnd);
        }

        var policy = new AccentPolicy
        {
            AccentState = variant.AccentState,
            AccentFlags = variant.AccentFlags,
            GradientColor = variant.AccentColor,
        };

        var size = Marshal.SizeOf<AccentPolicy>();
        var buffer = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(policy, buffer, false);
        try
        {
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = buffer,
                Size = size,
            };
            _ = SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        // 强制 DWM 重算（产品里同样补了这一步：不补的话设置常不刷新）。
        _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private static void ExtendGlass(IntPtr hwnd)
    {
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    /// <summary>
    /// 抓取探针窗口客户区的真实屏幕像素，按「黑 / 白 / 中间灰」分类并给出判读。
    /// <para>
    /// 为什么必须三种都统计：只看"中间灰=0"会把两种情况混为一谈 ——
    /// 「窗口全黑（没透明像素/没出图）」与「窗口透明但没模糊（看得见清晰条纹）」都是 0% 灰，
    /// 但结论完全相反（前者要改窗口样式，后者要改配方/环境前提）。
    /// </para>
    /// </summary>
    private static Measurement Measure(Window window)
    {
        var pixels = CaptureWindowClient(window, out var count);
        if (pixels is null || count == 0)
        {
            return new Measurement(0, 0, 0, "抓屏失败（窗口未就绪？）");
        }

        int black = 0, white = 0, grey = 0;
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var brightness = (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3;
            if (brightness <= GreyLow)
            {
                black++;
            }
            else if (brightness >= GreyHigh)
            {
                white++;
            }
            else
            {
                grey++;
            }
        }

        var blackPercent = (int)Math.Round(black * 100.0 / count);
        var whitePercent = (int)Math.Round(white * 100.0 / count);
        var greyPercent = 100 - blackPercent - whitePercent;
        return new Measurement(blackPercent, whitePercent, greyPercent, Interpret(blackPercent, whitePercent, greyPercent));
    }

    private static string Interpret(int black, int white, int grey)
    {
        if (grey >= 60)
        {
            return "✅ 模糊生效（背景被糊成中间灰）";
        }

        if (black >= 85)
        {
            return "❌ 整窗不透明黑（无透明像素 / 未出图）";
        }

        if (white >= 85)
        {
            return "❌ 整窗不透明白";
        }

        if (grey <= 15)
        {
            return "透明但无模糊（看到清晰条纹 ⇒ 配方/环境没生效）";
        }

        return "弱模糊（部分生效）";
    }

    /// <summary>抓探针窗口客户区对应的屏幕像素（BGRA，自上而下）。</summary>
    private static byte[]? CaptureWindowClient(Window window, out int pixelCount)
    {
        pixelCount = 0;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out var rc))
        {
            return null;
        }

        var origin = new PointStruct { X = 0, Y = 0 };
        _ = ClientToScreen(hwnd, ref origin);

        var width = rc.Right - rc.Left;
        var height = rc.Bottom - rc.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        pixelCount = width * height;
        return CaptureScreen(origin.X, origin.Y, width, height);
    }

    private static byte[]? CaptureScreen(int x, int y, int width, int height)
    {
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return null;
        }

        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        var previous = SelectObject(memDc, bitmap);
        try
        {
            // CAPTUREBLT：必须带，否则抓不到分层窗口（我们的探针正是分层窗口）。
            if (!BitBlt(memDc, 0, 0, width, height, screenDc, x, y, Srccopy | Captureblt))
            {
                return null;
            }

            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height, // 负数 = 自上而下，免得再翻一次行序
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                    SizeImage = width * height * 4,
                },
            };

            var stride = width * 4;
            var bytes = new byte[stride * height];
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var scanned = GetDIBits(memDc, bitmap, 0, (uint)height, handle.AddrOfPinnedObject(), ref info, 0);
                return scanned == 0 ? null : bytes;
            }
            finally
            {
                handle.Free();
            }
        }
        finally
        {
            _ = SelectObject(memDc, previous);
            _ = DeleteObject(bitmap);
            _ = DeleteDC(memDc);
            _ = ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static Brush CreateStripeBrush()
    {
        // 6px 黑白竖条：模糊半径远大于条纹宽度 → 一糊就变大片中间灰。
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Black, null, new RectangleGeometry(new Rect(0, 0, 6, 6))));
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(6, 0, 6, 6))));
        group.Freeze();

        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 12, 6),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }

    private static void AppendEnvironment(StringBuilder report)
    {
        var composition = DwmIsCompositionEnabled(out var enabled) == 0 && enabled;
        report.AppendLine("--- 系统前提（这四项任一不满足，任何配方都不可能出图）---");
        report.AppendLine($"  DWM 合成启用      : {composition}");
        report.AppendLine($"  系统透明效果      : {ReadTransparencyEnabled()}" +
                          "   (HKCU\\...\\Themes\\Personalize\\EnableTransparency)");
        report.AppendLine($"  远程会话          : {GetSystemMetrics(SmRemoteSession) != 0}");
        report.AppendLine($"  省电模式          : {ReadPowerSaverOn()}");
        report.AppendLine($"  Windows build     : {Environment.OSVersion.Version.Build}" +
                          "   (<22621 = Win10/11 21H2，无 DWM 系统材质，只剩 accent 路径)");
        report.AppendLine($"  进程/系统         : {(Environment.Is64BitProcess ? "x64" : "x86")} / {Environment.OSVersion.VersionString}");
    }

    private static bool ReadTransparencyEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var raw = key?.GetValue("EnableTransparency");
            return raw is not int value || value != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static bool ReadPowerSaverOn()
    {
        var status = default(SystemPowerStatus);
        return GetSystemPowerStatus(ref status) && (status.SystemStatusFlag & 0x01) != 0;
    }

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const int Srccopy = 0x00CC0020;
    private const int Captureblt = 0x40000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowCompositionAttribute(IntPtr hWnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled(out bool enabled);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(ref SystemPowerStatus status);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RectStruct rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref PointStruct point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hDc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr destDc, int x, int y, int width, int height, IntPtr srcDc, int srcX, int srcY, int rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hDc, IntPtr hBitmap, uint start, uint lines, IntPtr bits, ref BitmapInfo info, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectStruct
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AclineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
        public uint Reserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public int Colors;
    }
}
