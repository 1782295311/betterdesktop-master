# 灵动岛媒体卡片升级（对齐声音面板 + 歌词模式）· 实施计划（2026-09-16）

> 状态：**计划就绪，待实施**。用户口径（2026-09-16）："音乐播放器的控制界面太弱了点，要和声音独立界面那个一样，甚至要能够切换到歌词显示这样的功能。"

## 1. 本轮 Objective

把岛的**展开态媒体卡片**从"标题 + 两个动作芯片"升级为**与 `SoundPanelWindow`（菜单栏声音独立面板）同档次**的播放控制面，并提供**控制 / 歌词**两态切换（歌词态显示当前曲目歌词并跟随进度高亮）。

## 2. 现状锚点（源码核实）

| 面 | 事实 |
|---|---|
| 岛媒体来源 | `shell-island/Sources/MediaActivitySource.cs`：1 s 轮询 `GetActiveSessionAsync` + 签名比对 → Post 一条 Sticky/Media 活动（Title=曲名、Body=`歌手 · 专辑`、Actions=上一首/播放暂停/下一首） |
| 岛渲染能力 | `IslandWindow`：头部行（字形 + 标题 + 进度环 + 百分比）+ `_detail`（副标题 + `_actions` 芯片行）；宽度区间收起 132~268 / 展开 244~384 DIP；**没有进度条、没有图片位、没有多行/多页布局** |
| 数据是否够 | 够。`MediaPlaybackSnapshot`（api/Music）已含 `Position/StartTime/EndTime/CanSeek/IsShuffle/IsRepeat/CanShuffle/CanRepeat/ThumbnailRef`；`MediaPlaybackCommand` 含 `Seek/ToggleShuffle/ToggleRepeat` |
| 歌词数据源 | `api/Music/IKugouMusicApi.cs`（shell-music 已实现：search → getSongInfo → krcs 歌词链）→ 按"曲名 + 歌手"在线取词 |
| 参照面 | `shell-menu-bar/Windows/SoundPanelWindow.cs`（40 KB）：音量主胶囊 + 设备列表 + 行内滑条/按钮的统一视觉语言；媒体卡片应复用同一套令牌与控件观感 |

## 3. 设计

### 3.1 展开态媒体卡片（控制页）

自上而下三行，宽度上限放宽到 **384 DIP**（与展开态一致，不再加宽，避免遮挡桌面）：

1. **曲目行**：封面缩略图（36×36 圆角 8，`ThumbnailRef` 异步取流；无封面用音符字形占位）＋ 曲名（Medium，单行省略）＋ 歌手 · 应用名（次级色，单行省略）。
2. **进度行**：可拖动进度条（`CanSeek` 决定是否可交互，拖动中暂停轮询回填防抖）＋ 左/右时间标签（等宽数字，`mm:ss`）；`EndTime` 未知时整行隐藏（而不是显示 0:00）。
3. **控制行**：`随机` · `上一首` · `播放/暂停`（主按钮，强调色实心）· `下一首` · `循环`；按钮禁用态由 `Can*` 位决定（`CanShuffle/CanRepeat` 恒真但失败返回 false → 失败后仅记日志不抖动 UI）；`IsShuffle/IsRepeat` 开启时按钮走 AccentTint 高亮。

### 3.2 歌词页（同一张卡片内切换，不新开窗口）

- 卡片右上角一枚 **词/控** 切换按钮（`主题词` 图标自绘）；点击在控制页与歌词页之间**就地淡入淡出**（复用既有 `DetailOpacity` 思路，不引入新窗口）。
- 歌词页：3 行歌词（上一行 / 当前行 / 下一行），当前行强调色 + 略大字号；随 `Position` 推进自动滚动；无歌词时显示"暂无歌词"并提供"重取"动作。
- 取词链路：曲目变化时按 `曲名 + 歌手` 走 `IKugouMusicApi` 搜索 → 取 LRC/KRC → 解析成 `(时间, 文本)` 列表并缓存（同一曲目只取一次；失败静默降级为"暂无歌词"，**绝不阻塞播放控制**）。
- 歌词是**只读展示**，不写库、不落盘（除内存缓存外）。

### 3.3 视觉与红线

- 颜色/圆角/字色一律走 `ThemeBrushes` + `IAppearanceService`（M6 单一来源），不新造令牌。
- 卡片仍在同一无焦点浮窗内自绘（不新开窗口、不抢焦点）；悬停展开仍由既有 `IslandMotionController` 驱动（尺寸弹簧已支持高度变化）。
- 进度/歌词的**逐帧刷新**只在媒体卡片处于展开态且可见时进行；收起态维持 1 s 慢轮询（空闲零重绘红线不破）。
- 拖动进度条期间**不回填**（防"手在拖、值在跳"），松手后恢复。

