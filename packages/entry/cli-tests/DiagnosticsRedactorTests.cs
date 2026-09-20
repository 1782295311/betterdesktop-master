// 诊断包脱敏（S5-4）的契约测试。
//
// 这组用例钉的是**四件事**（对应 `DiagnosticsRedactor` 头注里的四个设计点）：
//   ① 三类身份值（用户名 / 机器名 / SID）都要被换掉，且**只换命中段**；
//   ② 幂等（占位符不含原值 ⇒ 再跑一遍不变）；
//   ③ 值里含正则元字符也不受影响（字节级匹配，不做正则解释）；
//   ④ **自检真的能抓出漏网**（反过来也要证明"干净的包不会被误报"）。

using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace BetterDesktop.Cli.Tests;

/// <summary>诊断包脱敏器的契约测试。</summary>
public class DiagnosticsRedactorTests
{
    private static readonly DiagnosticsRedactor Sample = DiagnosticsRedactor.ForTest(
        (@"C:\Users\alice", DiagnosticsRedactor.UserProfileLabel),
        ("DESKTOP-ABC123", DiagnosticsRedactor.MachineLabel),
        ("S-1-5-21-111-222-333-1001", DiagnosticsRedactor.UserSidLabel));

    private static string RoundTrip(DiagnosticsRedactor redactor, string text)
        => Encoding.UTF8.GetString(redactor.Redact(Encoding.UTF8.GetBytes(text)));

    private static string RoundTrip(string text) => RoundTrip(Sample, text);

    /// <summary>
    /// 模拟"文档目录被重定向"的本机（用户名 `17822`，出现在 `D:\17822\` 与 `D:\Users\17822\` 下）。
    /// 这是真机实测的形态 —— 值级规则（`C:\Users\17822`）碰不到它们，只有段级规则能覆盖。
    /// </summary>
    private static DiagnosticsRedactor RedirectedMachine() => DiagnosticsRedactor.ForTestWithUserName(
        "17822",
        (@"C:\Users\17822", DiagnosticsRedactor.UserProfileLabel),
        ("DESKTOP-ABC123", DiagnosticsRedactor.MachineLabel));

