# BetterDesktop.Shell.Clipboard.Panel

剪贴板历史的**入口面**（独立 WPF exe）。数据面（监听/捕获/去重/存储/查询）全部在 Rust 引擎进程内，
本进程只通过命名管道 IPC 消费，**不落盘、不监听剪贴板**。

## 组成

| 文件 | 职责 |
|---|---|
| `App.xaml(.cs)` | 单实例 Mutex + `--open` 命名事件转发；启动引导（主题引导 → 装配入口） |
| `EntryHost.cs` | 按 `extensions.clipboard-history.entry-style` 装配入口（`sidebar`/`orb`/`both`/`off`），持有与引擎的唯一 IPC 连接 |
| `PanelMainWindow.cs` | 完整面板（搜索防抖 300ms、类型/分类 chips、分页加载、暂停横幅、引擎断线横幅） |
| `EdgeHandleWindow.cs` | 侧边栏收纳手柄（右缘「›」，**点击**展开、可拖动移动、位置持久化）—— **默认且唯一入口** |
| `RecentStrip.cs` | 单条渲染（缩略图 / 预览 3 行 / 类型 chip / 来源 / 次数 / 收藏） |
| `EngineProber.cs` / `PanelVibrancy.cs` / `PanelTheme.cs` / `PanelLog.cs` | 引擎探活与拉起 / 毛玻璃材质 / 主题引导 / 诊断日志 |

## 依赖方向

- `BetterDesktop.Shell.Core`：窗口统一基类 `ShellWindow`（含 `PopupWindowBase`）、主题令牌、材质、鼠标钩子。
- `BetterDesktop.Shell.Clipboard.Ipc`：`ClipboardIpcClient`（`IClipboardService` 的 IPC 实现）+ 引擎定位拉起。
- 不引用 `BetterDesktop.Shell.Clipboard`（宿主内旧实现）；三方仅经 `%LOCALAPPDATA%\BetterDesktop\` 约定路径互相定位。

## 构建与部署

```powershell
dotnet build BetterDesktop.slnx -c Debug
pwsh -File scripts/deploy-clipboard.ps1            # 面板 + cargo release 引擎 → %LOCALAPPDATA%\BetterDesktop
pwsh -File scripts/deploy-clipboard.ps1 -StopEngine   # 引擎在跑时先停再部署
```

宿主与系统右键均以 `"<panelExe>" --open` 打开本面板；`--open` 时若已有实例，经命名事件把窗口拉到前台（不产生新进程）。

## 交互入口（2026-09-13 更新）

| 操作 | 行为 |
|---|---|
| 左键单击行 | 复制到剪贴板 |
| **Ctrl + 左键** | 复制为**纯文本**（文件夹 → 路径文字） |
| **中键单击行** | 复制并粘贴回「打开面板时那个窗口」 |
| **Ctrl + 中键** | **临时粘贴**：复制并粘贴，随后把剪贴板**还原**成你原来的内容 |
| 行 📌 / 🎴 | 收藏 / 表情包标记（与收藏同级的独立标记，任意类型可标记） |
| 行左侧 40px 条带 · Space | 多选勾选（序号即按序粘贴顺序） |
| **列表键盘 `T`** | **编辑标签**（空格分隔多个；Enter 保存 / Esc 取消） |
| 行 `⊞`（仅表格条目） | **按格粘**：把 Excel/WPS 表格逐格粘到业务系统（先确认"几行几列"，可选粘完自动 Tab / Enter / 不自动），之后每按一次 Ctrl+V 粘一格 |
| **点击 `#标签` chip** | 按该标签筛选（等价于在搜索框输入 `tag:标签`） |
| 搜索框 `tag:标签` | **只搜该标签**（语义由引擎解析，面板只透传） |
| Esc | 先关标签编辑浮层（若开着），再收起面板 |

**敏感信息**：命中手机号 / 身份证 / 邮箱 / 银行卡 / 密钥的条目，预览会遮罩为 `前三••••后二`
（位数可在设置 → 剪贴板 → 隐私调整），并显示「敏感」徽标；**复制/粘贴出去的仍是全文**。

## Known Limitations

