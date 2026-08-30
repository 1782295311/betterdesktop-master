// AudioCore.dll 实现 — 纯 Core Audio 封装（零外部依赖）。
// 每次调用在当前线程做 COM 初始化（COINIT_APARTMENTTHREADED，WPF UI 线程即 STA，
// 兼容）。COM 调用失败统一返回 HRESULT，不抛异常、不碰 WinRT/UI。
#include <windows.h>
#include <mmdeviceapi.h>
#include <audiopolicy.h>
#include <endpointvolume.h>
#include <objbase.h>
#include <cstdint>
#include <cwchar>
#include <propkey.h>
#include <propvarutil.h>
#include <functiondiscoverykeys_devpkey.h>

#include "audio_core.h"

namespace
{
HRESULT OpenVolume(IMMDeviceEnumerator* enumerator, EDataFlow flow,
                   IAudioEndpointVolume** outVol)
{
    IMMDevice* device = nullptr;
    HRESULT hr = enumerator->GetDefaultAudioEndpoint(flow, eConsole, &device);
    if (FAILED(hr)) return hr;
    if (outVol)
    {
        hr = device->Activate(__uuidof(IAudioEndpointVolume), CLSCTX_INPROC_SERVER,
                              nullptr, reinterpret_cast<void**>(outVol));
        device->Release();
        return hr;
    }
    device->Release();
    return S_OK;
}

HRESULT CheckCaptureAvailable(IMMDeviceEnumerator* enumerator)
{
    IMMDevice* device = nullptr;
    HRESULT hr = enumerator->GetDefaultAudioEndpoint(eCapture, eConsole, &device);
    if (FAILED(hr)) return hr;
    DWORD state = 0;
    hr = device->GetState(&state);
    device->Release();
    if (FAILED(hr) || state != DEVICE_STATE_ACTIVE)
    {
        return AUDCLNT_E_DEVICE_INVALIDATED;
    }
    return S_OK;
}

// 策略引擎：设置默认端点需要通过 IPolicyConfigXXX（未文档化接口）。
// 这是 Windows 长期稳定的做法，尽管未文档化，但被主流开源播放器广泛使用。
struct IPolicyConfigVista : IUnknown
{
    virtual HRESULT STDMETHODCALLTYPE GetMixFormat(const WCHAR*, WAVEFORMATEX**) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetDeviceFormat(const WCHAR*, BOOL, WAVEFORMATEX**) = 0;
    virtual HRESULT STDMETHODCALLTYPE ResetDeviceFormat(const WCHAR*) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetDeviceFormat(const WCHAR*, WAVEFORMATEX*, WAVEFORMATEX*) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetProcessingPeriod(const WCHAR*, BOOL, INT64*, INT64*) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetProcessingPeriod(const WCHAR*, INT64) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetShareMode(const WCHAR*, struct DeviceShareMode*) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetShareMode(const WCHAR*, struct DeviceShareMode) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetPropertyValue(const WCHAR*, const PROPERTYKEY&, PROPVARIANT*) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetPropertyValue(const WCHAR*, const PROPERTYKEY&, PROPVARIANT&) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetDefaultEndpoint(const WCHAR*, ERole) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetEndpointVisibility(const WCHAR*, BOOL) = 0;
};
static const IID CLSID_CPolicyConfigClient =
    {0x870AF99C, 0x7D1D, 0x4F6D, {0xBB, 0x40, 0xC9, 0x7E, 0xC0, 0x1C, 0xD5, 0x20}};
static const IID IID_IPolicyConfigVista =
    {0xF8679F50, 0x850A, 0x41D9, {0xA0, 0x51, 0x3A, 0x84, 0x84, 0x87, 0x3B, 0xC2}};

HRESULT SetDefaultViaPolicy(const wchar_t* id)
{
    if (!id) return E_POINTER;
    IPolicyConfigVista* cfg = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_CPolicyConfigClient, nullptr,
                                  CLSCTX_INPROC_SERVER, IID_IPolicyConfigVista,
                                  reinterpret_cast<void**>(&cfg));
    if (FAILED(hr)) return hr;
    // 同步设置三个角色。
    ERole roles[] = { eConsole, eMultimedia, eCommunications };
    for (auto r : roles)
    {
        hr = cfg->SetDefaultEndpoint(id, r);
        if (FAILED(hr)) { cfg->Release(); return hr; }
    }
    cfg->Release();
    return S_OK;
}

void CopyWStride(const WCHAR* s, wchar_t* d, int stride)
{
    if (!d || stride <= 0) return;
    for (int i = 0; i < stride; ++i) d[i] = L'\0';
    if (!s) return;
    wcsncpy_s(d, (size_t)stride, s, _TRUNCATE);
}

