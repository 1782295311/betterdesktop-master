# BetterDesktop.Shell.Calendar.Tests

## 测试范围

日历服务：农历/公历转换、节假日、日程快照与事件广播。

## 运行方式

```bash
# 单工程
dotnet test packages/shell/shell-calendar-tests/BetterDesktop.Shell.Calendar.Tests.csproj

# 全量
dotnet test BetterDesktop.slnx
```

## Known Limitations

- 纯单元 / 集成测试，不启动真实 WPF 窗口；涉及 UI 控件的逻辑以 ViewModel / 服务层为主。
- 依赖原生 API（音频 / 网络 / 输入法 / 电池）的用例在无对应硬件或服务的环境下走降级路径，不断言具体硬件读数。
- 计时相关用例（防抖 / 轮询 / 看门狗）使用轮询等待 + 上限，CI 慢机可能偶发超时，重试即可。
