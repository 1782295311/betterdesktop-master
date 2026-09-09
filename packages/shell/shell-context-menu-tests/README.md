# BetterDesktop.Shell.ContextMenu.Tests

## 测试范围

右键菜单：原生菜单聚合、注册表 verb 枚举、GUID 反查（GuidInfo）、菜单管理器。

## 运行方式

```bash
# 单工程
dotnet test packages/shell/shell-context-menu-tests/BetterDesktop.Shell.ContextMenu.Tests.csproj

# 全量
dotnet test BetterDesktop.slnx
```

## Known Limitations

- 纯单元 / 集成测试，不启动真实 WPF 窗口；涉及 UI 控件的逻辑以 ViewModel / 服务层为主。
- 依赖原生 API（音频 / 网络 / 输入法 / 电池）的用例在无对应硬件或服务的环境下走降级路径，不断言具体硬件读数。
- 计时相关用例（防抖 / 轮询 / 看门狗）使用轮询等待 + 上限，CI 慢机可能偶发超时，重试即可。