// 从设备属性存储读取指定键的宽字符串属性值（与 Cairo 的 CoreAudioDeviceManager 对齐）。
// 返回 true 表示成功读到非空字符串；v 指向的 PROPVARIANT 需由调用方 PropVariantClear。
bool GetDeviceStrProp(IMMDevice* dev, const PROPERTYKEY& key, PROPVARIANT& v, const WCHAR*& outVal)
{
    outVal = nullptr;
    IPropertyStore* prop = nullptr;
    if (!dev || FAILED(dev->OpenPropertyStore(STGM_READ, &prop)) || !prop)
    {
        return false;
    }
    PropVariantInit(&v);
    HRESULT hr = prop->GetValue(key, &v);
    bool ok = SUCCEEDED(hr) && v.vt == VT_LPWSTR && v.pwszVal && v.pwszVal[0] != L'\0';
    if (ok)
    {
        outVal = v.pwszVal;
    }
    prop->Release();
    return ok;
}

// 取设备显示名：FriendlyName 优先（与系统音量面板一致），缺失依次回退
// DeviceInterface_FriendlyName -> DeviceDesc。
void GetBestDeviceName(IMMDevice* dev, const WCHAR*& outName)
{
    outName = nullptr;
    PROPVARIANT v{};
    const WCHAR* val = nullptr;
    // 1) FriendlyName（最完整、可读的中文名，如"麦克风阵列 (Realtek(R) Audio)"）
    if (GetDeviceStrProp(dev, PKEY_Device_FriendlyName, v, val)) { outName = val; return; }
    // 2) DeviceInterface_FriendlyName
    if (v.vt == VT_LPWSTR && v.pwszVal) { PropVariantClear(&v); }
    if (GetDeviceStrProp(dev, PKEY_DeviceInterface_FriendlyName, v, val)) { outName = val; return; }
    // 3) DeviceDesc（最粗的设备描述）
    if (v.vt == VT_LPWSTR && v.pwszVal) { PropVariantClear(&v); }
    if (GetDeviceStrProp(dev, PKEY_Device_DeviceDesc, v, val)) { outName = val; return; }
}

// ============================================================================
// G2 / P1-A：模块级共享 Enumerator + 专属 STA 通知线程
// ============================================================================
// 说明：
//   * G2 —— IMMDeviceEnumerator 在 STA 通知线程上创建一次并持有，生命周期=进程；
//           其方法均为线程安全，可从任意已 COM 初始化的线程调用。既有导出函数
//           优先复用本实例，避免每次调用重复 CoCreateInstance（拖拽滑块跟手性）。
//   * P1-A —— 通知线程注册 IMMNotificationClient / IAudioEndpointVolumeCallback /
//           IAudioSessionNotification，设备插拔、默认设备切换、音量/静音变化、
//           会话新增时由 STA 线程触发一次 AudioChangeCallback（C# 层用其调 PollNow）。

// —— 模块级共享锁与回调槽（跨线程读写） ——
static CRITICAL_SECTION g_stateLock;
static bool g_stateLockInit = false;

static void EnsureGlobalLocks()
{
    if (!g_stateLockInit)
    {
        InitializeCriticalSection(&g_stateLock);
        g_stateLockInit = true;
    }
}

// 变更回调（C# 层注入）。由 STA 通知线程调用。
static volatile AudioChangeCallback g_changeCb = nullptr;

static void FireChange()
{
    EnsureGlobalLocks();
    AudioChangeCallback cb = nullptr;
    EnterCriticalSection(&g_stateLock);
    cb = g_changeCb;
    LeaveCriticalSection(&g_stateLock);
    if (cb) cb();
}

// 模块级共享 Enumerator。初始化后由 STA 线程持有；安全读返回当前值（可能为 null）。
static IMMDeviceEnumerator* g_sharedEnum = nullptr;   // 原子读/写统一走 Interlocked*
static volatile LONG g_sharedReady = 0;               // 1=已就绪（可安全复用）

static IMMDeviceEnumerator* AcquireSharedEnumerator()
{
    // 只读原子取当前共享 Enumerator 指针（未修改）。
    return static_cast<IMMDeviceEnumerator*>(
        InterlockedCompareExchangePointer(
            reinterpret_cast<void* volatile*>(&g_sharedEnum), nullptr, nullptr));
}

// 通知线程持有、仅供其访问的实例（规避跨公寓误用）。
static IMMDeviceEnumerator* g_notifyEnum = nullptr;
static IAudioEndpointVolume* g_renderVol = nullptr;   // 默认输出端点（已挂音量回调）
static IAudioEndpointVolume* g_captureVol = nullptr;  // 默认输入端点（已挂音量回调）
static IAudioSessionManager2* g_sessMgr = nullptr;    // 默认输出端点的会话管理器
static HANDLE g_stopEvent = nullptr;                  // 通知线程停止信号（兼作"已初始化"标记）

// 回调实现类（优先于注册函数定义，使 g_volumeCb 等可完成到接口的隐式转换）。
struct AudioNotifyClient;
struct AudioVolumeCallback;
struct AudioSessionNotify;
static AudioNotifyClient*  g_notifyCb = nullptr;      // 设备变化通知
static AudioVolumeCallback* g_volumeCb = nullptr;     // 音量/静音变化通知
static AudioSessionNotify*  g_sessionCb = nullptr;    // 会话新增通知

// 供通知回调在默认设备切换时重挂注册（定义见后）。
static void RegisterVolumeCallbacks(EDataFlow flow);
static void RegisterSessionNotify();

