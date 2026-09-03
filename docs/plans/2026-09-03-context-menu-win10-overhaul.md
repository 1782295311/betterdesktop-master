# Cairo 开发计划

> Task: 右键菜单全面整改——Win10 化体验补齐（消费空转能力位、内置高频操作、图标/快捷键/助记、Shift 扩展项、文件剪贴板互通、固定到 Dock），并清除 Win11 式收纳残留。
> 证据基于 commit 56df0a10e3c9c1ce6c0161116071dcc43ace3759 验证（工作树 dirty，global dirty digest E4B77F950A4A97F1；基线 `dotnet build packages/shell/shell-context-menu -c Debug` = 0 警告 0 错误）。
> 技术力文档命中：72-右键菜单/windows-context-menu-registry-model（L2，ShellNew/verb/Extended 模型）、72/reg-take-ownership-write（本批次只读不涉及）；相邻复用：05-图标/501+502+504（图标提取/LRU/源三元）、62-多语言/6202（文案本地化范式）、07/710（命令门控范式）。
> cited-path manifest：22（见 §10/§11）。

## 1. Objective

把右键菜单从"M1 骨架 + 稀疏功能"整改为 Win10 式单层完整菜单：
1. 消费 11 个零消费能力位中本批次可落地的 9 个（Share 除外，见 §12-OQ5；ConvertToPdf 维持 Kind 判定不动）；
2. 补齐 Win10 高频操作（记事本打开、创建快捷方式、压缩/解压 ZIP、永久删除、粘贴到、设壁纸、终端回退链、ShellNew 全量新建）；
3. 渲染层 Win10 化：每项图标+文字、右侧快捷键列、字母助记、Shift 扩展项、图标异步补全不阻塞弹出；
4. 文件剪贴板从 Browser 内存态升级为 Windows CF_HDROP，实现与资源管理器双向互通；
5. shell-dock 注册"固定到 Dock"贡献者（MENU-SPECS §2 规划落空项）；
6. 删除 grouped 收纳模式等 Win11 残留，修订 MENU-SPECS。

**设计总纲（用户拍板，最高约束）：以 Win10 为正、以 Win11 为戒——单层完整、图标+文字、第三方平铺、秒开、一套皮、紧凑全键盘；低频项用 Win10 的 Shift 扩展机制而非二级收纳。**

## 2. Current Behaviour

- `MenuService.BuildAsync`（shell-context-menu/Services/MenuService.cs）：五区块骨架 + 能力位过滤 + 贡献者管线已工作；无 Shift/Extended 概念。
- `FileClassifier.Classify`（Services/FileClassifier.cs）：21 个 FileCapabilities 位；回收站分支 `Kind=RecycleBin, Caps=None`（Restore 位未给）；Edit/Print/Preview/PasteInto/Extract/SetAsWallpaper/Restore/Browse/PinToDock/Share 无任何菜单项消费 [verified]。
- `MenuHost.BuildItem`（Services/MenuHost.cs）：WPF MenuItem；`AttachIcon` 只识别 `tool:` 前缀（MenuIconCache.SHGetFileInfo），系统项全部无图标；图标未命中时同步提取（磁盘 IO 阻塞 UI 线程）；无助记、无快捷键列。
- `MenuStyling`（Services/MenuStyling.cs）：行 Grid 仅两列（18 图标 + 文字）；两处 `RecognizesAccessKey="False"`；行高 30px 密度已符合 Win10（保留）。
- `DesktopMenuTemplates`（shell-desktop/Templates/DesktopMenuTemplates.cs）：Desktop/DesktopIcon 两场景；新建仅硬编码 文件夹/文本/快捷方式；终端仅认 WindowsTerminal；无记事本打开/ZIP/快捷方式/永久删除/设壁纸/编辑打印预览。
- `FolderMenuTemplate`（shell-desktop/Templates/FolderMenuTemplate.cs）：ShellFile 场景仅 6 项轻量集。
- `DesktopBrowser`（shell-desktop/Services/DesktopBrowser.cs）：Cut/Copy/Paste 为 `_clipboardPaths` 内存态（L268-310 区间），跨进程无效；`FileOps.DeleteToRecycleBin` 已封装 SHFileOperation（FO_DELETE|FOF_ALLOWUNDO）。
- `DesktopIconsControl`（shell-desktop/Controls/DesktopIconsControl.cs，1751 行）：仅重命名 TextBox 处理 Enter/Esc（L1449），无 F2/Delete/Ctrl 组合/Alt+Enter 全局键。
- 贡献者仅 2 类：`UserMenuContributor`（ContextMenuPlugin 对全 scope 注册）、`ConvertToPdfContributor`（shell-convert/ConvertPlugin）；shell-dock 已 ProjectReference shell-context-menu 但未注册任何贡献者 [verified]。
- 测试项目 shell-context-menu-tests（xUnit 2.9.3）：FileClassifierTests、UserMenuContributorTests、Support/TempFileScope。

