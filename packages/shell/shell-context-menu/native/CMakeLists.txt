cmake_minimum_required(VERSION 3.20)
project(betterdesktop_shellmenu LANGUAGES CXX)

# ---- 静态链接 MSVC CRT（方案 D · 2026-09-18）----
# 与 Rust 侧 .cargo/config.toml 的 +crt-static 同一目的：本 DLL 此前同时导入
# VCRUNTIME140.dll / VCRUNTIME140_1.dll / MSVCP140.dll，于是要求目标机器装「VC++ 2015-2022 可再发行组件」，
# 而发布包并不带这些 DLL（干净机器上 in-proc 加载失败 → 系统右键菜单点了没反应）。
# 改成 /MT（MultiThreaded）后 CRT 编进产物，不再有这项外部前置。
# 注：cmake_minimum_required 3.20 已隐含 CMP0091=NEW，故 CMAKE_MSVC_RUNTIME_LIBRARY 生效。
# 验收：重编后扫描 DLL 应无 "vcruntime140" / "msvcp140"（忽略大小写）命中。
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# BetterDesktopShellMenu — 免宿主右键快捷功能原生扩展（双路：新菜单 IExplorerCommand + 经典菜单 IContextMenu）。
#
# 纪律（见 docs/plans/2026-09-11-hostless-shell-shortcuts-com-extension.md §5）：
#   - 极薄：只做「读配置 → 渲染菜单 → 派发执行」，零业务逻辑；
#   - 零第三方依赖（无 ATL / WIL / C++/WinRT / nlohmann）；
#   - 进程内（explorer）路径上任何异常都不得越过 COM 边界。
#
# 产物由 scripts/build-shellmenu.ps1 拷到 shell-context-menu/native/ 随插件分发。

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(SHELLMENU_SOURCES
    src/JsonLite.cpp
    src/MenuModel.cpp
    src/Launcher.cpp
    src/ShellMenuHandler.cpp
    src/ExplorerCommand.cpp
    src/ClassFactory.cpp
    src/dllmain.cpp
)

add_library(BetterDesktopShellMenu SHARED ${SHELLMENU_SOURCES})

target_include_directories(BetterDesktopShellMenu PRIVATE include)

target_compile_definitions(BetterDesktopShellMenu PRIVATE
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    _WIN32_WINNT=0x0A00
)

# /W4 严格；/utf-8 保中文源串；/permissive- 收紧模板解析
target_compile_options(BetterDesktopShellMenu PRIVATE /W4 /permissive- /utf-8)

# ---- 安全加固（C1 第五层：原生安全；机检 = scripts/verify-security.ps1 第 7 条）----
# 本 DLL 会**进程内加载进 explorer**，一旦内存破坏就是全桌面沦陷，故这些项在这里比在别处更要紧：
#   /GS            栈保护 cookie（默认开；显式写以固化契约）
#   /guard:cf      控制流保护 —— MSVC 默认**关**，必须显式开
#   /DYNAMICBASE   ASLR（链接器默认开；显式写）
#   /NXCOMPAT      DEP（链接器默认开；显式写）
#   /HIGHENTROPYVA 64 位高熵 ASLR
# 刻意**不加** /Qspectre：需要额外安装「C++ Spectre 缓解库」组件，未装即链接失败（见 build-explorertap.ps1 同类取舍）。
set(SHELLMENU_HARDEN_COMPILE /GS /guard:cf)
set(SHELLMENU_HARDEN_LINK /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /guard:cf)

target_compile_options(BetterDesktopShellMenu PRIVATE ${SHELLMENU_HARDEN_COMPILE})
target_link_options(BetterDesktopShellMenu PRIVATE ${SHELLMENU_HARDEN_LINK})

# x64 下 __declspec(dllexport) 不修饰名字；仍用 .def 显式声明导出，避免意外导出膨胀。
target_sources(BetterDesktopShellMenu PRIVATE shellmenu.def)