// —— 设备变化通知客户端 ——
struct AudioNotifyClient final : IMMNotificationClient
{
    ULONG ref = 1;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == __uuidof(IMMNotificationClient))
        {
            *ppv = static_cast<IMMNotificationClient*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement((LONG*)&ref); }
    ULONG STDMETHODCALLTYPE Release() override
    {
        ULONG r = InterlockedDecrement((LONG*)&ref);
        if (r == 0) delete this;
        return r;
    }
    HRESULT STDMETHODCALLTYPE OnDeviceStateChanged(LPCWSTR, DWORD) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE OnDeviceAdded(LPCWSTR) override { FireChange(); return S_OK; }
    HRESULT STDMETHODCALLTYPE OnDeviceRemoved(LPCWSTR) override { FireChange(); return S_OK; }
    HRESULT STDMETHODCALLTYPE OnDefaultDeviceChanged(EDataFlow flow, ERole role, LPCWSTR) override
    {
        if (role == eConsole)
        {
            FireChange();
            RegisterVolumeCallbacks(flow);   // 默认设备切换：在新默认端点上重挂音量回调
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnPropertyValueChanged(LPCWSTR, const PROPERTYKEY) override { return S_OK; }
};

// —— 音量/静音变化回调 ——
struct AudioVolumeCallback final : IAudioEndpointVolumeCallback
{
    ULONG ref = 1;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == __uuidof(IAudioEndpointVolumeCallback))
        {
            *ppv = static_cast<IAudioEndpointVolumeCallback*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement((LONG*)&ref); }
    ULONG STDMETHODCALLTYPE Release() override
    {
        ULONG r = InterlockedDecrement((LONG*)&ref);
        if (r == 0) delete this;
        return r;
    }
    HRESULT STDMETHODCALLTYPE OnNotify(PAUDIO_VOLUME_NOTIFICATION_DATA) override { FireChange(); return S_OK; }
};

// —— 会话新增/移除回调（对应系统"音量合成器"会话变化） ——
struct AudioSessionNotify final : IAudioSessionNotification
{
    ULONG ref = 1;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == __uuidof(IAudioSessionNotification))
        {
            *ppv = static_cast<IAudioSessionNotification*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement((LONG*)&ref); }
    ULONG STDMETHODCALLTYPE Release() override
    {
        ULONG r = InterlockedDecrement((LONG*)&ref);
        if (r == 0) delete this;
        return r;
    }
    HRESULT STDMETHODCALLTYPE OnSessionCreated(IAudioSessionControl*) override { FireChange(); return S_OK; }
};

// 在默认端点上创建 IAudioEndpointVolume 并挂回调；返回持有对象（重挂前须注销并释放），失败返回 null。
static IAudioEndpointVolume* RegisterVolumeEp(EDataFlow flow)
{
    if (!g_notifyEnum) return nullptr;
    IMMDevice* device = nullptr;
    if (FAILED(g_notifyEnum->GetDefaultAudioEndpoint(flow, eConsole, &device)) || !device)
        return nullptr;
    IAudioEndpointVolume* vol = nullptr;
    if (FAILED(device->Activate(__uuidof(IAudioEndpointVolume), CLSCTX_INPROC_SERVER,
                                nullptr, reinterpret_cast<void**>(&vol))))
    {
        device->Release();
        return nullptr;
    }
    device->Release();
    if (FAILED(vol->RegisterControlChangeNotify(g_volumeCb)))
    {
        vol->Release();
        return nullptr;
    }
    return vol;
}

// 重挂默认输出/输入端点的音量回调（默认设备切换时调用）。
static void RegisterVolumeCallbacks(EDataFlow flow)
{
    EnsureGlobalLocks();
    EnterCriticalSection(&g_stateLock);
    if (flow == eRender)
    {
        IAudioEndpointVolume* old = g_renderVol;
        g_renderVol = RegisterVolumeEp(eRender);
        if (old) { old->UnregisterControlChangeNotify(g_volumeCb); old->Release(); }
    }
    else
    {
        IAudioEndpointVolume* old = g_captureVol;
        g_captureVol = RegisterVolumeEp(eCapture);
        if (old) { old->UnregisterControlChangeNotify(g_volumeCb); old->Release(); }
    }
    LeaveCriticalSection(&g_stateLock);
}

// 重挂默认输出端点的会话通知（默认设备切换时调用，配合 g_stateLock 使用）。
static void RegisterSessionNotify()
{
    if (!g_notifyEnum) return;
    EnsureGlobalLocks();
    EnterCriticalSection(&g_stateLock);
    if (g_sessMgr)
    {
        g_sessMgr->UnregisterSessionNotification(g_sessionCb);
        g_sessMgr->Release();
        g_sessMgr = nullptr;
    }
    IMMDevice* device = nullptr;
    if (FAILED(g_notifyEnum->GetDefaultAudioEndpoint(eRender, eConsole, &device)) || !device)
    {
        LeaveCriticalSection(&g_stateLock);
        return;
    }
    HRESULT hr = device->Activate(__uuidof(IAudioSessionManager2), CLSCTX_INPROC_SERVER,
                                  nullptr, reinterpret_cast<void**>(&g_sessMgr));
    device->Release();
    if (SUCCEEDED(hr) && g_sessMgr)
        g_sessMgr->RegisterSessionNotification(g_sessionCb);
    LeaveCriticalSection(&g_stateLock);
}