    /// <summary>三类值**都要**被替换 —— 只做用户名是不够的（机器名与 SID 同样能定位到人）。</summary>
    [Fact]
    public void replaces_all_three_identity_classes()
    {
        var input = string.Join(
            "\n",
            @"目录 : C:\Users\alice\AppData\Local\BetterDesktop\",
            "主机 : DESKTOP-ABC123",
            "SID  : S-1-5-21-111-222-333-1001");

        var output = RoundTrip(input);

        Assert.DoesNotContain(@"C:\Users\alice", output, StringComparison.Ordinal);
        Assert.DoesNotContain("DESKTOP-ABC123", output, StringComparison.Ordinal);
        Assert.DoesNotContain("S-1-5-21-111-222-333-1001", output, StringComparison.Ordinal);

        Assert.Contains("<USERPROFILE>", output, StringComparison.Ordinal);
        Assert.Contains("<MACHINE>", output, StringComparison.Ordinal);
        Assert.Contains("<USER_SID>", output, StringComparison.Ordinal);

        // **只换命中段**：路径的其余部分必须原样保留（否则日志的可读性就没了）
        Assert.Contains(@"<USERPROFILE>\AppData\Local\BetterDesktop\", output, StringComparison.Ordinal);
    }

    /// <summary>占位符不含任何原始值 ⇒ 再跑一遍结果不变（幂等）。</summary>
    [Fact]
    public void redaction_is_idempotent()
    {
        var once = RoundTrip(@"C:\Users\alice\x.txt on DESKTOP-ABC123");
        var twice = RoundTrip(once);

        Assert.Equal(once, twice);
        Assert.DoesNotContain(@"C:\Users\alice", twice, StringComparison.Ordinal);
    }

    /// <summary>
    /// 用户名 / 机器名可能含正则元字符（`.` `(` `$` `[` …）。
    /// 字节级匹配把它们当普通字节 —— 这正是**不用** `Regex.Escape` 的原因，也是这条用例要钉住的。
    /// </summary>
    [Fact]
    public void values_with_regex_metacharacters_are_replaced_literally()
    {
        const string tricky = @"C:\Users\a.b(c)$[d]+?^|\";
        var redactor = DiagnosticsRedactor.ForTest((tricky, "<X>"));

        var output = RoundTrip(redactor, "head " + tricky + " tail");

        Assert.Equal("head <X> tail", output);
    }

    /// <summary>
    /// 一个值是另一个的子串时，**长串优先** —— 否则先替换短的会在原位留下半截原始值
    /// （例如先换掉 `DESKTOP`，`DESKTOP-ABC123` 就变成 `<SHORT>-ABC123`，机器名仍然可读）。
    /// </summary>
    [Fact]
    public void longer_value_wins_when_one_contains_another()
    {
        var redactor = DiagnosticsRedactor.ForTest(
            ("DESKTOP", "<SHORT>"),
            ("DESKTOP-ABC123", "<LONG>"));

        var output = RoundTrip(redactor, "x DESKTOP-ABC123 y");

        Assert.Equal("x <LONG> y", output);
    }

    /// <summary>
    /// **同一值出现多次**时必须全部替换（回归钉子）。
    ///
    /// 它钉的是一个真实踩过的 bug：`Span.IndexOf(切片)` 返回的是**切片内相对偏移**，
    /// 当成绝对索引用时第一个匹配恰好正确、**从第二个起**就越界
    /// （端到端导出时的报错是 `Offset and length were out of bounds … (Parameter 'count')`）。
    /// 只出现一次的用例完全看不出来 —— 是"同一路径在日志里出现几十次"把它逼出来的。
    /// </summary>
    [Fact]
    public void every_occurrence_is_replaced_not_just_the_first()
    {
        var input = @"a C:\Users\alice\1 b C:\Users\alice\2 c C:\Users\alice\3";

        var output = RoundTrip(input);

        Assert.Equal(
            @"a <USERPROFILE>\1 b <USERPROFILE>\2 c <USERPROFILE>\3",
            output);
        Assert.DoesNotContain("alice", output, StringComparison.Ordinal);
    }

    /// <summary>多个不同值、各自重复出现时也必须全部替换（混合场景，最接近真实日志）。</summary>
    [Fact]
    public void mixed_values_appearing_repeatedly_are_all_replaced()
    {
        const string input =
            @"C:\Users\alice on DESKTOP-ABC123; C:\Users\alice again; sid S-1-5-21-111-222-333-1001";

        var output = RoundTrip(input);

        Assert.DoesNotContain("alice", output, StringComparison.Ordinal);
        Assert.DoesNotContain("DESKTOP-ABC123", output, StringComparison.Ordinal);
        Assert.DoesNotContain("S-1-5-21-111-222-333-1001", output, StringComparison.Ordinal);
    }

    // ─────────── 用户名的第二种形态：段级规则（三项的**补全**，2026-09-19 定案） ───────────

    /// <summary>
    /// 重定向后的用户目录（`D:\17822\…` / `D:\Users\17822\…`）必须被替换。
    /// 驱动器根形态（`C:\17822\…`）也在这里 —— 它在字节上就是 `\17822\`（冒号后即首个反斜杠）。
    /// </summary>
    [Fact]
    public void segment_rule_replaces_redirected_user_directories()
    {
        var input = @"D:\17822\Documents\x.exe D:\Users\17822\AppData\Local\B\b.exe C:\17822\y";

        var output = RoundTrip(RedirectedMachine(), input);

        Assert.DoesNotContain(@"D:\17822\", output, StringComparison.Ordinal);
        Assert.DoesNotContain(@"D:\Users\17822\", output, StringComparison.Ordinal);
        Assert.Contains(@"D:\<USERNAME>\Documents\x.exe", output, StringComparison.Ordinal);
        Assert.Contains(@"D:\Users\<USERNAME>\AppData\Local\B\b.exe", output, StringComparison.Ordinal);
        Assert.Contains(@"C:\<USERNAME>\y", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 系统目录**不得**被动 —— 判据是"段 == 用户名"，`Windows` / `Users` 不等于 `17822`。
    /// （这条是段级规则最危险的失败形态：一旦误匹配，`C:\Windows\` 会变成 `C:\&lt;USERNAME&gt;\`。）
    /// </summary>
    [Fact]
    public void segment_rule_leaves_system_directories_alone()
    {
        const string input = @"C:\Windows\System32\drivers C:\Users\Public C:\Program Files\x";

        var output = RoundTrip(RedirectedMachine(), input);

        Assert.Equal(input, output);
        Assert.DoesNotContain(DiagnosticsRedactor.UserNameLabel, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// **段必须完整**：`D:\17822abc\`（用户名只是别的段的前缀）与 `D:\abc17822\` 都不匹配；
    /// 路径末尾（`D:\17822`，无尾随反斜杠）也不匹配 —— 宁漏不误。
    /// </summary>
    [Fact]
    public void segment_rule_requires_the_whole_segment()
    {
        const string input = @"D:\17822abc\ D:\abc17822\ D:\17822";

        var output = RoundTrip(RedirectedMachine(), input);

        Assert.Equal(input, output);
        Assert.DoesNotContain(DiagnosticsRedactor.UserNameLabel, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 用户名落在通用名黑名单时，**段级规则不启用** —— 否则 `C:\Windows\` 会被换成
    /// `C:\&lt;USERNAME&gt;\`、整份日志读坏。值级规则不受影响（完整路径照脱）。
    /// </summary>
    [Fact]
    public void segment_rule_is_disabled_for_generic_user_names()
    {
        // 用户名 = windows：系统目录必须原样，而**值级**仍要把真实用户目录脱掉
        var asWindows = DiagnosticsRedactor.ForTestWithUserName(
            "windows",
            (@"C:\Users\windows", DiagnosticsRedactor.UserProfileLabel));

        var output = RoundTrip(asWindows, @"C:\Windows\System32 and C:\Users\windows\x.txt");

        Assert.Contains(@"C:\Windows\System32", output, StringComparison.Ordinal);
        Assert.DoesNotContain(DiagnosticsRedactor.UserNameLabel, output, StringComparison.Ordinal);
        Assert.Contains(@"<USERPROFILE>\x.txt", output, StringComparison.Ordinal);

        // 用户名 = dev（黑名单里的另一个方向）
        var asDev = DiagnosticsRedactor.ForTestWithUserName("dev");
        Assert.Equal(@"D:\dev\p", RoundTrip(asDev, @"D:\dev\p"));
    }

    /// <summary>黑名单判据本身（纯函数）—— 空白名不得启用。</summary>
    [Theory]
    [InlineData("windows")]
    [InlineData("Users")]
    [InlineData("dev")]
    [InlineData("")]
    [InlineData("   ")]
    // 系统 / 程序目录名：Windows **不**拒绝它们当本地账户名（只拒绝设备保留名），
    // 而段级误伤的后果是"整份日志读坏" ⇒ 逐项登记，成本≈0
    [InlineData("Program Files")]
    [InlineData("Program Files (x86)")]
    [InlineData("WindowsApps")]
    [InlineData("System32")]
    [InlineData("SysWOW64")]
    [InlineData("WinSxS")]
    [InlineData("Common Files")]
    [InlineData("Microsoft")]
    [InlineData("Intel")]
    [InlineData("AMD")]
    [InlineData("NVIDIA")]
    [InlineData("PerfLogs")]
    [InlineData("Recovery")]
    public void generic_or_blank_user_names_do_not_get_a_segment_rule(string name)
        => Assert.False(DiagnosticsRedactor.ShouldAddSegmentRule(name));

    /// <summary>
    /// 用户名恰为 `Program Files`（本地账户名允许空格与括号）时，系统目录**必须**原样。
    /// 这是黑名单里最"看得见"的一条 —— 没有它，`C:\Program Files\` 会被换成
    /// `C:\&lt;USERNAME&gt;\`，整份日志读坏。
    /// </summary>
    [Fact]
    public void user_name_that_collides_with_a_system_directory_never_rewrites_it()
    {
        var redactor = DiagnosticsRedactor.ForTestWithUserName("Program Files");

        const string input = @"C:\Program Files\App\x.exe and C:\Windows\System32";

        var output = RoundTrip(redactor, input);

        Assert.Equal(input, output);
        Assert.DoesNotContain(DiagnosticsRedactor.UserNameLabel, output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("17822")]
    [InlineData("alice")]
    public void ordinary_user_names_get_a_segment_rule(string name)
        => Assert.True(DiagnosticsRedactor.ShouldAddSegmentRule(name));

    /// <summary>
    /// **同一重定向路径出现 5 次以上**时每次都要替换。
    ///
    /// 这条是给段级规则配的"同类 bug 防复发"钉子：上一轮的越界 bug（`Span.IndexOf` 返回相对偏移）
    /// 正是"只出现一次的用例测不出来、真实日志里出现几十次"才暴露的。
    /// </summary>
    [Fact]
    public void segment_rule_replaces_every_occurrence()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < 6; i++)
        {
            builder.Append(@"D:\17822\dir").Append(i).Append(@"\f.exe ");
        }

        var output = RoundTrip(RedirectedMachine(), builder.ToString());

        Assert.DoesNotContain(@"\17822\", output, StringComparison.Ordinal);

        var replacements = 0;
        var index = output.IndexOf(@"D:\<USERNAME>\", StringComparison.Ordinal);
        while (index >= 0)
        {
            replacements++;
            index = output.IndexOf(@"D:\<USERNAME>\", index + 1, StringComparison.Ordinal);
        }

        Assert.Equal(6, replacements);
    }

    /// <summary>**自检要覆盖段级规则**：只含段级残留（无值级残留）的包也必须被判为泄漏。</summary>
    [Fact]
    public void self_check_detects_a_leaked_user_name_segment()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bd-redact-segment-{Guid.NewGuid():N}.zip");
        try
        {
            WriteZip(path, "logs/host.log", @"hover exe=D:\17822\Documents\BetterGI\BetterGI.exe");

            var leaks = RedirectedMachine().ScanZipForLeaks(path);

            Assert.Contains(
                leaks,
                l => l.Contains(DiagnosticsRedactor.UserNameLabel, StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>没登记到任何值（例如拿不到 SID 且环境变量为空）⇒ 空操作，且调用方据此跳过自检。</summary>
    [Fact]
    public void without_rules_redaction_is_a_no_op()
    {
        var redactor = DiagnosticsRedactor.ForTest();
        var bytes = Encoding.UTF8.GetBytes("nothing to hide");

        Assert.False(redactor.HasRules);
        Assert.Empty(redactor.Labels);
        Assert.Equal(bytes, redactor.Redact(bytes));
        Assert.Empty(redactor.FindLeaks(bytes));
    }

    /// <summary>空白值不得进规则表（否则退化成"把所有空串位置都换掉"，会毁掉整份内容）。</summary>
    [Fact]
    public void blank_values_are_ignored()
    {
        var redactor = DiagnosticsRedactor.ForTest(("", "<X>"), ("   ", "<Y>"));

        Assert.False(redactor.HasRules);
    }

    /// <summary>**自检必须能抓出漏网** —— 它是"导出时脱敏"这名单点的唯一补偿手段。</summary>
    [Fact]
    public void self_check_detects_leaks_in_a_written_package()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bd-redact-leak-{Guid.NewGuid():N}.zip");
        try
        {
            WriteZip(path, "system-info.txt", @"目录 : C:\Users\alice\", "logs/host.log", "hello");

            var leaks = Sample.ScanZipForLeaks(path);

            Assert.Contains(leaks, l => l.Contains(DiagnosticsRedactor.UserProfileLabel, StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 反证：**已脱敏**的包再扫必须是干净的。
    /// 少了这条，"自检"可能因为别的原因恒报红（例如模式写错），于是它永远不会被发现是坏的。
    /// </summary>
    [Fact]
    public void self_check_passes_on_a_redacted_package()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bd-redact-clean-{Guid.NewGuid():N}.zip");
        try
        {
            WriteZip(
                path,
                "system-info.txt",
                @"目录 : " + DiagnosticsRedactor.UserProfileLabel + @"\",
                "logs/host.log",
                "hello " + DiagnosticsRedactor.MachineLabel);

            var leaks = Sample.ScanZipForLeaks(path);

            Assert.Empty(leaks);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>本机工厂：必须至少拿到"用户名"这一条（机器名与 SID 拿不到时也不该抛）。</summary>
    [Fact]
    public void for_current_machine_never_throws_and_registers_the_user_profile()
    {
        var redactor = DiagnosticsRedactor.ForCurrentMachine();

        Assert.True(redactor.HasRules, "本机必有用户目录");
        Assert.Contains(DiagnosticsRedactor.UserProfileLabel, redactor.Labels);
    }

    private static void WriteZip(string path, params string[] nameAndContentPairs)
    {
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        for (var i = 0; i + 1 < nameAndContentPairs.Length; i += 2)
        {
            var entry = archive.CreateEntry(nameAndContentPairs[i]);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(nameAndContentPairs[i + 1]);
        }
    }
}
