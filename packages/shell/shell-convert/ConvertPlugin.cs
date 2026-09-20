using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.Convert.Services;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.Convert;

// ============================================================
// 【白话导航 · 格式转换域】凭白话需求定位到精确文件：
//   "支持哪些格式互转 / 转换矩阵"        → Services/ConversionMatrix.cs（格式→引擎映射）
//   "PDF 转图片/文本、PDF 工具"          → Services/Engines/PopplerEngine.cs；合并/拆分 PdfComposeEngine.cs；加密 PdfSecurityEngine.cs；文本转 PDF TextPdfEngine.cs
//   "Office 文档转 PDF"                 → Engines/SofficeEngine.cs（LibreOffice 26.8.0 内置）、Engines/ComPdfEngine.cs（本机 Office/WPS COM）、Engines/ComPdfTwoHopEngine.cs（COM→PDF 中间态→Poppler 渲染）
//   "图片格式互转 / HEIC"               → Engines/ManagedImageEngine.cs、Engines/HeicEngine.cs、Engines/ImageTargetWriter.cs、RawDecodeEngine.cs
//   "音视频转码"                        → Engines/FfmpegEngine.cs
//   "电子书（epub/mobi）"               → Engines/CalibreEngine.cs
//   "OCR 文字识别"                      → Engines/TesseractEngine.cs
//   "Markdown / 文档互转"               → Engines/PandocEngine.cs + Services/Markdown/MarkdownTransformer.cs
//   "缺少转换工具时自动下载"            → Engines/EngineDownloader.cs
//   "引擎注册与选择"                    → Services/EngineRegistry.cs + Engines/IConversionEngine.cs（统一引擎契约）
//   "压缩 / 解压"                       → Services/ArchiveService.cs（IArchiveService）
//   "右键菜单里的转换入口"              → Services/ConvertMenuService.cs（IConvertMenuService）
// ============================================================

/// <summary>
/// 文档转换插件（shell-convert）：ConversionMatrix + EngineRegistry + 泛化 ConversionService。
/// 2026-09-07：转换入口挂点 = 桌面图标右键「转换为 ▸」（BuildIconMenuEntries）+ 系统右键级联 + 通配「更多格式…」（弹完整自绘菜单）。
/// 待各主体在自管菜单中显式申明转换项时恢复）；转换服务与引擎预热保持可用。
/// </summary>
public sealed class ConvertPlugin : IPlugin
{
    public string Name => "shell-convert";

    public IReadOnlyList<Type> Inject { get; } = [];

    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        var events = context.Get<IEventBus>();
        var settings = context.Get<ISettingsService>();

        // 引擎注册表（同类按注册序取第一个可用者；Prefer→Fallback 候选链见 EngineRegistry）
        // 2026-09-10 架构主线：pandoc 主 + soffice/COM 辅（LibreOffice 26.8.0 内置回归；ComPdfTwoHop 承接演示族 png/jpg 两跳）。
        var registry = new EngineRegistry()
            .Add(new Services.Engines.SofficeEngine())
            .Add(new Services.Engines.ComPdfEngine())
            .Add(new Services.Engines.ManagedEngine(settings))
            // 2026-09-10 收尾：ComPdfTwoHop 注册序在 TwoHopEngine 之前（演示族 png/jpg 两跳 COM 优先保真，
            // TwoHopEngine(soffice 中间态) 兜底——COM 缺失时保证"高亮=成功"契约成立）
            .Add(new Services.Engines.ComPdfTwoHopEngine())
            .Add(new Services.Engines.TwoHopEngine())
            .Add(new Services.Engines.ManagedImageEngine())
            .Add(new Services.Engines.PdfComposeEngine())
            .Add(new Services.Engines.PopplerEngine())
            .Add(new Services.Engines.PandocEngine())
            .Add(new Services.Engines.FfmpegEngine())
            .Add(new Services.Engines.TextPdfEngine())
            .Add(new Services.Engines.TesseractEngine())
            .Add(new Services.Engines.HeicEngine())
            .Add(new Services.Engines.RawDecodeEngine())
            .Add(new Services.Engines.CalibreEngine())
            // 2026-09-20 convert-lite 迁移：进程内轻量引擎（零外部 exe、恒可用）。
            // 放在最后只是因为它是最后加进来的 —— 解析按矩阵的 Prefer/Fallback 查 kind，与注册序无关。
            .Add(new Services.Engines.LiteEngine());
        var convertService = new ConversionService(registry, events, settings);
        // 【2026-09-07 恢复转换挂点】菜单服务 Provide 给桌面右键 / 系统右键命令桥共用：
        // 矩阵全部目标列出（引擎缺失置灰），执行 + MessageBox 反馈闭环见 ConvertMenuService。
        context.Provide<Contracts.IConvertMenuService>(new Services.ConvertMenuService(registry, convertService));
        // 2026-09-07：系统级联子菜单 convert-to-* 直转入口（命令桥经此执行，避免重复实例化）
        context.Provide<Services.ConversionService>(convertService);
        // 2026-09-07：压缩/解压（zip 内置 + WinRAR 引擎探测）——自绘右键 / 系统右键命令桥共用
        context.Provide<Contracts.IArchiveService>(new Services.ArchiveService());

        // 后台静默预热真实 --version 探测（dependency-on-demand 红线 2；菜单只读缓存零阻塞）
        _ = Task.Run(() =>
        {
            try
            {
                Services.Engines.SofficeEngine.EnsureProbed();
                Services.Engines.PopplerEngine.EnsureProbed();
                Services.Engines.PandocEngine.EnsureProbed();
                Services.Engines.FfmpegEngine.EnsureProbed();
                Services.Engines.TesseractEngine.EnsureProbed();
                Services.Engines.CalibreEngine.EnsureProbed();
                DiagnosticLog.Trace("shell-convert",
                    $"引擎预热完成 soffice={Services.Engines.SofficeEngine.EnsureProbed().Available} " +
                    $"poppler={Services.Engines.PopplerEngine.EnsureProbed().Available} " +
                    $"pandoc={Services.Engines.PandocEngine.EnsureProbed().Available} " +
                    $"ffmpeg={Services.Engines.FfmpegEngine.EnsureProbed().Available} " +
                    $"tesseract={Services.Engines.TesseractEngine.EnsureProbed().Available} " +
                    $"calibre={Services.Engines.CalibreEngine.EnsureProbed().Available}");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Trace("shell-convert", $"引擎预热异常(忽略): {ex.Message}");
            }
        }, cancellationToken);

        DiagnosticLog.Trace("shell-convert", "转换引擎已装载并预热（转换右键入口待主体自管菜单申明）");
        return Task.CompletedTask;
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