// —— 归属 notify 线程：创建 Enumerator + 注册三类回调 + 泵 COM 通知 ——
static DWORD WINAPI AudioNotifyThreadProc(LPVOID)
{
    HRESULT init = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(init) && init != RPC_E_CHANGED_MODE)
    {
        InterlockedExchange((LONG*)&g_sharedReady, 0);
        SetEvent(g_stopEvent);
        return 1;
    }

    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr,
                                  CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&g_notifyEnum));
    if (SUCCEEDED(hr) && g_notifyEnum)
    {
        hr = g_notifyEnum->RegisterEndpointNotificationCallback(g_notifyCb);
        if (SUCCEEDED(hr))
        {
            RegisterVolumeCallbacks(eRender);
            RegisterVolumeCallbacks(eCapture);
            RegisterSessionNotify();
            // 就绪后共享给操作函数复用（线程安全方法，可跨线程）。
            InterlockedExchangePointer(reinterpret_cast<void* volatile*>(&g_sharedEnum), g_notifyEnum);
            InterlockedExchange((LONG*)&g_sharedReady, 1);
        }
        else
        {
            InterlockedExchange((LONG*)&g_sharedReady, 0);
        }
    }
    else
    {
        InterlockedExchange((LONG*)&g_sharedReady, 0);
        if (g_notifyEnum) { g_notifyEnum->Release(); g_notifyEnum = nullptr; }
    }

    if (!g_stopEvent) g_stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE ev[] = { g_stopEvent };
    for (;;)
    {
        // MsgWaitForMultipleObjectsEx：既等待停止信号，又泵 COM 通知消息。
        DWORD r = MsgWaitForMultipleObjectsEx(1, ev, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
        if (r == WAIT_OBJECT_0) break;
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    // 清理（本线程 STA 上注销/释放）。
    InterlockedExchange((LONG*)&g_sharedReady, 0);
    InterlockedExchangePointer(reinterpret_cast<void* volatile*>(&g_sharedEnum), nullptr);
    if (g_renderVol) { g_renderVol->UnregisterControlChangeNotify(g_volumeCb); g_renderVol->Release(); g_renderVol = nullptr; }
    if (g_captureVol) { g_captureVol->UnregisterControlChangeNotify(g_volumeCb); g_captureVol->Release(); g_captureVol = nullptr; }
    if (g_sessMgr) { g_sessMgr->UnregisterSessionNotification(g_sessionCb); g_sessMgr->Release(); g_sessMgr = nullptr; }
    if (g_notifyEnum)
    {
        if (g_notifyCb) g_notifyEnum->UnregisterEndpointNotificationCallback(g_notifyCb);
        g_notifyEnum->Release();
        g_notifyEnum = nullptr;
    }
    if (g_notifyCb) { g_notifyCb->Release(); g_notifyCb = nullptr; }
    if (g_volumeCb) { g_volumeCb->Release(); g_volumeCb = nullptr; }
    if (g_sessionCb) { g_sessionCb->Release(); g_sessionCb = nullptr; }
    CoUninitialize();
    return 0;
}
} // namespace

// —— G2 / P1-A 导出函数 ——
extern "C" int __stdcall Audio_Initialize(void)
{
    EnsureGlobalLocks();
    EnterCriticalSection(&g_stateLock);
    if (g_stopEvent) // 已初始化（幂等）
    {
        LeaveCriticalSection(&g_stateLock);
        return 0;
    }
    HANDLE stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!stop)
    {
        LeaveCriticalSection(&g_stateLock);
        return static_cast<int>(HRESULT_FROM_WIN32(GetLastError()));
    }
    g_stopEvent = stop;
    g_notifyCb = new AudioNotifyClient();
    g_volumeCb = new AudioVolumeCallback();
    g_sessionCb = new AudioSessionNotify();
    HANDLE t = CreateThread(nullptr, 0, AudioNotifyThreadProc, nullptr, 0, nullptr);
    if (!t)
    {
        delete g_notifyCb; delete g_volumeCb; delete g_sessionCb;
        g_notifyCb = nullptr; g_volumeCb = nullptr; g_sessionCb = nullptr;
        CloseHandle(stop); g_stopEvent = nullptr;
        LeaveCriticalSection(&g_stateLock);
        return static_cast<int>(HRESULT_FROM_WIN32(GetLastError()));
    }
    CloseHandle(t);
    LeaveCriticalSection(&g_stateLock);
    return 0; // 初始化异步进行，操作函数在 g_sharedReady=1 前自动降级为逐次自建
}

extern "C" void __stdcall Audio_Shutdown(void)
{
    EnsureGlobalLocks();
    HANDLE stop = nullptr;
    EnterCriticalSection(&g_stateLock);
    stop = g_stopEvent;
    if (stop)
    {
        g_changeCb = nullptr;   // 先断开回调，避免通知线程触发 C# 回调
        SetEvent(stop);
    }
    LeaveCriticalSection(&g_stateLock);
    if (stop)
    {
        WaitForSingleObject(stop, 3000);
        CloseHandle(stop);
        EnterCriticalSection(&g_stateLock);
        g_stopEvent = nullptr;
        LeaveCriticalSection(&g_stateLock);
    }
}

