using System;

namespace BetterDesktop.Shell.AppSource.Models;

/// <summary>
/// 应用项的稳定业务标识符。
/// </summary>
/// <remarks>
/// ID 生成规则：
/// - Win32 / Url / UserAdded：以可执行目标路径作为稳定来源；
/// - Store：以 AppUserModelId 作为稳定来源。
/// </remarks>
public readonly struct AppItemId : IEquatable<AppItemId>, IComparable<AppItemId>
{
    private readonly string _value;

    public AppItemId(string value)
    {
        _value = value ?? string.Empty;
    }

    public string Value => _value;

    public bool IsEmpty => string.IsNullOrEmpty(_value);

    public bool Equals(AppItemId other) => string.Equals(_value, other._value, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is AppItemId other && Equals(other);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(_value);

    public int CompareTo(AppItemId other) => string.Compare(_value, other._value, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => _value;

    public static bool operator ==(AppItemId left, AppItemId right) => left.Equals(right);

    public static bool operator !=(AppItemId left, AppItemId right) => !left.Equals(right);

    public static implicit operator string(AppItemId id) => id._value;

    public static implicit operator AppItemId(string value) => new(value);

    public static readonly AppItemId Empty = new();
}
