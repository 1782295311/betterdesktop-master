// BetterDesktop.Shell.Dock — 拼音搜索匹配器
// 技术库资产：1201-pinyin-fuzzy-search（拼音/首字母/模糊搜索）的 C# 移植变体。
// 原变体 A 是 TypeScript（Fuse.js + pinyin-pro）；本变体 B 用 TinyPinyin.Net 驱动
// （netstandard 兼容，规避 NU1701×TreatWarningsAsErrors），仅做"包含/前缀"匹配
// 而非全量模糊评分——应用名集合有限，精确三路匹配足够且可解释。

using System.Collections.Concurrent;
using System.Text;
using TinyPinyin;

namespace BetterDesktop.Shell.Dock.Services;

/// <summary>
/// 拼音搜索匹配器：对应用名做三路匹配——名称直配 / 全拼包含 / 拼音首字母包含。
/// 例：微信 → weixin / wx，输 "wx" 或 "weixin" 都能命中。
/// 转换结果按名称缓存（ConcurrentDictionary）：应用名集合有限且重复出现，避免每次输入重转。
/// </summary>
internal static class PinyinMatcher
{
    private sealed record Keys(string FullLower, string Initials);

    private static readonly ConcurrentDictionary<string, Keys> Cache = new(StringComparer.Ordinal);

    /// <summary>关键词是否命中（名称直配 / 全拼包含 / 首字母包含，均忽略大小写）。</summary>
    public static bool Matches(string name, string keyword)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(keyword))
        {
            return false;
        }

        if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var keys = Cache.GetOrAdd(name, Build);
        return keys.FullLower.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || keys.Initials.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>关键词是否命中拼音前缀（全拼或首字母）——供 203 搜索排名加成。</summary>
    public static bool PinyinPrefixMatch(string name, string keyword)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(keyword))
        {
            return false;
        }

        var keys = Cache.GetOrAdd(name, Build);
        return keys.FullLower.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)
               || keys.Initials.StartsWith(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static Keys Build(string name)
    {
        var full = new StringBuilder(name.Length);
        var initials = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (PinyinHelper.IsChinese(ch))
            {
                var pinyin = PinyinHelper.GetPinyin(ch);
                if (!string.IsNullOrEmpty(pinyin))
                {
                    full.Append(pinyin);
                    initials.Append(char.ToLowerInvariant(pinyin[0]));
                }
            }
            else if (!char.IsWhiteSpace(ch) && !char.IsPunctuation(ch))
            {
                // 非汉字（字母/数字）原样进两条键：保证 "QQ" 输 "qq"、"7-Zip" 输 "7zip" 仍可命中
                var lower = char.ToLowerInvariant(ch);
                full.Append(lower);
                initials.Append(lower);
            }
        }

        return new Keys(full.ToString(), initials.ToString());
    }
}
