cmake_minimum_required(VERSION 3.20)
project(shell_natives LANGUAGES CXX)

# 底层原生封装工（零 UI、零业务逻辑）。
# 每个 DLL 只做一件事：把难调的 Windows 原生 API 封装成干净的 C 函数，
# 供 C# 端通过 Interop 薄封装调用。全部失败返回 HRESULT（0=成功）。
# 构建产物由 build_native.ps1 统一拷到 shell-status/natives/ 随插件打包。

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(NATIVE_COMPILE_DEFS
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
)

set(NATIVE_COMPILE_OPTS /W4 /permissive- /utf-8)

# Windows 子系统、无窗口、输出到统一 bin 目录。
function(betterdesktop_native_target NAME SRCS INCLUDES LIBS OUTPUT)
    add_library(${NAME} SHARED ${SRCS})
    target_include_directories(${NAME} PRIVATE ${INCLUDES})
    target_compile_definitions(${NAME} PRIVATE ${NATIVE_COMPILE_DEFS})
    target_compile_options(${NAME} PRIVATE ${NATIVE_COMPILE_OPTS})
    if(NOT LIBS STREQUAL "")
        target_link_libraries(${NAME} PRIVATE ${LIBS})
    endif()
    set_target_properties(${NAME} PROPERTIES
        OUTPUT_NAME ${OUTPUT}
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
        LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
        ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    )
endfunction()

# ---- AudioCore：Core Audio（音量/静音/端点可用性） ----
betterdesktop_native_target(audio_core
    src/audio_core.cpp
    include
    "ole32;uuid"                     
    AudioCore
)

# ---- WlanCore：wlanapi（当前连接 + 附近扫描） ----
betterdesktop_native_target(wlan_core
    src/wlan_core.cpp
    include
    "wlanapi"
    WlanCore
)

# ---- PowerCore：powrprof（电源状态 + 性能方案）+ setupapi（电池 IOCTL 直读） ----
betterdesktop_native_target(power_core
    src/power_core.cpp
    include
    "powrprof;setupapi"
    PowerCore
)

# ---- DisplayCore：dxva2 + WMI（显示器亮度读写） ----
betterdesktop_native_target(display_core
    src/display_core.cpp
    include
    "dxva2;user32;wbemuuid;ole32;oleaut32"
    DisplayCore
)

# ---- NetworkCore：iphlpapi（主接口流量 + IP 地址） ----
betterdesktop_native_target(network_core
    src/network_core.cpp
    include
    "iphlpapi;ws2_32"
    NetworkCore
)

# ---- CpuCore：NtQuerySystemInformation（CPU 使用率）+ WMI 热区温度 ----
betterdesktop_native_target(cpu_core
    src/cpu_core.cpp
    include
    "wbemuuid;ole32;oleaut32;user32"
    CpuCore
)

# ---- MemoryCore：PSAPI（内存全局 + 进程 WorkingSet + 物理插槽） ----
betterdesktop_native_target(memory_core
    src/memory_core.cpp
    include
    "psapi"
    MemoryCore
)

# ---- MediaCore：SMTC（媒体会话 + 控制），动态加载 combase，不静态依赖 winrt 库 ----
betterdesktop_native_target(media_core
    src/media_core.cpp
    include
    ""
    MediaCore
)