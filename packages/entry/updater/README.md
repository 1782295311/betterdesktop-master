# BetterDesktop.Updater

更新器：更新期间必须拉起自身组件（关闭后再替换），承担更新流程的固有职责。

## 依赖

- `BetterDesktop.Kernel`（进程拉起）
- 编译期共享 `shared/logging/*.cs`

## Known Limitations

- `UpdateSource.cs` 是外部输入点（管道/HTTP 来源 JSON），被 verify-security 门禁列为硬红线，新增 JSON 解析必须显式 MaxDepth
- 生命周期拉起职责被 lifecycle-owner 棘轮登记，收敛到 core 前不可新增拉起点
