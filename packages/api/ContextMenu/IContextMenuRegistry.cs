namespace BetterDesktop.Shell.ContextMenus.Contracts;

/// <summary>
/// 统一菜单注册服务（M3.1）：插件/内置功能以 <see cref="ContextMenuContribution"/> 声明一次，
/// 由实现负责系统注册表注入（HKCU verb，命令指向 BetterDesktop.Cli.exe --menu-cmd &lt;Action&gt;）。
/// 自绘路与 CLI 路通过「Action 标识」同源对齐（见 ContextMenuContribution 注释），不在本服务内重复登记。
/// 幂等：重复注册重写同值；Unregister 删除键树（含级联子键）。
/// </summary>
public interface IContextMenuRegistry
{
    /// <summary>注册（幂等重写）。返回 false = 注册失败（已记日志，不抛出）。</summary>
    bool Register(ContextMenuContribution contribution);

    /// <summary>注销（删键树，幂等）。</summary>
    bool Unregister(string id);

    /// <summary>当前是否已注册（IsChecked 状态源）。</summary>
    bool IsRegistered(string id);
}
