using System;

namespace BetterDesktop.Shell.Settings.Contracts;

/// <summary>
/// 设置变更事件载荷。
/// </summary>
public sealed record SettingsChangedEventArgs(string Key, object? Value);

/// <summary>
/// 统一设置服务：键值持久化（%APPDATA%\BetterDesktop\settings.json）+ 变更通知。
/// 所有插件经此读写自己的设置（如 menu-bar.displayMode / context-menu.shell.displayMode），
/// 避免各自维护配置文件。
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// 读取设置；不存在或类型不匹配时返回 <paramref name="defaultValue"/>。
    /// </summary>
    T? Get<T>(string key, T? defaultValue = default);

    /// <summary>
    /// 写入设置并持久化；变更经内核事件总线广播（事件名 ShellEvents.SettingsChanged）。
    /// </summary>
    void Set<T>(string key, T value);
}
