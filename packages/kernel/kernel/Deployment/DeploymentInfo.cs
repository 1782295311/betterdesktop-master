// BetterDesktop.Kernel.Deployment — 「程序装在哪」的单一真相（安装根指针）
//
// 【为什么需要它】安装器级落地（2026-09-17）：程序装到稳定目录
//   %LOCALAPPDATA%\BetterDesktop\app\<build>，而各消费者（宿主 / CLI / Agent / 桌面服务 / 托盘 / 看门狗）
//   原先各自写一套「同目录 → %LOCALAPPDATA%\BetterDesktop → …\DesktopControl」查找链 ——
//   既没有"当前安装"这一概念，也无法在升级换目录后自愈（注册与自启会静默漂移向旧目录）。
//   本类把"当前安装根"收成一个点：deployment.json（安装脚本写，注册中心读）。
//
// 【失败语义】读取失败一律不抛、不阻断：返回 false → 调用方回退既有查找链（开发态 bin 目录照常工作）。
//   「查不到」绝不等于「没问题」，所以错误原因经 out error 显式带出，由调用方决定是否留痕。
//
// 【跨进程契约】文件名与字段名与 scripts/install-betterdesktop.ps1 / uninstall-betterdesktop.ps1
//   逐字一致；托盘与看门狗是**零包引用**工程，各自复刻同一份读取逻辑（改一处务必改另一处）。

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterDesktop.Kernel.Deployment;

/// <summary>deployment.json 的内容（安装器写入；属性名是跨进程契约，不要改名）。</summary>
public sealed class DeploymentRecord
{
    public int SchemaVersion { get; set; } = DeploymentInfo.SchemaVersion;

    public string Product { get; set; } = DeploymentInfo.ProductName;

    /// <summary>产品版本（如 1.3.0）。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>构建号（如 2026.09.17.2140；同版本下的正确判据，与 updater 同语义）。</summary>
    public string Build { get; set; } = string.Empty;

    /// <summary>安装根绝对路径（含全部组件 exe 与 native\ 的那个目录）。</summary>
    public string InstallRoot { get; set; } = string.Empty;

    public string InstalledAt { get; set; } = string.Empty;

    /// <summary>A 路（Win11 新菜单稀疏包）注册模式：signed / loose / skipped / unknown。</summary>
    public string MsixMode { get; set; } = "unknown";
}

/// <summary>安装根指针读写（%LOCALAPPDATA%\BetterDesktop\deployment.json）。</summary>
public static class DeploymentInfo
{
    public const string ProductName = "BetterDesktop";

    /// <summary>指针文件名（跨进程契约；脚本与 C# 必须一致）。</summary>
    public const string FileName = "deployment.json";

    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // 中文/非 ASCII 路径原样写出（默认编码器会转 \uXXXX，排查时不友好）
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>指针所在目录（%LOCALAPPDATA%\BetterDesktop）。</summary>
    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);

    /// <summary>指针绝对路径。</summary>
    public static string FilePath => Path.Combine(DirectoryPath, FileName);

    /// <summary>读取指针（默认路径）。文件缺失/损坏/字段不全 → false（**不抛**），原因写 error。</summary>
    public static bool TryRead(out DeploymentRecord? record, out string? error)
        => TryRead(FilePath, out record, out error);

    /// <summary>读取指针（显式路径；单测用真实文件、生产走 <see cref="FilePath"/>）。</summary>
    public static bool TryRead(string path, out DeploymentRecord? record, out string? error)
    {
        record = null;
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                error = $"未找到 {path}（未用安装器安装？）";
                return false;
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            var parsed = JsonSerializer.Deserialize<DeploymentRecord>(json, ReadOptions);
            if (parsed is null)
            {
                error = "deployment.json 内容为空";
                return false;
            }

            if (string.IsNullOrWhiteSpace(parsed.InstallRoot))
            {
                error = "deployment.json 缺 installRoot";
                return false;
            }

            record = parsed;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>原子写入指针（默认路径）。失败返回 false，不抛。</summary>
    public static bool Write(DeploymentRecord record, out string? error)
        => Write(record, FilePath, out error);

    /// <summary>
    /// 原子写入指针（写临时文件再 Move 覆盖：读方永远看到完整内容）。
    /// 显式路径重载供单测使用（生产写 <see cref="FilePath"/>）。
    /// </summary>
    public static bool Write(DeploymentRecord record, string path, out string? error)
    {
        error = null;
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.InstallRoot))
        {
            error = "installRoot 不能为空";
            return false;
        }

        var temp = path + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            record.SchemaVersion = SchemaVersion;
            if (string.IsNullOrWhiteSpace(record.Product))
            {
                record.Product = ProductName;
            }
            if (string.IsNullOrWhiteSpace(record.InstalledAt))
            {
                record.InstalledAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
            }

            File.WriteAllText(temp, JsonSerializer.Serialize(record, WriteOptions), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // 清理失败不影响结果
            }
            return false;
        }
    }

    /// <summary>
    /// 解析"当前安装根"：读了指针且目录确实存在才返回；否则 null（调用方回退既有查找链）。
    /// 刻意**不**把"目录不存在"当成功 —— 升级换目录后旧指针会让所有消费者一起指空。
    /// </summary>
    public static string? ResolveInstallRoot() => ResolveInstallRoot(FilePath);

    /// <summary>显式路径重载（单测用；语义同默认路径版本）。</summary>
    public static string? ResolveInstallRoot(string path)
    {
        if (!TryRead(path, out var record, out _) || record is null)
        {
            return null;
        }

        try
        {
            var root = record.InstallRoot.Trim();
            return Directory.Exists(root) ? root : null;
        }
        catch
        {
            return null;
        }
    }
}