## 4. 实施顺序

1. `MediaActivitySource`：快照签名扩展（含 Position 秒级、Shuffle/Repeat、CanSeek）→ 活动 `Body` 之外新增**结构化载荷**（见 §5 决策 1）。
2. `shell-island`：`Rendering/MediaCardContent.cs`（媒体卡片专用内容模型：封面引用、进度、时间、能力位、歌词行）+ `Windows` 层新增 `MediaCardView`（WPF 子元素，复用现有 `_detail` 容器）。
3. 歌词：`Services/LyricsProvider.cs`（`IKugouMusicApi` 取词 + LRC 解析 + 缓存 + 失败降级）+ 单测（解析/缓存/降级）。
4. 岛窗口：控制页 / 歌词页切换与淡入淡出；进度条拖动 → `SendCommandAsync(Seek, position)`。
5. 单测 + 真机走查（用 `_island-demo.ps1` 的 media 段扩展成"有进度/有封面/有歌词"的合成数据）+ 文档。

## 5. 待用户拍板（实施前必须定）

1. **结构化载荷怎么过桥**：活动契约 `ActivityItem` 目前只有 Title/Body/Progress 等标量。媒体卡片的封面/进度/能力位/歌词需要结构体 —— 建议在 `api` 新增 `MediaCardPayload` 并给 `ActivityItem` 加一个**可选** `object? Payload`（可加性：带默认值，旧构造点零影响），避免把 UI 细节塞进字符串。
2. **歌词数据源**：在线（`IKugouMusicApi`，需联网、可能匹配错曲）／本地 `.lrc` 同名文件（离线但要求文件）／两者都做（在线优先、失败回落本地）。建议**先做在线 + "暂无歌词"降级**，本地 LRC 作为后续增强。
3. **封面取流开销**：`ThumbnailRef` 每次取流要读一次流（异步）。建议只在**展开态**取，且同一曲目只取一次（缓存），收起即释放位图。

## 6. 明确不做（本轮 scope 边界）

- 不做音量滑条（音量属于 `IAudioService`/声音面板的职责，岛只做"播放控制 + 歌词"；用户要的是"同档次"，不是"把声音面板搬进来"）。
- 不做分页/多会话列表（多播放器同时播放时仍取"活动会话"）。
- 不做歌词编辑、不做逐字卡拉 OK 动画（先做逐行高亮）。
- 不改引擎、不改 `IMediaPlaybackService` 契约（只用既有成员）。

## 7. DoD

- **D1** 展开媒体卡片：封面 + 曲名 + 歌手 + 进度条 + 左右时间 + 五键控制（随机/上一首/播放暂停/下一首/循环），禁用态由 `Can*` 决定。
- **D2** 拖动进度条 → 松手后跳到该位置（`Seek`），拖动期间数值不跳变。
- **D3** 点「词」切到歌词页：3 行歌词、当前行高亮并随进度推进；再点切回控制页。
- **D4** 无歌词 / 取词失败：显示"暂无歌词"并可点"重取"，播放控制不受影响。
- **D5** 收起态仍是 1 s 慢轮询（不因媒体卡片升级而提高空闲重绘）。
- **T1-T3**（机检）：歌词 LRC 解析（含 `[mm:ss.xx]` / 多时间戳一行 / 无时间行 / 空行）；进度时间格式化（未知时长隐藏）；能力位 → 按钮禁用态映射。
- **构建门禁**：`dotnet build BetterDesktop.slnx` 0 警告 0 错误；岛 + music 单测全绿；`verify-dotnet-format` / `md-wrap` / `md-links` / `doc-budgets` PASS。

## 8. 交接节（§14 · 技术力应用 skill 输入）

- **注入文档**：本计划；`packages/shell/shell-menu-bar/Windows/SoundPanelWindow.cs`（视觉语言参照）；`packages/api/Music/*`（快照/命令契约）；`TECH-KNOWLEDGE/` 媒体相关条目。
- **模式判定**：功能增量，纯 UI + 契约可加性扩展；**不改引擎、不改既有契约成员**。
- **风险**：① 歌词匹配错曲（在线匹配以"曲名+歌手"为键，需给出"重取/关闭歌词"出口）；② 封面取流在 UI 线程会造成卡顿（必须异步 + 缓存）；③ 卡片变高后展开/收起动画的尺寸弹簧需要重新标定（沿用 `SyncSizeTargets` 即可，无需新机制）。
