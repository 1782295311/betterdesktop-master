using System.Text.Json;
using System.Text.Json.Serialization;
using BetterDesktop.Kernel.Core;
using Xunit;

namespace BetterDesktop.Kernel.Tests;

/// <summary>
/// 原生 DLL 注册路径的**跨语言契约测试**：C# 实现必须通过
/// <c>protocols/native-dll-path-test-vectors.json</c> 的全部用例。
/// </summary>
/// <remarks>
/// <para>
/// Rust 侧 <c>shellmenu::tests::path_resolution_matches_the_shared_vector</c> /
/// <c>path_equivalence_matches_the_shared_vector</c> 跑同一批向量。两侧都绿 ⇒ 两套实现对
/// "注册表该写哪个路径"与"什么算漂移"的理解一致；只跑各自手写的单测则**完全不能**保证这一点
/// （那正是"各自猜边界"，也是本契约存在的理由）。
/// </para>
/// <para>
/// 本用例**必须读文件**（而不是内嵌向量）：否则改了向量而没改实现，测试不会红。
/// </para>
/// </remarks>
public sealed class NativeDllPathContractTests
{
    private static readonly string VectorsPath = Path.Combine(
        FindRepoRoot(), "protocols", "native-dll-path-test-vectors.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    [Fact(DisplayName = "共享向量文件存在（注册表路径契约的唯一权威）")]
    public void VectorsFileExists()
    {
        Assert.True(File.Exists(VectorsPath), $"共享向量缺失：{VectorsPath}");
    }

    [Fact(DisplayName = "解析用例：C# 实现必须与共享向量逐条一致")]
    public void ResolutionMatchesSharedVector()
    {
        var doc = LoadVectors();
        Assert.True(doc.Cases.Count >= 15, $"向量用例太少（{doc.Cases.Count}）—— 契约覆盖不足");

        var failures = new List<string>();
        foreach (var c in doc.Cases)
        {
            var actual = NativeDllPath.Resolve(
                new NativeDllPathInputs
                {
                    ProcessDir = c.Input.ProcessDir,
                    InstallRoot = c.Input.InstallRoot,
                    LocalAppData = c.Input.LocalAppData,
                    DevMode = c.Input.DevMode,
                },
                // 模拟文件系统：按 PathEq 比较（向量明说"大小写与分隔符按原样给出，由实现负责归一"）
                p => c.Input.ExistingFiles.Exists(f => NativeDllPath.PathEq(f, p)));

            if (!SamePath(actual, c.Expected))
            {
                failures.Add(
                    $"「{c.Name}」期望 {c.Expected ?? "<拒绝>"}，实得 {actual ?? "<拒绝>"}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact(DisplayName = "等价用例：PathEq 必须与共享向量逐条一致")]
    public void EquivalenceMatchesSharedVector()
    {
        var doc = LoadVectors();
        Assert.True(
            doc.PathEquivalence.Cases.Count >= 9,
            $"等价用例太少（{doc.PathEquivalence.Cases.Count}）");

        var failures = new List<string>();
        foreach (var c in doc.PathEquivalence.Cases)
        {
            var actual = NativeDllPath.PathEq(c.A, c.B);
            if (actual != c.Equivalent)
            {
                failures.Add($"「{c.Name}」期望 equivalent={c.Equivalent}，实得 {actual}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// 向量里必须**真的**覆盖住那两条会把 dev bin 写进注册表的用例 ——
    /// 否则契约可以"全绿"却漏掉 D6 事故形态。
    /// </summary>
    [Fact(DisplayName = "向量必须覆盖 D6 事故形态与 dev opt-in")]
    public void VectorCoversTheD6AccidentShape()
    {
        var doc = LoadVectors();

        var devBinRejected = doc.Cases.Exists(c =>
            c.Expected is null
            && c.Input.InstallRoot is null
            && !c.Input.DevMode
            && c.Input.ProcessDir is not null
            && c.Input.ProcessDir.Contains(@"\bin\Release", StringComparison.OrdinalIgnoreCase)
            && c.Input.ExistingFiles.Exists(f =>
                f.Contains(@"\bin\Release", StringComparison.OrdinalIgnoreCase)));

        Assert.True(
            devBinRejected,
            "向量必须含「安装根无效 + 进程在开发 bin → 拒绝」这条 —— 它就是 D6 的形态");

        var devOptIn = doc.Cases.Exists(c => c.Input.DevMode && c.Expected is not null);
        Assert.True(devOptIn, "向量必须含「devMode=true 时允许开发目录」这条");
    }

    /// <summary>
    /// <c>_non_goals</c> 那几条**必须有对应用例** —— 否则它们只是散文，
    /// 下一个实现者仍会按直觉实现（这正是补这一节要防的事）。
    /// </summary>
    [Fact(DisplayName = "_non_goals 的每一条都必须有用例钉住（否则只是散文）")]
    public void NonGoalsAreCoveredByCases()
    {
        var doc = LoadVectors();
        Assert.NotEmpty(doc.PathEquivalence.NonGoals.Items);

        // `.`/`..` 不展开 → 有一条判为不等价的用例
        Assert.True(
            doc.PathEquivalence.Cases.Exists(c =>
                !c.Equivalent && c.A.Contains(@"..\", StringComparison.Ordinal)),
            "缺「`..` 段不展开」的用例");

        // 8.3 短名不解析 → 有一条含 `~1` 的用例
        Assert.True(
            doc.PathEquivalence.Cases.Exists(c => !c.Equivalent && c.A.Contains('~')),
            "缺「8.3 短名不解析」的用例");

        // UNC 不特殊处理 → 有一条 `\\server\share` 的用例
        Assert.True(
            doc.PathEquivalence.Cases.Exists(c => c.A.StartsWith(@"\\", StringComparison.Ordinal)),
            "缺「UNC 路径」的用例");

        // 非绝对路径一律拒绝 → 有解析用例返回 null 且输入是相对的
        Assert.True(
            doc.Cases.Exists(c =>
                c.Expected is null
                && (IsRelative(c.Input.InstallRoot) || IsRelative(c.Input.ProcessDir))),
            "缺「非绝对路径一律拒绝」的解析用例 —— 这条是行为约束，不只是说明");

        static bool IsRelative(string? path) =>
            !string.IsNullOrWhiteSpace(path) && !Path.IsPathFullyQualified(path);
    }

    /// <summary>
    /// **不变量**：规则永不返回相对路径。这条是 ⓪ 条的目的本身，值得独立钉住 ——
    /// 它保证"将来往向量里加用例"时不会悄悄放行一个相对路径。
    /// </summary>
    [Fact(DisplayName = "不变量：解析结果永远不是相对路径")]
    public void ResolveNeverReturnsARelativePath()
    {
        var doc = LoadVectors();
        foreach (var c in doc.Cases.Where(c => c.Expected is not null))
        {
            Assert.True(
                Path.IsPathFullyQualified(c.Expected!),
                $"「{c.Name}」的期望值本身就不是绝对路径：{c.Expected}");
        }

        var resolved = doc.Cases
            .Select(c => NativeDllPath.Resolve(
                new NativeDllPathInputs
                {
                    ProcessDir = c.Input.ProcessDir,
                    InstallRoot = c.Input.InstallRoot,
                    LocalAppData = c.Input.LocalAppData,
                    DevMode = c.Input.DevMode,
                },
                p => c.Input.ExistingFiles.Exists(f => NativeDllPath.PathEq(f, p))))
            .Where(p => p is not null);

        foreach (var path in resolved)
        {
            Assert.True(Path.IsPathFullyQualified(path!), $"解析出了相对路径：{path}");
        }
    }

    private static bool SamePath(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return NativeDllPath.PathEq(a, b);
    }

    private static VectorsDoc LoadVectors()
    {
        var json = File.ReadAllText(VectorsPath);
        return JsonSerializer.Deserialize<VectorsDoc>(json, JsonOptions)
               ?? throw new InvalidOperationException($"共享向量反序列化失败：{VectorsPath}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("找不到仓库根（AGENTS.md）");
    }

    // ───────────────────────────── DTO ─────────────────────────────

    private sealed class VectorsDoc
    {
        public List<ResolveCase> Cases { get; set; } = [];

        [JsonPropertyName("_path_equivalence")]
        public EquivalenceSection PathEquivalence { get; set; } = new();
    }

    private sealed class ResolveCase
    {
        public string Name { get; set; } = string.Empty;

        public VectorInput Input { get; set; } = new();

        public string? Expected { get; set; }
    }

    private sealed class VectorInput
    {
        public string? ProcessDir { get; set; }

        public string? InstallRoot { get; set; }

        public string? LocalAppData { get; set; }

        public bool DevMode { get; set; }

        public List<string> ExistingFiles { get; set; } = [];
    }

    private sealed class EquivalenceSection
    {
        public List<EquivalenceCase> Cases { get; set; } = [];

        [JsonPropertyName("_non_goals")]
        public NonGoals NonGoals { get; set; } = new();
    }

    private sealed class NonGoals
    {
        public List<string> Items { get; set; } = [];
    }

    private sealed class EquivalenceCase
    {
        public string Name { get; set; } = string.Empty;

        public string A { get; set; } = string.Empty;

        public string B { get; set; } = string.Empty;

        public bool Equivalent { get; set; }
    }
}
