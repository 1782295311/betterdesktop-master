# 任务 04：shell-context-menu 实现（P1）

## 目标

统一右键入口（所有表面调 `IMenuService`）；文件场景支持三档呈现 + 文件能力过滤。

## 新建 `packages/shell/shell-context-menu/` 实现

| 模块 | 说明 |
|------|------|
| `MenuService` | `ShowAsync`（异步可取消）/ `Dismiss`（幂等）/ `Opening`（展示前注入点） |
| `MenuHost` | 基于 `ShellWindow` 的弹层；普通菜单 + 侧向面板（音量/亮度/网络详情） |
| `FileClassifier` | `IShellFolder.GetAttributesOf`（SFGAO）+ 扩展名 + 回收站/快捷方式目标 → `FileIdentity(Kind, Caps)` |
| `ShellMenuAdapter` | `IContextMenu/2/3` + 消息泵转发（WM_INITMENUPOPUP 等）+ verb 枚举 |
| `MenuFilters` | 第三方 verb 分类（`filters.ini` 关键词表）：压缩/编辑器/图片/剪贴板/搜索/终端/其他 |
| `ContextMenuPlugin.cs` | `Name="shell.context-menu"`，`Inject=[IVibrancyService]`；`Provide<IMenuService>` + `IFileClassifier` |

## 三档呈现（文件场景）

1. `native`：完整系统 `IContextMenu`（兼容/回退）。
2. `unified + full`：verb 重绘，第三方项直接平铺。
3. `unified + grouped`（默认）：第三方项收进"第三方工具 ▶"二级菜单（按类别分组）。

执行均走 `IContextMenu.InvokeCommand`（verb 保真）。

## 能力过滤（用户要求）

- `MenuItemDef.RequiredCapability` 不满足 → **隐藏**（隐藏优先）。
- 例外置灰（`hideDisabled=false` 可显示）：只读删除、无 UAC 管理员运行（置灰 + tooltip）。
- 多选混合类型 → 交集能力；未知无关联 → 仅 打开(选择默认程序)/复制/属性/发送到。

## 落地顺序（先易后难）

1. `IMenuService` 骨架 + `MenuHost` 普通菜单。
2. 接 Dock 项 / 任务栏 / 桌面 / 状态组件 4 个场景。
3. `FileClassifier` + 能力过滤。
4. `ShellMenuAdapter` native 档 → unified 档（消息泵是最高风险点）。

## 验收

- [ ] 构建 0 警告 0 错误
- [ ] 至少 4 个场景可弹菜单
- [ ] 文件菜单能力过滤生效（exe 无"编辑"，Archive 才有"解压"）
- [ ] COM 句柄开合 100 次无泄漏