## 3. Relevant Architecture

- 契约层 Contracts/（MenuItemDef/MenuPrimitives/MenuRequest/IFileClassifier/IContextMenuContributor）→ 服务层（MenuService 编排、MenuHost 渲染、FileClassifier 判定）→ 模板层（shell-desktop 两个 Template）+ 跨包贡献者（shell-convert 范式：插件包引用 shell-context-menu，在自身 Plugin 注册 IContextMenuContributor）。
- 依赖方向：shell-context-menu 不引用 shell-dock/shell-desktop；故"固定到 Dock"贡献者必须落在 shell-dock 内（与 shell-convert 同构）；通用文件操作贡献者落在 shell-context-menu 内供全 scope 共享。
- 设置：shell-settings 的 Get/Set 字符串键（DesktopIconsControl.InvokeGetBool 范式）；菜单设置 UI 在 Sections/ContextMenuSection.cs。
- 固定后端：shell-dock/Services/IDockAppsService.AddByPath(string) [verified]；shell-pinning/IPinningService.Pin(zone,AppItem)/IsPinned [verified]（dock 已引用 shell-pinning）。
- TFM net8.0-windows10.0.19041；UseWPF；TreatWarningsAsErrors=true；解决方案 BetterDesktop.slnx。

## 4. Technical-Knowledge Findings

- **windows-context-menu-registry-model（72，L2）**：①ShellNew 四分支 NullFile/FileName/Command/Data 与模板目录 `%WINDIR%\ShellNew`；②verb 项 `Extended` 值=按住 Shift 才显示（本计划 Shift 扩展项的原生语义依据）；③MUIVerb `@dll,-id` 显示名解析（M2 用，本批次 ShellNew 显示名需要时借用）；④三套隐藏机制（LegacyDisable/ProgrammaticAccessOnly/CommandFlags 0x639bc8）M2 用。
- **501 程序图标提取 / 502 LRU / 504 图标源三元**：现 MenuIconCache 是进程 Dict 无上限——按 502 加容量上限（512）；IconKey 三协议 sys/file/tool 对应 504"内建命令/exe 路径"三元思路。
- **6202 DisplayString 本地化**：新增菜单文案遵循"键名即字符串、静态字典、en 回退"范式，不硬编码散落字符串（本批次先中文，预留键）。
- **拆析-ContextMenuManager**：ShellNew 枚举与 $I/$R 回收站结构的参考实现来源（还原功能验证依据）。
- 影响半径：契约 2 文件、渲染 3 文件、分类器 1、新服务 4、模板 2、控件 1、dock 2、设置 1、规格文档 1、测试 1 项目；无 kernel/shell-core 改动。

## 5. Constraint Findings（机制文档验收标准 → 本计划约束）

1. **第一菜单原则（2026-09-02 拍板，MENU-SPECS §0）+ Win10 六红线**：单层；禁纯图标项；第三方/贡献项平铺；集合子菜单白名单（新建/查看/排序/发送到/打开方式/窗口）；溢出整菜单滚动。等价断言：MenuService 输出树深度 ≤2（根→白名单子菜单），代码审查 grep 不得出现"更多/显示全部/展开收纳"语义新代码。
2. **秒开纪律**：右键到首帧只允许内存计算与缓存命中图标；任何注册表读取（ShellNew）、SHGetFileInfo 未命中、ZIP 目录列举必须后台化，菜单项先以无图标/占位文本出现再补。等价断言：MenuService.BuildAsync 同步段不得出现未缓存的磁盘/注册表 IO；ShellNewCatalog 必须带内存缓存 + 后台刷新。
3. **GestureText 反假提示**：只允许标注真实响应的快捷键。因此 F2/Delete/Ctrl+C/X/V/A、Alt+Enter、Ctrl+Shift+C、Shift+Del 必须在 DesktopIconsControl 同步实现（步骤 8），否则对应 GestureText 不得出现。
4. **隐藏优先不置灰**（MENU-SPECS §0）：能力不满足的项直接不生成，沿用现有 RequiredCapability 机制，不引入 Enabled=false 占位。
5. **错误静默文化 M10 + DiagnosticLog.Trace("shell.contextmenu", …)**：所有文件操作失败走该通道，不弹未处理异常；但破坏性操作（永久删除/解压覆盖）需要系统确认或防覆盖命名。
6. **TreatWarningsAsErrors**：新增代码零警告；MenuItemDef 增字段必须用可选参数，保持全部现有构造调用不破坏。
7. **多选语义（DesktopIconsControl L1684 注释）**：右键项在选中集内则对整集操作、不重置选中；ZIP 多选打包、剪贴板多选沿用，不得回退为单选。

## 6. Proposed Changes

### A. 契约层（shell-context-menu/Contracts）

