# BetterDesktop.Kernel.Hmr.Tests

HMR 热重载内核扩展的契约测试：版本兼容、依赖校验、状态迁移、失败回滚、生命周期事件与 ALC 程序集加载。

## Known Limitations

- 程序集 ALC 卸载的确定性回收依赖 GC，弱引用断言属尽力而为，不在本测试集断言
- 并发热重载由管理器串行化（v1），本测试集不覆盖并发竞争语义
