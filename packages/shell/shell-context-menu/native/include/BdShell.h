// BetterDesktopShellMenu — 公共定义（CLSID / 场景 / 诊断日志）
//
// 对应计划 docs/plans/2026-09-11-hostless-shell-shortcuts-com-extension.md §6.1 / §6.6 / §6.7。

#pragma once

#include <windows.h>
#include <string>

namespace bdshell
{
    // ===== CLSID =====
    //
    // 三个类共享同一份实现（Scene 由工厂注入），用不同 CLSID 区分「挂在哪张菜单上」：
    //   kBClassic      — B 路：经典菜单 / Win11「显示更多选项」；IShellExtInit + IContextMenu
    //   kExplorerFiles — A 路：Win11 新菜单，ItemType = * / Directory；IExplorerCommand
    //   kExplorerBg    — A 路：Win11 新菜单，ItemType = Directory\Background；IExplorerCommand
    //
    // 为什么 A 路要分两个 CLSID：IExplorerCommand 每个方法都拿不到「我挂在哪个 ItemType 上」的
    // 上下文（MS 文档允许同一 CLSID 注册多个 ItemType），只能按 CLSID 区分场景。
    // shellmenu.def / AppxManifest.xml / C# 注册器三处的 GUID 必须逐字一致。
    inline constexpr GUID kBClassic = {
        0x7B2E9C41, 0x3D58, 0x4F0A, { 0x9E, 0x6B, 0x1A, 0x4C, 0x8D, 0x2F, 0x5E, 0x71 } };
    inline constexpr GUID kExplorerFiles = {
        0x7B2E9C41, 0x3D58, 0x4F0A, { 0x9E, 0x6B, 0x1A, 0x4C, 0x8D, 0x2F, 0x5E, 0x70 } };
    inline constexpr GUID kExplorerBg = {
        0x7B2E9C41, 0x3D58, 0x4F0A, { 0x9E, 0x6B, 0x1A, 0x4C, 0x8D, 0x2F, 0x5E, 0x72 } };
    inline constexpr GUID kExplorerDirectory = {
        0x7B2E9C41, 0x3D58, 0x4F0A, { 0x9E, 0x6B, 0x1A, 0x4C, 0x8D, 0x2F, 0x5E, 0x73 } };

    // ===== 场景 =====
    // 由「选择内容」推断（B 路）或由 CLSID 固定（A 路）：
    //   Files      — 至少选中一个文件
    //   Directory  — 选中的全是目录
    //   Background — 文件夹/桌面空白（B 路 pDataObj == nullptr；A 路 kExplorerBg）
    enum class MenuScene
    {
        Files,
        Directory,
        Background,
    };

    inline const wchar_t* SceneName(MenuScene scene) noexcept
    {
        switch (scene)
        {
        case MenuScene::Files: return L"files";
        case MenuScene::Directory: return L"directory";
        case MenuScene::Background: return L"background";
        }
        return L"unknown";
    }

    // ===== 诊断日志 =====
    //
    // %TEMP%\bdt-shellmenu.log，追加式、失败静默。只在「DLL 加载 / 菜单查询 / 派发」三类
    // 低频事件写入——绝不在每个菜单项的回调里写盘（explorer UI 路径红线）。
    void LogLine(const wchar_t* format, ...) noexcept;

    // 配置快照路径：%APPDATA%\BetterDesktop\shellmenu.json（与宿主写入端约定，单点维护）。
    std::wstring ConfigFilePath();

    // ===== 存活对象计数 =====
    //
    // DllCanUnloadNow 必须在外壳持有任何对象时返回 S_FALSE——否则 DLL 会被卸载，
    // 已返回的接口指针立刻变成野指针（explorer 崩溃）。每个 COM 对象持一枚
    // LiveObjectGuard，构造 +1、析构 -1；类工厂本身也算。
    extern LONG g_liveObjectCount;

    struct LiveObjectGuard
    {
        LiveObjectGuard() noexcept { ::InterlockedIncrement(&g_liveObjectCount); }
        ~LiveObjectGuard() noexcept { ::InterlockedDecrement(&g_liveObjectCount); }

        LiveObjectGuard(const LiveObjectGuard&) = delete;
        LiveObjectGuard& operator=(const LiveObjectGuard&) = delete;
    };
}
