// BetterDesktop.Shell.Core — 菜单栏扩展注册表契约（2026-09-07 P0-3/C2）
// 711 纪律：注册表只负责「登记 + 解析」，装配/装载由消费方（MenuBarWindow 右区 / MenuBarLeftZone 左区）
// 逐个容错执行——单扩展失败不拖垮宿主。

using System.Collections.Generic;

namespace BetterDesktop.Shell.Core.Contracts;

/// <summary>
/// 菜单栏扩展注册表：按 Id 登记/解析 IMenuBarExtension。
/// 注册线程安全；同 Id 重复注册以新替旧（先移除旧项）。
/// </summary>
public interface IMenuBarExtensionRegistry
{
    /// <summary>注册扩展（同 Id 已存在时先移除旧项再注册，幂等）。</summary>
    void Register(IMenuBarExtension extension);

    /// <summary>按 Id 移除扩展；返回是否确实移除了一个已注册扩展。</summary>
    bool Unregister(string id);

    /// <summary>按 Id 解析扩展；未注册返回 null。</summary>
    IMenuBarExtension? Get(string id);

    /// <summary>当前全部已注册扩展（装配顺序 = 注册顺序）。</summary>
    IReadOnlyList<IMenuBarExtension> GetAll();
}
