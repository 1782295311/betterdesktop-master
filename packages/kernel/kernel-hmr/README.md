# BetterDesktop.Kernel.Hmr

Cordis 内核的 HMR 热重载扩展：运行时动态加载 / 卸载 / 重载插件（双 AssemblyLoadContext 切换 + 旧状态迁移 + 失败回滚）。

## 依赖

- `BetterDesktop.Kernel`（零第三方运行时依赖）

## 扩展点

- `IHmrManager` / `HmrManager`：插件动态加载、卸载与热重载的唯一入口
- `PluginManifest`：插件清单（id / 名称 / 版本 / 内核 ABI / 依赖 / 程序集入口）
- `IPluginStateProvider`：可选能力，实现旧状态捕获与恢复（旧配置迁移）
- `IPluginSource` / `AssemblyPluginSource` / `DelegatePluginSource`：插件实例来源抽象与实现
- `PluginDependencyResolver`：必需依赖校验与稳定拓扑排序
- `HmrEvents` + `PluginLifecycleEvent`：热重载生命周期事件（经内核事件总线）

## Known Limitations

- v1 热重载以插件为粒度整体替换，不支持模块级（类型/方法）局部替换
- 依赖插件版本变化不触发依赖者级联重载（依赖者自动重载属后续版本）
- 程序集卸载依赖 ALC 的可回收性，确定性回收受 GC 时机影响
- 管理器对并发重载操作串行化，同一插件并发重载不保证交错语义
- 并发读取运行状态可能看到重载中间态（快照为尽力而为）
