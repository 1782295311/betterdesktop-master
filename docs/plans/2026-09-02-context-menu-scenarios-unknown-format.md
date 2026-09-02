# 计划：cordis 右键菜单开工——场景功能定版与未知格式处理（含文档格式转换集成）

> 仓库：`better-desktop-cordis` @ `952f1170`（工作树有未提交改动：host csproj/Bootstrap、Taskbar、shell-dock 等——开工前先由用户决定是否先落盘这些改动）
> 类别：功能开发（大功能从零建组件）· 深度 default · freshness accept（MENU-SPECS/README 与现状一致）
> 2026-09-02 追加：文档格式转换（doc/docx/ppt 等）集成进右键菜单（用户确认）
> 技术力命中：`TECH-KNOWLEDGE/72-右键菜单/windows-context-menu-registry-model` [verified，2026-09-01 入库]；相邻：`72-右键菜单/reg-take-ownership-write`、`05-图标/501`、拆析-ContextMenuManager
> 设计输入：`packages/shell/shell-context-menu/README.md` + `MENU-SPECS.md`（11 场景规格）+ `docs/实现拆解/任务04-shell-context-menu实现.md`——本计划**不重写规格**，只做"第一版定版 + 未知格式决策 + 落地顺序"

---

## 1. 目标（本次开工要敲定什么）

1. **场景功能定版**：11 个场景按三批落地，M1 只做 2 个场景（桌面空白/桌面图标）——其余场景 M2/M3，避免 11 场景齐头并进。
2. **未知格式处理定版**：判定链 + 菜单集 + 与 Shell 集成的联动行为。
3. 建 `shell-context-menu` 组件骨架（MenuService/MenuHost/FileClassifier），桌面现有两份自绘菜单迁移为第一批消费者。

## 2. 场景功能定版（第一版决策，覆盖 MENU-SPECS）

| 批次 | 场景 | 第一版菜单集（差异标注） |
|---|---|---|
| **M1** | 桌面空白（Desktop） | 保留现有：新建文件夹/粘贴/整理/刷新/显示/个性化；**新增**：新建▶（文本文档/快捷方式）、查看▶（大/中/小图标·自动排列·对齐网格→落 `desktop.*` 设置）、排序▶、终端（检测 Windows Terminal 存在才显示，隐藏优先）。第三方原生项（NVIDIA 等）**M1 不做**，随 native 档进 M2 |
| **M1** | 桌面图标（DesktopIcon） | 保留现有：打开/剪切/复制/重命名/删除(回收站)/属性；**新增**：以管理员运行（exe 或 lnk→exe）、打开文件位置（lnk，复用 `ShellLinkResolver`）、发送到▶、固定到 Dock（插件贡献项走 `IContextMenuContributor`）、**转换为 ▶（文档类，见 §3.5）**。虚拟项（::{CLSID}）仅"打开" |
| M2 | 文件场景 Shell 原生层 | `ShellMenuAdapter` native 档（消息泵）→ unified 档（verb 枚举+grouped 收纳）；任务栏/Dock 固定/Dock 运行 |
| M3 | 其余 6 场景 | Cairo 图标/状态组件/窗口/AppCenter/控制中心——纯自绘，无 COM，风险低 |

定版原则：**M1 是"纯自绘 + 能力过滤"**（不动 COM），可独立交付；`shell.integration = native|unified` 配置键 M1 就预留，值缺省 native 生效前 unified 档未实现时忽略。

**★ 第一菜单原则（用户拍板 2026-09-02）**：不把功能藏进二级菜单——对应场景的高频功能**始终放第一菜单**：
1. 高频操作（打开/复制/删除/转换为/自定义工具项…）一层直达，零跳转。
2. 第三方工具项（unified 档枚举的 verb、用户自定义项）**直接平铺第一层**，不收进"第三方工具 ▶"。
3. 溢出用**菜单内滚动**（MENU-SPECS §12 已有整菜单滚动），**禁用"更多…"子菜单收纳**。
4. 仅两类允许子菜单：语义本身就是集合的（新建▶/查看▶/排序▶/发送到▶/窗口▶）与动态列表（窗口缩略图列表）。
5. **需同步修订 MENU-SPECS**（实现 M2 前完成）：§8.3 默认值 `grouped` → **`full`**（grouped 降级为可选）；§0"组内 >6 移入更多"条款删除，改为滚动。

