# BetterDesktop.Shell.Island — 灵动岛（活动呈现面）

菜单栏中置的液体胶囊：**从菜单栏长出来**，把"程序里的所有消息"收敛成一条会呼吸的呈现面。
它是**纯视觉表面**——不产生数据、不拥有业务状态，只消费 `IActivityService` 的仲裁结果。

## 一句话理解

```
来源（剪贴板 / 格式转换 / 媒体播放 / 后续：系统通知、截图、OCR）
        ↓  Post(ActivityItem)
   IActivityService（shell-core，唯一仲裁者：优先级抢占 / 同源合并 / TTL / 抑制）
        ↓  IEventBus: shell.activity/changed（跨包唯一通道）
   IslandController → IslandWindow（本包）
        ↓
   自绘液态轮廓 + 弹簧动效 + 脉冲进度环（形状自绘，文字与按钮走 WPF 子元素）
```

## 形态与动效

| 状态 | 表现 |
|---|---|
| 隐藏（默认无活动态） | **完全藏进菜单栏**：无形（窗口常驻但整幅不绘制 → `WM_NCHITTEST` 返回 `HTTRANSPARENT`，点击落到下层） |
| 小凸起（可选） | 打开 `island.idle-visible`（默认**关**）后，无活动时留一枚 64×8 DIP 的小凸起做位置提示：只画形状不画内容、不接管点击 |
| 胶囊 | 贴在菜单栏下沿、水平居中；左右用**凹角肩部**与菜单栏融合（"像从菜单栏里长出来"） |
| 脱离过程 | 肩部随脱离距离变宽 → **颈部变细**（水滴表面张力拉丝），吸附时归零（收出末段的手感） |
| 展开 | 鼠标悬停（可关）或点击 → 卡片向下生长出副标题与动作按钮（如播放/暂停/切歌） |

> **为什么默认完全收起**：2026-09-16 曾默认留一枚矮胶囊做"存在感"，用户实测后反馈"还有一小条挂在
> 菜单栏下沿，是不是没完全藏进去"，于是**默认改为完全收起**（Apple 的岛也只在有活动时出现）；
> 需要位置提示的人可自行打开 `island.idle-visible`。早先那版"看不到痕迹"的顾虑，靠活动上屏解决而不是靠常驻。
> 且它是静止画面 —— 常驻可见与"空闲零重绘"并不冲突。

弹簧参数（`Rendering/IslandMotion.cs`，禁止各处硬编码）：收入 ω=22 ζ=0.72，收出 ζ=1.0（临界），
展开 ω=18 ζ=0.78，收起 ζ=0.95；**尺寸另有一对弹簧**（宽 ω=20 ζ=0.9 / 高 ω=22 ζ=0.95），
让"换内容 / 收成休眠"时宽高是生长过去的而不是瞬间跳变；
积分器为半隐式欧拉 + 固定子步 1/240 s（掉帧不抽搐，帧率无关）。
动效强度三档 `full / lite / off`，默认尊重系统"允许动画"设置（`off` 档尺寸也瞬切）。

进度呈现（`Rendering/PulseRing.cs`）：弧长按进度映射 + 未完成段低亮轨迹 + 与进度解耦的 1.5 Hz 脉冲；
不确定进度走旋转扫描（**不显示假百分比**）；失败终态用警示色，不做循环闪烁。

## 三条硬约束

| 约束 | 实现 |
|---|---|
| 不抢焦点 | `WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW`（`ShellWindow.UseNoActivateWindowStyle`）+ `WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE` |
| 不挡点击 | 自建 `WM_NCHITTEST`：命中点不在当前轮廓（含 2 DIP 容差）内 → `HTTRANSPARENT`；**无活动（休眠态）时一律放行**，不让一枚静止胶囊挡住菜单栏点击 |
| 空闲零重绘 | 唯一的 `CompositionTarget.Rendering` 订阅在全部弹簧（含尺寸）都静止时自动退订（`IslandMotionController.IsAnimating`）；休眠胶囊是静止画面，不产生帧 |

## 消息来源

| 来源 | 依赖 | 活动形态 |
|---|---|---|
| 剪贴板按序粘贴 | `IEventBus` 的 `shell.clipboard/paste-session`（载荷 `ClipboardPasteSessionNotice`，**只含第几项/共几项**） | Progress：标题「按序粘贴 / 按格粘」+ 进度环与百分比（如 2/5 = 40%），会话结束即收起 |
| 格式转换 | `IEventBus` 的 `convert/progress\|finished\|failed\|batch-finished` | Progress：进度环 + 阶段；终态提示；批量折叠计数（"转换完成 ×N"） |
| 媒体播放 | `IMediaPlaybackService`（**自持 1 s 慢轮询 + 本地变更检测**，不订阅其裸 event） | Sticky/Media：标题 + 歌手，展开给上一首/播放暂停/下一首 |
| 系统通知 / 截图 / OCR | 未接入（见 Known Limitations） | — |