extern "C" void __stdcall Audio_SetChangeCallback(AudioChangeCallback cb)
{
    EnsureGlobalLocks();
    EnterCriticalSection(&g_stateLock);
    g_changeCb = cb;
    LeaveCriticalSection(&g_stateLock);
}

extern "C" int __stdcall Audio_GetEndpointStatus(int flow, float* volume, int* muted, int* ok)
{
    if (!volume || !muted || !ok) return static_cast<int>(E_POINTER);
    *volume = 0.0f; *muted = 0; *ok = 0;

    HRESULT init = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(init) && init != RPC_E_CHANGED_MODE)
        return static_cast<int>(init);
    bool ownCoinit = SUCCEEDED(init);

    // G2：优先复用模块级共享 Enumerator；未就绪时降级为逐次自建（shared=false）。
    IMMDeviceEnumerator* enumerator = AcquireSharedEnumerator();
    bool shared = (enumerator != nullptr);
    if (!shared)
        CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&enumerator));
    HRESULT hr = enumerator ? S_OK : E_FAIL;
    if (SUCCEEDED(hr))
    {
        if (flow == AudioFlow_Render || flow == AudioFlow_Capture)
        {
            const EDataFlow df = (flow == AudioFlow_Render) ? eRender : eCapture;
            IAudioEndpointVolume* vol = nullptr;
            hr = OpenVolume(enumerator, df, &vol);
            if (SUCCEEDED(hr) && vol)
            {
                float v = 0.0f; int m = 0;
                HRESULT vhr = vol->GetMasterVolumeLevelScalar(&v);
                HRESULT mhr = vol->GetMute(reinterpret_cast<INT*>(&m));
                if (SUCCEEDED(vhr) && SUCCEEDED(mhr))
                {
                    *volume = v; *muted = m; *ok = 1;
                }
                else
                {
                    hr = FAILED(vhr) ? vhr : mhr;
                }
                vol->Release();
            }
            else if (flow == AudioFlow_Capture && FAILED(hr))
            {
                // 捕获端点打开失败但状态仍"存在"时，仅标可用位（用于展示设备列表）
                hr = CheckCaptureAvailable(enumerator);
                if (SUCCEEDED(hr)) *ok = 1;
            }
        }
        else
        {
            hr = E_INVALIDARG;
        }
        if (!shared) enumerator->Release();
    }
    if (ownCoinit) CoUninitialize();
    return static_cast<int>(FAILED(hr) ? hr : S_OK);
}

extern "C" int __stdcall Audio_SetMasterVolume(float volume)
{
    if (volume < 0.0f || volume > 1.0f) return static_cast<int>(E_INVALIDARG);
    HRESULT init = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(init) && init != RPC_E_CHANGED_MODE)
        return static_cast<int>(init);
    bool ownCoinit = SUCCEEDED(init);

    // G2：优先复用共享 Enumerator。
    IMMDeviceEnumerator* enumerator = AcquireSharedEnumerator();
    bool shared = (enumerator != nullptr);
    if (!shared)
        CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&enumerator));
    HRESULT hr = enumerator ? S_OK : E_FAIL;
    if (SUCCEEDED(hr))
    {
        IAudioEndpointVolume* vol = nullptr;
        hr = OpenVolume(enumerator, eRender, &vol);
        if (SUCCEEDED(hr) && vol)
        {
            hr = vol->SetMasterVolumeLevelScalar(volume, nullptr);
            vol->Release();
        }
        if (!shared) enumerator->Release();
    }
    if (ownCoinit) CoUninitialize();
    return static_cast<int>(FAILED(hr) ? hr : S_OK);
}

extern "C" int __stdcall Audio_SetMute(int muted)
{
    HRESULT init = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(init) && init != RPC_E_CHANGED_MODE)
        return static_cast<int>(init);
    bool ownCoinit = SUCCEEDED(init);

    // G2：优先复用共享 Enumerator。
    IMMDeviceEnumerator* enumerator = AcquireSharedEnumerator();
    bool shared = (enumerator != nullptr);
    if (!shared)
        CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&enumerator));
    HRESULT hr = enumerator ? S_OK : E_FAIL;
    if (SUCCEEDED(hr))
    {
        IAudioEndpointVolume* vol = nullptr;
        hr = OpenVolume(enumerator, eRender, &vol);
        if (SUCCEEDED(hr) && vol)
        {
            hr = vol->SetMute(muted != 0, nullptr);
            vol->Release();
        }
        if (!shared) enumerator->Release();
    }
    if (ownCoinit) CoUninitialize();
    return static_cast<int>(FAILED(hr) ? hr : S_OK);
}

