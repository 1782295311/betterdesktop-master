// 诊断包脱敏（S5-4）：把**能定位到人**的三类值从导出内容里换掉。
//
// # 为什么必须做
//
// 用户导出 zip 就是为了**发给别人**（issue / 微信 / 邮件 / IM）。要求"想公开分享就自己先脱敏"
// 是不负责任的 —— 他们不会做。所以**默认脱敏**；要原始包必须显式 `--no-redact`。
//
// # 归哪三类（缺一不可）
//
// | 值 | 为什么必须归一 |
// |---|---|
// | `%USERPROFILE%` 实际值（`C:\Users\<name>`） | PII，可直接定位到个人 |
// | `Environment.MachineName` | 主机名可**唯一标识设备** |
// | 当前用户 SID（`S-1-5-21-…`） | 强标识符，泄漏等于泄漏身份 |
//
// 机器名与 SID 最常被忽略，但它们和用户名一样能精确定位到人。
//
// **刻意不归一**：部署路径（`%LOCALAPPDATA%\BetterDesktop` 是固定值，不敏感）、
// IP / MAC（日志里通常没有，且优先级低）。
//
// # 用户名的**两种形态**（同名一条风险面的两个形态，不是两个风险）
//
// `%USERPROFILE%` 只覆盖"标准用户目录前缀"这一种形态。真机实证（2026-09-19）：
// 文档目录被重定向后，日志里会出现 `D:\17822\Documents\…` 与 `D:\Users\17822\AppData\…` ——
// 它们**不含** `C:\Users\17822` 前缀，值级规则碰不到，用户名仍然留在包里。
// 目标没变（防定位）、实现漏了形态 ⇒ 这是**补全**，故增加**段级**规则：
//
// ```text
// 匹配：\<UserName>\        （前后都是反斜杠；驱动器根 C:\<name>\ 天然含 \ <name> \）
// 替换：\<USERNAME>\
// ```
//
// # 段级规则的**保守判据**（宁漏不误）
//
// | 判据 | 理由 |
// |---|---|
// | **前后都是 `\`**（段必须完整） | 不匹配 `D:\17822abc\` 这种"用户名只是别的段的前缀" |
// | **段等于 UserName**（不做子串匹配） | `C:\Windows\` 不匹配 —— 因为 `Windows` ≠ 用户名 |
// | **UserName 不在通用名黑名单**（[`GenericUserNames`]） | 用户名恰好是 `windows` / `users` / `dev` … 时，段级替换会把 `C:\Windows\` 换成 `C:\<USERNAME>\`，**读坏整份日志** |
//
// 取舍方向与"字节级替换"同源：漏替换的代价是"残留一处用户名"（`%USERPROFILE%` 已覆盖主要路径），
// 误替换的代价是**证据损坏**。宁可漏，不可误。
//
// # 为什么是**字节级**替换，而不是正则替换字符串
//
// （曾建议用 `Regex.Escape` + 一次性 OR 正则；这里**有意偏离**，理由如下。）
//
// 日志文件**不保证是 UTF-8**。走 `Encoding.UTF8.GetString` → 替换 → `GetBytes` 会把非法字节
// 换成 U+FFFD —— 那等于在"脱敏"的同时**损坏证据**，而诊断包的全部价值就是证据。
// 字节级做法只动命中的那段字节，**其余字节一个都不碰**，对任意编码都安全
// （三类目标值都是 ASCII；`\` 是 0x5C，在 UTF-8 里永远不会是多字节序列的中间字节，
// 所以按 `\` 分段的段级匹配也不会切坏汉字或 emoji）。
//
// 正则方案的三条设计点全部保留：
//   · **placeholder 表**（`<USERPROFILE>` / `<USERNAME>` / `<MACHINE>` / `<USER_SID>`）；
//   · **长串优先**（规则按 pattern 长度降序 —— 值级规则比段级长，故 `C:\Users\x` 先变成
//     `<USERPROFILE>`，段级规则不再重复命中同一个位置）；
//   · **天然幂等**（placeholder 不含任何原始值，再跑一遍不会二次替换）。
// 也**不需要** `Regex.Escape`：字节级匹配没有元字符概念 —— 用户名里的 `.` `(` `$` 都是普通字节。
//
// # 自检是这套机制的"锚"
//
// 导出时脱敏是**单点**：实现有 bug 就漏，而且漏得无声无息（包看起来是成功的）。
// 所以打完包必须**重开 zip 逐条目扫一遍**（[`ScanZipForLeaks`]）：命中即删包 + 报错，绝不落盘。
// 段级规则搭上同一条管道 ⇒ 自检**自动**覆盖它（`FindLeaks` 遍历的正是同一张规则表），
// 于是"我写了段级替换"变成"我验证了段级替换生效"。
//
// # 已知未覆盖：域账户形态（**deferred**，不是遗漏）
//
// 当前只归一 `Environment.UserName`，即**本地账户形态**（无域前缀，出现在 `\name\` 段里）。
// 域账户的显示形态可能是 `DOMAIN\username` —— 那条路径里的 `DOMAIN\` 段不在规则表内。
//
// 刻意**现在不做**：它需要另一段值（`Environment.UserDomainName`）与一套"域段是否该脱"的判断，
// 而当前面向的场景是本地账户。**触发条件：出现域账户用户的诊断包 issue。**
// 到那时的最小做法：把域名单点也作为一条段级规则（判据同上 —— 段完整 + 黑名单；
// 域名单点如 `CONTOSO` 通常也不在系统目录名里，误伤面很小）。

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Principal;
using System.Text;

