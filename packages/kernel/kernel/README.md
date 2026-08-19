# BetterDesktop.Kernel

Cordis 风格插件内核的 C# 复刻：服务图、插件运行时、托管清理与事件分发（API 面由 `docs/architecture/ADR-002.md` 冻结）。

## 依赖

本包零第三方运行时依赖（ADR-002 D2）；仅 BCL。

## 扩展点

- `IContext`：服务图与插件生命周期容器（Get / Provide / Extend / Plugin / Effect）
- `IPlugin` / `IPluginHandle`：插件与运行时句柄（状态机 Pending→Loading→Active→Failed→Unloading→Disposed）
- `IEventBus`：事件名「域/动作」+ 强类型载荷 + 五种分发（emit / parallel / serial / bail / waterfall）
- `CapabilityAttribute`：能力元数据声明位（M16 前置）

## Known Limitations

- v1 无 HMR、无进程外隔离、无配置拦截合并、无 transient 作用域（P2 范畴，见 ADR-002 D1/D3）
- 依赖重载为整实例粒度：依赖类型任一实例变化即触发依赖者重载，完整 epoch 去重在 P1 后续版本
- 日志默认无持久化 sink（P2 宿主接文件管道，见 `docs/runtime-health.md`）
- 事件载荷类型与事件名不匹配抛 InvalidCastException（调用方契约错误，见 ADR-002 D4）
