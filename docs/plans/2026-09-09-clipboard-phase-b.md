# Cairo 开发计划 · 剪贴板 Phase B（面板 UI + 热键 + 粘贴增强 + 消费板块 + 配置化）

> **状态（2026-09-10 标注）**：✅ **已实施收口（v1.3 终态）**——面板 UI/热键/自动分段/按类写回/消费板块 I6-I10/按序粘贴/G6 配置化全部落地，单测 85 例全绿。两处实施拍板与本文的差异：① K5 热键改键降级为设置分区**只读展示**（组合键由 Manager 常量锁定，避免热键冲突面失控）；② I9 自绘右键入口随自绘菜单管线退役（2026-09-05），由 I10 系统右键覆盖同语义。终态核销见 `docs/audits/2026-09-10-clipboard-plan-completion-review.md`。

> Task: 在 better-desktop-cordis 完成剪贴板 Phase B 全集并收口 v1.3。**用户确认**：「也做，我们都是 v1.3 版本了」——L 按序粘贴（原规划 v1.1）、I6-I10 消费板块（原规划 v1.1）、G6/K5 配置化（原规划 v1.1）**全部纳入本次 v1.3 实施**，不再推后。前置收口（Pause 单测 + D2 文件捕获真机）按「不留技术债」原则并入本计划。
> 蓝图基线：`docs/plans/archive/2026-09-08-clipboard-history-extension.md`（功能全集 A-N 与版本归属、三方融合矩阵、生死线）；`docs/plans/2026-09-09-clipboard-public-api.md`（Phase A 已实施，契约/服务/接线现状 = 本计划实施起点）。
> 证据基线：HEAD `dffd25e`（2026-09-09 Phase A 收口提交，工作区 clean，门禁 14 道全绿）。技术力命中：**3101-global-hotkey**（C#/L2：0x581/释放纪律）、**3603-菜单栏插入按钮弹窗模式**（L1：IMenuBarExtension/MenuBarPopupWindow 范式）、**windows-context-menu-registry-model**（C#/L2 复合：右键场景路径/命令格式/显示名规则）、**shell-menu-injection**（L1：注入避让/%1/%V/键名唯一）、**3401-windows-input-simulator**（C#/L2：SendInput 门面，粘贴模拟参考）。

## 1. Objective

**产品定位（Phase B）**：把 Phase A 的「契约 + 后台服务」变成**用户可见可操作的完整闭环**——历史面板、全局热键、自动分段粘贴、分类合适粘贴、搜索筛选、多板块入口（菜单栏/自绘右键/系统右键/搜索框）、按序粘贴、容量与热键配置化。v1.3 一次收口，不留 v1.1 尾巴。

用户可感知的结果（场景语言）：
1. **随时召回复制内容**：按 `Ctrl+Shift+V` 光标旁弹出历史面板；搜索、按类型/分类/来源筛选、收藏、删除；单击或 Enter 粘贴（原格式，Ctrl+Enter 纯文本），数字键 1-9 直达，Ctrl+Shift+P 收藏视图，Ctrl+Shift+Backspace 暂停/恢复；多选合并粘贴、拖拽导出。
2. **大段内容一次粘贴全部成功**：网页/Word 复制的大段内容，面板一次粘贴 → 目标应用出现完整、分段、带格式内容；短条目单次粘贴；记事本（无富文本）自动降级纯文本仍全部成功；中途失败即停不产生乱序半截。**用户无感分段过程，零分段 UI**。
3. **复杂混合内容合适粘贴**：代码条目强制纯文本（IDE 不出现富文本乱码）；图文混合写回完整 HTML（Word 图文混排）；表格保留；纯富文本多格式写回目标自选；面板显示分类 chip 并可筛选。
4. **多入口打开面板**：菜单栏新增剪贴板按钮、自绘右键菜单「剪贴板历史…」、系统右键（文件/目录/空白/桌面 4 场景）「BetterDesktop 剪贴板历史」→ 均打开同一面板；搜索框可检索历史/最近复制。
5. **按序粘贴（表单填写）**：勾选 N 条 → 面板触发粘贴下一条（写剪贴板+SendPaste，不抢焦点），服务层状态机可查可取消可重置；配合 Tab/点击逐项填写。
6. **可配置**：设置分区新增「剪贴板」：容量/过期天数/图片上限/热键组合可改，改后即时生效（重启保持）。
7. **Phase A 缺口收口**：暂停/恢复有单测锁定；文件捕获真机补走。

验收标准：§13 DoD（D1-D5/D7-D11 + 前置收口）通过 + 构建 0 警告 0 错误 + 单测全绿 + 门禁 14 道全绿。

## 2. Current Behaviour（[verified]，均基于 HEAD dffd25e）