## 2.5 通用基础菜单 + 场景适配（用户拍板 2026-09-02）

**架构**：MenuHost 渲染层与场景内容解耦——
```
MenuHost（通用渲染：区块骨架/图标/键盘导航/滚动/置灰/关闭语义）
   ↑ 填充
MenuTemplate（每场景一份声明式模板，决定各区块放什么）
   ↑ 供项
系统基础项 + IContextMenuContributor（插件/转换/自定义项） + ShellMenuAdapter（M2）
```
- **通用骨架（所有场景同一顺序，第一菜单原则固化在此层）**：
  `①常用操作组 → ②管理组 → ③插件贡献组 → ④系统组 → ⑤动态/原生组（可无）`
- 高频项永远落 ①，插件贡献永远落 ③——新增场景只写 Template，不碰渲染；「第一菜单」规则由骨架层强制（①组内禁止再收纳）。
- MenuItemDef 增加 `MenuGroup` 字段（Common/Manage/Contribution/System/Dynamic），Contributor 声明组别，Template 可微调排序但不可跨组。

## 2.6 场景形状验证（文字 mockup，初步效果确认）

**通用骨架**（每个菜单的区块顺序恒定）：
```
┌ ①常用操作组 ────────────┐
├ ②管理组 ────────────────┤
├ ③插件贡献组 ────────────┤
├ ④系统组 ────────────────┤
└ ⑤动态/原生组 ───────────┘
```

**场景 A：桌面空白（M1）**
```
┌────────────────────────────┐
│ 新建                ▶     │ ① 集合语义子菜单
│ 粘贴        (剪贴板空置灰) │ ①
│ 刷新                       │ ①
├────────────────────────────┤
│ 查看                ▶     │ ②
│ 排序方式            ▶     │ ②
│ 整理图标                   │ ②
├────────────────────────────┤
│ [插件贡献项平铺]           │ ③
├────────────────────────────┤
│ 显示设置                   │ ④
│ 个性化                     │ ④
│ 在终端中打开  (检测到 WT)  │ ④ 隐藏优先
└────────────────────────────┘
```

**场景 B：桌面图标·可执行/常规文件（M1）**
```
┌────────────────────────────┐
│ 打开            (默认加粗) │ ①
│ 以管理员运行      (exe)    │ ① 能力过滤
│ 打开文件位置      (lnk)    │ ① 能力过滤
├────────────────────────────┤
│ 剪切                       │ ②
│ 复制                       │ ②
│ 删除       (只读置灰)      │ ②
│ 重命名                     │ ②
├────────────────────────────┤
│ 固定到 Dock      (插件)    │ ③
│ 转 PDF           (文档类)  │ ③ 能力过滤·平铺
│ [用户自定义项平铺…]        │ ③
├────────────────────────────┤
│ 发送到              ▶     │ ④ 集合语义
│ 属性                       │ ④
└────────────────────────────┘
```

**场景 B'：未知格式文件（M1 判定为 Unknown）**
```
┌────────────────────────────┐
│ 打开方式…    (openas verb) │ ① 定版菜单集
├────────────────────────────┤
│ 复制                       │ ②
├────────────────────────────┤
│ [声明适用 Unknown 的自定义项]│ ③
├────────────────────────────┤
│ 发送到              ▶     │ ④
│ 属性                       │ ④
└────────────────────────────┘
  编辑/打印/剪切/删除… 全部隐藏（隐藏优先）
```

