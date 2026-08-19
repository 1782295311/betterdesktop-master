// BetterDesktop.Kernel.Tests — 架构守护测试（P1 文本护栏 v1）
// 覆盖：TFM 一致 / 内核零运行时依赖 / Contracts 不引用 Core / 禁 Console.WriteLine
// 说明：P1 用 csproj/源码文本断言（老仓 T-TFM 同构）；ArchUnitNET IL 级护栏随包数量增长在 P1 后续落地

using System.Text.RegularExpressions;
using Xunit;

namespace BetterDesktop.Kernel.Tests;

public sealed class ArchitectureGuardTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact(DisplayName = "全部包的 TFM 一致为 net8.0-windows")]
    public void All_Csproj_TargetFramework_IsNet8Windows()
    {
        foreach (var csproj in EnumerateCsproj())
        {
            var text = File.ReadAllText(csproj);
            Assert.Matches(
                "<TargetFramework>net8\\.0-windows</TargetFramework>",
                text);
        }
    }

    [Fact(DisplayName = "内核基础包零第三方运行时依赖")]
    public void Kernel_Core_HasNoPackageReference()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, "packages", "kernel", "kernel", "BetterDesktop.Kernel.csproj"));
        Assert.DoesNotContain("PackageReference", text);
    }

    [Fact(DisplayName = "Contracts 命名空间不引用 Core 实现")]
    public void Contracts_DoNotReferenceCoreNamespace()
    {
        var contractsDir = Path.Combine(RepoRoot, "packages", "kernel", "kernel", "Contracts");
        foreach (var file in Directory.EnumerateFiles(contractsDir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("BetterDesktop.Kernel.Core", text);
        }
    }

    [Fact(DisplayName = "业务代码禁止 Console/Debug.WriteLine")]
    public void No_ConsoleOrDebugWriteLine_InPackages()
    {
        var pattern = new Regex(@"\b(Console|Debug|Trace)\.Write(Line)?\(");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot, "packages"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains("\\obj\\") || file.Contains("\\bin\\"))
            {
                continue;
            }
            var text = File.ReadAllText(file);
            var match = pattern.Match(text);
            Assert.False(match.Success, $"{file} 命中日志禁令：{match.Value}");
        }
    }

    private static IEnumerable<string> EnumerateCsproj()
    {
        return Directory.EnumerateFiles(Path.Combine(RepoRoot, "packages"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains("\\obj\\") && !p.Contains("\\bin\\"))
            .OrderBy(p => p, StringComparer.Ordinal);
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
}
