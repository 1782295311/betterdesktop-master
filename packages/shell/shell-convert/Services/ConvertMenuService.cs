using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.Convert.Contracts;

namespace BetterDesktop.Shell.Convert.Services;

/// <summary>
/// 转换菜单服务实现：矩阵全部目标列出（"要全，不能遗漏"），引擎缺失置灰（IsEnabled=false）不隐藏；
/// 点击 → ConversionService 异步执行 → MessageBox 反馈成功/失败计数。
/// </summary>
public sealed class ConvertMenuService : IConvertMenuService
{
    /// <summary>目标类别分组顺序（菜单内自上而下：文本→文档→表格→图像→音频→视频→其他）。</summary>
    private static readonly TargetCategory[] CategoryOrder =
    [
        TargetCategory.Text,
        TargetCategory.Document,
        TargetCategory.Spreadsheet,
        TargetCategory.Image,
        TargetCategory.Audio,
        TargetCategory.Video,
        TargetCategory.Other,
    ];

    /// <summary>目标类别 → 组标题文本（2026-09-10 分类分组）。</summary>
    private static readonly IReadOnlyDictionary<TargetCategory, string> CategoryNames =
        new Dictionary<TargetCategory, string>
        {
            [TargetCategory.Text] = "文本类",
            [TargetCategory.Document] = "文档类",
            [TargetCategory.Spreadsheet] = "表格类",
            [TargetCategory.Image] = "图像类",
            [TargetCategory.Audio] = "音频类",
            [TargetCategory.Video] = "视频类",
            [TargetCategory.Other] = "其他",
        };

    private readonly EngineRegistry _registry;
    private readonly ConversionService _service;

    public ConvertMenuService(EngineRegistry registry, ConversionService service)
    {
        _registry = registry;
        _service = service;
    }

    /// <inheritdoc />
    public IReadOnlyList<MenuItemDef> BuildMenuItems(IReadOnlyList<string> paths)
    {
        var items = new List<MenuItemDef>();
        if (paths is not { Count: > 0 })
        {
            return items;
        }

        var first = paths[0];
        if (string.IsNullOrWhiteSpace(first) || !File.Exists(first))
        {
            return items;
        }

        // 多选：全 PDF → 合并；全图片 → 合成 + 图片互转批量
        if (paths.Count > 1)
        {
            if (ConversionMatrix.AllPdf(paths))
            {
                items.Add(new MenuItemDef
                {
                    Id = "mergePdf",
                    Text = "合并 PDF",
                    Kind = MenuItemKind.Command,
                    IsEnabled = IsEngineReady(first, "pdf"),
                    Command = () => RunAndNotify(paths, "pdf"),
                });
                return items;
            }

            if (ConversionMatrix.AllImages(paths))
            {
                items.Add(new MenuItemDef
                {
                    Id = "composePdf",
                    Text = "合成 PDF",
                    Kind = MenuItemKind.Command,
                    IsEnabled = IsEngineReady(first, "pdf"),
                    Command = () => RunAndNotify(paths, "pdf"),
                });
                AddConvertSubmenu(items, paths);
                return items;
            }
        }

        // 混合类型（多选且扩展名不一致）→ 不提供批量转换
        if (paths.Select(p => Path.GetExtension(p)).Distinct().Count() > 1)
        {
            return items;
        }

        AddConvertSubmenu(items, paths);

        // PDF 安全（2026-09-07 补全）：加密/解密需密码（菜单输入），单文件常驻项
        if (paths.Count == 1 && Path.GetExtension(first).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new MenuItemDef { Id = "pdfEncrypt", Text = "加密 PDF", Kind = MenuItemKind.Command, Command = () => RunEncryptDecrypt(paths, isEncrypt: true) });
            items.Add(new MenuItemDef { Id = "pdfDecrypt", Text = "解密 PDF", Kind = MenuItemKind.Command, Command = () => RunEncryptDecrypt(paths, isEncrypt: false) });
        }

