using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BetterDesktop.Shell.Music.Contracts;

namespace BetterDesktop.Shell.Music.Services;

/// <summary>
/// 酷狗音乐私有 API 的 C# 移植（7407 变体 A：Mineradio kugou-api.js → C#）。
/// 红线落地（7405→7407）：
///   ①SALT 版本分立：ANDROID（API 参数）/ H5（网页）/ SIGN_KEY（播放链接）三套盐不混用（红线 1）；
///   ②签名参数必须按键名升序排序拼接（红线 2）；
///   ③四式签名各司其职：signatureAndroidParams / signatureH5Params / signKey / paramsKey（红线 3）；
///   ④UA 必须伪装客户端 Chrome120，裸 UA 会被风控拒绝（红线 4）；
///   ⑤平台 SALT/端点随 App 升级漂移——复用前必须实测（红线 8），失败返回空/null 不抛。
/// 合规：仅元数据/搜索/播放链接/歌词查询，无 DRM 解密、无 VIP 绕过、无登录。
/// </summary>
public sealed class KugouMusicApi : IKugouMusicApi
{
    // ---- 端点（7407 §二 酷狗表 [verified]） ----
    private const string SearchEndpoint = "http://songsearch.kugou.com/song_search_v2";
    private const string SongInfoEndpoint = "http://m.kugou.com/app/i/getSongInfo.php";
    private const string LyricSearchEndpoint = "https://krcs.kugou.com/search";
    private const string LyricDownloadEndpoint = "https://krcs.kugou.com/download";

    // ---- SALT（7407 §二 [verified]；平台升级会漂移，签名失败先查这里） ----
    private const string AndroidSalt = "OIlwieks28dk2k092lksi2UIkp";
    private const string H5Salt = "NVPh5oo715z5DIWAeQlhMDsWXXQV4hwt";
    private const string SignKeySalt = "57ae12eb6890223e355ccfcb74edf70d";

    // ---- 客户端常量（7407 §二：APPID 1005(Android) / CLIENTVER 20489） ----
    private const string AppId = "1005";
    private const string ClientVer = "20489";

