// BetterDesktop.Kernel.Hmr — SemanticVersion 语义化版本（SemVer 子集）
// 插件与内核 ABI 版本比较的唯一载体

using System.Globalization;

namespace BetterDesktop.Kernel.Hmr;

/// <summary>语义化版本（SemVer 子集）：Major.Minor.Patch[-PreRelease]。</summary>
public readonly struct SemanticVersion : IEquatable<SemanticVersion>, IComparable<SemanticVersion>
{
    /// <summary>构造：预发布段可为 null（表示正式版）。</summary>
    public SemanticVersion(int major, int minor, int patch, string? preRelease = null)
    {
        if (major < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(major));
        }
        if (minor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minor));
        }
        if (patch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(patch));
        }
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = string.IsNullOrWhiteSpace(preRelease) ? null : preRelease;
    }

    /// <summary>主版本号。</summary>
    public int Major { get; }

    /// <summary>次版本号。</summary>
    public int Minor { get; }

    /// <summary>修订号。</summary>
    public int Patch { get; }

    /// <summary>预发布段（正式版为 null）。</summary>
    public string? PreRelease { get; }

    /// <summary>解析版本字符串，失败抛 FormatException。</summary>
    public static SemanticVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"无法解析语义化版本：{text}");

    /// <summary>尝试解析版本字符串（容忍 v 前缀与缺省段）。</summary>
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var input = text.Trim();
        if (input.Length > 0 && (input[0] == 'v' || input[0] == 'V'))
        {
            input = input[1..];
        }
        var dash = input.IndexOf('-');
        string numeric;
        string? pre = null;
        if (dash >= 0)
        {
            numeric = input[..dash];
            pre = input[(dash + 1)..];
            if (pre.Length == 0)
            {
                return false;
            }
        }
        else
        {
            numeric = input;
        }
        var parts = numeric.Split('.');
        if (parts.Length is < 1 or > 3)
        {
            return false;
        }
        var values = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]) || values[i] < 0)
            {
                return false;
            }
        }
        version = new SemanticVersion(values[0], values[1], values[2], pre);
        return true;
    }

    /// <summary>是否满足最低版本且主版本一致（ABI 兼容判定：主版本变更视为不兼容）。</summary>
    public bool IsAbiCompatibleWith(SemanticVersion minimum) => Major == minimum.Major && CompareTo(minimum) >= 0;

    /// <inheritdoc />
    public int CompareTo(SemanticVersion other)
    {
        var comparison = Major.CompareTo(other.Major);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = Minor.CompareTo(other.Minor);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = Patch.CompareTo(other.Patch);
        if (comparison != 0)
        {
            return comparison;
        }
        if (PreRelease is null)
        {
            return other.PreRelease is null ? 0 : 1;
        }
        if (other.PreRelease is null)
        {
            return -1;
        }
        return StringComparer.Ordinal.Compare(PreRelease, other.PreRelease);
    }

    /// <inheritdoc />
    public bool Equals(SemanticVersion other) => CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);

    /// <inheritdoc />
    public override string ToString() => PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";

    /// <summary>相等比较。</summary>
    public static bool operator ==(SemanticVersion left, SemanticVersion right) => left.Equals(right);

    /// <summary>不等比较。</summary>
    public static bool operator !=(SemanticVersion left, SemanticVersion right) => !left.Equals(right);

    /// <summary>小于比较。</summary>
    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    /// <summary>小于等于比较。</summary>
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    /// <summary>大于比较。</summary>
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    /// <summary>大于等于比较。</summary>
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;
}
