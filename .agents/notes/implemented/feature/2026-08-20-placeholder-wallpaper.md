# Agent Note: 壁纸使用 XAML 渐变占位

Status: implemented

## Problem

真壁纸引擎（图片/视频/在线源）需要资源加载、缓存与性能管理，是独立大模块，不适合塞进 v1。

## Decision

v1 壁纸用 XAML LinearGradientBrush 深蓝到紫的渐变占位，零外部图片资源，桌面窗口本身即壁纸基底。

## Alternatives considered

- 加载本地图片文件：需要资源管道与路径约定，增加复杂度，否决。
- 接入在线壁纸 API：引入网络依赖与隐私问题，否决。
- 直接黑屏占位：太简陋，渐变占位既零依赖又体现美化方向，采纳。

## Consequences

零外部资源、零网络依赖、构建可复现；代价是壁纸非真实图片，真壁纸引擎留 P1。