- **A1. MenuItemDef.cs**：新增可选属性 `string? GestureText = null`、`bool Extended = false`；主构造与 Child 快捷工厂追加对应可选参数（默认值保证旧调用源码兼容）。Extended=true 表示 Win10 Shift 扩展项。
- **A2. MenuRequest.cs**：record 追加 `bool ShiftPressed = false`。
- **A3. MenuService.cs BuildAsync**：①Opening 注入后、分组前过滤 `def.Extended && !request.ShiftPressed`（子菜单父项被过滤时其子项一并消失，递归过滤）；②设置键 `shell.extendedItemsShift`（默认 true）为 false 时不过滤（全部常驻），由构造注入的 ISettings 读取（与 UserMenuStore 同范式）。
- **A4. FileClassifier.cs**：回收站分支补 `Caps=Restore`（多选 ClassifyMany 交集语义自动生效）；补单元测试覆盖，不改其他判定。

### B. 渲染层 Win10 化（shell-context-menu/Services）

- **B1. MenuStyling.cs**：菜单项 Grid 改三列 `18/*/auto`：图标列（不变）、文字列、快捷键列（右对齐，#9CA3AF，11.5px，Margin 0,0,4，绑定 GestureText，空则折叠不占位）；两处 `RecognizesAccessKey` 改 `True`；子菜单 ItemContainerStyle 同步。
- **B2. 新增 Services/MenuAccessKeys.cs**：同层菜单项访问键唯一分配纯函数 `IEnumerable<MenuItemDef> Assign(IEnumerable<MenuItemDef>)`——优先取文本括号显式键，否则按标题去标点后首个未占用字符（中文取拼音首字母需引包，**第一版不引包**：中文标题跳过自动分配、仅英文/数字分配，中文访问键走显式标注 `(_X)`，避免生僻音库依赖 [assumed 见 OQ7]）；输出 `显示(_X)` 形式 Header。MenuHost.BuildItem 调用，递归对子菜单同样处理。
- **B3. MenuHost.cs AttachIcon 扩三协议**：`tool:<path>`/`file:<path>` → MenuIconCache.Get（缓存命中同步、未命中返回 null 并触发 B4 异步补）；`sys:<name>` → BuiltInMenuIcons 字体 glyph。无 IconKey 不留空白以外行为。
- **B4. 新增 Services/BuiltInMenuIcons.cs**：Segoe MDL2 Assets 字体 glyph 静态表（Copy E8C8/Cut E8C6/Paste E77F/Delete E74D/Rename E8AC/Properties E946/Notepad E70F?/Zip E897?/Wallpaper E771?/Terminal E756?/Shortcut E1D5?/Extract E897? 等——**实现时逐个用字符映射表核对，无法确认的 glyph 降级留空，禁止猜图标**）；返回冻结 TextBlock/Geometry。
- **B5. MenuIconCache.cs**：Dict 改有界 LRU（上限 512，按 502 范式最简 LinkedList+Dictionary 实现即可）；新增 `Task<ImageSource?> GetAsync(path)`（后台线程提取后 Dispatcher 回补）；MenuHost 对未命中项先渲染文本、订阅 Task 续体补 Icon（菜单关闭即丢弃续体，不回写已关闭 UI）。

### C. 内置操作（shell-context-menu/Services，新建）

- **C1. Services/FileClipboard.cs（静态）**：Windows 文件剪贴板统一入口。
  - `Copy(IEnumerable<string>)`/`Cut(...)`：写 `System.Windows.Clipboard.FileDropList`（StringCollection）+ `CFSTR_PREFERREDDROPEFFECT`（DROPFILES 效果：复制=5/移动=2，MemoryStream 1 字节）；STA 要求由调用方 UI 线程满足（菜单点击本就在 UI 线程）。
  - `bool HasFileDrop()`、`IReadOnlyList<string> GetPaths()`、`bool IsCut()`。
  - `PasteInto(string destDir, bool move)`：优先 SHFileOperation（FO_COPY/FO_MOVE，FOF_ALLOWUNDO|FOF_NOCONFIRMMKDIR，多路径双 NUL），获系统进度框与撤销；FileOps 从 shell-desktop 内部类提升为本类公共 P/Invoke 收口（shell-desktop 改为引用本类，删除重复定义）。
  - `PasteShortcutInto(destDir)`：对剪贴板每个路径创建 .lnk（见 C2）。
