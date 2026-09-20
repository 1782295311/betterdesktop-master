// MediaCore.dll 实现 — Windows.Media.Control（SMTC 会话）封装（C++/WinRT 真实实现）。
// 原托管实现（MediaPlayerCore.cs）的 WinRT 投影路径迁入本层：
//   会话枚举、时间线、随机/循环状态、缩略图字节、控制命令。
// 依赖 Windows SDK 10.0.17763+ 的 C++/WinRT 预生成投影头（winrt/Windows.Media.Control.h）。
// 失败一律返回 HRESULT（0=成功），调用方负责降级（空会话/命令失败）。
//
// 线程模型：C# 侧经 Task.Run 从线程池调用（MTA）。首个调用线程按 MTA 初始化公寓；
// 若调用线程已按其他模式初始化（RPC_E_CHANGED_MODE）则忽略——SMTC 对象是敏捷的，跨公寓可用。
#include <windows.h>
#include <objbase.h>
#include <stdint.h>
#include <algorithm>
#include <cstring>
#include <cwchar>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Media.h>
#include <winrt/Windows.Media.Control.h>
#include <winrt/Windows.Storage.Streams.h>

#include "media_core.h"

#pragma comment(lib, "runtimeobject.lib")

namespace winrt
{
using namespace Windows::Media;
using namespace Windows::Media::Control;
using namespace Windows::Storage::Streams;
} // namespace winrt

namespace
{
using Session = winrt::GlobalSystemMediaTransportControlsSession;
using SessionManager = winrt::GlobalSystemMediaTransportControlsSessionManager;

std::mutex g_lock;
bool g_initAttempted = false;
SessionManager g_manager{ nullptr };
std::vector<Session> g_sessions;
std::unordered_map<int64_t, Session> g_byId;
int64_t g_nextId = 1;

// 惰性初始化 SMTC 管理器（幂等；失败后不再重试，本进程生命周期内降级为空会话）。
bool EnsureManager()
{
    std::lock_guard<std::mutex> guard(g_lock);
    if (g_manager)
    {
        return true;
    }
    if (g_initAttempted)
    {
        return false;
    }
    g_initAttempted = true;
    try
    {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
    }
    catch (winrt::hresult_error const& e)
    {
        // 调用线程已按其他模式初始化：SMTC 对象敏捷，继续使用即可。
        if (e.code() != RPC_E_CHANGED_MODE)
        {
            return false;
        }
    }
    try
    {
        g_manager = SessionManager::RequestAsync().get();
        return g_manager != nullptr;
    }
    catch (...)
    {
        return false;
    }
}

// 刷新会话表：保留仍在的会话（按对象身份），新会话分配稳定 id，已消失的会话移除。
// 调用方必须已持有 g_lock。
void RefreshSessions()
{
    if (!g_manager)
    {
        return;
    }
    std::vector<Session> current;
    try
    {
        auto view = g_manager.GetSessions();
        uint32_t const size = view.Size();
        current.reserve(size);
        for (uint32_t i = 0; i < size; ++i)
        {
            current.push_back(view.GetAt(i));
        }
    }
    catch (...)
    {
        return;
    }

    std::unordered_map<int64_t, Session> next;
    std::vector<Session> nextSessions;
    nextSessions.reserve(current.size());
    for (auto& s : current)
    {
        auto it = std::find_if(g_byId.begin(), g_byId.end(),
            [&](std::pair<int64_t const, Session> const& kv) { return kv.second == s; });
        int64_t id = (it != g_byId.end()) ? it->first : g_nextId++;
        next.emplace(id, s);
        nextSessions.push_back(s);
    }
    g_sessions = std::move(nextSessions);
    g_byId = std::move(next);
}

void CopyWStringToBuf(std::wstring const& src, wchar_t* out, int cch)
{
    if (!out || cch <= 0)
    {
        return;
    }
    if (cch == 1)
    {
        out[0] = L'\0';
        return;
    }
    size_t n = std::min<size_t>(src.size(), static_cast<size_t>(cch) - 1);
    wcsncpy_s(out, static_cast<size_t>(cch), src.c_str(), n);
    out[n] = L'\0';
}

// WinRT 播放状态枚举 → C# MediaPlaybackState 数值（两者数值不同，必须显式映射）。
int MapPlaybackStatus(winrt::GlobalSystemMediaTransportControlsSessionPlaybackStatus s)
{
    switch (s)
    {
        case winrt::GlobalSystemMediaTransportControlsSessionPlaybackStatus::Closed: return 0;
        case winrt::GlobalSystemMediaTransportControlsSessionPlaybackStatus::Opened: return 1;
        case winrt::GlobalSystemMediaTransportControlsSessionPlaybackStatus::Playing: return 2;
        case winrt::GlobalSystemMediaTransportControlsSessionPlaybackStatus::Paused: return 3;
        case winrt::GlobalSystemMediaTransportControlsSessionPlaybackStatus::Changing: return 4;
        case winrt::GlobalSystemMediaTransportControlsSessionPlaybackStatus::Stopped: return 5;
        default: return 0;
    }
}

int MapRepeatMode(winrt::MediaPlaybackAutoRepeatMode mode)
{
    switch (mode)
    {
        case winrt::MediaPlaybackAutoRepeatMode::Track: return 1;
        case winrt::MediaPlaybackAutoRepeatMode::List: return 2;
        default: return 0;
    }
}

bool IsRepeatActive(winrt::MediaPlaybackAutoRepeatMode mode)
{
    return mode == winrt::MediaPlaybackAutoRepeatMode::Track
        || mode == winrt::MediaPlaybackAutoRepeatMode::List;
}
} // namespace

