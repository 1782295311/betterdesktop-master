using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using BetterDesktop.Capture.Contracts;
using BetterDesktop.Shell.Capture.Core;
using BetterDesktop.Shell.Capture.Native;

namespace BetterDesktop.Shell.Capture.Cli;

/// <summary>
/// 无头截图 CLI（headless）：采集 → 写剪贴板（走系统剪贴板，引擎监听主路径捕获，零新 IPC 动词）→ 可选存盘。
/// exit 0 = 成功，1 = 失败（参数错误/采集失败/写剪贴板失败）。
/// 输出一行 JSON（stdout）：{ ok, width, height, backend, degradeReason, error, savedTo }。
/// </summary>
public static class CaptureCli
{
    public static int Run(string[] args)
    {
        try
        {
            var request = Parse(args, out string? saveTo, out string? error);
            if (request is null)
            {
                Print(new { ok = false, error = error ?? "参数错误" });
                return 1;
            }

            using var service = new ScreenCaptureService();
            var result = service.CaptureAsync(request, default).GetAwaiter().GetResult();
            if (!result.Success || result.PngPath is null)
            {
                Print(new { ok = false, error = result.Error ?? "采集失败", backend = result.BackendUsed.ToString() });
                return 1;
            }

            if (saveTo is not null)
            {
                try
                {
                    File.Copy(result.PngPath, saveTo, overwrite: true);
                }
                catch (Exception ex)
                {
                    Print(new { ok = false, error = $"保存失败：{ex.Message}" });
                    TempFileManager.DeleteBestEffort(result.PngPath);
                    return 1;
                }
            }

            byte[] png = File.ReadAllBytes(result.PngPath);
            if (!ClipboardImageWriter.WritePng(png, out string writeError))
            {
                Print(new { ok = false, error = $"写剪贴板失败：{writeError}", backend = result.BackendUsed.ToString() });
                TempFileManager.DeleteBestEffort(result.PngPath);
                return 1;
            }

            Print(new
            {
                ok = true,
                width = result.Width,
                height = result.Height,
                backend = result.BackendUsed.ToString(),
                degradeReason = result.DegradeReason,
                saveTo,
            });
            TempFileManager.DeleteBestEffort(result.PngPath);
            return 0;
        }
        catch (Exception ex)
        {
            Print(new { ok = false, error = $"内部异常：{ex.Message}" });
            return 1;
        }
    }

    private static CaptureRequest? Parse(string[] args, out string? saveTo, out string? error)
    {
        saveTo = null;
        error = null;

        var mode = CaptureMode.FullScreen;
        PixelRect? region = null;
        IntPtr window = IntPtr.Zero;
        bool includeCursor = true;
        bool hdrTonemap = true;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--region":
                    if (++i >= args.Length || !TryParseRect(args[i], out region))
                    {
                        error = "--region 需要 X,Y,Width,Height（物理像素）";
                        return null;
                    }
                    mode = CaptureMode.Region;
                    break;
                case "--window":
                    if (++i >= args.Length || !long.TryParse(args[i], out long hwnd) || hwnd <= 0)
                    {
                        error = "--window 需要十进制窗口句柄";
                        return null;
                    }
                    window = new IntPtr(hwnd);
                    mode = CaptureMode.Window;
                    break;
                case "--save":
                    if (++i >= args.Length)
                    {
                        error = "--save 需要输出路径";
                        return null;
                    }
                    saveTo = args[i];
                    break;
                case "--no-cursor":
                    includeCursor = false;
                    break;
                case "--no-tonemap":
                    hdrTonemap = false;
                    break;
                default:
                    error = $"未知参数：{args[i]}（支持 --region X,Y,W,H / --window HWND / --save PATH / --no-cursor / --no-tonemap）";
                    return null;
            }
        }

        return mode switch
        {
            CaptureMode.Region => CaptureRequest.RegionOf(region ?? default, includeCursor, hdrTonemap),
            CaptureMode.Window => CaptureRequest.WindowOf(window, includeCursor, hdrTonemap),
            _ => CaptureRequest.FullScreen(includeCursor, hdrTonemap),
        };
    }

    private static bool TryParseRect(string s, out PixelRect? rect)
    {
        rect = null;
        var parts = s.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
        {
            return false;
        }
        if (!int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y) ||
            !int.TryParse(parts[2], out int w) || !int.TryParse(parts[3], out int h) || w <= 0 || h <= 0)
        {
            return false;
        }
        rect = new PixelRect(x, y, w, h);
        return true;
    }

    private static void Print(object payload) =>
        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false }));
}