**场景 C：Dock 固定项（M2 预览，验证骨架跨场景复用）**
```
┌────────────────────────────┐
│ 打开    (未运行启动/运行激活)│ ①
│ 新窗口        (多实例时)   │ ①
├────────────────────────────┤
│ 取消固定                   │ ②
│ 打开文件位置               │ ②
├────────────────────────────┤
│ [插件贡献项平铺]           │ ③
├────────────────────────────┤
│ 窗口        ▶  (动态列表) │ ⑤ 缩略图列表
│ 关闭所有窗口  (无窗置灰)   │ ⑤
└────────────────────────────┘
```

**验证结论清单**（形状确认点）：高频项全部 ① 组一层直达 ✓；插件/自定义/转换平铺 ③ ✓；仅集合语义与动态列表有二级 ✓；各场景仅 ①②④ 内容不同、骨架与顺序恒定 ✓。

## 3. 未知格式处理定版（用户核心关切）

**判定链**（FileClassifier，M1 落地）：
1. 虚拟项 `IsShellNamespace` → 走虚拟项菜单（仅"打开"），不进分类器。
2. `.lnk/.url` → `ShellLinkResolver.Resolve` 取目标，**继承目标能力**（目标 exe→RunAsAdmin；目标失效→降级 Unknown）。
3. 扩展名关联探测：`Path.GetExtension` 非空 → 查 `HKCR\<ext>` 存在且有 `shell\open\command`（或 UserChoice）→ 有关联；**[inferred]** 用 `SHAssocEnumHandlers`/`AssocQueryString` 优于裸注册表读（处理 UserChoice 与 PerceivedType），M1 先裸注册表探测（只读，无需提权），M2 随 ShellMenuAdapter 换 Assoc API。
4. 判不出来 → `FileKind.Unknown`。

**Unknown 菜单集**（隐藏优先，与 MENU-SPECS §8.4 一致并定版）：
`打开`（文本=「选择默认程序…」→ 执行 `openas` verb 或 `ms-settings:defaultapps`）｜`打开方式 ▶`｜`复制`｜`发送到 ▶`｜`属性`。其余（剪切/删除/重命名 保留；编辑/打印/解压/设壁纸等一律隐藏）。
依据：Windows 对未知类型的原生菜单来源于 `HKCR\Unknown\shell` 注册表场景（[verified] 72-右键菜单 场景表）——native 档自动继承该行为，unified 档由 FileKind.Unknown 显式映射同一菜单集，两档体验一致。

**只读红线**：cordis 对注册表**只读**（枚举关联/图标），永不写 HKCR——不触发 reg-take-ownership 红线 [verified]。

## 3.5 文档格式转换集成（转换为 ▶，用户确认需求）

**归属**：转换执行是通用能力（未来文件管理器/批量转换复用），落新工程 `packages/shell/shell-convert`（`BetterDesktop.Shell.Convert`）；菜单集成走 **`IContextMenuContributor` 贡献者模式**（shell-convert 注册 `Scope=DesktopIcon/ShellFile` 的 Contributor）——转换功能成为贡献者扩展点的第一个真实消费者，反向验证 M1 架构。shell-context-menu 只依赖其契约接口（依赖倒置：契约放 shell-context-menu/Contracts 或 kernel 契约工程）。

**文档分类扩展（FileClassifier）**：
- `FileKind` 增：`WordDocument(doc/docx/docm/rtf/odt)`、`ExcelWorkbook(xls/xlsx/xlsm/csv/et)`、`Presentation(ppt/pptx/pps/dps)`（扩展名表可配置）。
- `FileCapabilities` 增：`ConvertToPdf`、`ConvertToDocx`（能力判定 = 分类命中 **且** 转换引擎可用，见下）。

**引擎能力探测链**（决定菜单是否显示，隐藏优先；探测结果缓存 + FS 变化失效）：
1. MS Office COM：`HKCR\Word.Application` / `Excel.Application` / `PowerPoint.Application` ProgID 存在 [verified 判定法：72-右键菜单 模型只读枚举]。
2. WPS：`KWPS.Application` / `KET.Application` / `KWpp.Application`。
3. LibreOffice headless：soffice 路径解析（安装目录 `Program Files\LibreOffice\program\soffice.exe` → 便携目录 → 环境变量 override）——**复用 `66-文档转换/local-engine-orchestration` 的路径解析+探测+错误分类模式 [verified]** 与 `dependency-on-demand` 的「本机优先，缺失可下载便携运行时」策略 [verified]。
- 三者皆无 → `ConvertToPdf` 能力不满足 → 整组隐藏。

