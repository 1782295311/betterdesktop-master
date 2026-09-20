// BetterDesktop.Shell.ContextMenus — 原生右键扩展注册器（B 路：经典菜单 / Win11「显示更多选项」）
//
// 【生产注册的唯一权威】native/src/dllmain.cpp 里也有 DllRegisterServer（regsvr32 诊断用），
// 两者写出的 GUID 与键位必须逐字一致；生产路径一律走本类，因为只有它能被设置开关随时启停。
//
// 【为什么写 HKCU】HKCU\Software\Classes 参与 HKCR 合并视图，普通用户权限即可写，
// 不需要管理员（与全仓其他右键注册一致，见技术力文档 shell-menu-injection 红线）。
//
// 【生效时机】explorer 在启动时枚举 shellex 处理程序并缓存 —— 新增/删除本扩展后
// **必须重启 explorer** 才生效（设置分区提供「立刻重启桌面」按钮）。

using System;
using System.Collections.Generic;
using System.IO;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Kernel.Deployment;
using Microsoft.Win32;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>原生右键扩展（B 路）注册/注销。所有方法都不抛异常，失败返回结果 + 记日志。</summary>
public static class ComShellExtensionRegistrar
{
    /// <summary>经典菜单处理器 CLSID —— 必须与 native/include/BdShell.h 的 kBClassic 完全一致。</summary>
    public static readonly Guid ClassicClsid = new("7B2E9C41-3D58-4F0A-9E6B-1A4C8D2F5E71");

    /// <summary>处理程序键名（\shellex\ContextMenuHandlers\&lt;本名&gt;）。</summary>
    public const string HandlerKeyName = "BetterDesktop";

    /// <summary>CLSID 键下的友好名（第三方菜单管理器显示用）。</summary>
    public const string FriendlyName = "BetterDesktop 右键快捷功能";

    /// <summary>原生 DLL 文件名（**别名**，唯一权威是 <see cref="NativeDllPath.DllName"/> —— 不复制字面量）。</summary>
    public const string NativeDllName = NativeDllPath.DllName;

    /// <summary>
    /// "当前是一次 dev 注册"的标记文件名（落在 <c>%LOCALAPPDATA%\BetterDesktop\</c> 下，
    /// 与 <c>shellmenu-unregistered.flag</c> 等四个标记同款约定）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 【为什么必须有它】<c>--dev</c> 会把**开发目录**的 DLL 路径写进注册表（这是显式 opt-in，允许），
    /// 但之后从安装根运行的 core / agent 会按规则算出"期望 = 安装根"，看到注册表指向 dev 路径 ⇒
    /// 判漂移 ⇒ 每 60 秒"修"回去 —— dev 注册被反复清掉，且用户看到注册在跳动。
    /// 标记让双方都能识别"这是有意为之的 dev 注册"，从而**停手**。
    /// </para>
    /// <para>
    /// 【**它是文件，不是注册表值** —— 别去注册表里找它】曾考虑写成 CLSID 下的一个值，结论是不必：
    /// core 已经在读这个目录（上面那四个标记同款），再引入一处注册表读写面**只增加面、不增加语义**。
    /// 两者表达力完全等价（"标记存在与否"），而文件与既有约定一致、且 core 不必多碰注册表。
    /// 语义由 core 的 <c>Observed.dev_marker</c> → <c>Registration::DevRegistered</c> →
    /// <c>should_trigger_repair() == false</c> 承接，并已由对照实验验证（标记在 → 不判漂移；移走 → 判漂移）。
    /// </para>
    /// </remarks>
    public const string DevMarkerFileName = "BetterDesktopDev.flag";

    /// <summary>dev 标记文件完整路径。</summary>
    public static string DevMarkerPath => Path.Combine(DeploymentInfo.DirectoryPath, DevMarkerFileName);

    /// <summary>注册的目标场景（HKCU\Software\Classes 下的相对路径）。</summary>
    private static readonly string[] Scenes =
    [
        @"*",
        @"Directory",
        @"Directory\Background",
        @"DesktopBackground",
    ];

    private const string ClassesRoot = @"Software\Classes";