        return items;
    }

    /// <inheritdoc />
    public IReadOnlyList<ConvertSystemMenuTarget> BuildSystemMenuTargets()
    {
        var byFormat = new Dictionary<string, TargetAccumulator>(StringComparer.Ordinal);
        var firstSeen = new List<string>();

        foreach (var extension in ConversionMatrix.AllInputExtensions)
        {
            // 合成路径：各引擎的 CanHandle 只读 Path.GetExtension（不校验文件是否存在），
            // 因此无需真实文件即可判定「该源扩展 → 该目标」的引擎就绪性。
            var sample = "sample" + extension;
            foreach (var target in ConversionMatrix.GetTargets(extension))
            {
                if (!target.Lossless)
                {
                    continue; // 红线 8：系统菜单只放无损项（出现即承诺可无损转成功）
                }
                if (!IsEngineReady(sample, target.Format))
                {
                    continue; // 引擎缺失不注册 —— 原生菜单无法置灰，宁可不出现
                }

                if (!byFormat.TryGetValue(target.Format, out var accumulator))
                {
                    accumulator = new TargetAccumulator(target.Label, target.Category);
                    byFormat[target.Format] = accumulator;
                    firstSeen.Add(target.Format);
                }
                accumulator.Sources.Add(extension);
            }
        }

        // 展示顺序：先按既有类别顺序（与自绘菜单分组一致），同类别内保持矩阵首现顺序（LINQ OrderBy 稳定）。
        return firstSeen
            .Select(format => (Format: format, Accumulator: byFormat[format]))
            .OrderBy(pair => Array.IndexOf(CategoryOrder, pair.Accumulator.Category))
            .Select(pair => new ConvertSystemMenuTarget(
                pair.Format, pair.Accumulator.Label, pair.Accumulator.Sources))
            .ToList();
    }

    /// <summary>同一目标格式跨多个源扩展的累加器（SourceExtensions 全集 + 首次出现的类别/显示名）。</summary>
    private sealed class TargetAccumulator(string label, TargetCategory category)
    {
        public string Label { get; } = label;

        public TargetCategory Category { get; } = category;

        public List<string> Sources { get; } = [];
    }

    /// <summary>PDF 加密/解密：弹密码框（UI 线程）→ 带密码转换 → 反馈。密码不落日志。</summary>
    private void RunEncryptDecrypt(IReadOnlyList<string> paths, bool isEncrypt)
    {
        _ = Task.Run(async () =>
        {
            string message;
            try
            {
                var title = isEncrypt ? "加密 PDF" : "解密 PDF";
                var prompt = isEncrypt ? "设置 PDF 打开密码：" : "输入 PDF 打开密码（原密码）：";
                var password = Application.Current?.Dispatcher.Invoke(
                    () => PasswordPrompt.Show(title, prompt, isPassword: true));
                if (string.IsNullOrWhiteSpace(password))
                {
                    message = "已取消：未输入密码";
                }
                else
                {
                    var results = await _service.ConvertWithPasswordAsync(
                        paths, isEncrypt ? "pdf-encrypt" : "pdf-decrypt", password);
                    var ok = results.Count(r => r.Success);
                    message = ok == 1
                        ? $"{(isEncrypt ? "加密" : "解密")}完成：{results[0].Output ?? "(未知输出)"}"
                        : $"{(isEncrypt ? "加密" : "解密")}失败：{(string.IsNullOrWhiteSpace(results[0].Message) ? results[0].Error.ToString() : results[0].Message)}";
                }
            }
            catch (Exception ex)
            {
                message = $"{(isEncrypt ? "加密" : "解密")}异常：{ex.Message}";
            }

            NotifyOnUi(message);
        });
    }

    /// <summary>「格式转换」全部可用目标子菜单。
    /// 2026-09-10 分类分组：目标按 TargetCategory 分组显示（组标题=置灰文本项、空组/全不可用组隐藏）；
    /// 只显示引擎就绪可用的目标（排除无关/缺失，不置灰展示不可用项）；
    /// 高亮=可无损转换（Highlighted=加粗，用户拍板）；有损项不高亮、点击弹风险确认（源文件不受影响）。</summary>
    private void AddConvertSubmenu(List<MenuItemDef> items, IReadOnlyList<string> paths)
    {
        var first = paths[0];
        var ext = Path.GetExtension(first);
        var targets = ConversionMatrix.GetTargets(ext);
        if (targets.Count == 0)
        {
            return;
        }

        var children = new List<MenuItemDef>();
        foreach (var category in CategoryOrder)
        {
            var groupTargets = targets.Where(t => t.Category == category).ToList();
            if (groupTargets.Count == 0)
            {
                continue; // 空组隐藏
            }

            var groupItems = new List<MenuItemDef>();
            foreach (var target in groupTargets)
            {
                var format = target.Format;
                var ready = IsEngineReady(first, format);
                if (!ready)
                {
                    continue; // 2026-09-10：只显示引擎就绪可用的目标（排除无关/缺失），不置灰展示不可用项
                }
                groupItems.Add(new MenuItemDef
                {
                    Id = "convertTo" + format,
                    Text = target.Label,
                    Kind = MenuItemKind.Command,
                    IsEnabled = true,
                    // 2026-09-10 高亮契约：只有「可无损转换」才高亮（加粗，用户拍板"对象本身可被无损转换才被高亮标记起来"）；
                    // 有损项正常显示（不置灰）但不高亮，点击弹风险确认（源文件不受影响）；无损项同时被系统级联独占注册。
                    Highlighted = target.Lossless,
                    // M3.1 统一标识：= 系统注册表 verb = CLI 路由键（自绘/系统/CLI 同源）
                    Action = "convert-to-" + format,
                    Command = () => RunAndNotify(paths, format, target.Lossless),
                });
            }
            if (groupItems.Count == 0)
            {
                continue; // 组内无可执行项 → 整组隐藏（不显示空组标题）
            }

            if (children.Count > 0)
            {
                children.Add(new MenuItemDef
                {
                    Id = "sep-" + category,
                    Text = CategoryNames[category],
                    Kind = MenuItemKind.Command,
                    IsEnabled = false, // 组标题（置灰文本，非可执行项）
                });
            }
            else
            {
                children.Add(new MenuItemDef
                {
                    Id = "hdr-" + category,
                    Text = CategoryNames[category],
                    Kind = MenuItemKind.Command,
                    IsEnabled = false, // 首个组标题
                });
            }
            children.AddRange(groupItems);
        }

        if (children.Count > 0)
        {
            items.Add(new MenuItemDef
            {
                Id = "convertTo",
                Text = "格式转换",
                Kind = MenuItemKind.Submenu,
                Children = children,
            });
        }
    }

    /// <summary>目标当前是否有可执行引擎（Resolve = 引擎可用 + 能处理该文件）。</summary>
    private bool IsEngineReady(string path, string format)
    {
        var target = ConversionMatrix.Find(Path.GetExtension(path), format);
        return target is not null && _registry.Resolve([path], target) is not null;
    }

    /// <summary>异步执行 + UI 线程 MessageBox 反馈（成功/失败计数，禁止部分成功当全成功）。
    /// 有损目标（lossless=false）先弹风险确认（用户拍板：不置灰、提示风险、源文件不受影响）。</summary>
    private void RunAndNotify(IReadOnlyList<string> paths, string format, bool lossless = true)
    {
        if (!lossless)
        {
            var confirm = MessageBox.Show(
                "该转换结果可能损失质量/信息，源文件不会被修改。继续？",
                "格式转换",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK)
            {
                return; // 用户取消，不执行
            }
        }

        _ = Task.Run(async () =>
        {
            string message;
            try
            {
                var results = await _service.ConvertAsync(paths, format);
                var ok = results.Count(r => r.Success);
                var fail = results.Count - ok;
                message = results.Count == 1
                    ? (ok == 1
                        ? $"转换完成：{results[0].Output ?? "(未知输出)"}"
                        : $"转换失败：{(string.IsNullOrWhiteSpace(results[0].Message) ? results[0].Error.ToString() : results[0].Message)}")
                    : $"转换完成：成功 {ok} 个，失败 {fail} 个";
            }
            catch (Exception ex)
            {
                message = $"转换异常：{ex.Message}";
            }

            NotifyOnUi(message);
        });
    }

    private static void NotifyOnUi(string message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            MessageBox.Show(message, "格式转换");
            return;
        }

        dispatcher.BeginInvoke(() => MessageBox.Show(message, "格式转换"));
    }
}