**第一版目标格式定版**：仅 **「转为 PDF」**（doc/docx/xls/xlsx/ppt/pptx → 同名 .pdf）；`doc→docx` 等互转列入 M2 后置。三条引擎路径均成熟：Office COM `ExportAsFixedFormat`（Word）/`SaveAs FileFormat:=xlTypePDF`、WPS 同构 API、`soffice --headless --convert-to pdf --outdir <dir> <file>`。

**执行与反馈**：
- 转换在后台 Task 执行（COM 引擎必须 **STA 专用线程** + `Marshal.ReleaseComObject` 逐层释放 + 超时强杀进程，防孤儿 WINWORD.EXE 挂锁文件——红线）；soffice 走子进程+超时（local-engine-orchestration 模式）。
- 进度/结果经 IEventBus：`convert/started|finished|failed`（payload: 源路径/目标路径/引擎/耗时/错误分类）→ shell-notification 发通知。
- 输出安全：**不覆盖已存在文件**——同目录重名自动追加序号（`name (2).pdf`），复用 `66-文档转换/pdf-edit-safe-output` 的「输入校验+临时文件原子发布+稳定错误码」契约 [verified]。

**菜单形态**（第一菜单原则修订：单目标直接平铺，不设二级）：
```
│ 转 PDF（第一层平铺；无引擎时不显示；转换中禁用并显示进度通知）
```
（未来目标格式 ≥2 个时才升级为「转换为 ▶」子菜单）

**复用资产清单**：`local-engine-orchestration`（引擎编排）、`dependency-on-demand`（便携 LibreOffice 可选下载）、`pdf-edit-safe-output`（安全输出）——均为 66-文档转换域 L2 [verified] 资产，实现前按其红线执行。

## 3.6 用户自定义菜单项 / 第三方工具调用（两层 DIY，用户确认方向）

**定位**：右键菜单 = 系统基础项 + 插件贡献项 + Shell 原生项 + **用户自定义项**。自定义项是零代码 DIY 层，也是 `IContextMenuContributor` 最轻的真实消费者（M1 即落，先于 shell-convert 验证扩展点）。

**两层 DIY 边界**：
| 层 | 谁用 | 机制 |
|---|---|---|
| 插件层（代码级） | 插件/组件开发者 | `IContextMenuContributor`（shell-dock「固定到 Dock」、shell-convert「转换为」皆走此路） |
| 用户层（零代码） | 普通用户 | `UserMenuSpec` 配置文件 → `UserMenuContributor` 渲染；设置分区可视化编辑 |

**UserMenuSpec 数据模型**（存储走 ISettingsService 用户配置文件，前缀 `context-menu.custom.*`，JSON/ini 跟随现有惯例 [inferred：对齐 desktop.* 键模式]）：
```
Id / Name / IconKey(或 IconPath) / Command / Arguments / WorkingDir
AppliesTo: Scope + FileKind 过滤(如 仅.docx；空白处=仅 Desktop Scope)
Placement: Priority / 分组 / 排序
RunMode: Normal | RunAs(UAC) | Minimized
Enabled: bool
```

**参数占位符**（自定义命令的核心，第一版定版）：
`%file%`（选中单个路径）/ `%files%`（多选路径，逐个或空格引号包裹）/ `%dir%`（所在目录）/ `%filename%`（含扩展名）/ `%name%`（无扩展名）/ `%desktop%`。**红线：路径占位符必须自动加引号包裹**（防空格路径撕裂命令行）；未识别占位符原样保留并记 DiagnosticLog。

**能力过滤联动**：自定义项声明 `AppliesTo` → 由既有能力过滤管线统一执行（Unknown 文件上只显示声明了 Unknown 的自定义项；虚拟项不显示文件类自定义项）——与「隐藏优先」原则一致。