> **隐私红线（2026-09-16 用户要求）**：岛不会因普通复制而弹出；会话状态在面板进程，经 MenuCmd 管道推给宿主。，并先 `Complete` 上一条：仲裁对"同 Id 再 Post"按设计不做变更广播
> （那是给进度用的节流），沿用旧 Id 会让岛上停在旧预览；不 Complete 旧 Id 则旧预览会在本条过期后回锅再弹一次。
>
> 媒体来源**不用** `MediaPlaybackChanged` 事件：该 event 定义在 api 程序集、消费在本包，属 ADR-002 D4
> 禁止的跨程序集裸 event（门禁 `verify-no-cross-assembly-event` 会拦）；改用自持 1 s 轮询 + 签名比对，
> 与实现层原本的检测粒度一致，代价等价。

抑制：`SHQueryUserNotificationState` 每秒查询，`QUNS_BUSY / RUNNING_D3D_FULL_SCREEN / PRESENTATION_MODE`
时调 `IActivityService.SetSuppressed(true)`（活动进队列，退出后按优先级补播）。

## 设置键（前缀 `island.`，设置中心「灵动岛」分区）

| 键 | 默认 | 说明 |
|---|---|---|
| `island.enabled` | true | 总开关（关闭即拆窗与退订全部来源） |
| `island.motion` | 跟随系统 | `full` / `lite` / `off` |
| `island.hover-expand` | true | 悬停是否展开详情（关闭后仅点击展开） |
| `island.idle-visible` | **false** | 无活动时是否留一枚小凸起做位置提示（默认关 = 不用时完全藏进菜单栏） |
| `island.source.paste-session` | true | 按序粘贴/按格粘会话进度（普通复制不上岛） |
| `island.source.convert` | true | 转换进度与终态提示 |
| `island.source.media` | true | 媒体播放控制 |
| `island.offset-x` / `island.offset-y` | 0 | 位置微调（DIP） |

## 装配

- `host/cordis.yml`：`island`（排在 `clipboard-history` 之后——三个来源的服务届时都已就绪）
- `host/Bootstrap.cs`：`["island"] = () => new BetterDesktop.Shell.Island.IslandPlugin()`
- 服务的 Provide：`IActivityService` 由 `shell-core` 的 `ActivityPlugin` 提供（`cordis.yml` 的 `activity` 条目；
  含 1 s TTL 心跳与变更广播）。**注意**：同包的旧 `ShellCorePlugin` 在 cordis 迁移后已无任何引用，
  把服务挂在它身上等于没 Provide —— 真机日志曾实证 `IActivityService 未入图`

## 测试

`packages/shell/shell-island-tests`：动效（临界阻尼无超调 / 欠阻尼回弹 ≤6% / 固定子步帧率无关 /
空闲可静止判定）、轮廓（附着命中 / 脱离颈部变细 / 就地更新 / 退化不抛）、抑制判定、
活动映射、转换来源 × 仲裁接线（含退订配对）。

## Known Limitations

- 按序粘贴**会话进度**已接入（2026-09-16：面板经 MenuCmd 管道上报，含按格粘）；：会话队列（`ClipboardIpcClient` 的 `private SequentialItem`）活在**面板 exe 进程**里，
  壳进程的客户端看不到，需要在剪贴板引擎侧新增会话状态与推送动词（属引擎改造，另行立项）。
- **格式转换进度目前收不到事件（有前置）**：转换实际由 `BetterDesktop.Cli` 的 `HeadlessExecutor` 在**CLI 进程内**
  执行（`new ConversionService(registry)`，没有 IEventBus），而 `IEventBus` 是**进程内**总线，
  `--menu-cmd convert` 走"需宿主"分支只弹提示、不转发。因此本来源虽已按事件契约接好，
  在当前执行路径下不会触发；要真正点亮需二选一：① 让转换在宿主进程执行；② 加一条跨进程事件桥（CLI → 宿主 IEventBus）。
- 自渲染层未采用 D3D11/DirectComposition + HLSL metaball：该前置（引入 D3D11/DComp/D2D 互操作库）尚待拍板。
  当前用解析几何构造同一形态语义的轮廓（无着色器依赖、任意 DPI 矢量、零逐帧分配）；届时替换面只有"谁把轮廓画成像素"。
- 系统通知 / 截图 / OCR 进岛未接入：分别依赖系统通知采集（稀疏包 + 签名前置）与各自文档，
  但活动模型与呈现面已就绪，接线只需再写一个 `IActivitySource`。
- 多显示器仅支持主屏：岛按主屏菜单栏定位；`IslandWindow.ResolveAnchor` 是唯一锚点解析点，per-monitor 化改动面小但未做。
- 系统"精简动画"以外的可访问性档位（如高对比度）未单独适配：颜色全部走主题令牌，会跟随主题，但没有专门的高对比度配色分支。
- 媒体封面未显示：`MediaPlaybackSnapshot.ThumbnailRef` 是 WinRT 流引用，需要异步取流 + 缓存，本轮只显示来源字形（音符）。