namespace BetterDesktop.Cli;

/// <summary>诊断包脱敏器：把可定位到人的三类值换成固定占位符。</summary>
internal sealed class DiagnosticsRedactor
{
    public const string UserProfileLabel = "<USERPROFILE>";
    public const string UserNameLabel = "<USERNAME>";
    public const string MachineLabel = "<MACHINE>";
    public const string UserSidLabel = "<USER_SID>";

    /// <summary>
    /// 通用名黑名单：这些名字当用户名时，**段级**替换的误伤代价大于收益（见头注的保守判据）。
    ///
    /// 只影响段级规则；值级规则（`%USERPROFILE%` 精确值）不受影响 ——
    /// 无论用户名多通用，那个**完整路径**都是该脱敏的。
    /// </summary>
    /// <remarks>
    /// 【为什么"Windows 会拒绝这些保留名"不是不登记的理由】Windows 只拒绝设备保留名
    /// （`CON` / `PRN` / `NUL` / `COM1-9` / `LPT1-9`），**不**拒绝 `Program Files`、`System32` 这类
    /// 目录名当账户名（本地账户名允许空格与括号）。"一般不会"与"不可能"是两件事，
    /// 而这里的代价是非对称的：多登记一项的成本≈0，漏一项的后果是 `C:\Program Files\` 被换成
    /// `C:\&lt;USERNAME&gt;\`、**整份日志读坏**。
    /// </remarks>
    private static readonly HashSet<string> GenericUserNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── 系统 / 程序目录（段级误伤的**主要**风险面）──
        "windows", "windowsapps", "system", "system32", "syswow64", "winsxs", "recovery",
        "program", "program files", "program files (x86)", "programdata", "perflogs",
        "common files", "microsoft", "intel", "amd", "nvidia",

        // ── 用户目录与其常见子目录 ──
        "users", "public", "default", "all",
        "documents", "desktop", "downloads", "music", "pictures", "videos", "onedrive",

        // ── 临时 / 本地 ──
        "temp", "local", "localhost",

        // ── 开发目录（日志里高频，也常被当示例目录名）──
        "src", "bin", "obj", "build", "release", "debug", "node_modules", ".git",
        "doc", "docs", "test", "dev",