extern "C" int __stdcall Audio_SetCaptureVolume(float volume, int muted)
{
    if (volume < 0.0f || volume > 1.0f) return static_cast<int>(E_INVALIDARG);
    HRESULT init = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(init) && init != RPC_E_CHANGED_MODE)
        return static_cast<int>(init);
    bool ownCoinit = SUCCEEDED(init);

    // G2：优先复用共享 Enumerator。
    IMMDeviceEnumerator* enumerator = AcquireSharedEnumerator();
    bool shared = (enumerator != nullptr);
    if (!shared)
        CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&enumerator));
    HRESULT hr = enumerator ? S_OK : E_FAIL;
    if (SUCCEEDED(hr))
    {
        IAudioEndpointVolume* vol = nullptr;
        hr = OpenVolume(enumerator, eCapture, &vol);
        if (SUCCEEDED(hr) && vol)
        {
            HRESULT h1 = vol->SetMasterVolumeLevelScalar(volume, nullptr);
            HRESULT h2 = vol->SetMute(muted != 0, nullptr);
            if (FAILED(h1)) hr = h1; else if (FAILED(h2)) hr = h2; else hr = S_OK;
            vol->Release();
        }
        if (!shared) enumerator->Release();
    }
    if (ownCoinit) CoUninitialize();
    return static_cast<int>(FAILED(hr) ? hr : S_OK);
}

extern "C" int __stdcall Audio_EnumerateDevices(
    int flow,
    int maxDevices,
    wchar_t* ids, int idStrideChars,
    wchar_t* names, int nameStrideChars,
    int* defaultConsoleIndex,
    int* filled)
{
    if (!ids || !names || !defaultConsoleIndex || !filled)
        return static_cast<int>(E_POINTER);
    if (maxDevices <= 0 || idStrideChars <= 0 || nameStrideChars <= 0)
        return static_cast<int>(E_INVALIDARG);
    *filled = 0;
    *defaultConsoleIndex = -1;
    // 清零输出
    for (int i = 0; i < maxDevices; ++i)
    {
        wchar_t* r1 = ids + (ptrdiff_t)i * idStrideChars;
        for (int j = 0; j < idStrideChars; ++j) r1[j] = L'\0';
        wchar_t* r2 = names + (ptrdiff_t)i * nameStrideChars;
        for (int j = 0; j < nameStrideChars; ++j) r2[j] = L'\0';
    }

    HRESULT init = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(init) && init != RPC_E_CHANGED_MODE)
        return static_cast<int>(init);
    bool ownCoinit = SUCCEEDED(init);

    const EDataFlow df = (flow == AudioFlow_Capture) ? eCapture : eRender;
    IMMDeviceEnumerator* enumerator = nullptr;
    IMMDeviceCollection* devices = nullptr;
    IMMDevice* defDev = nullptr;
    LPWSTR defId = nullptr;
    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr,
                                  CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&enumerator));
    if (FAILED(hr)) { if (ownCoinit) CoUninitialize(); return (int)hr; }

    hr = enumerator->EnumAudioEndpoints(df, DEVICE_STATE_ACTIVE, &devices);
    if (FAILED(hr) || !devices)
    {
        enumerator->Release();
        if (ownCoinit) CoUninitialize();
        return 0;
    }

    if (SUCCEEDED(enumerator->GetDefaultAudioEndpoint(df, eConsole, &defDev)) && defDev)
    {
        defDev->GetId(&defId);
    }

    UINT count = 0;
    devices->GetCount(&count);
    int n = 0;
    for (UINT i = 0; i < count && n < maxDevices; ++i)
    {
        IMMDevice* dev = nullptr;
        LPWSTR id = nullptr;
        if (FAILED(devices->Item(i, &dev)) || !dev) continue;
        if (FAILED(dev->GetId(&id)) || !id) { dev->Release(); continue; }
        const WCHAR* bestName = nullptr;
        GetBestDeviceName(dev, bestName);
        const WCHAR* name = bestName ? bestName : L"(未知设备)";
        wchar_t* rId = ids + (ptrdiff_t)n * idStrideChars;
        wchar_t* rName = names + (ptrdiff_t)n * nameStrideChars;
        CopyWStride(id, rId, idStrideChars);
        CopyWStride(name, rName, nameStrideChars);
        if (defId && id && wcscmp(defId, id) == 0)
        {
            *defaultConsoleIndex = n;
        }
        ++n;
        if (id) CoTaskMemFree(id);
        dev->Release();
    }
    *filled = n;
    if (defId) CoTaskMemFree(defId);
    if (defDev) defDev->Release();
    if (devices) devices->Release();
    enumerator->Release();
    if (ownCoinit) CoUninitialize();
    return 0;
}

extern "C" int __stdcall Audio_SetDefaultDevice(int flow, const wchar_t* deviceId)
{
    if (!deviceId) return static_cast<int>(E_POINTER);
    (void)flow; // IPolicyConfigVista.SetDefaultEndpoint 方向由 Endpoint ID 内在决定。
    HRESULT init = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(init) && init != RPC_E_CHANGED_MODE)
        return static_cast<int>(init);
    bool ownCoinit = SUCCEEDED(init);
    HRESULT hr = SetDefaultViaPolicy(deviceId);
    if (ownCoinit) CoUninitialize();
    return static_cast<int>(FAILED(hr) ? hr : S_OK);
}