    /// <summary>
    /// 定位应当被注册的原生 DLL（**规则**在 <see cref="NativeDllPath.Resolve"/>，
    /// 与 Rust 侧共用 <c>protocols/native-dll-path-test-vectors.json</c>；本方法只负责喂真实环境）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>顺序与"运行时定位"（<c>DesktopControlLocator</c> / <c>ComponentPaths</c>）相反是有意的</b>：
    /// 那边是"拉起一个进程"，调用方自己那份优先；这里是"写进注册表的持久引用"，必须指向最持久的位置
    /// （安装根）。<b>不要"统一"这两条规则</b> —— 见向量文件头部与 <see cref="NativeDllPath"/>。
    /// </para>
    /// <para>
    /// 本重载的 devMode 取自"当前是否是一次 dev 注册"（标记文件），所以它的结果会随 dev 注册改变 ——
    /// 那是**想要**的：dev 注册期间，status 与自愈都该按 dev 的期望路径看待它。
    /// <b>写注册表时不要用本重载</b>，用 <see cref="ResolveNativeDllPath(bool)"/> 显式传 devMode：
    /// 否则"上一次 dev 注册留下的标记"会让一次普通注册又写回 dev 路径。
    /// </para>
    /// </remarks>
    public static string? ResolveNativeDllPath() => ResolveNativeDllPath(IsDevRegistered());

    /// <summary>按**显式** devMode 解析（写注册表用；不读标记文件）。</summary>
    public static string? ResolveNativeDllPath(bool devMode) => NativeDllPath.Resolve(
        new NativeDllPathInputs
        {
            ProcessDir = AppContext.BaseDirectory,
            InstallRoot = DeploymentInfo.ResolveInstallRoot(),
            LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DevMode = devMode,
        },
        File.Exists);

    /// <summary>注册结果（三态 —— "拒绝"必须与"失败"分开：前者不该重试，也不该被报成故障）。</summary>
    public enum RegisterOutcome
    {
        /// <summary>已注册（含幂等重写）。</summary>
        Registered,

        /// <summary>按规则解析不出任何可用的 DLL 路径 ⇒ <b>拒绝注册</b>，不写注册表，只记日志。</summary>
        DllMissing,

        /// <summary>写注册表时出错。</summary>
        Failed,
    }

    /// <summary>
    /// 注册扩展（HKCU + 幂等重写）。绝不抛出。
    /// </summary>
    /// <param name="devMode">
    /// 显式开发者模式（<c>--dev</c>）：允许注册**开发目录**里的 DLL，并写下
    /// <see cref="DevMarkerFileName"/> 标记让 core / agent 停手。
    /// </param>
    /// <param name="error">失败原因（成功时为 null）。</param>
    public static RegisterOutcome Register(bool devMode, out string? error)
    {
        error = null;
        var dllPath = ResolveNativeDllPath(devMode);
        if (dllPath is null)
        {
            // 这条消息是**给人排障用的**，必须能回答"那我现在该怎么办"：
            // 只说"未找到 DLL"会让人去翻部署目录，而真因往往是"进程在开发 bin 里"。
            error =
                $"拒绝注册：按注册表路径规则解析不出可用的 {NativeDllName}。" +
                "规则 = 安装根（deployment.json）有效时只认安装根；安装根无效时只接受 " +
                $@"%LOCALAPPDATA%\BetterDesktop 之下的进程目录。开发态（bin 里直接跑）" +
                "需显式 opt-in：bdctl --shellmenu-register --dev";
            DiagnosticLog.Trace("shell.context-menu", error);
            return RegisterOutcome.DllMissing;
        }

        var clsidText = ClassicClsid.ToString("B"); // {xxxxxxxx-...}
        try
        {
            // ① CLSID → InprocServer32（explorer 进程内加载）
            using (var clsidKey = Registry.CurrentUser.CreateSubKey($@"{ClassesRoot}\CLSID\{clsidText}"))
            {
                clsidKey.SetValue(string.Empty, FriendlyName);
            }
            using (var serverKey = Registry.CurrentUser.CreateSubKey(
                $@"{ClassesRoot}\CLSID\{clsidText}\InprocServer32"))
            {
                serverKey.SetValue(string.Empty, dllPath);
                serverKey.SetValue("ThreadingModel", "Apartment");
            }

            // ② 四场景处理程序键
            foreach (var scene in Scenes)
            {
                using var handlerKey = Registry.CurrentUser.CreateSubKey(
                    $@"{ClassesRoot}\{scene}\shellex\ContextMenuHandlers\{HandlerKeyName}");
                handlerKey.SetValue(string.Empty, clsidText);
            }

            DiagnosticLog.Trace("shell.context-menu",
                $"原生右键扩展已注册: {dllPath}（{Scenes.Length} 场景，需重启 explorer 生效）");

            // 标记与本次注册**同时**落定：dev 注册写标记（让 core/agent 停手），普通注册清标记。
            // 两侧都必须做 —— 只写不清会让一次 dev 注册永久"免疫"自愈，用户再也修不好。
            if (devMode)
            {
                SetDevMarker(dllPath);
                // WARN 而不是 info：这条路会把"一 clean 就失效"的路径写进注册表，必须显眼。
                DiagnosticLog.Trace("shell.context-menu",
                    $"DEV 注册：注册表已指向开发目录 {dllPath} —— 该目录被 clean/重建后菜单会静默失效，" +
                    "且 core / agent 的注册自愈已按标记停手（重新安装或 --system-integration register 可恢复）");
            }
            else
            {
                ClearDevMarker();
            }

            return RegisterOutcome.Registered;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            DiagnosticLog.Trace("shell.context-menu", $"原生右键扩展注册失败: {ex.Message}");
            return RegisterOutcome.Failed;
        }
    }

