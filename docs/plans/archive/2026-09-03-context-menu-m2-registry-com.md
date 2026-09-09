# Cairo 开发计划

> Task: 右键菜单 M2——注册表静态 verb 枚举拿回第三方软件右键项 + ShellEx COM 透传（IContextMenu）。
> 依据：M1.5 批次 §12-OQ5 登记；领域知识 TECH-KNOWLEDGE/72-右键菜单/windows-context-menu-registry-model.md（L2，源码级）。
> 基线：d1bdc89；sln 0 错误；194 测试全绿。

## 1. Objective
把第三方软件写进注册表的右键项拿回我们的自绘菜单（当前只有内置 7 服务 + 用户自定义工具）：
1. **静态 verb**：`shell\<verb>` 键（MUIVerb/Icon/Extended/Position/command）→ MenuItemDef；
2. **COM 透传**：`shellex\ContextMenuHandlers\<CLSID>` → IShellExtInit/IContextMenu QueryContextMenu/InvokeCommand；
3. 全部经 RegistryVerbContributor 并入统一菜单（Group=Contribution），Shift 扩展/能力过滤/秒开纪律沿用。

## 2. Current Behaviour
- 菜单贡献者仅 3 类：UserMenu（自定义工具）、BuiltInOps（M1.5）、ConvertToPdf/PinToDock。注册表侧零消费。
- FileClassifier 已产出 Kind/Caps，但不含注册表 verb。

## 3. 设计（领域模型 → 实现）
### 3.1 静态 verb（RegistryVerbs.cs）
- **场景路径集**（按 FileKind 选）：文件=`*` + `AllFilesystemObjects` + `SystemFileAssociations\<.ext>` + `HKCR\<ext>` 及其 ProgID；目录=`Folder` + `Directory` + `AllFilesystemObjects`；驱动器=`Drive`。
- **显示名链**（72-文档红线 2）：MUIVerb > 项默认值(仅非子菜单) > 内置名称字典(open/edit/print/find/play/runas/explore/preview → @windows.storage.dll,-id) > KeyName；`@dll,-id` 一律经 `SHLoadIndirectString` 解析，失败回退原文；文本 ≥80 字符跳过（系统同款隐藏规则）。
- **可见性**（72-文档 4）：`HideBasedOnVelocityId=0x639bc8` / `LegacyDisable` / `ProgrammaticAccessOnly` / `CommandFlags%16>=8` 任一命中即不显示；`Extended` 存在 → MenuItemDef.Extended。
- **图标**：`Icon` 值 = `路径,-索引` → 仅 exe/dll 全路径时经 MenuIconCache（file: 协议）；其余留空（§红线：不猜图标）。
- **命令执行**：`command` 默认值 %1/%L/%V/%V 用双引号目标路径替换（多选逐个启动）；WorkingDirectory=目标目录（`NoWorkingDirectory` 存在则不设）；`DelegateExecute`/`ExplorerCommandHandler` 存在 = 动态命令 → 归 COM 透传路径，静态分支跳过。
- **缓存**：`ConcurrentDictionary<(string scene, string ext), List<RegistryVerb>>` + 时间戳 TTL 5min；注册表扫描绝不在 Build 同步段首次执行——ContextMenuPlugin 预热 + 未命中时返回空并在后台刷新（§5-2 秒开）。

### 3.2 COM 透传（ShellMenuInterop.cs）
- 枚举 `shellex\ContextMenuHandlers`（场景路径集同上，另含 `<ProgID>\shellex`）的 CLSID → `CoCreateInstance` → `IShellExtInit.Initialize(pidlFolder, hglobalDrop, hkeyProgId)` → `IContextMenu.QueryContextMenu(hmenu, 0, cmdFirst=1, cmdLast=0x7FFF, CMF_DEFAULTONLY=0)` → `HMENU` 遍历转 MenuItemDef（id ∈ [cmdFirst,cmdLast] 的项可用；GetSubMenu → Children；分隔线 → Separator）。
- **调用**：点击闭包持 (IContextMenu, verb 字符串) —— GetCommandString(GCS_VERBW) 取 verb，InvokeCommand(CMINVOKECOMMANDINFOEX{lpVerbW})；比 MAKEINTRESOURCE 偏移更稳（防止跨菜单复用偏移漂移）。
- **生命周期**：COM 对象持到菜单关闭（MenuResult 后统一 Release；ItemDef Command 闭包持 WeakReference?——直接持引用，菜单关闭后由 GC 释放 RCW）。
- **安全**：每个 handler 全程 try/catch；**进程内崩溃风险明示**（SEH 无法托管隔离）——缓解：host 有 watchdog/recovery 进程（仓库现存）；v1 接受，隔离方案（out-of-proc broker）登记 beyond。
- **性能**：QueryContextMenu 每次 Build 都要实例化 COM（首次右键数十~数百 ms）→ 按 (clsid, 目标路径集) 缓存 60s（面板 1s 刷新不存在于此路径；同一目标重复右键零开销）；handler 白名单黑名单设置键 `context-menu.com.disabled`（List<string> clsid）。

### 3.3 贡献者
- `RegistryVerbContributor(IFileClassifier, scope)`：static verbs（同步缓存命中）+ COM items（缓存命中才返回，未命中后台预热返回空——首帧秒开，第二次右键出现）。Priority -180（BuiltInOps 之后）。

## 4. Tests
- `RegistryVerbNameTests`：显示名链四段（MUIVerb/默认值/内置字典/KeyName）、@dll 解析失败回退、≥80 截断跳过（纯函数 internal seam，不碰注册表）。
- `RegistryVerbCommandTests`：%1/%V 替换、引号目标、NoWorkingDirectory。
- `RegistryVerbVisibilityTests`：四个隐藏标记 + Extended。
- COM 透传：薄封装不测（需真 shell handler）；门控矩阵测贡献者合并顺序与去重（内置词典 verb "open" 与模板重复 → 去重规则：RegistryVerbContributor 产出 `reg:*` Id，模板已有 Id 冲突时 MenuService 侧丢弃?——v1 简单跳过名为 open/edit/print 的键，模板已提供）。

## 5. DoD
1. 装 7-Zip/VS Code 的机器：文件右键出现其注册表项（含图标/Extended）并可执行；
2. 压缩软件的 ContextMenuHandlers（7-Zip .dll 实现）在 Shift/普通右键出现并可用；
3. 秒开不回归（首帧不含未缓存 COM 项）；
4. 全 sln 0 错误 + 测试全绿；MENU-SPECS 更新 M2 状态。
