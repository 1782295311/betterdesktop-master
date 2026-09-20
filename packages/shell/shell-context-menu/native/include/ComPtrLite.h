// BetterDesktopShellMenu — 最小 RAII COM 指针（不引入 ATL/WRL）
//
// 只做三件事：持有一枚引用、析构即 Release、可移交所有权。刻意不实现拷贝构造，
// 需要的场景一律显式 AddRef（见 Clone 用法）。

#pragma once

#include <windows.h>
#include <utility>

namespace bdshell
{
    template <typename T>
    class ComPtr
    {
    public:
        ComPtr() noexcept = default;
        ~ComPtr() noexcept { Reset(); }

        ComPtr(const ComPtr&) = delete;
        ComPtr& operator=(const ComPtr&) = delete;

        ComPtr(ComPtr&& other) noexcept : _ptr(other._ptr) { other._ptr = nullptr; }
        ComPtr& operator=(ComPtr&& other) noexcept
        {
            if (this != &other)
            {
                Reset();
                _ptr = other._ptr;
                other._ptr = nullptr;
            }
            return *this;
        }

        T* Get() const noexcept { return _ptr; }
        T* operator->() const noexcept { return _ptr; }
        explicit operator bool() const noexcept { return _ptr != nullptr; }

        /// <summary>取输出参数位（会先释放已有引用）。</summary>
        T** Put() noexcept { Reset(); return &_ptr; }

        /// <summary>接管一枚已 AddRef 的裸指针。</summary>
        void Attach(T* raw) noexcept { Reset(); _ptr = raw; }

        /// <summary>交出所有权（不 Release）。</summary>
        T* Detach() noexcept { T* raw = _ptr; _ptr = nullptr; return raw; }

        void Reset() noexcept
        {
            if (_ptr != nullptr)
            {
                _ptr->Release();
                _ptr = nullptr;
            }
        }

    private:
        T* _ptr = nullptr;
    };
}
