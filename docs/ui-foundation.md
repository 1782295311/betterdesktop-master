# ui-foundation.md — 统一 UI 地基与可复用资产规则

> 地位：一切 UI 资产的**唯一来源规范**（对应 DSH 的 web-styling.md 思路：视觉与窗口行为只有一套真相源）。
> 载体：`packages/shell/shell-foundation` 包（P2 落地）。在该包存在之前，本文就是规则——**规则先于代码**。
> 治理地位：MECHANISMS.md M6 的唯一真相源。

## 一、全局字体（唯一令牌）

1. 全程序字体只允许三个语义令牌：`FontToken.Body`（正文）、`FontToken.Mono`（等宽/代码）、`FontToken.Display`（大字号标题）。
2. 任何 XAML / 代码**禁止硬编码 FontFamily**；需要字体只引用令牌。
3. 字体令牌默认值与回退链（含简体中文回退）只在 shell-foundation 定义一次；主题可覆盖，包不可。

## 二、统一窗口属性

1. 所有外壳窗口必须继承统一基类 `ShellWindow`；禁止任何包自建 Window 派生体系或裸用 `Window`。
2. 窗口属性（圆角、阴影、背景材质策略、`DWMWCP_*` 参数、最小尺寸、DPI 声明、关闭/失焦行为）一律由 `ShellWindow` 集中实现，经令牌配置，**禁止按包硬编码**。
3. DPI 声明全局唯一：`PerMonitorV2`（ADR-001 冻结，未来加 R 级护栏）。
4. 背景材质（Mica / Acrylic / 透明磨砂）策略集中一处，主题切换时全局生效。

## 三、主题令牌（语义化）

1. 颜色、圆角、间距、字号一律走语义令牌（如 `ColorToken.Surface.Base`），**禁止 magic number 与一次性色值**。
2. 令牌定义唯一存放于 shell-foundation 的资源字典；主题包只允许覆盖令牌值，不允许绕过令牌。
3. 主题切换经内核 Themes 服务广播，所有包监听刷新（对应内核 Events 机制）。

## 四、公共控件、行为与转换器

1. 通用控件（图标按钮、玻璃面板、圆角边框等）、Attached Behavior、ValueConverter、扩展方法一律归 shell-foundation。
2. **重复定义即违规**：任何包重新定义已有公共控件/转换器，按 reuse-rules.md 的复制条款处理。

## 五、资源与图标

1. 图标与静态资源走统一资源字典与命名规范（`Icons.*` / `Brushes.*` 前缀），单点维护。
2. 包私有资源放包内 `Resources/`，但不得与地基资源重名或重复。

## 六、机检状态（诚实标注，防假绿）

| 规则 | 当前机检 | 规划机检（P2） |
|---|---|---|
| 禁止硬编码 FontFamily / 色值 / 圆角 | **暂无机检**（靠评审） | `verify-ui-hardcoded.ps1`（XAML 文本扫描） |
| 令牌覆盖（令牌存在且被引用） | **暂无机检** | `verify-token-coverage.ps1` |
| 单一 ShellWindow 体系 | **暂无机检** | 架构测试（R 级护栏） |
| 资源不重复 | **暂无机检** | `verify-resource-integrity.ps1` |

> 每一条「暂无机检」在 P2 前仍是评审义务，不是免责条款。
