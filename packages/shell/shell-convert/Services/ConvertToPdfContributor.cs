using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;

namespace BetterDesktop.Shell.Convert.Services;

/// <summary>
/// 「转 PDF」菜单贡献项（第一菜单原则：第一层平铺，不设二级）。
/// 隐藏优先：仅文档类（Word/Excel/Presentation）且至少一个引擎可用时显示；转换中防重复请求。
/// </summary>
public sealed class ConvertToPdfContributor(DocumentConversionService service, MenuScope scope) : IContextMenuContributor
{
    /// <summary>排在用户自定义项（-100）之后：组件贡献项基数更低。</summary>
    public int Priority => -200;

    public MenuScope Scope { get; } = scope;

    public IReadOnlyList<MenuItemDef> Build(MenuRequest request)
    {
        if (request.File is not { } identity)
        {
            return [];
        }

        // 文档类判定（M1 支持集；Unknown/其它类型不显示）
        if (identity.Kind is not (FileKind.WordDocument or FileKind.ExcelWorkbook or FileKind.Presentation))
        {
            return [];
        }

        // 引擎可用性（进程级缓存；无引擎整组隐藏）
        if (!ConvertEngineLocator.HasPdfEngine())
        {
            return [];
        }

        var path = identity.Path;
        return
        [
            new MenuItemDef
            {
                Id = "convert.to-pdf",
                Text = "转 PDF",
                Group = MenuGroup.Contribution,
                Command = () =>
                {
                    // 菜单点击立即返回，转换后台执行；结果经 convert/* 事件通知
                    _ = service.ConvertToPdfAsync(path);
                },
            }
        ];
    }
}