**编辑 UI**（M2）：`ContextMenuSection` 设置分区提供列表管理（增删/排序/启用禁用）+ 编辑对话框（引用 ExplorerRestarter 不需要——自绘菜单即时生效）。M1 先手编配置文件打通链路。

**参照资产**：cairoshell `710-command-system`（字符串标识符命令 + IsAvailable 门控 + 事件）与 ContextMenuManager「自定义 shell 项」模型（72-右键菜单）——本设计取「用户配置驱动、不写注册表、能力门控」三者之长。

## 4. 技术力约束切片（生死线，摘自 72-右键菜单/windows-context-menu-registry-model [verified]）

1. unified 档枚举静态项时：显示名解析 `MUIVerb` > 默认值(非多级) > 内置字典(open→8496…) > 键名；`@dll,-id` 必须 `SHLoadIndirectString` 解析。
2. 图标解析链末端 `imageres.dll,-2` 兜底；文本 ≥80 需截断/拒写。
3. verb 系统判定前必须判 `WinOsVersion`（`HideBasedOnVelocityId=0x639bc8` 仅 1703+）——M2 unified 档"隐藏菜单项管理"若做才需要；M1 不涉及。
4. COM CLSID 键名不能直接当显示名，必须反查（GuidInfo 链）——M2 ShellEx 项适用。

## 5. 改动面（M1）

