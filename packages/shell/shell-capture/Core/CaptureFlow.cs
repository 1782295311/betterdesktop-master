using System;
using System.Threading.Tasks;
using System.Windows;
using BetterDesktop.Capture.Contracts;
using BetterDesktop.Shell.Capture.Native;
using BetterDesktop.Shell.Capture.UI;

namespace BetterDesktop.Shell.Capture.Core;

/// <summary>
/// 交互截图流编排（一条会话 = 全屏采集 → 覆盖层选区 → 工具条 → [编辑器/OCR/贴图] → 写剪贴板）。
/// 帧与临时文件生命周期在此收敛：会话结束（完成/取消/异常）删除临时 PNG；
/// 任何一步失败不产生历史条目、不残留 temp（红线 3）。
/// </summary>
public sealed class CaptureFlow
{
    private readonly ScreenCaptureService _service;
    private readonly Action<string?> _completed;

    private string? _fullPngPath;
    private string? _artifactPngPath;
    private PixelRect _artifactRect;
    private OverlayController? _overlay;
    private string? _ocrText; // OCR 面板识别文本（完成后尽力挂到同一截图条目，DoD D5）

    public bool IsActive { get; private set; }

    public CaptureFlow(ScreenCaptureService service, Action<string?> completed)
    {
        _service = service;
        _completed = completed;
    }

    /// <summary>开始一次截图（异步采集全屏 → 覆盖层）。</summary>
    public async void Start()
    {
        if (IsActive)
        {
            return;
        }
        IsActive = true;
        try
        {
            CaptureLog.Info("截图会话开始（全屏采集）");
            var result = await _service.CaptureAsync(CaptureRequest.FullScreen(), default);

            if (!result.Success || result.PngPath is null)
            {
                CaptureLog.Error($"全屏采集失败：{result.Error}");
                IsActive = false;
                _completed?.Invoke($"截图失败：{result.Error ?? "未知原因"}");
                return;
            }

            if (result.DegradeReason is { Length: > 0 } degrade)
            {
                CaptureLog.Warn($"截图降级提示：{degrade}");
            }

            _fullPngPath = result.PngPath;
            _artifactPngPath = result.PngPath;
            _artifactRect = result.VirtualScreenRect;

            _overlay = new OverlayController(
                _fullPngPath,
                selected: OnRegionSelected,
                confirmed: OnOverlayCompleted,
                annotate: OpenEditor,
                ocr: OpenOcrPanel,
                sticker: OpenSticker,
                cancelled: OnOverlayCancelled);
            _overlay.Show();
        }
        catch (Exception ex)
        {
            CaptureLog.Error($"截图会话异常：{ex}");
            CleanupSession();
            IsActive = false;
            _completed?.Invoke($"截图失败：{ex.Message}");
        }
    }

    private void OnOverlayCancelled()
    {
        CleanupSession();
        _completed?.Invoke(null);
    }

    /// <summary>拖选定稿：从全屏 PNG 裁剪出产物（同一帧，不二次采集）。覆盖层保持"已截图"状态。</summary>
    private void OnRegionSelected(PixelRect region)
    {
        if (_fullPngPath is null)
        {
            CleanupSession();
            return;
        }

        try
        {
            byte[] full = System.IO.File.ReadAllBytes(_fullPngPath);
            if (!PngCodec.CropPng(full, region, out byte[] cropped, out string cropError))
            {
                CaptureLog.Error($"选区裁剪失败：{cropError}");
                _completed?.Invoke($"截图失败：{cropError}");
                CleanupSession();
                return;
            }

            // 旧产物（=全屏）在裁剪后不再需要；新产物落盘。
            DeleteBestEffort(_artifactPngPath);
            _artifactPngPath = TempFileManager.WriteTempPng(cropped);
            _artifactRect = region;
            CaptureLog.Info($"选区定稿：{region.Width}×{region.Height}@{region}");
        }
        catch (Exception ex)
        {
            CaptureLog.Error($"选区处理异常：{ex}");
            _completed?.Invoke($"截图失败：{ex.Message}");
            CleanupSession();
        }
    }

