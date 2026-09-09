# Cairo 开发计划

> Task: 右键菜单 M3——Taskbar/DockItem/Window/AppCenterItem/ControlCenter 五场景接入统一菜单。
> 依据：M1.5 批次 §12-OQ5 登记。基线：d1bdc89。

## 1. 宿主判别表（2026-09-03 源码验证）

| 场景 | 宿主现状 | 判定 |
|---|---|---|
| DockItem | `DockWindow.ShowItemContextMenu`（L1482）已用 IMenuService 但**裸 items 列表**，未走模板/贡献者/能力过滤 | ✅ **本批落地**：DockItemTemplate（Scope=DockItem）+ MenuRequest 全参（Target=item + File=Classify(TargetPath)）→ 免费获得 C7/M2 全部能力（打开方式/压缩/固定相关/注册表 verb） |
| ControlCenter | `ControlCenterWindow.OnTileRightClick` 已有右键钩子（L324） | ⏸ 后续：构造链未注入 IMenuService（StatusBarMenuBarExtension 装配），改造涉及面板装配；模板内容（音量混合器/设置/电源选项）需逐 feature 定义 |
| Window（标题栏/缩略图） | dock 缩略图 flyout（ThumbnailWindow）与菜单栏前台窗口标题区均有潜在钩子 | ⏸ 后续：系统菜单（还原/移动/大小/最小化/最大化/关闭）需 GetSystemMenu/WM_SYSCOMMAND 桥（新 Win32 收口），并逐宿主接 PreviewMouseRightButtonUp |
| Taskbar | **无自绘任务栏宿主**（Taskbar 包=外观引擎；net8 任务栏由 dock 接管、HTML 任务栏另有通道） | ⏸ 宿主落地后再接（scope 枚举保留） |
| AppCenterItem | AppGrabber 已废弃（开始菜单"所有应用"接管）；StartMenuService.Menus 已注入但行级右键未统一 | ⏸ 后续：AllApps 行右键 → Scope=AppCenterItem 模板（打开/固定到 Dock/打开文件位置/卸载） |

## 2. 本批范围（M3-第 1 落地波）：DockItem
- `shell-dock/Services/DockItemTemplate.cs`（IMenuTemplate，Scope=DockItem）：
  启动/从 Dock 移除/打开所在目录（现裸菜单项迁入）+ 文件能力区（经 RequiredCapability：打开方式…/剪切/复制/重命名仅 lnk 语义适配，v1 收敛为：打开方式…/打开文件位置/复制路径 + C7/M2 贡献项自动出现）；
  系统组保留 开始菜单/应用提取器（dock 自身语义）。
- `DockWindow.ShowItemContextMenu`：改走 `ShowAsync(new MenuRequest(Scope.DockItem, item, pos, File: classifier.Classify(targetPath)))`；DockPlugin 构造注入 IFileClassifier（ContextMenus 包已引用）。
- MenuRequest.Target = DockItemData（模板自取）；SelectedPaths=单元素。

## 3. Tests
- DockItemTemplateTests：Target 缺失返回空；菜单含 dockitem.launch/remove/dir + 文件能力项门控（lnk→OpenFileLocation）。

## 4. DoD
1. Dock 图标右键出现统一菜单（三列/图标/助记同款外观），原 5 项全保留且新增能力项按目标类型显隐；
2. 其余四场景状态在本文件判别表可追溯，宿主就绪后按同构模式接入；
3. sln 0 错误 + 测试全绿；MENU-SPECS 场景表更新。