- **多显示器 v1 仅主屏**：手柄与面板位置均取 `SystemParameters.WorkArea`（主屏工作区）。
- **滚轮 = 像素滚动 + 虚拟化回收（两者可兼得）**：`VirtualizingPanel.ScrollUnit=Pixel` 让 `VerticalOffset` 以像素计（可逐帧补间 = 平滑），同时 VirtualizingStackPanel 照常回收视野外容器（实测 25 条与 527 条数据下，ListBox 实际实现容器数都是 **9**，内存不随数据量增长）。**不要**为"滚动平滑"退回 `CanContentScroll=false` —— 那会丢掉虚拟化，只能靠行数硬顶（曾设 600 行上限，等于给用户一堵"没数据了"的假墙，已撤除）。
- **图片必须懒解码 + 滚出即释放**：`RecentStrip` 在进入视口（`Loaded`）时才解码缩略图，滚出（`Unloaded`）即置空 `Source`。虚拟化**只回收容器、不回收内容对象持有的位图**，缺这步万条滚动会累计数百 MB。
- **搜索/筛选全部下沉引擎**：客户端不再本地过滤；`keyword` 为忽略大小写子串匹配（无模糊/分词/拼音）。
- **毛玻璃走宿主同一份实现**（`shell-core/Vibrancy/DwmHelper`，2026-09-14 起面板不再自带 `PanelVibrancy`）：主路径 = `ACCENT_ENABLE_BLURBEHIND`（透亮模糊）；只有它**真失败**（返回 FALSE）才降级 DWM Mica。注意 `SetWindowCompositionAttribute` 返回 **BOOL（非零 = 成功）**——旧代码按 HRESULT 判 0 成功，把成功当失败并每次叠加降级材质（已修，见 `NativeMethodsContractTests`）。
- **悬浮球已删除**（2026-09-12）：`FloatingOrbWindow`/`OrbPanel` 无任何调用点、且继承裸 `Window` 违反统一基类纪律，已移除；历史配置 `entry-style=orb/both` 静默降级为侧边栏。
- **手柄拖动为自实现**（非 WPF `DragMove`）：`DragMove` 会吞掉后续 `MouseLeftButtonUp` 且无法锁水平方向 —— 故用 `CaptureMouse` + 手动位移（垂直自由、水平锁右缘），松手时位移 > 3px 判为拖动、否则判为单击展开。位置存 `%LOCALAPPDATA%\BetterDesktop\clipboard-panel-state.json`（**不写宿主 settings.json**，避免与宿主并发双写互相覆盖）。
- **不要用 `LostMouseCapture` 清拖动态**（2026-09-12 踩坑）：捕获若在按下时即丢失，它会立刻清掉 `_dragging`，随后 `MouseUp` 走进 `if (!_dragging) return;` —— **点击语义被整条吃掉**（症状：点手柄不展开，`popup-trace.log` 无任何新记录）。正解是只在 `MouseMove` 内自愈：`_dragging && 左键已松开` → `ReleaseMouseCapture()` + 复位（同时修掉"点击后移开鼠标被误当拖动、位置被写坏"）。
- **收起方式可配**（2026-09-12）：`dismiss-mode` = `auto`（默认，点面板外/失焦即收起）/ `manual`（仅点「✕」或 Esc 收起）。由 `PopupWindowBase.AutoHideOnOutsideClick` 统一受控**两条**路径（外点钩子 + `Deactivated`），只关一处会漏。手动模式下底部提示显示「手动收起（✕ / Esc）」。改后需重启面板。
- **表情包导入（2026-09-12）**：底部「＋ 表情包」导入本地图片/动图（gif/webp/apng/png/jpg/bmp，可多选）→ 引擎把**原文件字节级复制**进 `clipboard\stickers\`，条目显示**首帧缩略图**与尺寸。粘贴时多格式齐发（`CF_HDROP` 保动画 + `CF_DIB` 首帧兜底）。失败逐条可见（首条上 toast、全量进 `panel.log`）。
  ⚠️ **2026-09-13 起语义已变**：表情包不再是"分类"，而是**标记**（见下一条）；导入只是"创建条目 + 打标记"的一条入口，且命中历史已有条目时**只打标记**。详见 `docs/plans/2026-09-13-sticker-history-dedup.md`。
- **粘贴回原窗口：鼠标中键（默认）＋「可选」用户自定义热键（2026-09-13 最终裁定）**：
  **行中键单击** = 复制并**直接粘贴回"打开面板时那个窗口"** —— 这是**唯一的默认入口**（不必配置、不必记）。
  想要键盘入口的用户，去 **设置 → 剪贴板 → 快捷键 → 粘贴回原窗口** 自己录一个组合键
  （存 `extensions.clipboard-history.paste-back-hotkey`）。
  **热键由面板自己注册、且只在面板可见期间存在**（每次打开面板时读配置 → 注册到面板窗口；
  收起时 `OnBeforeHide` 注销）—— 于是：① 不长期占用全局热键、不与其它程序长期冲突；
  ② 无需为"引擎 → 已运行的常驻面板"新建跨进程事件通道（引擎的全局热键只能 spawn exe，驱动不了运行中的面板）。
  引擎的 3 个内置全局热键（`Ctrl+Shift+V/P/Backspace`）不受影响，两者互不干涉。
  <br>**入口迭代的最终教训**（连续四轮被否，务必记住）：**别替用户猜输入设备与手型**。
  `Ctrl+Enter`（跨键盘两端，单手按不了）→ `Shift+Enter` → `Enter` 单键 → 双击 → 中键，
  最终用户裁定：**"鼠标中间就可以了，如果用户想要热键，让用户自己设置就行了，我们提供入口在设置中。"**
  可用性判据依然是"**一次动作**"，但结论是：**默认入口只留一个，其余交给用户自配**。
  另一条坑：**不要为支持双击而让单击"延迟等一等"**（那会让最常用的单击复制平白多等 250ms）。
  <br>实现要点：
  目标窗口在**点手柄那一刻**记录（`EdgeHandleWindow.ForegroundBeforeOpen`，那时前台还是用户的应用）；
  注入前用 `WindowActivator.Activate`（AttachThreadInput 范式 + ALT 抖动兜底）**显式激活**，
  不再赌"面板 Hide 之后前台恰好落在谁身上"。顺序固定为**先收起 → 再激活 → 最后延迟 120ms 注入**
  （激活异步生效，立刻注入会打进旧前台）。入口用键盘而非双击：用户此前明确否决过双击粘贴，尊重该约定。
- **回环抑制按「内容指纹」而非「次数」（2026-09-13）**：写回剪贴板前登记 `ClipboardEntry::fingerprint()`
  （**先登记后写回**；写回失败则清除），捕获侧构造条目后用**同一函数**比对，命中即吞掉（一次性，2 秒窗口）。
  旧实现是"消费式计数令牌"，在"写回触发 0 次"（令牌残留 → 误吞用户真复制）与"触发多次"（令牌不够 →
  自己写回被记一条）两种真实情形下**必然数错**。三态（无标记/指纹相同/指纹不同）各有测试守门。
- **复制有两条通道：单击 vs Ctrl+单击（2026-09-13）**：
  `单击 = 复制`（带完整格式）；`Ctrl+单击 = 复制为纯文本`（剪贴板里**只有文本**）。
  这是为"文件夹"这类条目准备的 —— 默认复制带 `CF_HDROP`，**文件管理器能粘出完整文件夹**；
  而网页聊天框会把目录**展开成一堆子项**（浏览器按"上传文件夹"处理，与资源管理器自身行为一致）。
  想在那里贴出**路径文字**就 Ctrl+单击：因为剪贴板里**没有 `CF_HDROP`**，
  聊天框的"上传文件"机制**无从触发**，只能拿到文本 —— 这是确定性生效的（实测）。
  **纪律**：纯文本写回必须**按条目类型取字段**（文件/目录在 `filePaths`、图片在 `imagePath`、
  文本在 `content`），否则会粘出空字符串。
- **表情包是「标记」，不是「类型」（2026-09-13）**：`ClipboardEntry.IsSticker` 与 `IsPinned` **同级**，
  任何条目都能被标记（**文字颜文字** / 静态图 / 动图 / 文件），标记只影响两件事：
  ① 出现在「表情包」筛选里（引擎 `query` 的 `sticker` 参数）；② 与收藏一样豁免驱逐与「清理未收藏」。
  **纪律**：
  - **不要**再用 `ContentCategory.Sticker` 判断"是不是表情包"（该枚举值已降级为旧数据兼容的遗留值，引擎不再产生）；
  - 面板行内 🎴/😀 按钮 → `SetSticker`；预览形态用「内容类型 + 标记」**共同**判定（文字表情包走文本预览）；
  - 导入一张**已在历史里**的图 → 引擎**只打标记**（返回 `upgraded`），**不新增条目、不改条目类型**；
  - 新增任何"用户可对任意内容表达的意愿"时，一律做成**标记**而非分类 —— 分类只能描述内容，标记才能跨类型。
  详情见 `docs/plans/2026-09-13-sticker-history-dedup.md`。
- **UI 收敛纪律（2026-09-12 · 多轮功能追加后的整体整合）**：面板历经 8+ 轮功能追加，做过一次针对性收敛，**以下三条是红线**：
  ① **状态只有一条原子通道** —— 新增任何"提示/横幅"都必须走 `PanelMainWindow.SetStatusLane(意图)` + `UpdateStatusSlot()` 按优先级裁决，
     **禁止**在自己的逻辑里直接改控件 `Visibility`（旧实现 5 处各改各的 → 4 条横幅可同时全显、垂直堆叠把列表下推，
     甚至"引擎未连接"与"暂无剪贴板历史"互相矛盾地同时显示）；新增状态类型只需加 `StatusLane` 枚举值 + 排位，**不必动布局行号**。
  ② **一切尺寸/字号/圆角/强调色透明度走 `PanelTheme.Scale`** —— 就地写数值即回归（曾散出 FontSize 10/11/12/13/15、
     水平内缩 14 与 10、圆角 4/6/7/8/10/12、同一语义强调色 5 档透明度）。
  ③ **可交互元素一律键盘可达、且不依赖 hover** —— 行内操作/勾选圈可见条件为 `IsMouseOver ∪ IsKeyboardFocusWithin`；
     图标按钮带 `AutomationProperties.Name`；列表项 `ToString()` 必须是**条目摘要**
     （WPF 的 `ListBoxItem` 用 `Content.ToString()` 当自动化名，只给内容控件设 Name **无效**，读屏会念出类型全名）；
     Tab 用 `KeyboardNavigationMode.Once` 把列表当一个站点。开关类 chip 需可聚焦 + Enter/Space 可操作。
     详见 `docs/plans/2026-09-12-clipboard-panel-ui-consolidation.md`。
- **视觉收敛（2026-09-15 · 视觉 pass）**：在 09-12 三条红线之上补四条，都是**踩过才知道**的：
  ① **令牌必须先在 `PanelTheme` 里定义才准引用**。面板是独立 exe，只复刻了宿主 `AppearanceService`
     推送的令牌子集；`SetResourceReference(key)` 在 **key 不存在时静默解析成 null**。实测后果：
     `_toast`（复制/收藏/删除的反馈）前景解析为 null → 退回默认**黑字** → 深色底上等于看不见；
     状态卡与多选操作条的 `BorderStrokeAccent` 描边全部丢失。已在 `ApplyToAppResources` 补齐
     `StatusSuccess/Warning/Danger` + `BorderStroke/Subtle/Accent`（取值对齐 host/App.xaml；
     描边在浅色模式改黑，否则白描边不可见）。**今后任何新令牌键，先定义再引用。**
  ② **筛选条必须能换行**。8 个 chip 在 420px 面板里的合计宽度超过可用宽度（388px），
     而水平 `StackPanel` **既不换行也不裁剪**，末尾的「收藏」被顶到面板外（用户根本点不到）。
     已改 `WrapPanel` + 收紧 chip 内边距/字号（12px 内边距 + FontBody 会必然溢出）。
  ③ **状态切换一律 150ms 过渡**（`PanelTheme.Scale.MotionFastMs` + `PanelUi.MotionEase()`）。
     行底色与按钮 hover 都由 `ColorAnimation` 驱动，禁止 0ms 瞬变；
     动画起点必须**先 Blend 成不透明色**（半透明色之间插值会插出"中途变淡"的假象），
     且画刷必须**逐行/逐按钮自带**（用共享令牌画刷做动画会改到所有元素并永久改写令牌）。
  ④ **禁止用 emoji 当图标**：`★ 已收藏` / `😀 表情包` 已换成 Segoe MDL2（E718 / E90E），
     与底部操作区同一字形（emoji 是彩色字形，字体回退下样子不可控）。
  <br>同批修正：`ThemeMutedForeground`（深色）由 `#E2E2E6` → `#A0A0A6` —— 原值与正文前景
  `#F2F2F2` 几乎同亮，元信息与预览正文**没有层级**（新值在 `#222226` 底上对比度 ≈6:1）；
  条目行新增「当前操作目标」态（`ListBoxItem.IsSelected` 驱动，不是自维护标志位 —— 容器会被虚拟化复用，
  标志位会变成"上一行的状态"）；时间戳改为**独立右对齐列**（留在信息组里时，来源名一长整行就溢出被裁成
  `26 分…`）；「N 次复制」只在 ≥2 次时显示（每条都印"1 次复制"等于没印，还挤掉真正有意义的重复信息）；
  空状态补大图标（原先只有两行小字浮在大片空白里，看起来像加载失败）。
  <br>**验证方式（本包无单测，视觉只能真机走查）**：`dotnet build <面板 csproj> -c Debug -p:Platform=x64`
  → 停掉运行中的面板 → 拷到 `%LOCALAPPDATA%\BetterDesktop` → 用 `--open` 拉起后截图。
  ⚠️ 截图脚本**必须显式声明 DPI 感知**（`SetProcessDPIAware`）：否则 `GetWindowRect` 返回被系统
  虚拟化过的坐标，位图比真实窗口小、内容被裁掉右侧，会把"右侧被裁"误判成"布局溢出"（本轮踩过）。
