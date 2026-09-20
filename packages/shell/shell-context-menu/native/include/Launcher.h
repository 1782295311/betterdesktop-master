// BetterDesktopShellMenu — 动作派发（写批文件 + 分离启动 CLI）
//
// 【为什么走临时文件而不是命令行】
//   ① 多选时路径数量与长度不可控，命令行 32767 上限会截断；
//   ② 路径含空格/引号/Unicode 时拼命令行极易转义出错。
//   落一份 UTF-8 JSON 交给 CLI 解析，命令行里只有一个引号包裹的文件路径。
//
// 【为什么立即返回】
//   explorer / dllhost 的 Invoke 路径上禁止等待外部进程（MS 文档：耗时操作放到 Invoke 之后）。
//   CreateProcessW 后立刻关闭句柄返回，真正的耗时工作由 CLI 进程承担（崩溃隔离）。

#pragma once

#include "BdShell.h"

#include <vector>

namespace bdshell
{
    struct LaunchRequest
    {
        std::wstring action;
        std::vector<std::wstring> args;
        std::vector<std::wstring> paths;
    };

    /// <summary>
    /// 派发一次菜单动作。成功 = 批文件已落盘且 CLI 进程已启动（不等结果）。
    /// 失败只记日志并返回 false，绝不抛异常越过 COM 边界。
    /// </summary>
    bool LaunchAction(const LaunchRequest& request) noexcept;

    /// <summary>同目录 CLI 路径（缺失返回空串；供诊断输出）。</summary>
    std::wstring ResolveCliPath();

    /// <summary>同目录宿主路径（CLI 缺失时的兜底目标；缺失返回空串）。</summary>
    std::wstring ResolveHostPath();

    /// <summary>序列化为 CLI 约定的批文件 JSON（公开供冒烟测试覆盖）。</summary>
    std::string BuildBatchJson(const LaunchRequest& request);

    /// <summary>JSON 字符串转义（含控制字符 → \u00XX）。</summary>
    void AppendJsonString(std::string& out, const std::wstring& text);
}