| 动作 | 文件 |
|---|---|
| 新建工程 | `packages/shell/shell-context-menu/BetterDesktop.Shell.ContextMenu.csproj`（net8.0-windows10.0.19041.0，UseWPF，引 kernel/shell-core/shell-settings；**不引 shell-desktop**——依赖方向由消费者持菜单服务） |
| 新建契约 | `Contracts/IMenuService.cs`、`MenuRequest/MenuResult/MenuItemDef/MenuScope`、`IFileClassifier.cs`、`IContextMenuContributor.cs`（签名照 README §4-6，不发明新形态） |
| 新建实现 | `Services/MenuService.cs`、`Services/MenuHost.cs`（基于 ShellWindow 弹层 + `DesktopMenuStyling` 令牌工厂**抽出复用**或等价迁移）、`Services/FileClassifier.cs` |
| 迁移 | `DesktopIconsControl.BuildBlankMenu/BuildIconMenu` → `MenuTemplates/DesktopTemplate.cs` + `DesktopMenuContributor`（Scope=Desktop/DesktopIcon），原逻辑保留为回退开关 `context-menu.migrated=false` 时走旧路 |
| 注册 | `ContextMenuPlugin : IPlugin`（Name="context-menu"，Provide<IMenuService>/IFileClassifier）；host csproj 加 ProjectReference；设置分区 `ContextMenuSection` 后置到 M2 |
| 新建工程（转换） | `packages/shell/shell-convert/BetterDesktop.Shell.Convert.csproj`（引 kernel + 契约工程）：`ConvertToPdfContributor`（IContextMenuContributor）、`DocumentConversionService`（引擎探测/STA COM/soffice 子进程/超时强杀）、`ConvertPlugin : IPlugin`（IEventBus convert/* 事件）；host csproj 加 ProjectReference |
| 新建（自定义项 M1 最小版） | `Services/UserMenuContributor.cs`（读 `context-menu.custom.*` 配置 → MenuItemDef，含占位符展开+引号包裹）+ UserMenuSpec 模型；编辑 UI 留 M2 |

## 6. 实现顺序（M1）

1. csproj + 契约层（半天）
2. MenuService/MenuHost（复用 WPF ContextMenu 定位语义 + 主题令牌，第一版 MenuHost 可以就是「统一构建 ContextMenu 的服务」而非新弹层——README 开放问题「Popup vs WebView2」在 M1 不决，延后）[inferred]
3. FileClassifier + Unknown 判定链
4. 桌面两场景迁移 + `IContextMenuContributor` 排序
5. `UserMenuContributor`（自定义项最小版，占位符+能力过滤联动）——Contributor 扩展点首验
6. shell-convert：引擎探测链 → DocumentConversionService（先 soffice 子进程路径，后 Office COM STA）→ ConvertToPdfContributor 接入菜单
7. 构建/手测验收

## 7. 验收标准

- [ ] 构建 0 错 0 警（`dotnet build BetterDesktop.slnx`，**先 `Stop-Process -Name BetterDesktop.Host`**；验证产物在 `host/bin/x64/Debug/net8.0-windows10.0.19041.0/`——勿被 bin/Debug 旧副本误导，DiagnosticLog 落盘桌面 `BetterDesktop_debug.log`）
- [ ] 桌面空白/图标右键走 IMenuService，行为与迁移前等价（回归项：右键未选中先单选、粘贴 IsEnabled 刷新、重命名内联）
- [ ] Unknown 文件右键仅显示 定版菜单集；exe 显示管理员运行、lnk 显示打开文件位置；只读文件删除置灰带 tooltip
- [ ] `IContextMenuContributor` Priority 排序稳定；Esc/失焦/再右键幂等关闭
- [ ] 新增 `IFileClassifier` 单测（Unknown/lnk 失效目标/只读/回收站判定）在 kernel-tests 或新 tests 工程跑绿
- [ ] 转换：docx→PDF 产出可打开且页数一致（Office COM 与 soffice 各验一次）；无引擎机器菜单组隐藏；重名输出不覆盖（自动 (2)）；转换中无孤儿 WINWORD/WPS 进程残留；失败经 IEventBus 通知且菜单不崩
- [ ] 自定义项：手编配置添加「用 VS Code 打开文件夹」(`code %dir%`) 与「Hash 校验」(`hash.exe %file%`) → 空白处/文件图标分别命中；含空格路径不撕裂命令行；禁用项不渲染；多选时 `%files%` 正确展开

## 8. 风险

| 风险 | 缓解 |
|---|---|
| MenuHost 形态（Popup/WebView2）未决 | M1 用 WPF ContextMenu 统一服务，形态决策延到 M2，不阻塞 |
| 扩展名关联探测误判（UserChoice/HKCU 优先级） | M1 裸探测只影响菜单项显隐，不阻塞打开动作；M2 换 Assoc API 修正 |
| 迁移回归桌面现有体验 | 保留回退开关 + 逐项对照清单 |
| COM（M2）消息泵崩溃风险 | M1 完全不碰 COM；M2 单独立计划 |
| Office COM 孤儿进程（WINWORD.EXE 锁文件/占内存） | STA 专用线程 + 逐层 ReleaseComObject + 超时 Process.Kill；先 soffice 子进程路径（进程边界天然隔离）验证端到端，再接 COM 引擎 |

## 9. 超越需求建议（beyond，全部可选）

1. `beyond` **verb 本地化**：unified 档用 72 文档的 `DefaultNameIndexs` 内置字典（open→"打开"等 8 项）直接本地化系统 verb，省翻译表 [verified 机制]。
2. `beyond` **发送到/WinX 数据源**：`shell-dock` 已有 Win+X 菜单——其数据可沉淀为「快速链接」服务被菜单场景复用（72-右键菜单 场景表已列 `%LocalAppData%\...\WinX`）。
3. `beyond` **图标提取归一**：`IconHelper/ShellNamespaceHelper` 与 05-图标 501 走归一变体，菜单图标与桌面图标同源缓存。
4. `beyond` **隐藏菜单项管理**（右键菜单管理器式"关闭某第三方项"）依赖 M2 unified 档 + `HideBasedOnVelocityId` 机制，M3 后再议。

## 9.5 技术力复用盘点（全库通览，扫地僧 2026-09-02）

通览 `TECH-KNOWLEDGE/index-ai.md` 全表（72 域 130+ 资产）对照 cordis 现状与右键菜单需求，命中如下：

**A. 直接复用（右键菜单本体，落地即引用）**：
| 资产 | 域 | 用途 |
|---|---|---|
| `flyout-position`（贴靠/翻转/clamp） | 06 | MenuHost 定位：DPI/多屏/屏幕边缘反转 |
| `flyout-nofocus`（NOACTIVATE 无焦点窗） | 06 | 菜单弹层不抢宿主焦点 |
| `flyout-dismiss`（失焦/外部点击/Esc） | 06 | 关闭语义直接对照实现（README 难点 4） |
| `flyout-state-machine`（Cloak 防白闪+去抖） | 06 | M2 MenuHost 独立弹层（若弃 WPF ContextMenu） |
| `icon-extract`/`icon-lru`/`icon-preload` | 05 | 菜单项图标提取+LRU 缓存+可见区预热 |
| `cmd-execute-gbk`（chcp/超时/maxBuffer） | 32 | 自定义项命令执行、soffice 子进程的代码页与超时红线 |
| `search-throttle`（防抖+取消旧任务） | 02 | 动态项（窗口列表）500ms 异步构建取消 |
| `704-config-hotreload`（事务式热更新+回滚） | 07 | `context-menu.custom.*` 配置热加载（编辑 UI 改配置即时生效不炸菜单） |
| `708-error-culture`（稳定错误码+教学文案） | 07 | 转换失败/自定义命令失败的错误分类与提示 |
| `displaystring-localization`(6202) | 62 | 菜单文本本地化（变体 C：ini 外置） |

**B. 模式参照（实现时对照其契约/红线）**：
| 资产 | 参照点 |
|---|---|
| `710-command-system` | MenuItemDef 的 Id/IsAvailable 门控 + 命令执行审计（Opening 事件前门控） |
| `local-engine-orchestration`/`dependency-on-demand`/`pdf-edit-safe-output` | 已并入 §3.5（引擎编排/便携下载/安全输出） |
| `unified-multiformat-export`（67 域 DOCX/PPTX/DOCX 统一导出模型） | M2 doc→docx 互转的目标格式矩阵设计参照 |
| `winx 菜单模型`（72-右键菜单 §扩展点） | shell-dock 已有 Win+X 菜单——数据可沉淀为共享"快速链接"服务 |
| `dwm-live-thumbnail` | Dock/窗口菜单的「窗口 ▶」缩略图列表（shell-dock 已有 DwmThumbnail，对齐其 DPI/Unregister 红线） |

**C. 周边机会（不在本次 scope，登记备查）**：
- `3101-global-hotkey`：菜单项字母快捷键（MENU-SPECS §12 键盘导航）M2。
- `1301-clipboard-history`：桌面「粘贴」增强为剪贴板历史（beyond，独立立项）。
- `506-app-enumeration`/`icon-source-trimodel`：shell-app-source 已消费；菜单图标源可对齐三元模型。
- `709-mef-dependency-load`：若未来要第三方 DLL 热插贡献菜单（现 IPlugin 机制足够，暂不引入）。

**结论**：右键菜单所需机制库内**零缺口**——定位/关闭/图标/热更新/子进程/错误码全有已验证资产，实现阶段按表逐个对照红线，禁止凭记忆重写。

## 10. 决策记录（2026-09-02 全部拍板 ✅）

1. ✅ M1 场景菜单集照单全收（含「转为 PDF」平铺、自定义项）。
2. ✅ Unknown「打开」= `openas` verb（弹系统"你要如何打开"）。
3. ✅ 引擎优先级 **Office COM > WPS > LibreOffice**（保真度优先）。
4. ✅ 便携 LibreOffice 按需下载启用（`dependency-on-demand` 模式，镜像+SHA256；M1 先探测本机已有，下载通道随实现接入）。
5. ✅ 转换输出 = 同目录自动重名序号（零交互，不覆盖）。
6. ✅ 多选 `%files%` = 逐个调用命令（工具单文件语义优先）。
7. ✅ 占位符集合按 §3.6 定版。
8. ✅ 通用基础菜单 + 场景适配架构（§2.5）+ 文字形状验证（§2.6）。
9. ⬜ 工作树未提交改动 commit 时机：开工第一步处理（用户确认）。

**计划状态：定版（v1.0），待 commit 后按 §6 顺序开工。**
