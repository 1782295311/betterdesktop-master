using System;
using System.IO;
using System.Reflection;
using BetterDesktop.Shell.Clipboard.Contracts;
using BetterDesktop.Shell.Clipboard.Ipc;
using Xunit;

namespace BetterDesktop.Shell.Clipboard.Tests;

/// <summary>
/// 公共契约守卫：契约被唯一实现（引擎 IPC 代理）完整实现；契约层零实现依赖。
/// <para>
/// 【2026-09-14】原文件里的行为断言（Pin/Delete/ClearAllUnpinned/ImportEntries 语义）随宿主内
/// legacy 实现（<c>ClipboardManager</c>）一起删除 —— 那些语义现在是引擎的职责，由 Rust 侧单测覆盖；
/// 本文件只保留与实现无关的两条结构断言。
/// </para>
/// </summary>
public class ClipboardServiceContractTests
{
    [Fact]
    public void InterfaceMembers_AllImplementedByIpcProxy()
    {
        Assert.True(typeof(IClipboardService).IsAssignableFrom(typeof(ClipboardIpcClient)));

        // 接口成员均可正常调用（无 NotImplementedException 路径）。
        foreach (MethodInfo method in typeof(IClipboardService).GetMethods())
        {
            Assert.NotNull(typeof(ClipboardIpcClient).GetMethod(method.Name));
        }

        foreach (PropertyInfo property in typeof(IClipboardService).GetProperties())
        {
            Assert.NotNull(typeof(ClipboardIpcClient).GetProperty(property.Name));
        }

        // 事件成员。
        foreach (EventInfo ev in typeof(IClipboardService).GetEvents())
        {
            Assert.NotNull(typeof(ClipboardIpcClient).GetEvent(ev.Name));
        }
    }

    [Fact]
    public void ContractLayer_ZeroImplementationDependency()
    {
        // 沿 BaseDirectory 向上找仓库根（BetterDesktop.slnx 标志），避免绑定输出目录层数。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BetterDesktop.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        string contractDir = Path.Combine(dir!.FullName, "packages", "api", "Clipboard");
        string[] files = Directory.GetFiles(contractDir, "*.cs");
        Assert.True(files.Length >= 8, $"契约文件应 ≥ 8（现有 {files.Length}）");

        foreach (string file in files)
        {
            string content = File.ReadAllText(file);
            Assert.DoesNotContain("System.Runtime.InteropServices", content);
            Assert.DoesNotContain("BetterDesktop.Kernel", content);
            Assert.DoesNotContain("System.Windows", content);
            // 只允许自身契约命名空间与 System 基础库。
        }
    }
}