namespace
{
// 取某进程的可执行文件名（仅文件名部分），失败返回 L""。
void GetProcessBaseName(DWORD pid, wchar_t* name, size_t cch)
{
    if (!name || cch == 0) return;
    name[0] = L'\0';
    if (pid == 0) return;
    HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!h) return;
    wchar_t path[MAX_PATH * 2] = {};
    DWORD n = MAX_PATH * 2;
    if (QueryFullProcessImageNameW(h, 0, path, &n) && n > 0)
    {
        const wchar_t* b = wcsrchr(path, L'\\');
        const wchar_t* base = b ? (b + 1) : path;
        wcsncpy_s(name, cch, base, _TRUNCATE);
    }
    CloseHandle(h);
}

// 动态解析本地化资源引用（如 @%SystemRoot%\\System32\\AudioSrv.Dll,-202，系统提示音）。
// SHLoadIndirectStringW 从 shlwapi.dll 运行时装载，避免头文件/链接依赖。
bool ResolveIndirectString(const wchar_t* src, wchar_t* out, size_t outChars)
{
    if (!src || !out || outChars == 0) return false;
    out[0] = L'\0';
    typedef HRESULT(__stdcall* FnLoadIndirectString)(PCWSTR, PWSTR, UINT, PVOID);
    HMODULE h = LoadLibraryW(L"shlwapi.dll");
    if (!h) return false;
    auto fn = reinterpret_cast<FnLoadIndirectString>(GetProcAddress(h, "SHLoadIndirectStringW"));
    if (!fn) { FreeLibrary(h); return false; }
    HRESULT hr = fn(src, out, (UINT)outChars, nullptr);
    FreeLibrary(h);
    return SUCCEEDED(hr) && out[0] && out[0] != L'@';
}
} // namespace

extern "C" int __stdcall Audio_EnumerateSessions(
    int maxSessions,
    wchar_t* names, int nameStrideChars,
    float* volumes, int* muted, int* pids,
    int* filled)
{
    if (!names || !volumes || !muted || !pids || !filled || maxSessions <= 0 || nameStrideChars <= 0)
        return static_cast<int>(E_POINTER);
    if (maxSessions > 32) maxSessions = 32;
    *filled = 0;
    for (int i = 0; i < maxSessions; ++i)
    {
        wchar_t* row = names + (ptrdiff_t)i * nameStrideChars;
        for (int j = 0; j < nameStrideChars; ++j) row[j] = L'\0';
        volumes[i] = 1.0f;
        muted[i] = 0;
        pids[i] = 0;
    }

    HRESULT init = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(init) && init != RPC_E_CHANGED_MODE)
        return static_cast<int>(init);
    bool ownCoinit = SUCCEEDED(init);

    IMMDeviceEnumerator* enumerator = nullptr;
    IMMDevice* device = nullptr;
    IAudioSessionManager2* mgr = nullptr;
    IAudioSessionEnumerator* iter = nullptr;
    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr,
                                  CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&enumerator));
    if (SUCCEEDED(hr))
        hr = enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device);
    if (SUCCEEDED(hr))
        hr = device->Activate(__uuidof(IAudioSessionManager2), CLSCTX_INPROC_SERVER,
                              nullptr, reinterpret_cast<void**>(&mgr));
    if (SUCCEEDED(hr))
        hr = mgr->GetSessionEnumerator(&iter);

    if (SUCCEEDED(hr) && iter)
    {
        int count = 0;
        iter->GetCount(&count);
        int n = 0;
        for (int i = 0; i < count && n < maxSessions; ++i)
        {
            IAudioSessionControl* ctl = nullptr;
            if (FAILED(iter->GetSession(i, &ctl)) || !ctl) continue;

            // 显示名（系统音量合成器的可读名称）
            LPWSTR disp = nullptr;
            ctl->GetDisplayName(&disp);

            DWORD pid = 0;
            IAudioSessionControl2* c2 = nullptr;
            if (SUCCEEDED(ctl->QueryInterface(__uuidof(IAudioSessionControl2),
                                              reinterpret_cast<void**>(&c2))))
            {
                c2->GetProcessId(&pid);
            }

            float vol = 1.0f; int m = 0;
            ISimpleAudioVolume* sv = nullptr;
            if (SUCCEEDED(ctl->QueryInterface(__uuidof(ISimpleAudioVolume),
                                              reinterpret_cast<void**>(&sv))))
            {
                float v = 1.0f; BOOL mm = FALSE;
                if (SUCCEEDED(sv->GetMasterVolume(&v))) vol = v;
                if (SUCCEEDED(sv->GetMute(&mm))) m = mm ? 1 : 0;
                sv->Release();
            }

            // 名称优先用系统显示名，缺失回退到进程可执行文件名；
            // 本地化资源引用（如 @%SystemRoot%\System32\AudioSrv.Dll,-202，系统提示音）先解成可读名。
            wchar_t nameBuf[AUDIOCORE_MAX_SESSION_NAME_CHARS] = {};
            wchar_t proc[MAX_PATH * 2] = {};
            GetProcessBaseName(pid, proc, MAX_PATH * 2);

            bool haveName = false;
            if (disp && disp[0])
            {
                if (disp[0] == L'@')
                {
                    wchar_t resolved[1024] = {};
                    if (ResolveIndirectString(disp, resolved, _countof(resolved)))
                    {
                        wcsncpy_s(nameBuf, _countof(nameBuf), resolved, _TRUNCATE);
                        haveName = true;
                    }
                    else
                    {
                        // 本地化解析失败：系统提示音会话（AudioSrv）直接映射为可读名，
                        // 其余资源引用取最后一个反斜杠后的文件名兜底。
                        if (wcsstr(disp, L"AudioSrv") != nullptr)
                        {
                            wcscpy_s(nameBuf, L"系统提示音");
                        }
                        else if (const wchar_t* mm = wcsrchr(disp, L'\\'))
                        {
                            wcsncpy_s(nameBuf, _countof(nameBuf), mm + 1, _TRUNCATE);
                        }
                        haveName = true;
                    }
                }
                else
                {
                    wcsncpy_s(nameBuf, _countof(nameBuf), disp, _TRUNCATE);
                    haveName = true;
                }
            }
            if (!haveName && proc[0])
            {
                wcsncpy_s(nameBuf, _countof(nameBuf), proc, _TRUNCATE);
                haveName = true;
            }
            if (!haveName)
            {
                if (pid == 0)
                    swprintf_s(nameBuf, L"系统提示音");
                else
                    swprintf_s(nameBuf, L"进程 #%lu", (unsigned long)pid);
            }

            if (disp) CoTaskMemFree(disp);
            if (c2) c2->Release();
            ctl->Release();

            wchar_t* row = names + (ptrdiff_t)n * nameStrideChars;
            CopyWStride(nameBuf, row, nameStrideChars);
            volumes[n] = vol;
            muted[n] = m;
            pids[n] = (int)pid;
            ++n;
        }
        *filled = n;
    }

    if (iter) iter->Release();
    if (mgr) mgr->Release();
    if (device) device->Release();
    if (enumerator) enumerator->Release();
    if (ownCoinit) CoUninitialize();
    return static_cast<int>(FAILED(hr) ? hr : S_OK);
}