extern "C" int __stdcall Media_GetSessionCount(int* count)
{
    if (!count)
    {
        return static_cast<int>(E_POINTER);
    }
    *count = 0;
    if (!EnsureManager())
    {
        return 0;
    }
    try
    {
        std::lock_guard<std::mutex> guard(g_lock);
        RefreshSessions();
        *count = static_cast<int>(g_sessions.size());
        return 0;
    }
    catch (...)
    {
        return 0;
    }
}

extern "C" int __stdcall Media_GetSession(
    int index,
    wchar_t* title, int titleCch,
    wchar_t* artist, int artistCch,
    wchar_t* src, int srcCch,
    int* canPlay, int* canPause, int* canNext, int* canPrev, int* canSeek,
    int* isShuffle, int* isRepeat, int* repeatMode, int* hasThumbnail,
    int* playbackState,
    int64_t* positionTicks, int64_t* startTicks, int64_t* endTicks,
    int64_t* sessionId)
{
    if (!title || !artist || !src || !canPlay || !canPause || !canNext || !canPrev || !canSeek
        || !isShuffle || !isRepeat || !repeatMode || !hasThumbnail || !playbackState
        || !positionTicks || !startTicks || !endTicks || !sessionId)
    {
        return static_cast<int>(E_POINTER);
    }
    if (titleCch > 0) title[0] = L'\0';
    if (artistCch > 0) artist[0] = L'\0';
    if (srcCch > 0) src[0] = L'\0';
    *canPlay = 0; *canPause = 0; *canNext = 0; *canPrev = 0; *canSeek = 0;
    *isShuffle = 0; *isRepeat = 0; *repeatMode = 0; *hasThumbnail = 0;
    *playbackState = 0;
    *positionTicks = 0; *startTicks = 0; *endTicks = 0;
    *sessionId = 0;

    Session session{ nullptr };
    {
        std::lock_guard<std::mutex> guard(g_lock);
        if (index < 0 || static_cast<size_t>(index) >= g_sessions.size())
        {
            return static_cast<int>(E_BOUNDS);
        }
        session = g_sessions[static_cast<size_t>(index)];
    }

    try
    {
        // 元数据（标题/艺术家/缩略图引用）：单点失败降级为空，不扩散。
        std::wstring titleStr;
        std::wstring artistStr;
        try
        {
            auto props = session.TryGetMediaPropertiesAsync().get();
            if (props)
            {
                // hstring::c_str() 恒非空（空串返回空串指针），无需判空。
                titleStr = props.Title().c_str();
                artistStr = props.Artist().c_str();
                *hasThumbnail = props.Thumbnail() ? 1 : 0;
            }
        }
        catch (...)
        {
        }

        auto info = session.GetPlaybackInfo();
        if (info)
        {
            *playbackState = MapPlaybackStatus(info.PlaybackStatus());
            auto controls = info.Controls();
            if (controls)
            {
                *canPlay = controls.IsPlayEnabled() ? 1 : 0;
                *canPause = controls.IsPauseEnabled() ? 1 : 0;
                *canNext = controls.IsNextEnabled() ? 1 : 0;
                *canPrev = controls.IsPreviousEnabled() ? 1 : 0;
                *canSeek = controls.IsPlaybackPositionEnabled() ? 1 : 0;
            }
            *isShuffle = info.IsShuffleActive() ? 1 : 0;
            // AutoRepeatMode 投影为 IReference<枚举>：判空后取 Value()。
            auto modeRef = info.AutoRepeatMode();
            auto mode = modeRef ? modeRef.Value() : winrt::MediaPlaybackAutoRepeatMode::None;
            *isRepeat = IsRepeatActive(mode) ? 1 : 0;
            *repeatMode = MapRepeatMode(mode);
        }

        try
        {
            auto tl = session.GetTimelineProperties();
            if (tl)
            {
                *positionTicks = tl.Position().count();
                *startTicks = tl.StartTime().count();
                *endTicks = tl.EndTime().count();
            }
        }
        catch (...)
        {
        }

        std::wstring srcId;
        try
        {
            srcId = session.SourceAppUserModelId().c_str();
        }
        catch (...)
        {
        }

        CopyWStringToBuf(titleStr, title, titleCch);
        CopyWStringToBuf(artistStr, artist, artistCch);
        CopyWStringToBuf(srcId, src, srcCch);

        {
            std::lock_guard<std::mutex> guard(g_lock);
            auto it = std::find_if(g_byId.begin(), g_byId.end(),
                [&](std::pair<int64_t const, Session> const& kv) { return kv.second == session; });
            if (it != g_byId.end())
            {
                *sessionId = it->first;
            }
            else
            {
                *sessionId = g_nextId++;
            }
        }
        return 0;
    }
    catch (...)
    {
        return static_cast<int>(E_FAIL);
    }
}

