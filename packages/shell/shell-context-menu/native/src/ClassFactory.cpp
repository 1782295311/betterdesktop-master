// BetterDesktopShellMenu — IClassFactory 实现（按 CLSID 分派到 B 路 / A 路对象）

#include "ShellMenuHandler.h"
#include "ExplorerCommand.h"

#include <windows.h>
#include <new>

namespace bdshell
{
    /// <summary>模块锁计数：影响 DllCanUnloadNow（外壳可随时卸载未持有的 DLL）。</summary>
    extern LONG g_moduleLockCount;

    namespace
    {
        class ShellMenuClassFactory final : public IClassFactory
        {
        public:
            explicit ShellMenuClassFactory(REFCLSID clsid) noexcept : _clsid(clsid) {}

            IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) noexcept override
            {
                if (ppv == nullptr) { return E_POINTER; }
                *ppv = nullptr;
                // 统一用 __uuidof：见 ShellMenuHandler::QueryInterface 的 IID 踩坑记录
                // （本机 SDK 的 extern IID 符号不可信，必须走编译期常量）。
                if (::IsEqualIID(riid, __uuidof(IUnknown)) || ::IsEqualIID(riid, __uuidof(IClassFactory)))
                {
                    *ppv = static_cast<IClassFactory*>(this);
                    AddRef();
                    return S_OK;
                }
                return E_NOINTERFACE;
            }

            IFACEMETHODIMP_(ULONG) AddRef() noexcept override
            {
                return static_cast<ULONG>(::InterlockedIncrement(&_refCount));
            }

            IFACEMETHODIMP_(ULONG) Release() noexcept override
            {
                const LONG remaining = ::InterlockedDecrement(&_refCount);
                if (remaining == 0) { delete this; }
                return static_cast<ULONG>(remaining);
            }

            IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) noexcept override
            {
                if (ppv == nullptr) { return E_POINTER; }
                *ppv = nullptr;
                if (outer != nullptr)
                {
                    return CLASS_E_NOAGGREGATION;
                }

                try
                {
                    // B 路：经典菜单处理器
                    if (::IsEqualCLSID(_clsid, kBClassic))
                    {
                        auto* handler = new (std::nothrow) ShellMenuHandler();
                        if (handler == nullptr) { return E_OUTOFMEMORY; }
                        const HRESULT hr = handler->QueryInterface(riid, ppv);
                        handler->Release();
                        return hr;
                    }

                    // A 路：Win11 新版菜单命令
                    return CreateExplorerCommand(_clsid, riid, ppv);
                }
                catch (...)
                {
                    LogLine(L"CreateInstance 未捕获异常（已吞）");
                    return E_FAIL;
                }
            }

            IFACEMETHODIMP LockServer(BOOL lock) noexcept override
            {
                if (lock)
                {
                    ::InterlockedIncrement(&g_moduleLockCount);
                }
                else
                {
                    ::InterlockedDecrement(&g_moduleLockCount);
                }
                return S_OK;
            }

        private:
            LiveObjectGuard _guard; // 类工厂本身也是存活对象
            LONG _refCount = 1;
            CLSID _clsid = {};
        };
    }

    HRESULT CreateClassFactory(REFCLSID clsid, REFIID riid, void** ppv) noexcept
    {
        if (ppv == nullptr)
        {
            return E_POINTER;
        }
        *ppv = nullptr;

        const bool known =
            ::IsEqualCLSID(clsid, kBClassic) ||
            ::IsEqualCLSID(clsid, kExplorerFiles) ||
            ::IsEqualCLSID(clsid, kExplorerDirectory) ||
            ::IsEqualCLSID(clsid, kExplorerBg);
        if (!known)
        {
            return CLASS_E_CLASSNOTAVAILABLE;
        }

        auto* factory = new (std::nothrow) ShellMenuClassFactory(clsid);
        if (factory == nullptr)
        {
            return E_OUTOFMEMORY;
        }

        const HRESULT hr = factory->QueryInterface(riid, ppv);
        factory->Release();
        return hr;
    }
}
