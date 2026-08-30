// MediaCore.dll — 底层原生 API 封装工（纯 C 导出，零 UI、零业务逻辑）。
// 封装 GlobalSystemMediaTransportControlsSessionManager (WinRT / COM)：
//   枚举当前正在播放的媒体会话（每个会话=某个在播 App），
//   读取会话标题+作者，并提供 Play/Pause/Toggle/Next/Previous 命令。
// 所有字符串由调用方缓冲区接收；命令函数返回 S_OK 表示调用成功但不保证设备已执行
//   （某些 App 会忽略命令）。
#pragma once

#ifdef __cplusplus
extern "C" {
#endif

#define MEDIACORE_MAX_TITLE_CHARS   256
#define MEDIACORE_MAX_ARTIST_CHARS  256
#define MEDIACORE_MAX_SRC_CHARS     128

// 获取当前正在播放的会话总数（可能 0）。
__declspec(dllexport) int __stdcall Media_GetSessionCount(int* count);

// 读取指定索引的会话基本信息。index ∈ [0, count-1]。
//   title/artist/src（sourceAppId） 输出：UTF-16 文本
//   canPlay/canPause/canNext/canPrev  输出：会话的命令能力位
//   playbackState  输出：播放状态，枚举值与 C# 约定一致（0=关闭,1=打开,2=正在播放,3=暂停,4=更改中）
__declspec(dllexport) int __stdcall Media_GetSession(
    int index,
    wchar_t* title, int titleCch,
    wchar_t* artist, int artistCch,
    wchar_t* src, int srcCch,
    int* canPlay, int* canPause, int* canNext, int* canPrev,
    int* playbackState);

// 对当前"最新焦点"会话（通常就是用户想控制的那个）发送控制命令。
//   cmd：0=TogglePlayPause, 1=Play, 2=Pause, 3=Next, 4=Previous
__declspec(dllexport) int __stdcall Media_SendControl(int cmd);

#ifdef __cplusplus
}
#endif
