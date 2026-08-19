# BetterDesktop.Kernel.Timer

托管定时器服务（Cordis timer 插件对应物）：所有定时任务可取消、异常隔离，随插件生命周期受控。

## 依赖

- `BetterDesktop.Kernel`（零第三方依赖）

## 扩展点

- `ITimerService`：SetTimeout（一次性）/ SetInterval（周期）；返回注销句柄；回调异常被隔离并终止该定时器

## Known Limitations

- v1 仅 setTimeout / setInterval；debounce / throttle 在 P1 后续版本
- 回调在 ThreadPool 执行：UI 操作必须自行经 Dispatcher 服务封送（coding-standards 并发纪律）