- **主题推导已上提到 shell-core（2026-09-15）**：`PanelTheme` 现在**只读剪贴板扩展自己的键**
  （enabled / entry-style / capacity / dismiss-mode / 热键 / 敏感遮罩），`appearance.*` → App.Resources
  的令牌推导、设计刻度 `Scale`、`Blend` 等全部移到 `shell-core/Surface/EntryTheme`（截图入口面
  `BetterDesktop.Capture` 要用**同一份**推导与同一套数字）。面板侧仍以 `PanelTheme` 作为扩展配置入口，
  主题相关调用点改为 `EntryTheme.*`；启动装配顺序 = `PanelTheme.Load()`（内部转调 `EntryTheme.Load()`）
  → `EntryTheme.ApplyToAppResources()` → `SlimScrollBar.Install(this)`（顺序不可颠倒，滚动条模板引用令牌）。
  同批：面板与截图共用 `shell-core/Surface/SlimScrollBar`（无箭头 6px 拇指细滚动条），历史列表的默认
  系统滚动条一并替换。另注意：`PanelUi.BuildSurfaceButtonTemplate` 现在把 `Button.Padding` 转发到 Border
  ——自定义 `ControlTemplate` **不会**自动应用 `Padding`（自带模板是靠 `Margin="{TemplateBinding Padding}"`），
  删了这行按钮文字就会贴边。
- **按序粘贴的队列是「内容快照」**（2026-09-12，用户实测"选了按序粘贴它会自己释放"的根治）：点「按序粘贴」时逐条抓全文存入队列，条目随后被删除/「清理未收藏」/迁移合并都不再影响已排好的序（引擎写回失败会自动降级为本地写回，HTML 走标准 CF_HTML 包装）。失效与降级会在状态条留提示、绝不静默跳过；行内删除（`DropSelection`）与「清理未收藏」（`ClearSelection`）会同步剔除勾选，避免勾选里残留死条目。
- **`shell-clipboard` 包内的旧面板**（`ClipboardHistoryWindow`）已于 2026-09-14 随宿主内 legacy 实现一并删除，本包是唯一面板实现。
