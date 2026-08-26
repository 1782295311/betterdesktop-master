using System;

namespace BetterDesktop.Shell.Dock.Models;

/// <summary>
/// Dock 项目业务主键（强类型）。
/// Win32/Url 以路径为主键来源；UWP 以 AppUserModelId 为主键来源。
/// </summary>
public readonly record struct DockItemId(string Value) : IEquatable<DockItemId>
{
    /// <inheritdoc />
    public bool Equals(DockItemId other) => string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value ?? string.Empty);

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}
