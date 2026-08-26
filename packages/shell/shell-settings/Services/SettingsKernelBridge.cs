// BetterDesktop.Shell.Settings — SettingsKernelBridge 设置分区↔内核桥接
// 设置分区（ISettingsSection.Build 仅接 ISettingsService/IThemeTokens）需要访问内核单例
// （如 IHmrManager 以即时改内存治理阈值）。本桥接在 SettingsPlugin.LoadAsync 时把内核引用存入，
// 供各分区静态取回，避免改动分区接口签名（接口冻结面 ADR-002 D1）。

using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Hmr;

namespace BetterDesktop.Shell.Settings.Services;

/// <summary>设置分区访问内核单例的轻量桥接（进程级，由 SettingsPlugin 注入）。</summary>
public static class SettingsKernelBridge
{
    private static IHmrManager? _hmrManager;

    /// <summary>由 SettingsPlugin 在 LoadAsync 时调用，注入内核引用。</summary>
    public static void Bind(IContext context)
    {
        _hmrManager = context.Get<IHmrManager>();
    }

    /// <summary>尝试取回已绑定的 HMR 管理器（未绑定返回 false）。</summary>
    public static bool TryGetHmrManager(out IHmrManager hmr)
    {
        hmr = _hmrManager!;
        return hmr is not null;
    }
}
