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

    /// <summary>PDF 快捷项（置顶）+ 「转换为 ▸」全部目标子菜单（含引擎置灰）。</summary>
    private void AddConvertSubmenu(List<MenuItemDef> items, IReadOnlyList<string> paths)
    {
        var first = paths[0];
        var ext = Path.GetExtension(first);
        var targets = ConversionMatrix.GetTargets(ext);
        if (targets.Count == 0)
        {
            return;
        }

        // PDF 快捷项置顶（矩阵含 pdf 时）
        if (targets.Any(t => t.Format == "pdf"))
        {
            items.Add(new MenuItemDef
            {
                Id = "convertPdf",
                Text = "转换为 PDF",
                Kind = MenuItemKind.Command,
                IsEnabled = IsEngineReady(first, "pdf"),
                Command = () => RunAndNotify(paths, "pdf"),
            });
        }

        // 「转换为 ▸」：全部目标（矩阵全量，引擎缺失置灰不隐藏——"要全"）
        var children = new List<MenuItemDef>();
        foreach (var target in targets)
        {
            if (target.Format == "pdf")
            {
                continue; // PDF 已作为快捷项
            }

            var format = target.Format;
            children.Add(new MenuItemDef
            {
                Id = "convertTo" + format,
                Text = target.Label,
                Kind = MenuItemKind.Command,
                IsEnabled = IsEngineReady(first, format),
                Command = () => RunAndNotify(paths, format),
            });
        }

        if (children.Count > 0)
        {
            items.Add(new MenuItemDef
            {
                Id = "convertTo",
                Text = "转换为",
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

    /// <summary>异步执行 + UI 线程 MessageBox 反馈（成功/失败计数，禁止部分成功当全成功）。</summary>
    private void RunAndNotify(IReadOnlyList<string> paths, string format)
    {
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