    /// <summary>兼容重载（不启用 dev 模式）。</summary>
    public static bool Register(out string? error) =>
        Register(devMode: false, out error) == RegisterOutcome.Registered;

    /// <summary>注销扩展（删键树，幂等）。失败只记日志。</summary>
    public static void Unregister()
    {
        // 标记描述的是"注册表里那一次注册" —— 注册表都清了，标记必须一起清，
        // 否则下一次普通注册会被一个陈旧标记压着（自愈永不上手）。
        ClearDevMarker();
        try
        {
            var clsidText = ClassicClsid.ToString("B");
            foreach (var scene in Scenes)
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    $@"{ClassesRoot}\{scene}\shellex\ContextMenuHandlers", writable: true))
                {
                    key?.DeleteSubKeyTree(HandlerKeyName, throwOnMissingSubKey: false);
                }
            }

            // 先删子键再删父键（RegDeleteTree 语义；DeleteSubKeyTree 会递归，这里显式删父键）
            using (var clsidParent = Registry.CurrentUser.OpenSubKey($@"{ClassesRoot}\CLSID", writable: true))
            {
                clsidParent?.DeleteSubKeyTree(clsidText, throwOnMissingSubKey: false);
            }

            DiagnosticLog.Trace("shell.context-menu", "原生右键扩展已注销（需重启 explorer 生效）");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Trace("shell.context-menu", $"原生右键扩展注销失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 扩展当前是否已注册。判据 = CLSID 的 InprocServer32 有效 **且至少一个场景键存在**。
    /// <para>
    /// 【为什么不是只查一个场景】四个场景可能被分别写入（不同开关、不同注册路径），
    /// 只查 `*` 会在"只注册了桌面空白场景"时误报"未注册"——设置页开关随之显示为关闭、
    /// 用户切不动，且 Agent 自愈会误判需要修复。
    /// </para>
    /// </summary>
    public static bool IsRegistered()
    {
        try
        {
            var clsidText = ClassicClsid.ToString("B");
            using (var serverKey = Registry.CurrentUser.OpenSubKey(
                $@"{ClassesRoot}\CLSID\{clsidText}\InprocServer32"))
            {
                if (serverKey?.GetValue(string.Empty) is not string value || value.Length == 0)
                {
                    return false;
                }
            }

            return RegisteredSceneCount() > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>已注册的场景数量（0~<see cref="Scenes"/>.Length）：诊断与"部分注册"判定的依据。</summary>
    public static int RegisteredSceneCount()
    {
        try
        {
            var count = 0;
            foreach (var scene in Scenes)
            {
                using var handlerKey = Registry.CurrentUser.OpenSubKey(
                    $@"{ClassesRoot}\{scene}\shellex\ContextMenuHandlers\{HandlerKeyName}");
                if (handlerKey?.GetValue(string.Empty) is string clsid && clsid.Length > 0)
                {
                    count++;
                }
            }

            return count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>已注册场景数是否与预期一致（Agent 自愈据此判断"注册不全"）。</summary>
    public static bool IsFullyRegistered() => RegisteredSceneCount() == Scenes.Length;

    /// <summary>注册表里记录的 DLL 路径（诊断：与当前部署路径不一致说明需要重新注册）。</summary>
    public static string? GetRegisteredDllPath()
    {
        try
        {
            var clsidText = ClassicClsid.ToString("B");
            using var serverKey = Registry.CurrentUser.OpenSubKey(
                $@"{ClassesRoot}\CLSID\{clsidText}\InprocServer32");
            return serverKey?.GetValue(string.Empty) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 当前注册是否是一次 **dev 注册**（<see cref="DevMarkerFileName"/> 存在）。
    /// </summary>
    /// <remarks>
    /// 语义 = "这次注册是**有意**指向开发目录的，别按生产规则判它漂移"。
    /// <b>它只表示"别自动修"，不表示"注册是对的"</b> —— 用户仍可显式重注册。
    /// </remarks>
    public static bool IsDevRegistered() => File.Exists(DevMarkerPath);

    /// <summary>写下 dev 标记（内容仅供人看；判据是文件存在与否）。</summary>
    private static void SetDevMarker(string dllPath)
    {
        try
        {
            Directory.CreateDirectory(DeploymentInfo.DirectoryPath);
            File.WriteAllText(
                DevMarkerPath,
                $"BetterDesktopDev=1{Environment.NewLine}registered={dllPath}{Environment.NewLine}" +
                $"at={DateTimeOffset.Now:O}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 标记写不上 → core/agent 会把这次 dev 注册当漂移反复修回去（用户体感"注册不上"），必须留痕
            DiagnosticLog.Trace("shell.context-menu",
                $"写 dev 标记失败（自愈不会停手，dev 注册可能被反复覆盖）: {ex.Message}");
        }
    }

    /// <summary>清除 dev 标记（普通注册 / 注销时调用）。</summary>
    private static void ClearDevMarker()
    {
        try
        {
            if (File.Exists(DevMarkerPath))
            {
                File.Delete(DevMarkerPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Trace("shell.context-menu",
                $"清除 dev 标记失败（自愈会继续停手）: {ex.Message}");
        }
    }

    /// <summary>
    /// 本扩展**拥有**的全部键路径（父键在前）—— 备份 / 回滚 / 干净度检查的**唯一范围定义**。
    /// </summary>
    /// <remarks>
    /// 【为什么必须与注册/注销同源】备份范围若另写一份清单，就会长出"注册删 6 个键、备份只记 4 个"
    /// 这类**静默不完整**——回滚时看不出少了什么，只会在某天发现某个键没被还原。
    /// 故这里从 <see cref="GetSceneKeyPaths"/> 与 <see cref="ClassicClsid"/> 推导，不复制字面量。
    /// </remarks>
    public static IReadOnlyList<string> GetOwnedKeyPaths()
    {
        var clsid = ClassicClsid.ToString("B");
        var list = new List<string>(Scenes.Length + 2)
        {
            $@"{ClassesRoot}\CLSID\{clsid}",
            $@"{ClassesRoot}\CLSID\{clsid}\InprocServer32",
        };
        list.AddRange(GetSceneKeyPaths());
        return list;
    }

    /// <summary>四场景键的完整路径清单（诊断/单测用；避免测试里重复硬编码）。</summary>
    public static IReadOnlyList<string> GetSceneKeyPaths()
    {
        var list = new List<string>(Scenes.Length);
        foreach (var scene in Scenes)
        {
            list.Add($@"{ClassesRoot}\{scene}\shellex\ContextMenuHandlers\{HandlerKeyName}");
        }
        return list;
    }
}
