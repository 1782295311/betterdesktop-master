namespace BetterDesktop.Shell.IndexIpc;

/// <summary>
/// 索引引擎状态快照（对应引擎 <c>model.rs::IndexStatus</c>，JSON camelCase）。
///
/// 【内存治理 · 计划 §5.4】引擎不在 <c>IResourceGovernor</c> 的插件 subject 域内，
/// 故 <see cref="RssBytes"/> / <see cref="AppCount"/> / <see cref="FileCount"/> 由引擎自报，
/// 供设置中心「索引服务」状态行展示。
/// </summary>
public sealed class IndexStatus
{
    public string Version { get; init; } = string.Empty;

    public int Pid { get; init; }

    public long UptimeSeconds { get; init; }

    /// <summary>索引构建中——构建期消费者应回退本地实现，不得阻塞等待。</summary>
    public bool Building { get; init; }

    public int AppCount { get; init; }

    public int FileCount { get; init; }

    /// <summary>上次构建耗时（毫秒；未构建过为 0）。</summary>
    public long LastBuildMs { get; init; }

    /// <summary>上次构建完成时刻（Unix 毫秒；0 = 尚未构建）——设置中心状态行据此显示「索引有多新」。</summary>
    public long LastBuildAtMs { get; init; }

    /// <summary>是否降级（构建失败/超出上限/根目录不可用）。</summary>
    public bool Degraded { get; init; }

    /// <summary>降级原因（未降级为 null）——降级必须可见，禁止静默。</summary>
    public string? DegradeReason { get; init; }

    /// <summary>进程工作集（字节；采集失败为 0）。</summary>
    public long RssBytes { get; init; }

    /// <summary>图标缓存条目数（M4；0 = 还没人取过图标）。</summary>
    public int IconCacheEntries { get; init; }

    /// <summary>图标缓存字节数（M4）——引擎只驻留 PNG 字节（不解码），故远小于位图占用。</summary>
    public long IconCacheBytes { get; init; }
}

/// <summary>
/// 应用候选（raw，对应引擎 <c>model.rs::AppCandidate</c>）。
///
/// 【权威源纪律 · 计划 §5.1】这是磁盘/开始菜单的**事实**，不含 `AppItem` 语义
/// （固定态/分组/分类/图标缓存键）。消费方须经自身 `ResolveFromPath` / `ShellLinkResolver`
/// 升格为 `AppItem`——升格职责绝不落到引擎或本 DTO。
/// </summary>
public sealed class AppCandidate
{
    /// <summary>磁盘路径（.lnk 保留自身路径）。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>目标路径（引擎不解析 lnk，恒等于 <see cref="Path"/>；C# 侧解析后覆盖）。</summary>
    public string TargetPath { get; init; } = string.Empty;

    /// <summary>名称候选（文件 stem；C# 侧以 FileDescription 覆盖）。</summary>
    public string NameHint { get; init; } = string.Empty;

    /// <summary>来源：<c>start-menu</c> | <c>program-files</c>。</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>图标键（M4 才填充；M2 恒为空）。</summary>
    public string IconKey { get; init; } = string.Empty;
}

/// <summary><c>list_apps</c> 响应（对应引擎 <c>engine.rs</c> list_apps 返回体）。</summary>
public sealed class ListAppsResult
{
    public IReadOnlyList<AppCandidate> Apps { get; init; } = Array.Empty<AppCandidate>();

    public int Count { get; init; }

    /// <summary>构建中——消费者应回退本地实现，不得阻塞等待。</summary>
    public bool Building { get; init; }

    public bool Degraded { get; init; }

    public string? DegradeReason { get; init; }
}

/// <summary>
/// 文件命中（对应引擎 <c>model.rs::FileHit</c>）。
///
/// 【权威源纪律】引擎只给文件事实（路径/名称/大小/修改时间），**不含**排序分与 `Category`——
/// 那些由 `FileSearchProvider.ScoreFileName` 与 `SearchResult` 在 C# 侧决定。
/// </summary>
public sealed class FileHit
{
    public string Path { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    /// <summary>修改时间（Unix 毫秒；取不到为 0）。</summary>
    public long ModifiedMs { get; init; }
}

/// <summary><c>search_files</c> 响应（对应引擎 <c>engine.rs</c> search_files 返回体）。</summary>
public sealed class SearchFilesResult
{
    public IReadOnlyList<FileHit> Files { get; init; } = Array.Empty<FileHit>();

    public int Count { get; init; }

    /// <summary>构建中——消费者应回退本地实现（不得把「构建中」当作「没有文件」）。</summary>
    public bool Building { get; init; }

    public bool Degraded { get; init; }

    public string? DegradeReason { get; init; }
}

/// <summary>
/// 单个图标的字节（对应引擎 <c>get_icons</c> 的 icons 元素）。
///
/// 【只产一档大图 · 2026-09-14 用户硬约束】引擎一律返回 **256×256 PNG**；
/// 显示端按目标尺寸**倍缩**（WPF <c>DecodePixelWidth</c> + <c>BitmapScalingMode.HighQuality</c>），
/// 故任何尺寸的消费方都复用同一张图，不再「用多大就申请多大」。
/// </summary>
public sealed class IconHit
{
    /// <summary>缓存键（与请求里的键逐字对应；目标 exe 路径优先，UWP 为 AUMID）。</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>256×256 PNG 的 base64。</summary>
    public string PngBase64 { get; init; } = string.Empty;

    /// <summary>
    /// 解码为 PNG 字节；base64 非法/为空时返回 <c>null</c>（**不抛**）。
    /// <para>图标是非关键资源：一张坏图不该让整个图标加载流程失败——调用方据 null 走 glyph 兜底。</para>
    /// </summary>
    public byte[]? TryDecodePng()
    {
        if (string.IsNullOrWhiteSpace(PngBase64))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(PngBase64);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// <c>get_icons</c> 响应（对应引擎 <c>engine.rs</c> get_icons 返回体）。
///
/// 【为什么缺失键不静默】提取失败的键**不出现在 <see cref="Icons"/> 里**，只由
/// <see cref="Missing"/> 计数暴露——调用方据此走 glyph 兜底，而不是把「取不到」当成「已取到空图」。
/// </summary>
public sealed class GetIconsResult
{
    public IReadOnlyList<IconHit> Icons { get; init; } = Array.Empty<IconHit>();

    public int Count { get; init; }

    /// <summary>提取失败/超限被跳过的键数（不静默）。</summary>
    public int Missing { get; init; }
}

/// <summary>
/// 设置增量更新载荷（仅非 null 字段下发）。
///
/// 【跨语言键名纪律】引擎按 **kebab-case** 解析（<c>app-source-backend</c> / <c>max-entries</c> /
/// <c>scan-roots</c> / <c>enabled</c>），故本类型的下发不使用命名策略，而是在
/// <c>IndexIpcClient</c> 内显式构造 kebab-case 键的字典——避免两侧命名策略漂移导致
/// 「请求被静默忽略」这类最难排查的故障（契约测试 <c>ApplySettingsWireKeysAreKebabCase</c> 守护）。
/// </summary>
public sealed class IndexSettingsPatch
{
    /// <summary>应用源后端：<c>engine</c> | <c>local</c>。</summary>
    public string? AppSourceBackend { get; init; }

    /// <summary>索引条目上限。</summary>
    public int? MaxEntries { get; init; }

    /// <summary>文件索引根目录覆盖（空数组 = 用引擎内置根集语义）。</summary>
    public IReadOnlyList<string>? ScanRoots { get; init; }

    /// <summary>索引总开关。</summary>
    public bool? Enabled { get; init; }
}
