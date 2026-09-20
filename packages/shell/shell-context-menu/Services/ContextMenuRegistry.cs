using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using Microsoft.Win32;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>
/// 统一菜单注册服务实现（M3.1）：ContextMenuContribution 声明 → HKCU 注册表 verb 注入。
/// 注入红线（复用 shell-menu-injection 资产）：HKCU 直写（普通用户权限，无夺权）；
/// verb = Id（同场景唯一）；命令 = CLI --menu-cmd &lt;Action&gt; &lt;Args&gt;；MUIVerb ≤ 80 字符；
/// Extended → 注册表 Extended 值（Shift 扩展）；幂等重写（宿主每次装配重写同值，天然去重）。
/// 自绘路与 CLI 路经「Action 标识」同源对齐，不在本类重复登记。
/// </summary>
public sealed class ContextMenuRegistry : IContextMenuRegistry
{
    private const string ClassesRoot = @"Software\Classes";

    /// <inheritdoc />
    public bool Register(ContextMenuContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        if (string.IsNullOrWhiteSpace(contribution.Id) || string.IsNullOrWhiteSpace(contribution.Title)
            || string.IsNullOrWhiteSpace(contribution.Action))
        {
            DiagnosticLog.Trace("shell.context-menu", $"ContextMenu 注册拒绝：Id/Title/Action 不能为空 (id={contribution.Id})");
            return false;
        }

        try
        {
            var cli = MenuCommandPaths.GetCliPath();
            foreach (var regPath in ScenePaths(contribution.Scene, contribution.Id))
            {
                using var key = Registry.CurrentUser.CreateSubKey(regPath);
                key.SetValue(null, contribution.Title);
                key.SetValue("MUIVerb", contribution.Title);
                if (!string.IsNullOrWhiteSpace(contribution.IconPath))
                {
                    key.SetValue("Icon", $"\"{contribution.IconPath}\"");
                }
                if (contribution.Extended)
                {
                    key.SetValue("Extended", string.Empty);
                }

                using var cmd = key.CreateSubKey("command");
                cmd.SetValue(null, BuildCommand(cli, contribution.Action, contribution.Args));
            }

            DiagnosticLog.Trace("shell.context-menu",
                $"ContextMenu 已注册: {contribution.Id} ({contribution.Scene}) → cli --menu-cmd {contribution.Action}");
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.context-menu", $"ContextMenu 注册失败 {contribution.Id}: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc />
    public bool Unregister(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        try
        {
            // 删除全部场景下的该 verb 键树（Id 即 verb 键名）。
            foreach (var scene in Enum.GetValues<MenuScene>())
            {
                foreach (var regPath in ScenePaths(scene, id))
                {
                    Registry.CurrentUser.DeleteSubKeyTree(regPath, throwOnMissingSubKey: false);
                }
            }

            DiagnosticLog.Trace("shell.context-menu", $"ContextMenu 已注销: {id}");
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.context-menu", $"ContextMenu 注销失败 {id}: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc />
    public bool IsRegistered(string id)
    {
        try
        {
            return Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\*\shell\" + id, writable: false) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>场景 → 注册表路径（Id 即 verb 键名；All = 三场景全注）。</summary>
    private static IEnumerable<string> ScenePaths(MenuScene scene, string verb)
    {
        switch (scene)
        {
            case MenuScene.File:
                yield return $@"{ClassesRoot}\*\shell\{verb}";
                break;
            case MenuScene.Directory:
                yield return $@"{ClassesRoot}\Directory\shell\{verb}";
                break;
            case MenuScene.Background:
                yield return $@"{ClassesRoot}\Directory\Background\shell\{verb}";
                break;
            case MenuScene.All:
                yield return $@"{ClassesRoot}\*\shell\{verb}";
                yield return $@"{ClassesRoot}\Directory\shell\{verb}";
                yield return $@"{ClassesRoot}\Directory\Background\shell\{verb}";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scene), scene, null);
        }
    }

    /// <summary>命令 = "cli" --menu-cmd &lt;action&gt; &lt;args...&gt;（参数原样拼入，含 %1/%V 占位）。</summary>
    private static string BuildCommand(string cli, string action, IReadOnlyList<string> args)
    {
        var quoted = args.Select(a => $"\"{a}\"");
        return $"\"{cli}\" --menu-cmd {action} {string.Join(" ", quoted)}".TrimEnd();
    }
}
