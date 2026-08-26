// BetterDesktop.Kernel.Hmr — PluginStateSnapshot 插件状态快照
// 键 + 不透明载荷（旧配置迁移载体）

namespace BetterDesktop.Kernel.Hmr;

/// <summary>插件状态快照：键 + 不透明载荷（JSON 或任意字符串）。</summary>
public sealed class PluginStateSnapshot
{
    /// <summary>构造。</summary>
    public PluginStateSnapshot(string key, string payload)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("状态键不能为空", nameof(key));
        }
        Key = key;
        Payload = payload ?? string.Empty;
    }

    /// <summary>状态键（schema 标识，用于跨版本迁移判断）。</summary>
    public string Key { get; }

    /// <summary>不透明载荷。</summary>
    public string Payload { get; }
}
