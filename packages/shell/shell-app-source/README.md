# BetterDesktop.Shell.AppSource

> 应用数据来源服务：统一的应用扫描、图标获取、新应用检测。
> 为 Dock、Launchpad 等 UI 插件提供通用的应用数据来源。

## 职责

- 扫描开始菜单、已安装程序，注册表解析
- 获取应用图标（Win32 Shell API）
- 检测新安装应用
- **不涉及**：UI 渲染、固定逻辑、Dock 特有状态

## 服务

| 接口 | 说明 |
|------|------|
| `IAppSourceService` | 应用扫描（新应用检测） |
| `IAppIconService` | 应用图标获取 |

## 模型

| 类型 | 说明 |
|------|------|
| `AppItem` | 通用应用数据模型 |
| `AppItemId` | 应用 ID（强类型） |
| `AppSource` | 来源类型枚举 |

## 已知限制

- 仅支持 Windows 10 19041+ (net8.0-windows10.0.19041.0)
