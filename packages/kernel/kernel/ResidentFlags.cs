// BetterDesktop.Kernel.Core — 常驻进程的看门狗豁免标记（跨进程契约）
//
// 【为什么"退出"必须写标记】看门狗是"进程消失就拉回"的设计（3s 轮询 + 8s 宽限）：
// 用户显式退出主程序时若不写标记，十几秒后壳会**自己回来** —— 2026-09-17 用户实测的"退不掉"。
// 标记存在 = 不守护；用户下次显式启动会清掉标记（tray/ProcessBridge.StartHost）。
//
// 【为什么名字在这里又有一份】托盘（BetterDesktop.Tray）与看门狗（BetterDesktop.Watchdog）是
// **零依赖进程**（壳损坏时它们必须照常工作，故不引用 kernel）→ 那两处各持一份字面量不可避免，
// 与 MenuCmd 管道名同款约定。本类是**宿主侧（Host / 插件）的唯一写入口**。
// ⚠️ 改动这些文件名需三处同步：本文件 / tray/ProcessBridge.cs / watchdog/Program.cs。
//
// 目录约定：%LOCALAPPDATA%\BetterDesktop\（与 watchdog/Program.cs 的 _dataDir 一致）。

using System;
using System.IO;

namespace BetterDesktop.Kernel.Core;

/// <summary>看门狗豁免标记（Host / Agent / 全局暂停）的路径与读写。</summary>
public static class ResidentFlags
{
    /// <summary>存在 → 看门狗不守护 Host（用户显式"退出 BetterDesktop"时写）。</summary>
    public const string HostStopped = "host-stopped.flag";

    /// <summary>存在 → 看门狗不守护 Agent（用户显式"停止常驻服务"时写）。</summary>
    public const string AgentStopped = "agent-stopped.flag";

    /// <summary>存在 → 全部暂停守护（应急开关，人工放置）。</summary>
    public const string WatchdogPaused = "watchdog-pause.flag";

    /// <summary>标记目录（%LOCALAPPDATA%\BetterDesktop）。</summary>
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BetterDesktop");

    /// <summary>标记文件绝对路径。</summary>
    public static string PathOf(string flagName) => Path.Combine(DataDirectory, flagName);

    /// <summary>写标记（内容为时间戳，仅供人工排查；失败静默——标记写不上不该阻断退出流程）。</summary>
    public static void Set(string flagName)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(PathOf(flagName), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        catch (Exception)
        {
            // 静默：退出路径上不能因为写标记失败而卡住
        }
    }

    /// <summary>清标记（用户重新启动对应进程时调用）。</summary>
    public static void Clear(string flagName)
    {
        try
        {
            var path = PathOf(flagName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 静默：清不掉最多是多守护一次
        }
    }

    /// <summary>标记是否存在。</summary>
    public static bool IsSet(string flagName) => File.Exists(PathOf(flagName));
}