        // ── 账户名 ──
        "administrator", "guest", "user", "testuser",
    };

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>规则表（已按 pattern 长度降序，即"长串优先"）。</summary>
    private readonly (string Label, byte[] Pattern, byte[] Replacement)[] _rules;

    private DiagnosticsRedactor(IEnumerable<(string Value, string Label)> values, string? userName = null)
    {
        var rules = new List<(string Label, byte[] Pattern, byte[] Replacement)>();

        foreach (var (value, label) in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                rules.Add((label, Utf8.GetBytes(value), Utf8.GetBytes(label)));
            }
        }

        if (ShouldAddSegmentRule(userName))
        {
            var name = userName!.Trim();
            // 前后都要 `\`：这是"段完整"的全部含义 —— 没有它，`D:\17822abc\` 会被切成半个段。
            // 驱动器根形态（`C:\17822\`）天然被覆盖：冒号后面那个字符就是这里的首个 `\`。
            rules.Add((
                UserNameLabel,
                Utf8.GetBytes("\\" + name + "\\"),
                Utf8.GetBytes("\\" + UserNameLabel + "\\")));
        }

        // 长串优先：一个 pattern 是另一个的子串时，先替换长的才不会在替换短的之后留下半截原始值。
        // （值级 `C:\Users\x` 必然比段级 `\x\` 长 ⇒ 用户目录永远走更准确的 `<USERPROFILE>`。）
        _rules = rules.OrderByDescending(r => r.Pattern.Length).ToArray();
    }

    /// <summary>本机三类值（用户名 / 机器名 / SID）+ 用户名的段级形态。</summary>
    public static DiagnosticsRedactor ForCurrentMachine()
    {
        var values = new List<(string, string)>
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), UserProfileLabel),
            (Environment.MachineName, MachineLabel),
        };

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity?.User?.Value;
            if (!string.IsNullOrEmpty(sid))
            {
                values.Add((sid!, UserSidLabel));
            }
        }
        catch (Exception)
        {
            // 拿不到 SID（极少见）：少一条规则，比让整次导出失败好。
            // 注意取舍的边界 —— 规则不进 _rules ⇒ 自检也不会要求它（自检只验证**已登记**的规则），
            // 所以这里不会制造"自检报一个我们并不打算脱的值"这种噪声。
        }

        return new DiagnosticsRedactor(values, Environment.UserName);
    }

    /// <summary>供单测注入任意规则（不含段级规则）。</summary>
    public static DiagnosticsRedactor ForTest(params (string Value, string Label)[] values)
        => new(values);

    /// <summary>供单测注入任意规则 + 一个假想用户名（段级规则走同一条黑名单判据）。</summary>
    public static DiagnosticsRedactor ForTestWithUserName(
        string userName,
        params (string Value, string Label)[] values)
        => new(values, userName);

    /// <summary>
    /// 是否为该用户名启用段级规则（**纯函数**，故黑名单判据可被单测穷举）。
    /// </summary>
    /// <remarks>
    /// 黑名单的取舍方向是"宁漏不误"：漏了只是残留一处用户名，误了会把 `C:\Windows\`
    /// 换成 `C:\&lt;USERNAME&gt;\`、读坏整份日志。
    /// </remarks>
    internal static bool ShouldAddSegmentRule(string? userName)
        => !string.IsNullOrWhiteSpace(userName) && !GenericUserNames.Contains(userName.Trim());

    /// <summary>是否有可用规则（无规则 ⇒ 脱敏是空操作，调用方应据此决定要不要自检）。</summary>
    public bool HasRules => _rules.Length > 0;

    /// <summary>已登记的占位符名（日志/诊断用）。</summary>
    public IReadOnlyList<string> Labels => _rules.Select(r => r.Label).ToArray();

    /// <summary>脱敏：**只动命中的字节段**，其余字节一个都不碰。</summary>
    public byte[] Redact(byte[] content)
    {
        var current = content;
        foreach (var rule in _rules)
        {
            current = ReplaceAll(current, rule.Pattern, rule.Replacement);
        }

        return current;
    }

    /// <summary>扫描内容里**仍然残留**的原始值/用户名段，返回命中的占位符名（自检用）。</summary>
    public IReadOnlyList<string> FindLeaks(byte[] content)
    {
        var hits = new List<string>();
        foreach (var rule in _rules)
        {
            if (IndexOf(content, rule.Pattern) >= 0)
            {
                hits.Add(rule.Label);
            }
        }

        return hits;
    }

    /// <summary>
    /// **自检**：重开刚写出的 zip，逐条目扫描所有已登记的规则（值级 **与段级**）。
    /// 返回余留的 <c>条目名: 占位符</c> 列表；空 = 干净。
    /// </summary>
    /// <remarks>
    /// 这是"导出时脱敏"这名单点的唯一补偿手段。没有它，实现里的一个 off-by-one
    /// 就会变成"包看起来正常、里面躺着用户名"—— 而那种包已经发给别人了。
    /// </remarks>
    public IReadOnlyList<string> ScanZipForLeaks(string zipPath)
    {
        var leaks = new List<string>();
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);

            foreach (var hit in FindLeaks(buffer.ToArray()))
            {
                leaks.Add($"{entry.FullName}: {hit}");
            }
        }

        return leaks;
    }

    /// <summary>把所有 pattern 出现处替换为对应占位符（其余字节原样保留）。</summary>
    private static byte[] ReplaceAll(byte[] data, byte[] pattern, byte[] replacement)
    {
        if (pattern.Length == 0 || data.Length < pattern.Length)
        {
            return data;
        }

        var index = IndexOf(data, pattern);
        if (index < 0)
        {
            return data;
        }

        using var output = new MemoryStream(data.Length);
        var position = 0;
        while (index >= 0)
        {
            output.Write(data, position, index - position);
            output.Write(replacement);
            position = index + pattern.Length;
            index = IndexOf(data, pattern, position);
        }

        output.Write(data, position, data.Length - position);
        return output.ToArray();
    }

    /// <summary>从 <paramref name="start"/> 起查找，返回**绝对**下标（-1 = 未找到）。</summary>
    /// <remarks>
    /// 【必须加回 start，否则是越界 bug】<c>Span.IndexOf</c> 返回的是**切片内**的相对偏移。
    /// 当成绝对索引用时，第一个匹配（start=0）恰好正确，**从第二个匹配起**就会算出偏小的位置 →
    /// 后续 <c>Write(data, position, index - position)</c> 得到负数长度或越界。
    /// 这条 bug 在只出现一次的用例里完全看不出来 —— 是端到端导出（同一路径在日志里出现几十次）
    /// 把它逼出来的。有了它，才有了"同一值出现多次"那组回归用例。
    /// </remarks>
    private static int IndexOf(byte[] haystack, byte[] needle, int start = 0)
    {
        if (needle.Length == 0 || start >= haystack.Length)
        {
            return -1;
        }

        var relative = haystack.AsSpan(start).IndexOf(needle);
        return relative < 0 ? -1 : start + relative;
    }
}
