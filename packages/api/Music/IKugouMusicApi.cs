namespace BetterDesktop.Shell.Music.Contracts;

/// <summary>
/// 酷狗音乐平台 API 契约（7407 变体 A 跨语言移植：Mineradio kugou-api.js → C#）。
/// 供未来桌宠/灵动岛消费（当前无 UI 消费方，只提供基础设施）。
/// 返回原始 JSON 字符串（System.Text.Json 交由消费方按需解析——平台字段随版本漂移，
/// 提前固化 DTO 会随平台升级腐化，7407 红线 8：复用前必须实测）。
///
/// 合规红线（7407）：仅元数据/搜索/播放链接/歌词查询——
/// 无 DRM 解密、无 VIP 绕过、无登录态、无音频下载缓存。
/// </summary>
public interface IKugouMusicApi
{
    /// <summary>搜索歌曲。返回原始 JSON（data.lists：FileHash/SongName/SingerName/AlbumID/Duration 等）。</summary>
    Task<string> SearchAsync(string keyword, int page = 1, int pageSize = 30, CancellationToken cancellationToken = default);

    /// <summary>取歌曲播放信息（含 play_url）。返回原始 JSON；失败返回空串。</summary>
    Task<string> GetSongInfoAsync(string fileHash, CancellationToken cancellationToken = default);

    /// <summary>解析播放链接（需签名 key = MD5(hash + SIGN_KEY + appid + mid + userid)）。失败返回 null。</summary>
    Task<string?> GetPlayUrlAsync(string fileHash, CancellationToken cancellationToken = default);

    /// <summary>取歌词（krcs search → download 链）。失败返回 null。</summary>
    Task<string?> GetLyricAsync(string fileHash, string keyword, CancellationToken cancellationToken = default);
}
