# BetterDesktop.Api — 契约包（Contract Assembly）

> 面向**外部创作者**的公开契约层，对标 deepseek-harness：插件、扩展与第三方工具通过本包接入 BetterDesktop 的信息源与系统能力，不依赖任何具体 shell 包。

## 设计原则

- **零项目引用**：本包只声明接口/模型/事件，不含实现；任何 shell 包都可实现这些契约。
- **命名空间保留**：契约文件迁移自原 shell 包，命名空间不变，存量代码零改动即可消费。
- **信息源 API 独立**：所有信息源（AppSource / Calendar / Dock / Music / Search / Status / WindowTracker 等）的提供接口单独拎出，给外部创作者留出扩展空间。

## 目录（域）

`AppSource` `Calendar` `ContextMenu` `Convert` `Core` `Desktop` `Dock` `Music` `Notification` `PluginWindow` `Recent` `Search` `Settings` `StartMenu` `Status` `Taskbar` `WindowTracker`

## 使用方式

- 消费方 csproj 引用本包，通过 `Inject` 声明所需契约（如 `IMemoryMonitor`、`IAppSourceService`）即可获得服务。
- 事件广播统一走 `IEventBus`（`status.changed` 等），详见 `docs/extension-dev.md`。

## Known Limitations

- 契约冻结于 2026-09 一阶段收口：新增/变更接口需经 ADR 决策并同步 `docs/extension-dev.md`。
- 部分契约（如 `Notification`）当前仅有接口与事件，系统通知接入尚未落地，外部消费者请以实际发布版本为准。
