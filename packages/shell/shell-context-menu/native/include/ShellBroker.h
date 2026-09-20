// BetterDesktopMenuBroker — 进程外第三方 shell 扩展 broker（跨语言候选评估 §4.1 第二步）
//
// 【它解决什么】第三方 handler 在宿主进程内跑，SEH 级 AV 无法托管隔离。本 broker 把第三方 handler 的
//   「实例化 + QueryContextMenu + InvokeCommand」整体搬到独立进程：宿主只发生一次「JSON 进 / JSON 出」，
//   handler 崩了死的是 broker，宿主照常可用（降级为"该扩展无内容"，绝不静默无菜单）。
//
// 【与 B 路同源】同一 CMake 工程、同一份 JsonLite / MenuModel / Launcher —— 免宿主 in-proc 扩展
//   （BetterDesktopShellMenu.dll，被 explorer 加载）与进程外 broker（本 EXE）共用 handler 调用链，
//   不维护第二套。这正是 §4.1 收益 1 要求的形态。
//
// 【协议】stdio 一进一出（与 shell-convert 的 convert-engine 同款）：
//   请求（stdin，UTF-8 JSON）：
//     {"op":"menu","background":false,"extended":false,"paths":["C:\\a.txt"],"clsids":["{...}"]}
//     {"op":"invoke","background":false,"paths":["C:\\a.txt"],"clsids":["{...}"],"verb":"open"}
//   响应（stdout，UTF-8 JSON）：{"ok":true,"items":[...]} / {"ok":false,"error":"..."}
//
//   【退出码契约】正常完成（含"handler 拒绝 / CLSID 不存在 / 初始化失败"）= 0；
//   非 0 只可能是进程异常终止（handler 里的 AV / 未捕获异常）——宿主据此把"这个 CLSID 把 broker
//   搞死了"记账（HandlerCrashGuard.RecordExternalCrash → 连续 3 次自动停用）。
//   因此宿主**每次只送一个 CLSID**：崩溃才能精确归因。

#pragma once

#include <string>
#include <vector>

namespace bdshell::broker
{
    /// <summary>一次 broker 请求（stdin JSON 解码后）。</summary>
    struct Request
    {
        std::wstring op;                    // "menu" | "invoke"
        bool background = false;             // 背景场景（pidlFolder=桌面、pDataObj=nullptr）
        bool extendedVerbs = false;          // Shift 扩展动词（CMF_EXTENDEDVERBS）
        std::vector<std::wstring> clsids;    // 目标 handler（宿主侧枚举后传入，含其屏蔽策略）
        std::vector<std::wstring> paths;     // 目标路径（background 时为空）
        std::wstring verb;                   // invoke 用：GCS_VERBW 动词
    };

    /// <summary>
    /// 执行一次请求，返回要写 stdout 的 UTF-8 JSON（**永远合法**，失败也是 {"ok":false,...}）。
    /// noexcept：任何异常都在内部吞掉转成 error 字段——broker 的退出码只用来表达"我死了"。
    /// </summary>
    std::string ExecuteJson(const std::string& requestUtf8) noexcept;

    /// <summary>自检（构建脚本用；不依赖本机是否注册了第三方扩展）。返回失败项数。</summary>
    int SelfTest() noexcept;
}
