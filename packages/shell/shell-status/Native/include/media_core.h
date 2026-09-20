// MediaCore.dll — 底层原生 API 封装工（纯 C 导出，零 UI、零业务逻辑）。
// 封装 GlobalSystemMediaTransportControlsSessionManager（SMTC，C++/WinRT）：
//   枚举当前正在播放的媒体会话（每个会话=某个在播 App），
//   读取会话标题/作者/时间线/播放模式，提供播放控制命令与缩略图字节。
// 所有字符串由调用方缓冲区接收；命令函数返回 S_OK 表示调用成功但不保证设备已执行
//   （某些 App 会忽略命令）。
// 注意：调用顺序为 Media_GetSessionCount → Media_GetSession（0..count-1）；
//   会话 id 由本层维护，跨调用稳定（同一会话对象恒同 id），用于命令/缩略图路由。
#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define MEDIACORE_MAX_TITLE_CHARS   256
#define MEDIACORE_MAX_ARTIST_CHARS  256
#define MEDIACORE_MAX_SRC_CHARS     128

// 获取当前正在播放的会话总数（可能 0）。失败返回 HRESULT（0=成功）。
__declspec(dllexport) int __stdcall Media_GetSessionCount(int* count);

// 读取指定索引的会话信息。index ∈ [0, count-1]。
//   title/artist/src（sourceAppId） 输出：UTF-16 文本（截断至缓冲区-1 并补 NUL）
//   canPlay/canPause/canNext/canPrev/canSeek  输出：会话命令能力位（1/0）
//   isShuffle/isRepeat  输出：随机/循环当前状态（1/0）
//   repeatMode  输出：循环模式 0=None, 1=Track, 2=List
//   hasThumbnail 输出：会话是否提供封面缩略图引用（1/0；字节经 Media_GetSessionThumbnail 懒取）
//   playbackState 输出：播放状态，数值 = C# MediaPlaybackState（0=Closed,1=Opened,2=Playing,3=Paused,4=Changing,5=Stopped）
//   positionTicks/startTicks/endTicks 输出：时间线（100ns tick）；未知为 0
//   sessionId 输出：稳定会话标识（供 Media_GetSessionThumbnail / Media_SendControl 路由）
__declspec(dllexport) int __stdcall Media_GetSession(
    int index,
    wchar_t* title, int titleCch,
    wchar_t* artist, int artistCch,
    wchar_t* src, int srcCch,
    int* canPlay, int* canPause, int* canNext, int* canPrev, int* canSeek,
    int* isShuffle, int* isRepeat, int* repeatMode, int* hasThumbnail,
    int* playbackState,
    int64_t* positionTicks, int64_t* startTicks, int64_t* endTicks,
    int64_t* sessionId);

// 读取指定会话的专辑封面缩略图字节（PNG/JPEG 由系统提供）。
// 返回 0 且 *outData 非空：成功，*outData 为 CoTaskMemAlloc 缓冲区，调用方用 Media_FreeBuffer 释放；
// 返回 0 且 *outData 为 null：会话无缩略图或读取失败（降级，不视为错误）；非 0：参数错误/会话已消失。
__declspec(dllexport) int __stdcall Media_GetSessionThumbnail(
    int64_t sessionId,
    uint8_t** outData,
    uint32_t* outSize);

// 释放 Media_GetSessionThumbnail 分配的缓冲区。
__declspec(dllexport) void __stdcall Media_FreeBuffer(uint8_t* data);

// 向指定会话发送控制命令。
//   cmd：0=Play, 1=Pause, 2=Next, 3=Previous, 4=Seek(positionTicks), 5=Toggle,
//        6=Shuffle(shuffleActive), 7=Repeat(repeatMode)
//   repeatMode：0=None, 1=Track, 2=List（对应 Windows.Media.MediaPlaybackAutoRepeatMode）
// 返回 0=命令已下发（不保证 App 执行）；非 0=失败/会话已消失。
__declspec(dllexport) int __stdcall Media_SendControl(
    int64_t sessionId,
    int cmd,
    int64_t positionTicks,
    int shuffleActive,
    int repeatMode);

#ifdef __cplusplus
}
#endif