### 2.1 Phase A 已落地（本计划实施起点）
| 资产 | 现状 |
|---|---|
| `packages/api/Clipboard/`（8 文件） | `IClipboardService` 契约：查询（GetFilteredEntries(kind,category,keyword,sourceApp)/GetSourceApps/GetLastCopiedContent）+ 变更（Pin/Unpin/TogglePin/Delete/DeleteEntries/ClearAllUnpinned/SetEntryTags）+ 粘贴（CopyEntryToClipboard/CopyEntryAsPlainText/PasteEntryToActiveWindow/OpenFileLocation）+ 录入（ImportEntries）+ 状态（IsMonitoringEnabled/PauseTemporarily/Resume/OpenHistoryWindow）+ 3 事件。**v1.1 按序粘贴以新增方法扩展（可加性纪律）** |
| `packages/shell/shell-clipboard/` | ClipboardManager（监听双通道/捕获/去重/隐私/暂停/容量/DPAPI 持久化/**按类别写回基础**：Code→SetText、RichText→多格式、Image→位图、File→FileDropList [verified：306-401]）+ ContentAnalyzer（分类入库 C1/C3）+ ClipboardNative（P/Invoke）+ ClipboardPlugin（观察者，Activate=Start/Deactivate=Stop） |
| 接线 | ExtensionCatalog/ExtensionsCenterWindow Implemented（开关真实启停）、host Bootstrap/cordis.yml/slnx、单测 60 例（11 文件） |
| **Phase B 未做（现状确认）** | 面板 UI（OpenHistoryWindow 打 Warn「Phase B 未接线」[verified：ClipboardManager.cs:542-545]）、热键（RegisterHotkey 无）、自动分段（GetSegments/Segmenter 无）、MergePaste（无）、按序粘贴（无）、消费板块（无）、设置分区（无） |

### 2.2 Phase B 技术力资产（[verified]，均刚读取）
| 文档 | 关键约束（计划引用） |
|---|---|
| 3101-global-hotkey | RegisterHotKey 系统级组合键唯一；0x581 冲突 try 捕获不崩溃；退出必须 Unregister（配对释放）；需要消息泵（本项目 HwndSource 已有） |
| 3603-菜单栏插入按钮弹窗模式 | 复用 `IMenuBarExtension`/`MenuBarPopupWindow`/`SetThemeBinding`；按钮插 `MenuBarStatusStrip`；M10 降级 |
| windows-context-menu-registry-model | 右键场景路径表：`*\shell`（所有文件）/`Directory\shell`（目录）/`Directory\Background\shell`（目录空白）/`DesktopBackground\shell`（桌面）；命令 = `{项}\command` 默认值；显示名 MUIVerb ≤80 字符；HKCU 侧 HKCR 无需夺权 |
| shell-menu-injection | 新键名 `GetNewPathWithIndex` 避让（Item→Item0→Item1…）；参数后缀：文件 `"%1"`、对象 `"%V"`、背景不追加；同场景 KeyName 唯一；`Registry.SetValue` 写默认值需先 CreateSubKey |
| 3401-windows-input-simulator | SendInput 门面参考；本项目 Phase A 已用 keybd_event SendPaste（保留，不换） |

### 2.3 本项目接线点现状（[verified]）
| 消费板块 | 落点 | 现状 |
|---|---|---|
| I6 菜单栏 | `shell-core/Services/MenuBarExtensionRegistry.cs`（IMenuBarExtension 字典）+ `shell-menu-bar/Services/StatusBarMenuBarExtension.cs`（MenuBarStatusStrip 按钮 + MenuBarPopupWindow 弹窗，Search 按钮实证） | Registry.Register(ext)；按钮 Clicked 事件分派 |
| I7 搜索框 | `shell-search/Services/StartMenuSearchService.cs` + `shell-menu-bar/Windows/SearchPopupWindow.cs` | 现成搜索服务/弹窗 |
| I9 自绘右键 | `shell-context-menu/Services/MenuManagerService.cs` + `shell-context-menu/Services/FileClipboard.cs` | 现成菜单构建器 |
| I10 系统右键 | `host/Bootstrap.cs` MenuCommandPipe.StartServer（action 路由：uiKey/desktop/压缩解压等，未知命令 Warn [verified：194-275]） | 加 `clipboard-history` action |
| G6/K5 配置 | `shell-settings/Sections/`（LeftDockSection/SystemSection/ThemeSection） | 加 ClipboardSection |
| J/K 面板热键 | `shell-clipboard/ClipboardManager.cs`（监听窗口 HwndSource 已建，可共用收 WM_HOTKEY）+ `shell-quick-note/QuickNoteLauncher.cs`（ShellWindow/SetThemeBinding 范式） | 面板新建 |

## 3. Relevant Architecture

```
                        ┌────────────────────────────────────────────────┐
                        │  内核服务图 CordisContext（ADR-002 D1）          │
                        │  Provide<IClipboardService>（Phase A 已注册）    │
                        └────────────────────────────────────────────────┘
        ▲  Get<IClipboardService>（消费板块全部经契约，不碰实现）
        │
┌───────┼────────────────────────────────────────────────────────────────┐
│  shell-clipboard（Phase B 增量）                                        │
│  · ClipboardSegmenter.cs（新，纯逻辑：HTML/Text 自动分段，headless 可测）│
│  · ClipboardManager：D3 分段粘贴接入 + D5 MergePaste + L 按序状态机     │
│    + K 热键注册（与监听共用 HwndSource 收 WM_HOTKEY）+ OpenHistoryWindow│
│  · ClipboardHistoryWindow.cs（新，ShellWindow 子类，1423 行移植）        │
│  · ClipboardPlugin：热键/面板生命周期（Activate 注册/Deactivate 注销）   │
└───────┼────────────────────────────────────────────────────────────────┘
        │ 消费板块（v1.3 全部接线，均 Get<IClipboardService>）
        ├── shell-menu-bar（I6：IMenuBarExtension 剪贴板按钮 → OpenHistoryWindow）
        ├── shell-search（I7：SearchPopupWindow 历史检索/最近复制）
        ├── shell-context-menu（I9：MenuManagerService「剪贴板历史…」项）
        ├── host（I10：MenuCommandPipe clipboard-history action + RegistryVerbs 4 场景）
        └── shell-settings（G6/K5：Sections/ClipboardSection 容量/过期/热键配置）
```

- **依赖方向**：shell-clipboard 新增引用 = shell-core（ShellWindow/IMenuBarExtension 不需要——面板自实现）+ shell-settings 可选（配置读 ISettingsService，Phase A 已引用）。**禁止**引用 shell-menu-bar/shell-context-menu（消费板块反向依赖不变）。
- **热键宿主**：`RegisterHotKey` 的 hWnd 用监听窗口已有 HwndSource（同线程 UI Dispatcher，Phase A RunOnUi 已保证）；WM_HOTKEY 进 OnWindowMessage 分派。
- **面板生命周期**：懒创建（首次触发 new），Deactivate/Unload 关闭销毁；失焦 Hide 不销毁（保留滚动/搜索状态）；主题走 SetThemeBinding。
- **按序粘贴状态机**：服务层（ClipboardManager）持 `SequentialPasteSession`（Current/Remaining/IsActive/Complete/Cancel/Reset，纯逻辑可测）；`IClipboardService` 新增方法（可加性纪律：`BeginSequentialPaste(IReadOnlyList<ClipboardEntry>)`/`PasteNextSequential()`/`CancelSequentialPaste()`/`IsSequentialPasteActive`）。

## 4. Technical-Knowledge Findings
见 §2.2（5 份文档命中）+ Phase A 已回写的 1301 C# 变体（Phase B 完成后按 §12 D6 再补「面板/热键/分段/消费板块」增量）。

## 5. Constraints & Invariants（生死线，沿用归档 + Phase A + Phase B 新增）

**沿用（归档 §5 + Phase A §5，全部继续有效）**：写回抑制令牌、捕获判据不靠立即读、热键配对释放（Phase B 强化）、隐私黑名单、DPAPI+图片元数据化、扩展中心契约只增不改、公共契约层纪律、**大段粘贴原子性无感（归档 §5-7 五条）**、**分类正确性（归档 §5-8 四条）**。

**Phase B 新增**：
1. **热键三键配对释放**：RegisterHotkeys（Ctrl+Shift+V/P/Backspace 三 id）与 UnregisterHotkeys 严格配对——Activate 注册 / Deactivate 注销（3101 红线 2）；任一注册 0x581 → `context.Logger.Warn` + 该键禁用（不崩溃），其余键继续；Deactivate 后 Unload 不再重复注销（幂等）。
2. **分段粘贴零 UI + 失败即停**（归档 §5-7 强化）：不新增任何分段/选段 UI；HTML 片段必须经 `ClipboardNative.SetHtmlText` CF_HTML 头包装；片段自包含（内联标签配对补齐）；深层嵌套无法安全切分 → IsFallback 降级纯文本；某段失败即停（保持已粘部分、日志留痕）；RTF 条目禁止分段整段带格式粘贴。
3. **面板单实例 + 失焦不销毁**：OpenHistoryWindow 幂等（已开则置顶/刷新焦点）；Deactivated → Hide（保留状态）；Unload/Deactivate 时 Close 销毁。
4. **消费板块不绕服务**：菜单栏按钮/自绘右键/系统右键/搜索框入口一律 `Get<IClipboardService>().OpenHistoryWindow()`，禁止直接 new 面板；I9 FileClipboard 剪切语义不破坏（复制→监听天然进历史；剪切标记 cut 字段不动，保持 Phase A 现状）。
5. **I10 注册可撤销 + 避让**：HKCU 侧 `Software\Classes` 下 4 场景；键名 `BetterDesktopClipboardHistory` 静态名 + `GetNewPathWithIndex` 避让（shell-menu-injection 红线 1）；命令 `BetterDesktop.exe --menu-cmd clipboard-history`（对象场景 `"%V"`，文件场景 `"%1"`，背景不追加——按 72 域红线 2）；注册/注销幂等；**不写 HKLM**（免夺权）。
6. **设置分区只读契约键**：容量/过期/图片上限/热键读 `ISettingsService`（`extensions.clipboard-history.capacity` 等，默认值与 Phase A 常量一致：10000/200/5MB/200MB/90 天）；热键默认 `Ctrl+Shift+V/P/Backspace`；改键即时生效 = 重启热键注册（先注销再注册，3101 纪律）。
7. **按序粘贴不抢焦点**：只写剪贴板 + SendPaste 到当前前台窗口，不切换焦点；状态机纯逻辑可测；粘贴完成/取消触发 HistoryChanged 之外的独立事件或属性查询（不加新事件——契约可加性下用属性查询即可）。

**验收标准**：§13 全部 DoD + 门禁 14 道 + 构建 0 警告 0 错误 + 新增单测全绿 + 消费板块零新增反向依赖（grep 验证 shell-menu-bar/shell-context-menu 无新引用 shell-clipboard）。

## 6. Proposed Changes

### 6.0 前置收口（Phase A 缺口，先做）
- **PauseTemporarily/Resume 单测**（shell-clipboard-tests 新增 `ClipboardPauseTests.cs`，2 例）：暂停期 OnClipboardUpdate 不触发 HistoryChanged/不写历史；Resume 后恢复记录。
- **D2 文件捕获真机补走**（实施完成后随 D2 走查执行）：资源管理器复制文件 → Files 条目 → OpenFileLocation 定位。

### 6.1 `packages/shell/shell-clipboard/ClipboardSegmenter.cs`（新，纯逻辑，headless 可测）
- `HtmlSegmenter.Split(string html) → IReadOnlyList<HtmlSegment{string Content; bool IsFallback}>`：按块级标签（`</p>`/`</div>`/`</li>`/`<br>`/`</tr>`/`</h1-6>`）切分；内联标签白名单（b/i/u/strong/em/span style/font/a/code/sub/sup）保留；剥离块级标签时补齐内联配对缺口（自包含）；解析失败/嵌套无法安全切分 → IsFallback=true（降级纯文本）；**不产出损坏 HTML**。
- `TextSegmenter.Split(string text)`：按空行（`\n\s*\n`）拆段（仅段落模式），去首尾空行；无空行单段不拆。
- RTF 条目 `GetSegments` 返回空（不分段）。
- 片段不二次包装 CF_HTML 头（统一由 `ClipboardNative.SetHtmlText`）。

### 6.2 `ClipboardManager.cs` 改动（Phase B 增量）
- **D3 自动分段接入**：`PasteEntryToActiveWindow` 入口检测「大段」（HTML ≥2 块级段 / 纯文本 ≥2 空行段）→ 分段逐段 `CopySegmentToClipboard`（HTML→SetHtmlText / Text→SetText，写前置抑制令牌）+ SendPaste，段间 250ms；单段/短条目走原单次路径；某段失败即停（终止后续段 + 日志）；`GetSegments`/`CopySegmentToClipboard` internal。
- **D5 多选合并**：`MergePasteToActiveWindow(IEnumerable<ClipboardEntry>, string separator)`（契约新增；分隔符默认换行，写合并文本一次粘贴）。
- **K 热键**：`RegisterHotkeys()/UnregisterHotkeys()`（Start/Stop 挂载，幂等）；与监听共用 HwndSource 收 WM_HOTKEY；Ctrl+Shift+V=OpenHistoryWindow、Ctrl+Shift+P=面板收藏视图（`ShowFavoritesOnly` 状态位）、Ctrl+Shift+Backspace=PauseTemporarily/Resume 切换；0x581 → Logger.Warn + 该键禁用。
- **L 按序粘贴状态机**（服务层）：`BeginSequentialPaste(IReadOnlyList<ClipboardEntry>)`（非空校验、快照队列、置 Active）、`PasteNextSequential()`（写剪贴板+SendPaste+前进；队列空→Complete）、`CancelSequentialPaste()`、`ResetSequentialPaste()`；`IsSequentialPasteActive` 属性 + `SequentialRemaining` 计数（供 UI 显示进度）。
- **J 面板挂载**：`OpenHistoryWindow()` 真实现（懒创建 ClipboardHistoryWindow、单实例、置顶/焦点）、`ShowFavoritesOnly` 支持、`CloseHistoryWindow()`（Deactivate 用）。
- 常量组 G 组补：`HotKeyIds`（V=0x01/P=0x02/Backspace=0x03）、`SequentialPasteDelayMs=250`。

### 6.3 `packages/shell/shell-clipboard/ClipboardHistoryWindow.cs`（新，ShellWindow 子类，1423 行移植）
- 基类 `ShellWindow`（quick-note 同款：构造 `(IAppearanceService?, IVibrancyService?)` + `SetThemeBinding` 令牌 + NullVibrancy 降级）。
- `ShowAtCursor()`：光标处 + WorkArea clamp（归档 1596 行 ShowAtCursor 逻辑）。
- 保留（迁移源现成）：搜索框 + 类型筛选 chips（全部/文本/图片/文件/收藏）+ **分类筛选 chips（文字/代码/富文本/图片/文件，混合显示「富文本·图/表」）**（C2/C4）+ 来源筛选栏（Top8）+ 日期分组头 + 条目行（图片缩略图 56px 异步解码自 ImagePath / 文件图标 / 文本图标；预览 + 分类 chip + 来源 emoji + 复制次数 + 相对时间；收藏⭐/删除）+ 右键菜单（粘贴/纯文本/收藏/编辑标签/删除/打开位置）+ 键盘导航（↑↓/Home/End/Enter/Ctrl+Enter/Del/P/T/O/数字 1-9/Esc）+ 多选合并粘贴（复选 + 分隔符 chips）+ 拖拽导出 + 空状态 + **暂停指示条**（J4）+ 暂停按钮。
- 订阅 `manager.HistoryChanged`（Dispatcher.BeginInvoke 刷新）；`OnDeactivated → Hide`；`Unloaded` 解绑。
- 依赖注入：构造 `(ClipboardManager manager, IAppearanceService? appearance, IVibrancyService? vibrancy)`。

### 6.4 `ClipboardPlugin.cs` 改动
- Activate：`manager.Start()`（Start 内注册热键）→ 面板在热键触发时懒创建（不经插件）。
- Deactivate：`manager.Stop()`（Unregister 热键 + 停监听 + FlushSave + CloseHistoryWindow）。
- UnloadAsync：`manager.CloseHistoryWindow()` 兜底。
- 读取热键/容量配置（G6/K5）：Start 前从 ISettingsService 读 `extensions.clipboard-history.hotkey-*`/`capacity` 等覆盖默认。

### 6.5 G6/K5 设置分区 `shell-settings/Sections/ClipboardSection.cs`（新）
- 照 LeftDockSection/SystemSection 分区范式；字段：容量（10000）、收藏上限（200）、单图上限 MB（5）、总量预算 MB（200）、过期天数（90）、热键三组合（Ctrl+Shift+V/P/Backspace）。
- 变更写入 ISettingsService（`extensions.clipboard-history.*` 键）；保存后发 `ShellEvents.SettingsChanged`（插件已订阅 key 匹配 → 热键重启/常量更新——常量改实例字段，去 static 化 G 组为实例配置）。
- 分区在 SettingsPlugin 注册列表追加（照 SystemSection 注册点）。

### 6.6 消费板块（I6/I7/I9/I10）
- **I6 菜单栏**：新 `packages/shell/shell-menu-bar/Extensions/ClipboardMenuBarExtension.cs`（IMenuBarExtension：`Id="clipboard-history"`、MenuBarStatusStrip 按钮 + MenuBarPopupWindow 弹窗内嵌历史面板宿主或直接调用 `OpenHistoryWindow()`——**决策：按钮点击直接 `context.Get<IClipboardService>()?.OpenHistoryWindow()`，面板独立（复用 J 面板），不重复实现弹窗**；注册进 MenuBarExtensionRegistry（照 Search 扩展注册点））。
- **I7 搜索框**：`SearchPopupWindow` 底部/侧栏加「剪贴板最近复制」区块：`Get<IClipboardService>()?.GetLastCopiedContent()` 显示最近条目 + 点击 → `OpenHistoryWindow()`；搜索框输入命中历史（可选，v1.3 只做最近复制区块，完整检索面板内已有）。
- **I9 自绘右键**：`MenuManagerService` 文本/文件场景合适位置追加「剪贴板历史…」项 → `Get<IClipboardService>()?.OpenHistoryWindow()`；FileClipboard 复制/剪切语义零改动（Phase A 命名空间遮蔽已修，不回归）。
- **I10 系统右键**：新 `packages/shell/shell-clipboard/RegistryVerbs/ClipboardRegistryVerb.cs`（或放 host，**决策：放 shell-clipboard，命名空间 BetterDesktop.Shell.Clipboard.RegistryVerbs**）——4 场景（`*\shell`/`Directory\shell`/`Directory\Background\shell`/`DesktopBackground\shell`）注册/注销幂等；命令 `BetterDesktop.exe --menu-cmd clipboard-history`；参数后缀按场景（72 域红线）；`GetNewPathWithIndex` 避让；菜单文本「BetterDesktop 剪贴板历史」（≤80 字符）。`host/Bootstrap.cs` MenuCommandPipe action 加 `clipboard-history` → `Get<IClipboardService>()?.OpenHistoryWindow()`（未知命令 Warn 分支前）。

### 6.7 单测新增（shell-clipboard-tests）
| 文件 | 用例 |
|---|---|
| ClipboardPauseTests.cs | 暂停期不记录；Resume 恢复（2 例，前置收口） |
| ClipboardSegmenterTests.cs | HTML 块级切分正确/内联保留/配对补齐/嵌套降级 IsFallback；文本空行拆段/去首尾/单段不拆；RTF 空（不分段）；片段无 CF_HTML 头（~8 例） |
| ClipboardAutoPasteTests.cs | HTML ≥2 块级段 → 分段路径；文本 ≥2 空行段 → 分段；单段走单次；某段失败即停（~5 例，经测试接缝） |
| ClipboardMergePasteTests.cs | 多选合并内容/分隔符/空列表（~3 例） |
| ClipboardHotkeyTests.cs | 注册/注销幂等；重复注册 0x581 捕获不崩溃；Deactivate 后无残留（~4 例，RegisterHotKey 失败路径经 P/Invoke 接缝或条件跳过——**真机为主，单测覆盖状态机**） |
| ClipboardSequentialPasteTests.cs | 状态机：Begin 快照/Next 前进/Complete/Cancel/Reset/空列表校验（~6 例，纯逻辑） |

## 7. Impact Analysis
- **契约可加性**：IClipboardService 新增 `MergePasteToActiveWindow`/`BeginSequentialPaste`/`PasteNextSequential`/`CancelSequentialPaste`/`ResetSequentialPaste`/`IsSequentialPasteActive`/`SequentialRemaining`（+`ShowFavoritesOnly`/`CloseHistoryWindow` 面板控制）——**只增不改**，Phase A 消费方零破坏。
- **依赖方向**：shell-menu-bar/shell-search/shell-context-menu 新增 `ProjectReference BetterDesktop.Api`（消费契约，零反向依赖）；shell-settings 分区零依赖（读 settings 键）。
- **热键系统级副作用**：Ctrl+Shift+V/P/Backspace 全局占用；0x581 降级日志；设置页可换键（K5 化解）。
- **性能**：面板懒创建；缩略图异步解码；分段粘贴仅大段触发；状态机纯内存。
- **前置收口**：Pause 单测补齐测试空白；D2 真机补走。

## 8. Testing Plan（内核逻辑单测，测非 UI）
§6.7 六文件 ~28 例新增 + Phase A 60 例回归 + 门禁（覆盖棘轮含新项目——shell-clipboard-tests 覆盖要求按门禁现状）。

## 9. Build & Verification
- `dotnet build BetterDesktop.slnx`（0 警告 0 错误，TreatWarningsAsErrors）。
- `dotnet test BetterDesktop.slnx`（新增全绿 + 回归）。
- `pwsh -NoProfile -ExecutionPolicy Bypass scripts/run-gates.ps1`（14 道全绿）。
- 反向依赖检查：`rg "shell-clipboard" packages/shell/shell-menu-bar packages/shell/shell-search packages/shell/shell-context-menu packages/shell/shell-settings`（应仅 csproj 的 BetterDesktop.Api 引用，无 shell-clipboard 引用）。

## 10. Rollout
1. 6.0 前置收口（Pause 单测）→ 6.1 Segmenter（纯逻辑先行可测）
2. 6.2 Manager 增量（D3 分段 → D5 合并 → K 热键 → L 状态机 → J 挂载）→ 6.4 Plugin 生命周期
3. 6.7 单测随 Manager 同步写 → 构建 + 门禁
4. 6.3 面板 UI 移植（1423 行）→ 6.5 设置分区 → 6.6 消费板块（I6→I7→I9→I10）
5. §13 全量真机走查（含前置 D2 文件捕获、D7 分段、D8 分类、D10 消费板块、D11 配置化）
6. 技术库回写（§12 D6）

## 11. Risks
| 风险 | 等级 | 缓解 |
|---|---|---|
| 1423 行 UI 移植量大、WPF 版本差异 | 高 | 以迁移源为骨架照搬语义；基类改 ShellWindow + SetThemeBinding；失焦 Hide；迁移源与实际行数以源码为准（现 1423 行非归档记 1596） |
| 热键冲突（0x581） | 中 | try/catch Warn + 该键禁用不崩溃；K5 设置页换键 |
| 分段粘贴损坏 HTML | 高 | Segmenter 自包含保证 + IsFallback 降级 + CF_HTML 头统一包装 + ClipboardSegmenterTests 锁定 |
| 消费板块反向依赖 | 中 | 消费方只引 BetterDesktop.Api；门禁 grep 检查 |
| I10 注册表残留 | 低 | HKCU 可撤销；注册/注销幂等；避让键名 |
| 按序粘贴焦点错乱 | 中 | 只写剪贴板+SendPaste 不抢焦点；状态机纯逻辑可测 |
| 与 Phase A 常驻服务交互（面板/热键生命周期） | 中 | RunOnUi 统一 marshal；单实例面板；Deactivate 全释放 |

## 12. Open Questions & Deferred
1. **I7 搜索框深度**：v1.3 只做「最近复制」区块 + 点击开面板；完整历史检索面板内已覆盖，搜索框不做全量检索（克制）。
2. **I8 状态栏指示器**（可选）：v1.3 不做。
3. **C5 语言识别 / E5 搜索历史 / F4 敏感内容识别**（可选）：不做。
4. **D6 技术库回写**：完成后更新 1301 C# 变体补「面板/热键/分段/消费板块/设置分区」增量；按「技术力积累」skill 执行。
5. **hash 去重加速缓存**：量级增大再引入。
6. **LMDB（H5）**：万条级 JSON 仍够用，不做。

## 13. Definition of Done
- **D1 端到端主场景（真机）**：启动 → 扩展中心开开关 → 复制 "abc" → Ctrl+Shift+V 面板弹出且首条 "abc" → 记事本 Enter 粘贴成功 → 再复制 "abc" 条目数不变（去重）→ 关开关 → Ctrl+Shift+V 无响应、复制无新增、服务仍可 Get。
- **D2 类型场景（真机，含前置收口）**：截图复制 → 面板图片缩略图 → 粘贴回 mspaint；图片落盘 PNG + JSON 无 base64；**资源管理器复制文件 → Files 条目 → 打开文件位置选中**；CopyEntryToClipboard 后目标应用粘贴成功。
- **D3 隐私/暂停场景（真机）**：复制密码管理器窗口内容 → 面板无该条目；面板暂停按钮/Ctrl+Shift+Backspace → 暂停条显示、60s 内不记录 → 恢复后正常。
- **D4 持久化（真机）**：重启后开关状态/历史/热键配置保持；存储 CBENC1 头；图片缩略图可显示。
- **D5 构建门禁**：0 警告 0 错误；单测全绿（60+28）；门禁 14 道全绿；反向依赖检查通过。
- **D7 大段粘贴（真机）**：网页多段加粗富文本 → Enter 一次粘贴 → Word 完整分段加粗保留（无感）；记事本同条目 → 全部进入（降级纯文本）；纯文本多空行段 → 依次进入；短条目单次。
- **D8 分类合适粘贴（真机）**：IDE 复制代码 → 面板「代码」chip → 粘贴 IDE 纯文本缩进完整；网页图文混合 → 「富文本·图」→ Word 图文混排；表格 → 表保留；分类筛选只显该类。
- **D9 服务契约（真机）**：`Get<IClipboardService>()` 非 null；`GetLastCopiedContent()` 返回最近复制；按序粘贴：勾选 3 条 → PasteNext×3 → 前台文档依次出现 3 条 → Cancel/Reset 生效。
- **D10 消费板块（真机）**：菜单栏剪贴板按钮 → 面板；自绘右键「剪贴板历史…」→ 面板；系统右键 4 场景（文件/目录/目录空白/桌面）→ 命令 → `--menu-cmd clipboard-history` → 面板；搜索框显示最近复制 → 点击开面板。
- **D11 配置化（真机）**：设置分区改热键 → 新组合生效旧键失效；改容量/过期 → 重启保持。

## 14. Handoff（交接给「技术力应用」skill）

### 注入清单（实现前必读）
1. 技术库：`3101-global-hotkey.md`、`3603-菜单栏插入按钮弹窗模式.md`、`72-右键菜单/windows-context-menu-registry-model.md`、`72-右键菜单/shell-menu-injection.md`、`3401-windows-input-simulator.md`（均刚读取，路径见 §2.2）。
2. 迁移源：`cairoshell-master/Cairo Desktop/CairoDesktop.MenuBar/Clipboard/ClipboardQuickAccessWindow.xaml.cs`（**现 1423 行**，UI 移植主体）、`ClipboardQuickAccessService.cs`（188 行，热键模式参考）。
3. 本项目范式：`shell-quick-note/QuickNoteLauncher.cs`（ShellWindow/SetThemeBinding/NullVibrancy/RunOnUi）、`shell-menu-bar/Services/StatusBarMenuBarExtension.cs`（IMenuBarExtension 范式）、`shell-settings/Sections/SystemSection.cs`（分区范式）、`host/Bootstrap.cs`（MenuCommandPipe action 路由）。
4. Phase A 现状（必须重读，勿凭摘要）：`shell-clipboard/ClipboardManager.cs`（粘贴 306-401、Pause 497-520、OpenHistoryWindow 542-545、Start/Stop 556+）、`ClipboardPlugin.cs`（Activate/Deactivate/RunOnUi）、`packages/api/Clipboard/IClipboardService.cs`（契约现状）。

### 模式判定与适配参数
- **模式**：成熟工程增量 + 大块 UI 移植（无匹配库文档 → 工程代码权威）；公共契约可加性扩展（新增方法不动既有成员）；热键/右键/设置分区走技术库 L2/L1 文档 + 工程范式。
- **适配参数**：热键 Ctrl+Shift+V(0x01)/P(0x02)/Backspace(0x03) 共用监听 HwndSource；分段触发 = HTML ≥2 块级段 / 文本 ≥2 空行段，段间 250ms，RTF 不分段，失败即停；面板类名 `ClipboardHistoryWindow`（ShellWindow 子类，ShowAtCursor + WorkArea clamp，失焦 Hide）；设置键 `extensions.clipboard-history.{capacity,pinned-limit,max-image-mb,max-total-image-mb,retention-days,hotkey-panel,hotkey-favorites,hotkey-pause}`；I10 场景 `*\shell`/`Directory\shell`/`Directory\Background\shell`/`DesktopBackground\shell`、键名 `BetterDesktopClipboardHistory` + 避让、命令 `"<exe>" --menu-cmd clipboard-history` + 场景参数后缀；测试程序集不变（BetterDesktop.Shell.Clipboard.Tests）。

### DoD 核销表（实现方逐项自检后由验收方确认）
| # | 核销项 | 结果 |
|---|---|---|
| 1 | 前置收口：ClipboardPauseTests 2 例全绿；D2 文件捕获真机通过 | ☐ |
| 2 | 6.1 ClipboardSegmenter + ClipboardSegmenterTests 全绿（8 例） | ☐ |
| 3 | 6.2 Manager 增量（D3 分段接入/D5 MergePaste/K 热键/L 状态机/J 挂载）+ AutoPaste/MergePaste/SequentialPaste 测试全绿 | ☐ |
| 4 | 6.3 ClipboardHistoryWindow 移植完成（搜索/筛选/分类 chip/暂停/键盘导航/多选合并/拖拽） | ☐ |
| 5 | 6.4 Plugin 热键/面板生命周期（Activate 注册/Deactivate 注销/CloseHistoryWindow） | ☐ |
| 6 | 6.5 ClipboardSection 设置分区（容量/过期/热键，改后即时生效 + 重启保持） | ☐ |
| 7 | 6.6 消费板块：I6 菜单栏按钮 + I7 搜索框最近复制 + I9 自绘右键项 + I10 系统右键 4 场景 + host clipboard-history action | ☐ |
| 8 | 构建 0 警告 0 错误 + 单测全绿 + 门禁 14 道全绿 + 反向依赖 grep 检查通过 | ☐ |
| 9 | §13 D1-D5/D7-D11 真机走查通过（含 D2 文件捕获补走） | ☐ |
| 10 | D6 技术库回写（1301 C# 变体补 Phase B 增量） | ☐ |

---

## § 可选增强 / 超越需求建议（beyond，不混入强制 scope）
- **beyond-1 · 分段粘贴进度日志**：分段期间 Logger.Info 每段结果（已有日志留痕需求，可加明细），排障友好。收益：D7 走查失败可定位。
- **beyond-2 · 面板「常用来源」快捷筛选**：GetSourceApps 已按计数排序，面板加一行常用来源 chips。收益：高频来源一键过滤。依赖：现成方法。
- **beyond-3 · 复制次数「常用」排序档**：CopyCount 字段已就绪，筛选 chips 加「常用」。收益：零成本。
- **beyond-4 · 设置分区容量输入校验**：容量 100-50000/收藏 10-500/图片 1-20MB/总量 50-2000MB/过期 1-365 天区间校验，非法输入回退默认并提示。收益：防配置损坏。
