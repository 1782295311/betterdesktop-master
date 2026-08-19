# BetterDesktop.Kernel

Cordis 风格插件内核的 C# 复刻：提供服务图、插件运行时、托管生命周期与事件分发能力。

## 依赖

本包是内核基础包，不依赖其他 BetterDesktop 包。它为所有插件提供以下核心服务：

- Context 服务图（依赖注入与服务解析）
- Plugin 生命周期管理（PENDING→LOADING→ACTIVE→FAILED→UNLOADING→DISPOSED）
- Effect 托管清理器（卸载时逆序并行执行）
- Events 事件分发（emit/parallel/serial/bail/waterfall）

## 扩展点

本包对其他插件开放以下接口：

- IContext：服务图与插件生命周期的容器
- IPlugin：插件基础接口
- IEffectManager：托管清理器注册与执行
- IEventBus：事件分发服务

## Known Limitations

- 当前为骨架实现，仅包含接口定义与基础类型，实际服务图逻辑待 P1 实现
- HMR（热模块重载）支持在 P2 阶段实现，当前仅提供基础框架
- 线程模型与 STA 封送服务待完善，当前未实现 WPF Dispatcher 集成
- 依赖选型未冻结：当前未引入任何第三方运行时依赖；是否引入 Microsoft DI 等由 ADR-002 裁定
