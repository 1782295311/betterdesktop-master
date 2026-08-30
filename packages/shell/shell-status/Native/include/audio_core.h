// AudioCore.dll — 底层原生 API 封装工（纯 C 导出，零 UI、零业务逻辑）。
// 本 DLL 只负责把 Windows Core Audio 的 COM 接口封装成干净的 C 函数，
// 供 C# 端通过 Interop 薄封装调用。所有失败统一返回 HRESULT（0=成功），
// 系统版本/驱动异常在 C++ 层兜底，C# 端只拿到标准错误码。
#pragma once

#ifdef __cplusplus
extern "C" {
#endif

// 端点数据流方向，与 C# 侧保持一致。
enum AudioFlow
{
    AudioFlow_Render = 0, // 输出（扬声器/耳机）
    AudioFlow_Capture = 1 // 输入（麦克风）
};

// 读取默认端点的状态。成功返回 0；失败返回负 HRESULT。
//   flow   输入：AudioFlow_Render / AudioFlow_Capture
//   volume 输出：主音量 0.0-1.0 标量（失败时为 0）
//   muted  输出：0=未静音 1=已静音（失败时为 0）
//   ok     输出：1=端点可用且读取成功，0=不可用（无声卡/驱动异常）
__declspec(dllexport) int __stdcall Audio_GetEndpointStatus(int flow, float* volume, int* muted, int* ok);

// 设置默认输出端点主音量（0.0-1.0 标量）。成功返回 0；失败返回负 HRESULT。
__declspec(dllexport) int __stdcall Audio_SetMasterVolume(float volume);

// 设置默认输出端点静音（muted != 0 表示静音）。成功返回 0；失败返回负 HRESULT。
__declspec(dllexport) int __stdcall Audio_SetMute(int muted);

// 设置默认输入端点（Capture）音量（0.0-1.0 标量）和静音。
__declspec(dllexport) int __stdcall Audio_SetCaptureVolume(float volume, int muted);

// 枚举音频端点设备。flow=AudioFlow_Render（输出）或 AudioFlow_Capture（输入）。
//   maxDevices        输入：传入的数组容量（最多返回 32，超出部分丢弃）
//   ids               输出：设备 ID（每行固定 260 字符，零结尾）；每一行 strideChars 宽字符。
//   names             输出：用户显示名（每行 260 字符，零结尾）；每一行 strideChars 宽字符。
//   defaultConsoleId  输出：第几个 (0-based) 设备是默认端点。若不存在则为 -1。
//   filled            输出：实际填充的设备数（0..maxDevices）
__declspec(dllexport) int __stdcall Audio_EnumerateDevices(
    int flow,
    int maxDevices,
    wchar_t* ids, int idStrideChars,
    wchar_t* names, int nameStrideChars,
    int* defaultConsoleIndex,
    int* filled);

// 把指定设备（按设备 ID，长度 < 260 即可）设为默认端点（Console/Multimedia/Communications 三个角色一起设）。
__declspec(dllexport) int __stdcall Audio_SetDefaultDevice(int flow, const wchar_t* deviceId);

#define AUDIOCORE_MAX_SESSION_NAME_CHARS 256

// 枚举默认输出端点上的按应用音频会话（对应系统"音量合成器"，含发声应用及系统进程）。
//   maxSessions     输入：数组容量（最多 32）
//   names           输出：会话显示名（每行 AUDIOCORE_MAX_SESSION_NAME_CHARS 字符，零结尾）
//   volumes         输出：每会话主音量 0.0-1.0
//   muted           输出：每会话是否静音（0=否 1=是）
//   pids            输出：每会话对应进程 ID（未知为 0）
//   filled          输出：实际填充的会话数（0..maxSessions）
// 会话名优先取系统提供的显示名（DisplayName），缺失时回退为进程可执行文件名。
__declspec(dllexport) int __stdcall Audio_EnumerateSessions(
    int maxSessions,
    wchar_t* names, int nameStrideChars,
    float* volumes, int* muted, int* pids,
    int* filled);

// 按进程 ID 设置某个会话的主音量和静音（0.0-1.0 标量）。
//   pid     进程 ID；找不到该会话或未发声则返回负 HRESULT。
__declspec(dllexport) int __stdcall Audio_SetSessionVolume(int pid, int muted, float volume);

// ---------------------------------------------------------------------------
// G2 / P1-A：COM 生命周期与事件通知
// ---------------------------------------------------------------------------

// 初始化音频模块：启动专属 STA 线程，在其上创建并持有 IMMDeviceEnumerator、
// 默认端点 IAudioEndpointVolume / IAudioSessionManager2，并注册
// IMMNotificationClient / IAudioEndpointVolumeCallback / IAudioSessionNotification。
// 初始化后既有导出函数复用此共享 Enumerator（不重复 CoCreateInstance）。
// 幂等：可重复调用，重复调用直接返回成功。成功返回 0；失败返回负 HRESULT。
__declspec(dllexport) int __stdcall Audio_Initialize(void);

// 关闭音频模块：注销全部回调、释放模块级 COM 对象并退出 STA 线程。幂等。
// 关闭后既有导出函数降级为逐次自建 Enumerator，功能不受影响。
__declspec(dllexport) void __stdcall Audio_Shutdown(void);

// 变更回调：设备插拔 / 默认设备切换 / 音量或静音变化 / 音频会话新增时，
// 由专属 STA 通知线程触发一次。传 null 注销回调。
// 该回调仅供 C# 层触发 PollNow()，实现须短小、非阻塞。
typedef void (__stdcall* AudioChangeCallback)(void);
__declspec(dllexport) void __stdcall Audio_SetChangeCallback(AudioChangeCallback cb);

#ifdef __cplusplus
}
#endif