    // 7407 红线 4：UA 必须伪装客户端（酷狗 Chrome120），裸 UA 被风控拒绝。
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        // 平台风控下超时/失败由调用方重试决策；连接池默认即可。
        AllowAutoRedirect = true
    })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static readonly string Mid = Guid.NewGuid().ToString("N").Substring(0, 32);

    /// <summary>
    /// 入站 JSON 的解析选项。
    /// <para>
    /// 【C1 为什么限深】响应来自**外部 HTTP API**（酷狗）—— 最不可信的一类输入。
    /// 显式限深 32，不依赖 STJ 的默认值：默认值是**库的行为**，显式值才是**我们的契约**。
    /// </para>
    /// </summary>
    private static readonly JsonDocumentOptions ResponseJsonOptions = new() { MaxDepth = 32 };

    // ---------------- 签名四式（7407 §三 [verified] 逐函数移植） ----------------

    /// <summary>Android API 参数签名：MD5(SALT + 排序后 k=v 串 + data + SALT)。</summary>
    internal static string SignatureAndroidParams(IReadOnlyDictionary<string, string> parameters, string? data = null)
    {
        // 红线 2：按键名升序排序后拼接 key=value（顺序错签名对不上）。
        var joined = string.Join("", parameters.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => $"{k}={parameters[k]}"));
        return Md5Hex($"{AndroidSalt}{joined}{data ?? string.Empty}{AndroidSalt}");
    }

    /// <summary>H5 请求签名：MD5(SALT + 排序后 k=v 串 + body + SALT)。</summary>
    internal static string SignatureH5Params(IReadOnlyDictionary<string, string> parameters, string? bodyJson = null)
    {
        var parts = parameters.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => $"{k}={parameters[k]}")
            .ToList();
        if (!string.IsNullOrEmpty(bodyJson))
        {
            parts.Add(bodyJson);
        }
        return Md5Hex($"{H5Salt}{string.Join("", parts)}{H5Salt}");
    }

    /// <summary>播放链接签名 key：MD5(hash + SIGN_KEY_SALT + appid + mid + userid)。</summary>
    internal static string SignKey(string hash, string mid, string userid, string? appid = null)
        => Md5Hex($"{hash}{SignKeySalt}{appid ?? AppId}{mid}{userid}");

    /// <summary>参数签名 key：MD5(appid + clientver + clienttime + ANDROID_SALT)。</summary>
    internal static string ParamsKey(string clientTime)
        => Md5Hex($"{AppId}{ClientVer}{clientTime}{AndroidSalt}");

    // ---------------- API ----------------

    /// <inheritdoc />
    public async Task<string> SearchAsync(string keyword, int page = 1, int pageSize = 30, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return string.Empty;
        }

        var url = $"{SearchEndpoint}?keyword={Uri.EscapeDataString(keyword)}&page={Math.Max(1, page)}&pagesize={Math.Clamp(pageSize, 1, 100)}";
        return await GetAsync(url, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> GetSongInfoAsync(string fileHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileHash))
        {
            return string.Empty;
        }

        var clientTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var parameters = new Dictionary<string, string>
        {
            ["appid"] = AppId,
            ["clienttime"] = clientTime,
            ["clientver"] = ClientVer,
            ["cmd"] = "playinfo",
            ["hash"] = fileHash,
            ["key"] = ParamsKey(clientTime),
            ["mid"] = Mid,
            ["userid"] = "0"
        };
        var query = string.Join("&", parameters.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        return await GetAsync($"{SongInfoEndpoint}?{query}", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> GetPlayUrlAsync(string fileHash, CancellationToken cancellationToken = default)
    {
        var json = await GetSongInfoAsync(fileHash, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json, ResponseJsonOptions);
            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("play_url", out var playUrl)
                && playUrl.GetString() is { Length: > 0 } url)
            {
                return url;
            }
        }
        catch (JsonException)
        {
            // 平台字段漂移/风控页 → 返回 null（7407 红线 8：复用前实测）。
        }
        return null;
    }

    /// <inheritdoc />
    public async Task<string?> GetLyricAsync(string fileHash, string keyword, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileHash))
        {
            return null;
        }

        // krcs 两段式：search（按 hash 找候选）→ download（取 content）。
        var searchJson = await GetAsync(
            $"{LyricSearchEndpoint}?keyword={Uri.EscapeDataString(keyword ?? string.Empty)}&hash={Uri.EscapeDataString(fileHash)}",
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(searchJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(searchJson, ResponseJsonOptions);
            if (!doc.RootElement.TryGetProperty("candidates", out var candidates)
                || candidates.GetArrayLength() == 0)
            {
                return null;
            }

            var first = candidates[0];
            var id = first.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            var accessKey = first.TryGetProperty("accesskey", out var akProp) ? akProp.GetString() : null;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(accessKey))
            {
                return null;
            }

            var downloadJson = await GetAsync(
                $"{LyricDownloadEndpoint}?keyword={Uri.EscapeDataString(keyword ?? string.Empty)}&id={Uri.EscapeDataString(id)}&accesskey={Uri.EscapeDataString(accessKey)}",
                cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(downloadJson))
            {
                return null;
            }

            using var dlDoc = JsonDocument.Parse(downloadJson, ResponseJsonOptions);
            if (dlDoc.RootElement.TryGetProperty("content", out var content)
                && content.GetString() is { Length: > 0 } lyric)
            {
                return lyric;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static async Task<string> GetAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // 红线 4：伪装客户端 UA；Referer 对齐酷狗域（7407 §四）。
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Referrer = new Uri("https://www.kugou.com/");
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return string.Empty; // 403/滑块 = 风控（先查 UA 与版本常量，7407 §五）
            }
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return string.Empty; // 网络失败不抛（M10 惯例；平台漂移实测责任在调用方）
        }
    }

    private static string Md5Hex(string input)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return System.Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
