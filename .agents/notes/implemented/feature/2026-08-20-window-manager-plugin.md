# Agent Note: 窗口管理插件实现（预览 / 切换 / 分屏 / 焦点 / 动画）

Status: implemented

## Problem

`shell-window-manager` 包最初只是占位桩：枚举、切换、分屏、缩略图均为「暂时留空」，无法提供任何窗口管理能力；此前的「不接管窗口管理」决策（2026-08-20-no-system-taskbar-takeover）针对的是「隐藏并替换原生任务栏」这一高风险范围，与「叠加一个自包含的窗口管理服务」并不冲突。

## Decision

在 `shell-window-manager` 包内实现一套自包含的窗口管理服务：Win32 顶层窗口枚举与可管理性过滤、PrintWindow 缩略图预览、MRU 排序的 Alt+Tab 任务切换浮层、左右/上下/多列/多行/网格分屏布局、焦点切换，以及浮层淡入淡出动画。纯逻辑（过滤、MRU、分屏、等比缩放）与 Win32/WPF 边界分离并配单测；不替换原生任务栏，不接管系统 Alt+Tab 之外的任何系统行为。

## Alternatives considered

- 隐藏原生任务栏并全量接管窗口管理：与系统强耦合、稳定性风险高，v1 不做（沿用已有否决）。
- 仅做缩略图不做任务切换：无法满足「类似 Alt+Tab」的诉求，否决。
- 用 RegisterHotKey 注册 Alt+Tab：该组合被系统保留，注册必然失败，改用低级键盘钩子。
- 引入第三方窗口管理库：内核基础包原则零第三方运行时依赖，否决。

## Consequences

窗口管理能力以服务形式对外提供，可被其它插件调用，也可随插件卸载整体清理；代价是 Alt+Tab 拦截依赖 UI 线程消息循环与 WPF Application，且 PrintWindow 快照对个别应用可能空白（均记入 Known Limitations）。
