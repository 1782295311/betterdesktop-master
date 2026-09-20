using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using BetterDesktop.Shell.ContextMenus.Services;
using Xunit;

namespace BetterDesktop.Shell.ContextMenu.Tests;

/// <summary>
/// 原生右键扩展的 C#↔C++ 契约单测：
///   · shellmenu.json 形状（**尤其是 filter 对象形状**：写成扁平字段会被原生侧静默忽略，
///     表现为"所有转换目标对所有文件都出现"——不报错、极难排查，必须有回归守卫）；
///   · CLSID / 场景键 与 native/include/BdShell.h 的一致性（跨语言契约漂移守卫）；
///   · 注册器读写 HKCU 的往返（用真实注册表，测后还原原状）。
/// </summary>
public sealed class ShellMenuConfigTests
{
    // ===================== 序列化形状 =====================

    [Fact]
    public void BuildJson_EmitsRootProtocolFields()
    {
        var json = ShellMenuConfigWriter.BuildJson([], extensionEnabled: false);
        var root = JsonNode.Parse(json)!.AsObject();

        Assert.Equal(ShellMenuConfigWriter.SchemaVersion, root["version"]!.GetValue<int>());
        Assert.False(root["extensionEnabled"]!.GetValue<bool>());
        Assert.NotNull(root["items"]);
        Assert.Empty(root["items"]!.AsArray());
    }