extern "C" int __stdcall Media_GetSessionThumbnail(
    int64_t sessionId,
    uint8_t** outData,
    uint32_t* outSize)
{
    if (!outData || !outSize)
    {
        return static_cast<int>(E_POINTER);
    }
    *outData = nullptr;
    *outSize = 0;

    Session session{ nullptr };
    {
        std::lock_guard<std::mutex> guard(g_lock);
        auto it = g_byId.find(sessionId);
        if (it == g_byId.end())
        {
            return static_cast<int>(E_NOTIMPL);
        }
        session = it->second;
    }

    try
    {
        auto props = session.TryGetMediaPropertiesAsync().get();
        if (!props)
        {
            return 0;
        }
        auto ref = props.Thumbnail();
        if (!ref)
        {
            return 0;
        }

        auto stream = ref.OpenReadAsync().get();
        if (!stream)
        {
            return 0;
        }
        uint64_t size = stream.Size();
        if (size == 0)
        {
            return 0;
        }
        // 防御：拒绝超大缩略图（SMTC 封面通常 < 1MB）。
        constexpr uint64_t kMaxThumbnailBytes = 16u * 1024u * 1024u;
        if (size > kMaxThumbnailBytes)
        {
            return 0;
        }

        winrt::Buffer buffer(static_cast<uint32_t>(size));
        stream.ReadAsync(buffer, static_cast<uint32_t>(size), winrt::InputStreamOptions::None).get();

        uint8_t* data = static_cast<uint8_t*>(CoTaskMemAlloc(static_cast<size_t>(size)));
        if (!data)
        {
            return static_cast<int>(E_OUTOFMEMORY);
        }
        memcpy(data, buffer.data(), static_cast<size_t>(size));
        *outData = data;
        *outSize = static_cast<uint32_t>(size);
        return 0;
    }
    catch (...)
    {
        // 读取失败视为无缩略图（降级，不扩散）。
        return 0;
    }
}

extern "C" void __stdcall Media_FreeBuffer(uint8_t* data)
{
    if (data)
    {
        CoTaskMemFree(data);
    }
}

extern "C" int __stdcall Media_SendControl(
    int64_t sessionId,
    int cmd,
    int64_t positionTicks,
    int shuffleActive,
    int repeatMode)
{
    Session session{ nullptr };
    {
        std::lock_guard<std::mutex> guard(g_lock);
        auto it = g_byId.find(sessionId);
        if (it == g_byId.end())
        {
            return static_cast<int>(E_NOTIMPL);
        }
        session = it->second;
    }

    try
    {
        switch (cmd)
        {
            case 0:
                return session.TryPlayAsync().get() ? 0 : static_cast<int>(E_FAIL);
            case 1:
                return session.TryPauseAsync().get() ? 0 : static_cast<int>(E_FAIL);
            case 2:
                return session.TrySkipNextAsync().get() ? 0 : static_cast<int>(E_FAIL);
            case 3:
                return session.TrySkipPreviousAsync().get() ? 0 : static_cast<int>(E_FAIL);
            case 4:
                return session.TryChangePlaybackPositionAsync(positionTicks).get() ? 0 : static_cast<int>(E_FAIL);
            case 5:
                {
                    // Toggle：按当前播放状态取反（语义同托管原实现）。
                    bool playing = false;
                    auto info = session.GetPlaybackInfo();
                    if (info)
                    {
                        playing = info.PlaybackStatus()
                            == winrt::GlobalSystemMediaTransportControlsSessionPlaybackStatus::Playing;
                    }
                    return (playing ? session.TryPauseAsync() : session.TryPlayAsync()).get()
                        ? 0 : static_cast<int>(E_FAIL);
                }
            case 6:
                return session.TryChangeShuffleActiveAsync(shuffleActive != 0).get()
                    ? 0 : static_cast<int>(E_FAIL);
            case 7:
                {
                    winrt::MediaPlaybackAutoRepeatMode mode = winrt::MediaPlaybackAutoRepeatMode::None;
                    if (repeatMode == 1)
                    {
                        mode = winrt::MediaPlaybackAutoRepeatMode::Track;
                    }
                    else if (repeatMode == 2)
                    {
                        mode = winrt::MediaPlaybackAutoRepeatMode::List;
                    }
                    return session.TryChangeAutoRepeatModeAsync(mode).get()
                        ? 0 : static_cast<int>(E_FAIL);
                }
            default:
                return static_cast<int>(E_INVALIDARG);
        }
    }
    catch (...)
    {
        return static_cast<int>(E_FAIL);
    }
}