- **C2. Services/ShellShortcut.cs**：动态 COM（Type.GetTypeFromProgID("WScript.Shell") + InvokeMember CreateShortcut，不引 IWshRuntime 互操作程序集）创建 .lnk（TargetPath/WorkingDirectory/IconLocation/Save）；"创建快捷方式"对选中项在同目录生成"名称.lnk"并重名自增；"粘贴快捷方式"同机制。[inferred WScript.Shell 全 Windows 可用；实现验证]
- **C3. Services/ZipOps.cs（静态）**：`Compress(paths, destZip)`（多源打包：文件用 ZipArchive 逐 Entry，目录用 CreateFromDirectory 到临时再合并，或统一 ZipArchive 递归）；`ExtractTo(zip, destDir)`（ExtractToDirectory，重名目录自增"名称 (2)"）；net8 内置 System.IO.Compression.FileSystem，零新包；目标命名与 DesktopBrowser.UniquePath 同款"副本/序号"规则。
- **C4. Services/WallpaperOps.cs（静态）**：SystemParametersInfo(SPI_SETDESKWALLPAPER=20, 0, path, SPIF_UPDATEINIFILE|SPIF_SENDCHANGE=3) P/Invoke；仅图片（FileCapabilities.SetAsWallpaper 门控）。
- **C5. Services/ShellNewCatalog.cs**：枚举 `Registry.ClassesRoot.GetSubKeyNames()` 中含 `ShellNew` 子键且以 "." 开头的扩展名；解析四分支（NullFile=空文件；FileName=%WINDIR%\ShellNew 模板复制；Command=含 "%1"/%1 占位的命令执行，创建时替换为目标路径；Data=hex 解码写入）；显示名取 HKCR\<ext> 默认值→ProgID→FriendlyTypeName；内存缓存 + 首次后台刷新 + 注册表变更不监听（下次打开重建，成本低）；`Create(entries[i], destDir)` 执行创建并重名自增。安全：Command 分支仅原样执行系统注册命令，不做字符串拼接改写以外的事，异常 Trace 静默。
- **C6. Services/TerminalLocator.cs（静态）**：发现链 wt.exe（PATH/LocalAppData\Microsoft\WindowsApps）→ pwsh.exe → powershell.exe → cmd.exe，返回 (exe,args,workingDir)；工作目录参数：wt -d / pwsh|powershell|cmd 直接 WorkingDirectory。
- **C7. Services/BuiltInOpsContributor.cs（IContextMenuContributor，Scope=DesktopIcon/ShellFile）**：按 request.File.Caps 与 Kind 产出 Manage/Contribution 组菜单项：
  - 记事本打开（全部文件，notepad.exe 直开；目录不显示）；
  - 编辑/打印（Edit/Print 位；ShellExecute verb "edit"/"print"）；预览（Preview 位；verb "preview"）；
  - 解压到"名称\"（Extract 位→ZipOps.ExtractTo）；压缩为"名称.zip"（文件/文件夹多选；ZipOps.Compress）；
  - 设为桌面背景（SetAsWallpaper 位→WallpaperOps）；
  - 创建快捷方式（文件/文件夹→ShellShortcut）；
  - 还原（Restore 位→C8）；在资源管理器中显示（Browse 位→explorer /select,）；
  - 粘贴到文件夹（PasteInto 位：菜单项可见性=FileClipboard.HasFileDrop()，动作 PasteInto；Opening 回调动态判定）；粘贴快捷方式（Extended）；
  - 永久删除（Extended；FileOps FO_DELETE **不带** FOF_ALLOWUNDO，带系统确认 FOF_ALLOWUNDO 反义：用 FOF_NOCONFIRMATION=false 保留确认框；多选整集）；
  - 以其他用户身份运行（Extended，仅 Exe/Msi/Shortcut；verb "runasuser"，失败 Trace 静默 [assumed OQ6]）。
  - 每项 IconKey=sys:<name>、GestureText 按 §5-3 纪律填写。