    [Fact]
    public void BuildJson_UsesFilterObjectShape_NotFlatFields()
    {
        // 原生侧 ParseItem 读的是 node.filter.{extensions,selection}；
        // 扁平 filterExtensions/filterSameExtension 会被**静默忽略**。
        var item = new ShellMenuItem
        {
            Id = "convert",
            Title = "格式转换",
            Kind = ShellMenuKind.Submenu,
            Scenes = [ShellMenuScene.Files],
            FilterExtensions = [".docx", ".md"],
            FilterSameExtension = true,
        };

        var root = JsonNode.Parse(ShellMenuConfigWriter.BuildJson([item], true))!.AsObject();
        var node = root["items"]!.AsArray()[0]!.AsObject();

        Assert.Null(node["filterExtensions"]);
        Assert.Null(node["filterSameExtension"]);

        var filter = node["filter"]!.AsObject();
        Assert.Equal(new[] { ".docx", ".md" },
            filter["extensions"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("sameExtension", filter["selection"]!.GetValue<string>());
    }

    [Fact]
    public void BuildJson_MapsScenesAndKinds()
    {
        var item = new ShellMenuItem
        {
            Id = "desktopControls",
            Title = "桌面控制",
            Kind = ShellMenuKind.Toggle,
            Scenes = [ShellMenuScene.Background, ShellMenuScene.Directory, ShellMenuScene.Files],
            IsChecked = true,
        };

        var root = JsonNode.Parse(ShellMenuConfigWriter.BuildJson([item], true))!.AsObject();
        var node = root["items"]!.AsArray()[0]!.AsObject();

        Assert.Equal("toggle", node["kind"]!.GetValue<string>());
        Assert.Equal(
            new[] { "background", "directory", "files" },
            node["scenes"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.True(node["checked"]!.GetValue<bool>());
    }

    [Fact]
    public void BuildJson_OmitsDefaultedFields_SoNativeSideOwnsDefaults()
    {
        var item = new ShellMenuItem { Id = "x", Title = "X" };

        var root = JsonNode.Parse(ShellMenuConfigWriter.BuildJson([item], true))!.AsObject();
        var node = root["items"]!.AsArray()[0]!.AsObject();

        Assert.Null(node["kind"]);            // Command = 默认
        Assert.Null(node["scenes"]);          // 空 = 所有场景
        Assert.Null(node["checked"]);
        Assert.Null(node["highlight"]);
        Assert.Null(node["default"]);
        Assert.Null(node["action"]);
        Assert.Null(node["args"]);
        Assert.Null(node["requiresCountMin"]);
        Assert.Null(node["requiresAllExtIn"]);
        Assert.Null(node["children"]);
    }

    [Fact]
    public void BuildJson_EmitsNestedChildrenRecursively()
    {
        var item = new ShellMenuItem
        {
            Id = "convert",
            Title = "格式转换",
            Kind = ShellMenuKind.Submenu,
            Children =
            [
                new ShellMenuItem
                {
                    Id = "convert-to-pdf", Title = "转为 PDF",
                    Action = "convert-to", Args = ["pdf"],
                    Highlight = true, IsDefault = true,
                    FilterExtensions = [".docx"],
                },
                new ShellMenuItem
                {
                    Id = "mergePdf", Title = "合并 PDF",
                    Action = "convert-to", Args = ["pdf"],
                    RequiresCountMin = 2, RequiresAllExtIn = [".pdf"],
                },
            ],
        };

        var root = JsonNode.Parse(ShellMenuConfigWriter.BuildJson([item], true))!.AsObject();
        var children = root["items"]!.AsArray()[0]!.AsObject()["children"]!.AsArray();

        Assert.Equal(2, children.Count);
        var first = children[0]!.AsObject();
        Assert.Equal("convert-to", first["action"]!.GetValue<string>());
        Assert.Equal(new[] { "pdf" }, first["args"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.True(first["highlight"]!.GetValue<bool>());
        Assert.True(first["default"]!.GetValue<bool>());

        var second = children[1]!.AsObject();
        Assert.Equal(2, second["requiresCountMin"]!.GetValue<int>());
        Assert.Equal(new[] { ".pdf" }, second["requiresAllExtIn"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void BuildJson_WritesChineseTitlesUnescaped()
    {
        // 默认编码器会把中文转成 \uXXXX —— 原生解析器能读，但配置文件不可读、不便排查。
        var json = ShellMenuConfigWriter.BuildJson(
            [new ShellMenuItem { Id = "x", Title = "桌面控制" }], true);

        Assert.Contains("桌面控制", json);
        Assert.DoesNotContain("\\u684C", json);
    }

    // ===================== 跨语言契约 =====================

    [Fact]
    public void ClassicClsid_MatchesNativeHeader()
    {
        // 漂移守卫：C# 注册器写进 InprocServer32 的 CLSID 必须与 native 侧 DllGetClassObject
        // 认识的 CLSID 一致 —— 不一致时 explorer 收到 CLASS_E_CLASSNOTAVAILABLE，菜单永不出现。
        var header = Path.Combine(
            FindRepositoryRoot(),
            "packages", "shell", "shell-context-menu", "native", "include", "BdShell.h");
        Assert.True(File.Exists(header), $"未找到原生契约头：{header}");

        var text = File.ReadAllText(header);
        // kBClassic = {0x7B2E9C41, 0x3D58, 0x4F0A, {0x9E,0x6B,0x1A,0x4C,0x8D,0x2F,0x5E,0x71}}
        Assert.Contains("0x7B2E9C41", text);
        Assert.Contains("kBClassic", text);
        Assert.Equal("7B2E9C41-3D58-4F0A-9E6B-1A4C8D2F5E71",
            ComShellExtensionRegistrar.ClassicClsid.ToString("D").ToUpperInvariant());

        // A 路三个 CLSID 也必须在头里（新菜单侧；由 AppxManifest 引用）
        Assert.Contains("0x7B2E9C41", text);
        Assert.Contains("kExplorerFiles", text);
        Assert.Contains("kExplorerDirectory", text);
        Assert.Contains("kExplorerBg", text);
    }

    [Fact]
    public void SceneKeyPaths_CoverAllFourScenes()
    {
        var paths = ComShellExtensionRegistrar.GetSceneKeyPaths();
        var joined = string.Join("\n", paths);

        Assert.Equal(4, paths.Count);
        Assert.Contains(@"*\shellex\ContextMenuHandlers\BetterDesktop", joined);
        Assert.Contains(@"Directory\shellex\ContextMenuHandlers\BetterDesktop", joined);
        Assert.Contains(@"Directory\Background\shellex\ContextMenuHandlers\BetterDesktop", joined);
        Assert.Contains(@"DesktopBackground\shellex\ContextMenuHandlers\BetterDesktop", joined);
        Assert.All(paths, p => Assert.StartsWith(@"Software\Classes\", p, StringComparison.Ordinal));
    }

    // ===================== 注册器往返（真实 HKCU，测后还原） =====================

    [Fact]
    public void Register_IsIdempotent_AndUnregisterRemovesKeys()
    {
        var wasRegistered = ComShellExtensionRegistrar.IsRegistered();
        var previousDll = ComShellExtensionRegistrar.GetRegisteredDllPath();
        try
        {
            ComShellExtensionRegistrar.Unregister();
            Assert.False(ComShellExtensionRegistrar.IsRegistered());

            var registered = ComShellExtensionRegistrar.Register(out var error);
            if (!registered)
            {
                // 原生 DLL 未部署（未跑 scripts/build-shellmenu.ps1）时跳过——不算失败，但必须给出原因
                Assert.NotNull(error);
                return;
            }

            Assert.Null(error);
            Assert.True(ComShellExtensionRegistrar.IsRegistered());
            Assert.Equal(ComShellExtensionRegistrar.ResolveNativeDllPath(),
                ComShellExtensionRegistrar.GetRegisteredDllPath());

            // 幂等：重复注册不报错、状态不变
            Assert.True(ComShellExtensionRegistrar.Register(out _));
            Assert.True(ComShellExtensionRegistrar.IsRegistered());

            ComShellExtensionRegistrar.Unregister();
            Assert.False(ComShellExtensionRegistrar.IsRegistered());
        }
        finally
        {
            // 还原测试前状态，绝不给用户留下"扩展被测试关掉"的机器。
            //
            // 【为什么不能只调 Register】Register 写的是**本测试进程**的 native 路径，
            // 直接断言它 == previousDll 是自相矛盾的（两者天生不同）；正确做法是把 previousDll
            // 原样写回注册表。2026-09-17 真机装上产品后 100% 复现：注册表里是安装根路径，
            // 而 Register 写回测试 bin 路径 → 断言必败（此前"通过"只是因为测试 bin 里没有
            // native DLL、Register 失败导致断言被短路跳过）。
            if (wasRegistered && !string.IsNullOrWhiteSpace(previousDll))
            {
                RestoreRegistration(previousDll!);
                Assert.Equal(previousDll, ComShellExtensionRegistrar.GetRegisteredDllPath());
            }
        }
    }

    /// <summary>把注册表还原成"CLSID 指向给定路径 + 四个场景键都存在"（与生产写出的形状一致）。</summary>
    private static void RestoreRegistration(string dllPath)
    {
        var clsidText = ComShellExtensionRegistrar.ClassicClsid.ToString("B");
        using (var server = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                   $@"Software\Classes\CLSID\{clsidText}\InprocServer32"))
        {
            server.SetValue(string.Empty, dllPath);
            server.SetValue("ThreadingModel", "Apartment");
        }

        foreach (var relative in ComShellExtensionRegistrar.GetSceneKeyPaths())
        {
            using var handler = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(relative);
            handler.SetValue(string.Empty, clsidText);
        }
    }

    // ===================== 辅助 =====================

    /// <summary>从运行目录向上找仓库根（含 BetterDesktop.slnx）。找不到则抛出（不静默跳过断言）。</summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && directory is not null; i++)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BetterDesktop.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException($"未能在 {AppContext.BaseDirectory} 之上找到 BetterDesktop.slnx");
    }
}