    /// <summary>完成（点空白处 / 底部"完成"）：写剪贴板 → 会话结束（覆盖层随 CleanupSession 关闭）。</summary>
    private void OnOverlayCompleted()
    {
        if (_artifactPngPath is null)
        {
            CleanupSession();
            return;
        }
        FinishToClipboard();
    }

    /// <summary>写剪贴板 → 会话结束（D1 主链路终点）。</summary>
    private void FinishToClipboard()
    {
        if (_artifactPngPath is null)
        {
            CleanupSession();
            return;
        }

        try
        {
            byte[] png = System.IO.File.ReadAllBytes(_artifactPngPath);
            if (!WritePngWithRetry(png, out string error))
            {
                CaptureLog.Error($"写剪贴板失败：{error}");
                _completed?.Invoke($"写剪贴板失败：{error}");
                CleanupSession();
                return;
            }

            CaptureLog.Info($"已写入剪贴板（{_artifactRect.Width}×{_artifactRect.Height}）");
            AttachOcrToEntryBestEffort(_artifactRect.Width, _artifactRect.Height);
            CleanupSession();
            _completed?.Invoke($"已复制截图（{_artifactRect.Width}×{_artifactRect.Height}）");
        }
        catch (Exception ex)
        {
            CaptureLog.Error($"写剪贴板异常：{ex}");
            _completed?.Invoke($"写剪贴板失败：{ex.Message}");
            CleanupSession();
        }
    }

    private void OpenEditor()
    {
        if (_artifactPngPath is null)
        {
            return;
        }
        // 功能与截图保存解耦（2026-09-15 用户）：点功能 = 离开覆盖层进入功能，不默认保存截图。
        // 功能窗口使用独立捕获的路径，不碰会话字段（防贴图停留期间新截图会话覆盖字段的竞态）。
        var srcPath = _artifactPngPath;
        var rect = _artifactRect;
        CloseOverlayOnly();
        try
        {
            var editor = new EditorWindow(srcPath, rect, (newPath, err) => FinishEditorFeature(srcPath, newPath, err));
            editor.Show();
        }
        catch (Exception ex)
        {
            // 功能窗口异常不崩进程：记录并提示
            CaptureLog.Error($"打开标注窗口失败：{ex}");
            _completed?.Invoke($"打开标注失败：{ex.Message}");
        }
    }

    /// <summary>标注功能收尾：完成=标注图保存进历史（主动产出）；取消=只清理本功能文件。</summary>
    private void FinishEditorFeature(string srcPath, string newPngPath, string? error)
    {
        if (error is not null || string.IsNullOrEmpty(newPngPath))
        {
            _completed?.Invoke(error ?? "标注已取消");
            DeleteBestEffort(srcPath);
            return;
        }
        DeleteBestEffort(srcPath);
        try
        {
            byte[] png = System.IO.File.ReadAllBytes(newPngPath);
            if (!WritePngWithRetry(png, out string writeError))
            {
                CaptureLog.Error($"写标注图失败：{writeError}");
                _completed?.Invoke($"写标注图失败：{writeError}");
                return;
            }
            CaptureLog.Info("标注完成，已写入剪贴板");
            _completed?.Invoke("已复制标注截图");
        }
        catch (Exception ex)
        {
            CaptureLog.Error($"写标注图异常：{ex}");
            _completed?.Invoke($"写标注图失败：{ex.Message}");
        }
        finally
        {
            DeleteBestEffort(newPngPath);
        }
    }

    private void OpenOcrPanel()
    {
        if (_artifactPngPath is null)
        {
            return;
        }
        // 功能与截图保存解耦：OCR 不保存截图本身；结果经「复制」进系统剪贴板 → 引擎自动入历史
        var srcPath = _artifactPngPath;
        CloseOverlayOnly();
        try
        {
            var ocr = new OcrPanelWindow(srcPath, () => OnOcrPanelClosed(srcPath));
            ocr.ResultCaptured += text => _ocrText = text;
            ocr.Show();
        }
        catch (Exception ex)
        {
            CaptureLog.Error($"打开 OCR 面板失败：{ex}");
            _completed?.Invoke($"打开 OCR 失败：{ex.Message}");
        }
    }