- **C8. Services/RecycleRestore.cs**：选中项位于 `X:\$Recycle.Bin\<SID>\` 时，同目录配对 `$I<name>` 文件解析原路径（$I 格式 Vista+：2×int32 头 + 8B 文件大小 + 520B UTF-16 路径），把 `$R<name>` 移回原路径（目录不存在则逐级创建，重名不自增覆盖——已存在则中止并 Trace）。[inferred 格式来自公开 $Recycle.Bin 规范；实现时写解析单测验证；若验证失败，本项整体下掉进 OQ，不阻塞批次]

### D. 模板层（shell-desktop/Templates）

- **D1. DesktopMenuTemplates.cs**：
  - Desktop 空白：新建子菜单改由 ShellNewCatalog 动态构建（固定顶：文件夹/快捷方式；分隔；枚举项；文本文档兜底）；终端改 TerminalLocator；粘贴改 FileClipboard.HasFileDrop 门控（支持从资源管理器粘贴到桌面）；补"粘贴快捷方式"(Extended)、"撤销"(beyond 见 §14，本批次不做)。
  - DesktopIcon：删除/剪切/复制/重命名/属性/复制路径补 GestureText（Del/Ctrl+X/Ctrl+C/F2/Alt+Enter/Ctrl+Shift+C）；C7 贡献者自动补入的项不重复手写；"复制路径"对多选复制全部（每行一条，现有单值改为 SelectedPaths 聚合）；管理员运行/打开方式等保留。
- **D2. FolderMenuTemplate.cs（ShellFile）**：补齐为 Win10 完整集——打开/在新窗口打开/终端此处(TerminalLocator)/粘贴到/粘贴快捷方式(Extended)/剪切复制（选中态由调用方上下文）/压缩/用记事本打开/文件位置/复制路径/重命名(F2)/删除(Del)/永久删除(Extended)/属性(Alt+Enter)；贡献项（BuiltIn/User/Convert）经 MenuService 自动出现，不手写。

### E. 固定到 Dock（shell-dock）

- **E1. 新建 Services/PinToDockContributor.cs（IContextMenuContributor，Scope=DesktopIcon/ShellFile）**：门控 Kind=File(Exe/Lnk/Url/AppX)/Folder；构造注入 IDockAppsService（DockPlugin 已有 DI 注册则复用，否则 GetService）；动作 `AddByPath(firstPath)`；已固定判定优先 IPinningService.IsPinned（zone="dock"），不可用则按 Pinned 列表路径比对，已固定显示"从 Dock 取消固定"（RemoveById——需要 AppItemId 反查，实现时按 IDockAppsService 真实能力落地，反查不到则只显示固定）。
- **E2. DockPlugin.cs**：与 ConvertPlugin 同范式 RegisterContributor（DesktopIcon、ShellFile 两 scope）。

### F. 文件剪贴板迁移（shell-desktop）

- **F1. DesktopBrowser.cs**：删除 `_clipboardPaths/_clipboardCut` 内存态；Cut()/Copy() 改调 FileClipboard.Cut/Copy（选中路径）；Paste() 改 FileClipboard.PasteInto(_location, isCut)；删除内部 FileOps 类（迁至 C1），Delete() 改调迁移后的公共 FileOps.DeleteToRecycleBin；新增 DeletePermanent()。
- **F2. DesktopIconsControl.cs**：InvokeBrowserCut/Copy/Delete 改走新路径（多选语义保留）；新增 InvokePermanentDelete、InvokeCopyPaths（SelectedPaths 聚合写文本剪贴板）。
- **F3. 回归点**：桌面内切/复/粘、跨目录粘贴、从资源管理器复制文件到桌面粘贴、桌面复制到资源管理器，四类均要手测（§8）。

### G. 键盘快捷键（shell-desktop/Controls/DesktopIconsControl.cs）

- **G1.** 控件根挂 PreviewKeyDown（或覆写现有按键管道，实现时定位最小侵入点）：F2→对主选中项 StartRename；Delete→InvokeBrowserDelete；Shift+Delete→InvokePermanentDelete；Ctrl+C/X/V/A（复制/剪切/粘贴/全选，全选接 Browser.SetSelection(全部)）；Ctrl+Shift+C→InvokeCopyPaths；Alt+Enter→InvokeShowProperties(主选中)。菜单打开时按键由菜单自身处理，避免重复触发（Popup 打开期间控件 PreviewKeyDown 让位）。

### H. 设置与规格

- **H1. ContextMenuSection.cs**：grep `displayMode`/`grouped`，存在则删除对应开关；新增"高级扩展项：仅按住 Shift 显示（Win10 风格）"开关（键 shell.extendedItemsShift，默认开），关闭则全部常驻。
- **H2. MENU-SPECS.md 修订**：删 §8.1 展开/收起切换项、§8.2/§8.3 grouped 模式与 shell.displayMode 键、§12"组内 >6 进更多子菜单"；新增"§A Win10 化设计红线"（六红线 + Shift Extended + 秒开 + 图标三协议 + 快捷键列与反假提示纪律）；场景表更新落地状态。
- **H3. TECH-KNOWLEDGE/72-右键菜单/README.md**：追加 M1.5 落地注记（实现完成后）。

## 7. Implementation Sequence

1. **契约先行**：A1→A2→A3（Extended 过滤）→A4；`dotnet build shell-context-menu` 绿（旧调用靠可选参数不破）。
2. **渲染 Win10 化**：B1 三列 → B2 助记纯函数（先单测）→ B4 图标表 → B3 协议 → B5 LRU/异步；build 绿后手测现有菜单外观（三列/助记 Alt 键下划线/系统 glyph）。
3. **内置服务**：C1 FileClipboard（含 FileOps 迁移）→ C2 Shortcut → C3 ZipOps → C6 Terminal → C5 ShellNew → C4 Wallpaper → C8 还原解析；每个纯逻辑部分随写随测。
4. **C7 BuiltInOpsContributor** 装配并在 ContextMenuPlugin 注册（DesktopIcon/ShellFile）；手测各类文件菜单显隐。
5. **F 剪贴板迁移 + D 模板补全 + G 键盘**：一组完成（共享 FileClipboard），build + 手测四类剪贴板场景。
6. **E Dock 贡献者**：shell-dock 内新增 + DockPlugin 注册；手测固定/取消。
7. **H 设置与规格修订**。
8. 全量 build + test + 手测清单（§8），更新 README 注记。
任一步结束树保持可编译；契约与渲染可独立先合。

## 8. Test Strategy

xUnit 项目 shell-context-menu-tests（已存在，xUnit 2.9.3；新增同风格测试类）：
- **FileClassifierTests 增补**：回收站对象→HasFlag(Restore)；多选交集（zip+txt）→Extract 保留、SetAsWallpaper 消失。
- **MenuExtendedFilterTests（新）**：Extended 项在 ShiftPressed=false 被过滤、true 保留；设置键关闭时全保留；Extended 父项过滤时子项不孤儿。
- **MenuAccessKeysTests（新）**：英文标题集分配唯一键并生成 `(_X)`；显式标注优先；全中文不抛错、不重复分配。
- **ZipOpsTests（新，TempFileScope）**：多文件压缩→解压往返内容一致；目录压缩往返；目标重名自增不覆盖。
- **ShellNewCatalogTests（新）**：把注册表读取抽成接口 `IShellNewSource`，测试用假源喂四分支样本→断言条目解析（NullFile/FileName/Command %1 替换/Data hex 解码）；不碰真实 HKCR。
- **RecycleRestoreTests（新，条件性）**：构造 $I 字节样本（手写头+UTF16 路径）→断言解析原路径；格式验证失败则按 OQ 下掉该功能与本测试。
- **FileClipboard 决策纯函数**：CFSTR_PREFERREDDROPEFFECT 字节↔cut 判定、SHFileOperation 参数拼装（pFrom 双 NUL、flags 组合）抽纯函数单测；真实剪贴板/系统调用薄封装不测（无 STA 测试设施，不引 StaFact）。
- **BuiltInOpsContributorTests（新）**：给定不同 Caps/Kind 的 MenuRequest→断言出现/缺失的 ItemId 集合（门控矩阵）。
- 回归：现有 FileClassifierTests、UserMenuContributorTests 全绿。
- 验证命令（真实存在）：
  - `dotnet build BetterDesktop.slnx -c Debug`（TreatWarningsAsErrors，0 警告）
  - `dotnet test packages/shell/shell-context-menu-tests/BetterDesktop.Shell.ContextMenu.Tests.csproj -c Debug`
- 手测清单（真机，Win10/Win11 各一遍）：桌面图标右键各类文件；Shift 右键扩展项；空白处新建子菜单；四类剪贴板互通；F2/Del/Ctrl 组合/Alt+Enter；ZIP 压/解；图片设壁纸；回收站还原；固定到 Dock；菜单秒开（冷启动首次右键计时感无延迟、图标渐进补全）。

## 9. Risk and Impact Analysis

- **MenuItemDef/MenuRequest 契约变更（d=1 消费方）**：DesktopMenuTemplates、FolderMenuTemplate、UserMenuContributor、ConvertToPdfContributor、MenuService/MenuHost/ContextMenuPopupWindow——全部用可选参数/可选 record 属性兼容，编译即覆盖；漏网由全 sln build 捕获。
- **剪贴板迁移行为变化（中风险）**：内存态→CF_HDROP 后，桌面内粘贴路径改走 SHFileOperation（系统进度框、可撤销，是收益）；风险是 WPF Clipboard 必须 STA、菜单回调与控件均在 UI 线程满足；F3 四类手测兜底。DesktopBrowser 内存字段删除前 grep 全仓确认无其他消费方。
- **ShellNew Command 分支（低-中）**：执行系统注册命令，严格只做 %1 路径替换，不引入自身命令拼接；枚举只读 HKCR，不需要管理员/夺权（reg-take-ownership 文档不适用本批次）。
- **回收站 $I 解析（中）**：格式为公开结构但跨版本需验证；隔离在单文件 + 单测，失败整体降级（菜单项不出现），不影响其他。
- **WScript.Shell 动态 COM（低）**：全 Windows 自带；非 AOT 场景（WPF net8）无裁剪问题；失败 Trace 静默并隐藏"创建快捷方式"。
- **性能**：ShellNew 枚举 HKCR 全子键（数千）首次可能数十 ms——必须后台缓存，绝不在 BuildAsync 同步跑（§5-2）；图标未命中同理。
- **glyph 错误风险**：Segoe MDL2 码位逐个核对，不确定就留空，不允许"猜一个像的"。
- **可观测性**：新服务统一 DiagnosticLog.Trace("shell.contextmenu", …)；文件操作失败计数不另建遥测（beyond）。
- **不影响**：kernel、shell-core、MenuHost 弹层定位/多屏/DPI 自愈逻辑、ContextMenuPopupWindow 窗口机制（仅三列样式与图标回调）。

## 10. Files Expected to Change

| File | Symbols | Reason |
|---|---|---|
| Contracts/MenuItemDef.cs | MenuItemDef（+GestureText/+Extended） | A1 |
| Contracts/MenuRequest.cs | MenuRequest（+ShiftPressed） | A2 |
| Services/MenuService.cs | BuildAsync（Extended 过滤、设置键） | A3 |
| Services/FileClassifier.cs | Classify 回收站分支 | A4 |
| Services/MenuStyling.cs | MenuItemStyle/子菜单 Style（三列、AccessKey） | B1 |
| Services/MenuAccessKeys.cs（新） | Assign 纯函数 | B2 |
| Services/BuiltInMenuIcons.cs（新） | sys: glyph 表 | B4 |
| Services/MenuHost.cs | BuildItem/AttachIcon（三协议+异步补） | B3/B5 |
| Services/MenuIconCache.cs | GetAsync、LRU 上限 | B5 |
| Services/FileClipboard.cs（新）+ FileOps 迁入 | Copy/Cut/HasFileDrop/PasteInto/PasteShortcutInto | C1 |
| Services/ShellShortcut.cs（新） | CreateShortcut | C2 |
| Services/ZipOps.cs（新） | Compress/ExtractTo | C3 |
| Services/WallpaperOps.cs（新） | Set | C4 |
| Services/ShellNewCatalog.cs（新） | Enumerate/Create + IShellNewSource | C5 |
| Services/TerminalLocator.cs（新） | Locate | C6 |
| Services/BuiltInOpsContributor.cs（新） | Contribute | C7 |
| Services/RecycleRestore.cs（新） | TryRestore | C8 |
| ContextMenuPlugin.cs | 注册 BuiltInOpsContributor | C7 |
| shell-desktop/Templates/DesktopMenuTemplates.cs | Desktop/DesktopIcon 两套模板 | D1 |
| shell-desktop/Templates/FolderMenuTemplate.cs | ShellFile 补全 | D2 |
| shell-desktop/Services/DesktopBrowser.cs | Cut/Copy/Paste/Delete、删内部 FileOps | F1 |
| shell-desktop/Controls/DesktopIconsControl.cs | 回调迁移 + PreviewKeyDown | F2/G |
| shell-dock/Services/PinToDockContributor.cs（新） | Contribute | E1 |
| shell-dock/DockPlugin.cs | RegisterContributor | E2 |
| Sections/ContextMenuSection.cs | 删 grouped、加 Shift 开关 | H1 |
| MENU-SPECS.md | §8/§12 删除、§A 新增 | H2 |
| shell-context-menu-tests/*（新 5 测试类 + 增补 1） | 见 §8 | 测试 |

## 11. Reusable Implementation Context

- HEAD 56df0a10；dirty digest E4B77F950A4A97F1；TFM net8.0-windows10.0.19041.0；x64；WPF；TreatWarningsAsErrors。
- 贡献者注册范本：shell-convert/Services/ConvertToPdfContributor.cs + ConvertPlugin.cs（跨包注册 IContextMenuContributor 的现成范式，E 照抄结构）。
- 设置读写范本：DesktopIconsControl.InvokeGetBool/InvokeSetBool（ISettings.Get/Set 字符串键）。
- P/Invoke 收口范本：DesktopBrowser.FileOps（SHFileOperation 结构体/双 NUL/flags，迁入 FileClipboard.FileOps 时保持 CharSet.Unicode）。
- 重名自增范本：DesktopBrowser.UniquePath（"副本(i)"规则，ZipOps/ShellShortcut 复用同规则）。
- 图标提取范本：MenuIconCache.SHGetFileInfo（file:/tool: 直接复用）。
- 固定接口：IDockAppsService.AddByPath / IPinningService.Pin|IsPinned（shell-dock 已引用两包）。
- 测试基建：xUnit 2.9.3、Support/TempFileScope.cs。
- 构建：`dotnet build BetterDesktop.slnx -c Debug`；单测：`dotnet test packages/shell/shell-context-menu-tests`。
- 领域知识：TECH-KNOWLEDGE/72-右键菜单/windows-context-menu-registry-model.md（ShellNew 四分支、Extended、verb 模型）；拆析-ContextMenuManager（$I/$R、注册表枚举参考）。

## 12. Assumptions and Open Questions

- **OQ1（已拍板 2026-09-03）grouped 模式处置**：**直接删除**，不保留死代码与隐藏开关。
  - 代码：`UserMenuContributor` 的"快捷工具 ▸ 分类 ▸"收纳改为 **Win10 式平铺**——第三方项直接出现在主菜单；`Category` 降为**排序键**（同类相邻、类间插一条分隔线，排序权重压缩0/编辑器1/办公2/图像3/播放器4/终端5/开发6/工具7/其他8）。
  - 配置键 `shell.displayMode`、MENU-SPECS §8.2 整节、底部"展开完整菜单/收起到第三方工具"固定项、README grouped 段落 → 全部删除，**各留一行"已按 Win10 方针移除，勿复活"注记**（FrostedShell 教训：回退靠考古）。
  - 例外保留：`新建 ▶ / 发送到 ▶ / 查看 ▶ / 排序方式 ▶` 属 Win10 原生集合语义子菜单，不算收纳。
- **OQ2（已拍板 2026-09-03）Extended 首批 3 项**：①永久删除（Shift+Del）②以其他用户身份运行（runasuser，真机验证失败即隐藏，走 C7 降级）③**复制到文件夹…**（替换原"粘贴快捷方式"——Win10 老菜单确有"复制到文件夹"，且能消费 `Browse` 能力位；"粘贴快捷方式"极低频）。下放下一批：移动到文件夹…、粘贴快捷方式。
- **OQ3 [assumed]** runasuser verb 在 Win10/11 的可用性需真机验证；失败则该项隐藏（C7 已有降级路径）。
- **OQ4 [inferred]** WScript.Shell 动态 COM 创建 lnk 可行且无需 NuGet；实现首步验证，失败改纯二进制 lnk 写入方案（另立，不阻塞）。
- **OQ5 本批次不做（deferred）**：Share 现代共享面板（IDataTransferManagerInterop，复杂度独立）；撤销菜单（FO_UNDO，见 §14-beyond1）；M2 注册表静态 verb 枚举 + IContextMenu COM 透传（另立计划，依据 72 域 L2 文档）；M3 场景模板 Taskbar/DockItem/Window/AppCenterItem/ControlCenter/MenuBar（另立）；打开方式程序列表（Assoc API，M2 先导）。
- **OQ6** DesktopBrowser 内存剪贴板字段删除前需全仓 grep 确认零外部消费（当前证据仅本类与 DesktopIconsControl 回调 [verified]）。
- **OQ7** 中文菜单字母助记第一版不引拼音库（避免依赖膨胀），中文项走显式 `(_X)` 标注；自动拼音助记列 beyond（TinyPinyin.Net 已在 dock 包，未来可提共享）。
- **OQ8** 多选"复制路径"输出格式：每行一条路径（与 Win11 一致；Win10 无原生此项），不包裹引号（含空格也不加，与现有单值行为一致）——实现时若发现现有行为带引号则保持现行为。

## 13. Definition of Done

1. 全 sln `dotnet build -c Debug` 0 警告 0 错误；shell-context-menu-tests 全绿（含 6 个新增/增补测试类）。
2. 菜单中 Edit/Print/Preview/PasteInto/Extract/SetAsWallpaper/Restore/Browse/PinToDock 九个能力位全部有可见项承接且门控正确；空转能力位仅剩 Share（OQ5 记录）。
3. 每个可见项图标+文字齐全（glyph 未覆盖的留图标空位而非缺列）；右侧快捷键列只显示真实响应键；Alt 访问键可见可用；行高密度不变。
4. Shift 右键出现扩展项、普通右键不出现；设置开关可反转；代码与 MENU-SPECS 中不存在 grouped/显示更多/展开收纳语义。
5. 四类剪贴板互通手测通过；ZIP 压解往返；ShellNew 新建类型与资源管理器列表一致（抽样 5 类）；终端在无 WT 机器回退 powershell/cmd；固定到 Dock 端到端可用。
6. 冷启动首次右键无可见卡顿（ShellNew/图标后台化生效，菜单先出后补）。
7. MENU-SPECS 修订完成、72 域 README 注记更新；实现完成后按闭环回写 3 份新技术文档：FileClipboard(CF_HDROP 文件剪贴板)、ShellNew 枚举、菜单图标三协议（归 72/13/05 域）。

## 14. 可选增强 / 超越需求建议（beyond，不混强制 scope）

- **beyond1 撤销（FO_UNDO）**：文件操作统一 SHFileOperation 后，桌面空白菜单可加"撤销"(Ctrl+Z)，FO_UNDO 撤销上一次系统文件操作，零状态维护；收益：追平 Win10 完整编辑语义。依赖：C1 落地后。
- **beyond2 文件操作全量统一 SHFileOperation**：DesktopBrowser.ImportFiles/CopyDirectory 手写递归可替换为 FO_COPY，获得系统进度框/长路径/可撤销；收益：更稳更省代码。依赖：C1。
- **beyond3 打开方式程序列表**：AssocQueryString + HKCR 关联枚举产出"打开方式 ▶"程序子菜单（白名单集合子菜单），替代仅弹系统对话框；M2 先导。依赖：72 域文档。
- **beyond4 助记分配器共享**：MenuAccessKeys 可提为 shell-core 通用件，供 menubar/开始菜单等所有自绘菜单复用。
- **beyond5 拼音首字母助记**：dock 包已引 TinyPinyin.Net，提共享后中文菜单可全自动分配访问键（对应 OQ7）。
- **beyond6 菜单点击诊断计数**：按 ItemId 聚合本地计数（不联网），为后续默认排序/精简提供数据；依赖 DiagnosticLog 现有通道。
