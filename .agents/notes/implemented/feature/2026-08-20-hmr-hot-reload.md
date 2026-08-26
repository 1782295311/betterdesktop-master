# Agent Note: HMR 热重载内核扩展

Status: implemented

## Problem

内核 v1 只有静态 loader（cordis.yml 装配内置工厂），插件一旦加载无法在运行时替换；TERMINOLOGY 定义 HMR 为「双 ALC 切换 + 旧配置迁移 + 失败回滚」，但 P2 前无实现。

## Decision

在 packages/kernel/kernel-hmr 落地 IHmrManager/HmrManager：每次重载经 AssemblyPluginSource 进入全新可回收 ALC 构造新实例，先立后破——新版本校验（内核 ABI + 必需依赖）并激活后，捕获旧状态（IPluginStateProvider）迁移到新实例，再卸载旧实例并释放旧 ALC；任一步失败自动回滚到旧版本。状态与计数器经 GetPluginRuntimeInfos 暴露，生命周期经 HmrEvents 走内核事件总线，日志走内核 Logger 单一管道。内核 PluginHandle.OnServiceChanged 增加 Disposed/Unloading 守卫，杜绝已卸载插件被服务变化幽灵重启。

## Alternatives considered

- 用 AssemblyLoadContext 之外的自研热替换机制：复杂度高且无收益，否决。
- 复用静态 loader 扩展热重载：loader 只装配内置工厂、无程序集隔离，语义不匹配，否决。
- 依赖者级联重载（依赖版本变化自动重载依赖者）：v1 范围外，记录为已知限制，否决延后。

## Consequences

插件可按程序集粒度运行时替换且失败可回滚；代价是 ALC 卸载回收受 GC 时机影响，依赖者级联重载尚未实现。