extern "C" int __stdcall Audio_SetSessionVolume(int pid, int muted, float volume)
{
    if (pid <= 0) return static_cast<int>(E_POINTER);
    if (volume < 0.0f || volume > 1.0f) return static_cast<int>(E_INVALIDARG);

    HRESULT init = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(init) && init != RPC_E_CHANGED_MODE)
        return static_cast<int>(init);
    bool ownCoinit = SUCCEEDED(init);

    IMMDeviceEnumerator* enumerator = nullptr;
    IMMDevice* device = nullptr;
    IAudioSessionManager2* mgr = nullptr;
    IAudioSessionEnumerator* iter = nullptr;

    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr,
                                  CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&enumerator));
    if (SUCCEEDED(hr))
        hr = enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device);
    if (SUCCEEDED(hr))
        hr = device->Activate(__uuidof(IAudioSessionManager2), CLSCTX_INPROC_SERVER,
                              nullptr, reinterpret_cast<void**>(&mgr));
    if (SUCCEEDED(hr))
        hr = mgr->GetSessionEnumerator(&iter);

    if (SUCCEEDED(hr) && iter)
    {
        int count = 0;
        iter->GetCount(&count);
        int matched = 0;
        HRESULT lastSet = S_OK;
        for (int i = 0; i < count; ++i)
        {
            IAudioSessionControl* ctl = nullptr;
            if (FAILED(iter->GetSession(i, &ctl)) || !ctl) continue;
            IAudioSessionControl2* c2 = nullptr;
            if (SUCCEEDED(ctl->QueryInterface(__uuidof(IAudioSessionControl2),
                                              reinterpret_cast<void**>(&c2))))
            {
                DWORD cpid = 0;
                c2->GetProcessId(&cpid);
                if (cpid == (DWORD)pid)
                {
                    // 同一进程可能有多个会话（如 Edge 多标签页），对所有会话生效。
                    ISimpleAudioVolume* sv = nullptr;
                    if (SUCCEEDED(c2->QueryInterface(__uuidof(ISimpleAudioVolume),
                                                     reinterpret_cast<void**>(&sv))) && sv)
                    {
                        HRESULT h1 = sv->SetMasterVolume(volume, nullptr);
                        HRESULT h2 = sv->SetMute(muted != 0, nullptr);
                        if (FAILED(h1)) lastSet = h1; else if (FAILED(h2)) lastSet = h2;
                        ++matched;
                        sv->Release();
                    }
                }
            }
            if (c2) c2->Release();
            ctl->Release();
        }
        if (matched == 0) hr = E_NOTFOUND;
        else if (FAILED(lastSet)) hr = lastSet;
        else hr = S_OK;
    }

    if (iter) iter->Release();
    if (mgr) mgr->Release();
    if (device) device->Release();
    if (enumerator) enumerator->Release();
    if (ownCoinit) CoUninitialize();
    return static_cast<int>(FAILED(hr) ? hr : S_OK);
}
