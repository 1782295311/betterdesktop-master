// BetterDesktop.Kernel.Loader — 加载选项
// 配置路径 + 内置插件工厂注册表

using BetterDesktop.Kernel.Contracts;

namespace BetterDesktop.Kernel.Loader;

/// <summary>loader 选项。</summary>
public sealed class LoaderOptions
{
    /// <summary>cordis.yml 路径（相对/绝对）。</summary>
    public string ConfigPath { get; set; } = "cordis.yml";

    /// <summary>内置插件工厂注册表：name → 工厂。未知工厂名按 fail-closed 记录失败。</summary>
    public Dictionary<string, Func<IPlugin>> Factories { get; } = new(StringComparer.Ordinal);
}