    private void OnOcrPanelClosed(string srcPath)
    {
        // OCR 会话结束：不保存截图（功能解耦）；只清理本功能文件
        _ocrText = null;
        DeleteBestEffort(srcPath);
    }

    /// <summary>
    /// 写图片剪贴板 + 重试：Win32 WriteFormats 失败 = OpenClipboard 失败 = 整块没写（不会部分写入，
    /// 与 .NET SetText 的分段行为不同），所以失败即真没写，重试竞争窗口即可。
    /// </summary>
    private static bool WritePngWithRetry(byte[] png, out string error)
    {
        error = string.Empty;
        for (int i = 0; i < 3; i++)
        {
            if (ClipboardImageWriter.WritePng(png, out error))
            {
                return true;
            }
            if (i < 2)
            {
                System.Threading.Thread.Sleep(150);
            }
        }
        return false;
    }

    /// <summary>
    /// 写剪贴板后：若本会话有 OCR 文本，尽力关联到引擎刚捕获的同一图片条目（DoD D5）。
    /// 轮询 get_last 直到出现与本产物同尺寸的新图片条目 → set_ocr_text（失败仅告警，不阻断主链路）。
    /// </summary>
    private void AttachOcrToEntryBestEffort(int width, int height)
    {
        if (string.IsNullOrEmpty(_ocrText))
        {
            return;
        }
        string ocrText = _ocrText!;
        _ = Task.Run(() =>
        {
            try
            {
                using var client = new BetterDesktop.Shell.Clipboard.Ipc.ClipboardIpcClient(
                    new BetterDesktop.Shell.Clipboard.Ipc.NamedPipeTransport());
                client.Connect();
                for (int i = 0; i < 12; i++) // ≤ ~3.6s
                {
                    System.Threading.Thread.Sleep(300);
                    var entry = client.GetLastEntry(TimeSpan.FromSeconds(6));
                    if (entry is null)
                    {
                        continue;
                    }
                    if (entry.ContentType == BetterDesktop.Shell.Clipboard.Contracts.ClipboardItemKind.Image &&
                        entry.ImageWidth == width && entry.ImageHeight == height &&
                        entry.Timestamp >= DateTime.Now - TimeSpan.FromSeconds(8))
                    {
                        client.SetEntryOcrText(entry.Id, ocrText);
                        CaptureLog.Info($"OCR 文本已关联截图条目 {entry.Id}");
                        return;
                    }
                }
                CaptureLog.Warn("OCR 文本关联失败：未在窗口内找到本截图条目（图片仍在剪贴板）");
            }
            catch (Exception ex)
            {
                CaptureLog.Warn($"OCR 文本关联失败：{ex.Message}");
            }
        });
    }

    private void OpenSticker()
    {
        if (_artifactPngPath is null)
        {
            return;
        }
        // 功能与截图保存解耦：贴图 = 桌面展示，不写剪贴板历史；关闭贴图才清理本功能文件
        var srcPath = _artifactPngPath;
        CloseOverlayOnly();
        try
        {
            var sticker = new StickerWindow(srcPath, CaptureSettings.StickerTopmost, () => DeleteBestEffort(srcPath));
            sticker.Show();
        }
        catch (Exception ex)
        {
            CaptureLog.Error($"打开贴图窗口失败：{ex}");
            DeleteBestEffort(srcPath);
            _completed?.Invoke($"打开贴图失败：{ex.Message}");
        }
    }

    /// <summary>仅关闭覆盖层（释放全屏 Topmost 挡屏），不删临时文件——功能窗口仍在使用产物。</summary>
    private void CloseOverlayOnly()
    {
        _overlay?.CloseAll();
        _overlay = null;
        IsActive = false;
    }

    private static void DeleteBestEffort(string? path) => TempFileManager.DeleteBestEffort(path);

    private void CleanupSession()
    {
        // 先关覆盖层（释放 BitmapImage 文件句柄），再删临时文件
        _overlay?.CloseAll();
        _overlay = null;
        IsActive = false;
        DeleteBestEffort(_artifactPngPath);
        DeleteBestEffort(_fullPngPath);
        _artifactPngPath = null;
        _fullPngPath = null;
    }
}