target_link_libraries(BetterDesktopShellMenu PRIVATE ole32 uuid shell32 shlwapi user32 advapi32)

set_target_properties(BetterDesktopShellMenu PROPERTIES
    OUTPUT_NAME "BetterDesktopShellMenu"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)

# ---- 纯函数冒烟测试（MenuConfig 解析 + MenuBuilder 选枝；不进 explorer，独立进程） ----
add_executable(ShellMenuSmoke tests/ShellMenuSmoke.cpp src/JsonLite.cpp src/MenuModel.cpp src/Launcher.cpp)
target_include_directories(ShellMenuSmoke PRIVATE include)
target_compile_definitions(ShellMenuSmoke PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX _WIN32_WINNT=0x0A00)
target_compile_options(ShellMenuSmoke PRIVATE /W4 /permissive- /utf-8)
target_compile_options(ShellMenuSmoke PRIVATE ${SHELLMENU_HARDEN_COMPILE})
target_link_options(ShellMenuSmoke PRIVATE ${SHELLMENU_HARDEN_LINK})
target_link_libraries(ShellMenuSmoke PRIVATE shell32 shlwapi ole32)
set_target_properties(ShellMenuSmoke PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)

# ---- 进程外 broker（跨语言候选评估 §4.1 第二步）----
# 第三方 handler 的「实例化 + QueryContextMenu + InvokeCommand」搬到独立进程：in-proc 的 SEH 级 AV
# 无法托管隔离，进程边界是唯一解。与 DLL 同源（JsonLite / MenuModel 公共 TU / Launcher 的 JSON 转义），
# 但**必须是独立 EXE**——DLL 无法自我隔离。宿主经 stdio JSON 一进一出调用，非 0 退出码即"handler 搞死了它"。
add_executable(BetterDesktopMenuBroker
    src/BrokerMain.cpp
    src/ShellBroker.cpp
    src/JsonLite.cpp
    src/MenuModel.cpp   # 公共 TU：LogLine / ConfigFilePath / g_liveObjectCount
    src/Launcher.cpp    # AppendJsonString（JSON 转义，含控制字符 -> \u00XX）
)
target_include_directories(BetterDesktopMenuBroker PRIVATE include)
target_compile_definitions(BetterDesktopMenuBroker PRIVATE
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    _WIN32_WINNT=0x0A00
)
target_compile_options(BetterDesktopMenuBroker PRIVATE /W4 /permissive- /utf-8)
# broker 会在进程外实例化**第三方** handler（潜在不可信代码），加固同样必备。
target_compile_options(BetterDesktopMenuBroker PRIVATE ${SHELLMENU_HARDEN_COMPILE})
target_link_options(BetterDesktopMenuBroker PRIVATE ${SHELLMENU_HARDEN_LINK})
target_link_libraries(BetterDesktopMenuBroker PRIVATE shell32 shlwapi ole32 uuid user32 advapi32)
set_target_properties(BetterDesktopMenuBroker PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)

# ---- 菜单管线探针（配置 JSON -> HMENU；真实 IDataObject；同样不进 explorer） ----
add_executable(ShellMenuHostProbe
    tests/ShellMenuHostProbe.cpp
    src/JsonLite.cpp
    src/MenuModel.cpp
    src/Launcher.cpp
    src/ShellMenuHandler.cpp
)
target_include_directories(ShellMenuHostProbe PRIVATE include)
target_compile_definitions(ShellMenuHostProbe PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX _WIN32_WINNT=0x0A00)
target_compile_options(ShellMenuHostProbe PRIVATE /W4 /permissive- /utf-8)
target_compile_options(ShellMenuHostProbe PRIVATE ${SHELLMENU_HARDEN_COMPILE})
target_link_options(ShellMenuHostProbe PRIVATE ${SHELLMENU_HARDEN_LINK})
target_link_libraries(ShellMenuHostProbe PRIVATE shell32 shlwapi ole32 uuid)
set_target_properties(ShellMenuHostProbe PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)
