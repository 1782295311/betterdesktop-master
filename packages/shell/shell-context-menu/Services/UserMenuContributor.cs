using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>
/// 用户自定义菜单项（零代码 DIY 层，计划 §3.6）。
/// 存储于设置键 <c>context-menu.custom.items</c>（List&lt;UserMenuSpec&gt;，JSON 序列化）；
/// 编辑 UI 留 M2，M1 手编配置文件即时生效（每次 Build 读取）。
/// </summary>
public sealed class UserMenuSpec
{
    /// <summary>稳定标识（菜单项 Id = user.&lt;Id&gt;）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>菜单显示名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>可执行文件路径（不含参数；含空格无需引号——FileName 单独传递）。</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>参数模板，支持占位符 %file%/%files%/%dir%/%filename%/%name%/%desktop%（路径占位符自动引号包裹）。</summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>工作目录（支持 %dir%/%desktop% 占位符；空 = 沿用当前进程）。</summary>
    public string? WorkingDir { get; set; }

    /// <summary>作用域名（MenuScope 枚举名，如 Desktop / DesktopIcon）。</summary>
    public string Scope { get; set; } = nameof(MenuScope.DesktopIcon);

    /// <summary>适用文件类别（FileKind 枚举名逗号分隔，如 "WordDocument,Config"；空 = 该 Scope 全部适用）。</summary>
    public string? FileKinds { get; set; }

    /// <summary>以管理员运行（UAC 弹窗）。</summary>
    public bool RunAs { get; set; }

    /// <summary>最小化启动。</summary>
    public bool Minimized { get; set; }

    /// <summary>启用开关（false 不渲染）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>贡献优先级（越大越靠前）。</summary>
    public int Priority { get; set; }
}

/// <summary>
/// 用户自定义菜单项贡献者（每 Scope 一实例；Build 时读设置实现热更新）。
/// 占位符定版（计划 §3.6）：
///   %file%/%files% → 当前路径（多选逐个调用，每次注入一个路径；自动引号包裹）
///   %dir%          → 所在目录（引号包裹）
///   %filename%     → 文件名含扩展名；%name% → 无扩展名（纯文本不包裹）
///   %desktop%      → 桌面路径（引号包裹）
///   未识别占位符原样保留并记 DiagnosticLog（红线）。
/// </summary>
public sealed class UserMenuContributor(ISettingsService? settings, MenuScope scope) : IContextMenuContributor
{
    public const string SettingsKey = "context-menu.custom.items";

    public MenuScope Scope { get; } = scope;

    /// <summary>贡献排序：用户 Priority 之上加固定基数，保证自定义项排在组件贡献项之后、相对顺序用户可控。</summary>
    public int Priority => -100;

    public IReadOnlyList<MenuItemDef> Build(MenuRequest request)
    {
        if (settings is null) return [];
        List<UserMenuSpec>? specs;
        try
        {
            specs = settings.Get<List<UserMenuSpec>>(SettingsKey);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("context-menu", $"自定义项配置读取失败（忽略）: {ex.Message}");
            return [];
        }

        if (specs is null || specs.Count == 0) return [];

        var collected = new List<(UserMenuSpec Spec, MenuItemDef Item)>();
        foreach (var spec in specs)
        {
            if (!spec.Enabled || !string.Equals(spec.Scope, Scope.ToString(), StringComparison.OrdinalIgnoreCase))
                continue;
            if (!MatchesFileKind(spec, request))
                continue;

            var target = spec; // 捕获局部防闭包变更
            collected.Add((target, new MenuItemDef
            {
                Id = $"user.{target.Id}",
                Text = string.IsNullOrWhiteSpace(target.Name) ? target.Id : target.Name,
                Group = MenuGroup.Contribution,
                Command = () => Execute(target, request),
            }));
        }

        // 用户自定义项间按 spec.Priority 排序（越大越靠前；与全局贡献排序正交）
        return [.. collected.OrderByDescending(c => c.Spec.Priority).Select(c => c.Item)];
    }

    /// <summary>FileKind 过滤：声明为空 = 全部适用；否则 request.File.Kind 必须命中声明集合。</summary>
    private static bool MatchesFileKind(UserMenuSpec spec, MenuRequest request)
    {
        if (string.IsNullOrWhiteSpace(spec.FileKinds)) return true;
        if (request.File is null) return false;
        var declared = spec.FileKinds.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return declared.Any(d => string.Equals(d, request.File.Kind.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>执行：多选逐个调用（工具单文件语义优先，用户拍板）。</summary>
    private void Execute(UserMenuSpec spec, MenuRequest request)
    {
        var paths = request.SelectedPaths is { Count: > 0 } sel
            ? sel
            : request.File?.Path is { Length: > 0 } p ? [p] : [string.Empty];

        foreach (var path in paths)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = spec.Command,
                    Arguments = ExpandArguments(spec.Arguments, path),
                    UseShellExecute = true,
                    WindowStyle = spec.Minimized ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal,
                    WorkingDirectory = string.IsNullOrWhiteSpace(spec.WorkingDir)
                        ? string.Empty
                        : ExpandArguments(spec.WorkingDir, path).Trim('"'),
                    Verb = spec.RunAs ? "runas" : null,
                });
                DiagnosticLog.Trace("context-menu", $"自定义项 {spec.Id} 已启动 pid={process?.Id}");
            }
            catch (OperationCanceledException)
            {
                // UAC 取消
            }
            catch (Exception ex)
            {
                DiagnosticLog.Trace("context-menu", $"自定义项 {spec.Id} 执行失败: {ex.Message}");
            }
        }
    }

    /// <summary>占位符展开（静态便于单测；路径占位符自动引号包裹——红线：防空格路径撕裂命令行）。</summary>
    public static string ExpandArguments(string template, string path)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;

        var dir = string.IsNullOrEmpty(path) ? string.Empty : Path.GetDirectoryName(path) ?? string.Empty;
        var filename = Path.GetFileName(path);
        var name = Path.GetFileNameWithoutExtension(path);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        var result = template
            .Replace("%file%", Quote(path))
            .Replace("%files%", Quote(path))       // 多选由 Execute 逐个调用消化；单次展开等价 %file%
            .Replace("%dir%", Quote(dir))
            .Replace("%desktop%", Quote(desktop))
            .Replace("%filename%", filename)
            .Replace("%name%", name);

        foreach (Match m in Regex.Matches(template, "%[a-zA-Z0-9_]+%"))
        {
            if (!IsKnownPlaceholder(m.Value))
                DiagnosticLog.Trace("context-menu", $"未识别占位符 {m.Value}（原样保留）");
        }
        return result;
    }

    private static bool IsKnownPlaceholder(string token) => token.ToLowerInvariant() is
        "%file%" or "%files%" or "%dir%" or "%filename%" or "%name%" or "%desktop%";

    private static string Quote(string value) => $"\"{value}\"";
}
