# MECHANISMS.md — 机制唯一化注册表

> 地位：「一个关注点一套机制」裁决的**唯一真相源**。新增/替换机制必须先登记或引用本表。
> 与 ADR-001 的关系：ADR-001 是宪法，本表是宪法的机制清单；任一方改动时必须当场确认另一方仍指得通。
> 版本：v0.10 · 2026-09-16（v0.10 补 M23；v0.9 补 M20-M22）

## 登记表

| # | 关注点 | 选用机制（唯一） | 禁止的重复机制 | 关联护栏 |
|---|---|---|---|---|
| M1 | 门禁执行入口 | `scripts/run-gates.ps1`（门禁注册表 + 依赖 + 失败聚合） | 各处自写门禁跑法、绕过 run-gates 的 CI 脚本 | run-gates 自身即护栏；每条门禁须有门禁单测（`verify-*.Tests.ps1`） |
| M2 | 决策记录 | `.agents/notes/{proposed,implemented,rejected}/{class}/` | 另立 `decisions/`、根目录散落的决定文档、任何 INDEX 索引 | `verify-agent-note` / `verify-archived-notes`（格式与归档由门禁强制） |
| M3 | 文档机器校验 | `scripts/verify-*.ps1` 门禁集 + `run-gates.ps1` 注册表（**注册表是唯一清单，本文不复述枚举**） | 手写一次性检查脚本、人肉核对文档一致性 | run-gates 注册表 |
| M4 | 旧仓资产借用 | 白名单登记制：仅个别**原生结构体布局**可借鉴（逐条 ABI 核对后登记） | 复制旧仓逻辑代码 / 主题 / 图标 / XAML 资源 | ADR-001 红线 R5；白名单见本表附录 A |
| M5 | 插件内核 | Cordis 内核 C# 复刻（P1 落地，此处登记） | 第二套插件机制、MEF、外挂式扩展宿主模型 | P1 起 `kernel` 包 + 内核测试集 |
| M6 | 统一 UI 地基 | 单一 `packages/shell/shell-foundation` 包 + [`docs/ui-foundation.md`](ui-foundation.md)（字体三令牌 / 统一 ShellWindow / 语义令牌 / 公共控件唯一来源） | 自建窗口体系、硬编码字体/颜色/圆角、第二套主题令牌、重复控件 | P2 机检（`verify-ui-hardcoded` / `verify-token-coverage`） |
| M7 | 功能复用与反臃肿 | [`docs/reuse-rules.md`](reuse-rules.md)（单一实现 / 共享层上移 / 包粒度判据 / 禁复制 / 依赖单向） | 复制粘贴、同义实现并存、越层依赖、包无节制膨胀 | P1 机检（`verify-code-duplication` + 架构测试） |
| M8 | 扩展性与社区契约 | [`docs/extension-rules.md`](extension-rules.md)（扩展点显式化 / plugin.json v1 草案 / API 兼容与弃用 / 文档义务）+ [`../CONTRIBUTING.md`](../CONTRIBUTING.md) | 隐式扩展点、破坏性变更静默、无文档能力、第二套扩展机制 | `package-readme` 已强制扩展点节；其余 P1/P2 逐步机检 |
| M9 | 测试与取证 | [`docs/testing.md`](testing.md)（金字塔 / 契约优先 / 禁 --no-build / flaky 处置 / 性能基准） | 无基准的「绿」、拷贝产物取证、Skip 了事 | P1 覆盖棘轮 + 架构测试；P2 benchmark 门禁 |
| M10 | 运行时健康 | [`docs/runtime-health.md`](runtime-health.md)（日志单管道 / 崩溃三入口 / fail-fast / 降级上报 / fail-closed / 超时上限） | `Console.WriteLine`、吞异常、静默降级、第二套日志、默认放行守卫 | P1：日志/吞异常文本扫描 + 架构测试 |
| M11 | 安全与供应链 | [`docs/security.md`](security.md)（插件最小权限 / 依赖锁定与漏洞扫描 / 许可证合规 / 敏感信息 / 更新签名） | 未审计依赖、敏感信息入库、fail-open 授权、裸拷 vendoring | P1：`dotnet list package --vulnerable`；P2：secrets 扫描 + notices 生成 |
| M12 | 构建与发布 | [`docs/build-release.md`](build-release.md)（SemVer 单一来源 / SDK 锁定 / 发布门禁 / CHANGELOG / 插件禁 AOT） | 手工散改版本号、产物混装、无门禁发布 | P1：SDK 锁定检查；P2：release 门禁 |
| M13 | 产品质量 | [`docs/product-quality.md`](product-quality.md)（性能预算 / 可访问性 / 本地化 / 设置单一来源与迁移 / 隐私） | 硬编码文案、第二套设置源、无迁移 schema 变更、无预算性能回归 | P1：设置单一来源架构测试；P2：硬编码字符串扫描 + benchmark |
| M14 | 插件化行为 | [`docs/pluginization.md`](pluginization.md)（角色插槽 / 先立后破替换 / 崩溃隔离分级 / 熔断回退 / 回退锚点） | 中间态替换、插件崩溃拖垮外壳、无回退路径、静默拒绝加载 | P2：替换演练 + 崩溃注入演练（物证制） |
| M15 | 代码评审 | [`docs/code-review.md`](code-review.md)（检查单 / 合入门槛 / 争议裁决）+ 根 `.editorconfig` 格式唯一来源 | 免评审合入、规则空白处自由发挥、夹带改动、伪证采信 | 暂无机检（P2 PR 模板 + CI 检查项）；P1 起 `dotnet format` 门禁 |
| M16 | AI 控制面 | [`docs/ai-control.md`](ai-control.md)（能力目录 / 三档入口 / 权限分级与人在环 / 可审计可撤销 / 防失控） | AI 直连功能硬调用、无权限模型、第二套能力注册表、审计旁路 | P2/P3：目录 / 越级 / 审计 / 停用四组演练（物证制） |
| M17 | 原生与跨语言承载 | 独立进程 + 命名管道 JSON-RPC（模板：剪贴板/索引引擎），宿主侧仅 IPC 客户端代理 | 进程内脚本引擎（Jint / Python.NET）、长任务塞进 UI 进程 | 引擎 README 与 `docs/cross-language/`；回退路径纳入回归 |
| M18 | 对外命令入口 | `BetterDesktop.Cli.exe`：headless 直执行 + "需宿主"命令转发；系统右键统一指向它 | 各表面自造命令、右键直指宿主、静默拉起宿主 | `scripts/verify-shellmenu.ps1` 与 CLI 入口约定 |
| M19 | 截图与 OCR 接入 | 独立入口面进程 `BetterDesktop.Capture.exe`（WGC→DXGI→BitBlt 降级链 + 选区/编辑器/贴图/OCR 面板）；PNG 落 temp → 写系统剪贴板 → 引擎监听入库；OCR 走 Windows.Media.Ocr（L1），经 `set_ocr_text` 挂回同一条目参与搜索 | 第二套采集实现、写回专用 IPC 动词（`set_ocr_text` 除外）、进程内直连引擎写库、录屏/云上传 | 降级可见（日志+stdout）；失败不落半成品；受保护内容启发式；不抢前台 |
| M20 | 热键注册表 | `IHotkeyRegistryService`（shell-core `Hotkeys/`）：`HWND_MESSAGE` 中心窗口统一 `RegisterHotKey` + 单 `WH_KEYBOARD_LL` 钩子查表；`HotkeySpec` 为解析/内置键唯一来源；冲突 fail-closed、作用域过滤、隐藏≠静音、`settings.json hotkeys.*` 持久化 | 第二套钩子/解析器、各处自调 `RegisterHotKey`、静默覆盖冲突、劫持引擎内置三键 | 冲突/服务单测（假窗口/假键/settings 往返）；P5 迁移既有热键并回归 |
| M21 | 点击穿透两态窗口 | `ClickThroughWindow`（shell-core `Windowing/`）：`WS_EX_TRANSPARENT` 位设/清两态 + `MakeFloatingNoActivate`；门控 `RightAltGate`（默认右 Alt，按住+点击，AltGr 防护可配置） | 第二套穿透实现（自绘 `WM_NCHITTEST` 等）、无门控"一按即切" | 样式位单测 + AltGr 双条件单测；真机走查待 P5 |
| M22 | 岛活动仲裁 | `IActivityService`（api）+ `ActivityService`（shell-core）：优先级抢占（同优先级后到先显示）、被抢占回队列、Sticky 常驻 / Transient+Progress 按 TTL 收起、同源折叠计数、抑制期进队列解除后补播、Changed 仅身份/顺序变化触发、零订阅者零开销 | 第二套活动队列、各源直写胶囊、进度更新逐帧触发订阅 | 仲裁单测（时钟注入）；广播 `shell.activity/changed`（1 s TTL 心跳在 `ActivityPlugin`） |
| M23 | 岛表面与呈现 | 唯一表面 `shell-island`：独立无焦点浮窗 + 自建 `WM_NCHITTEST` 轮廓命中 + 自绘液态轮廓（肩部/颈部）+ 自有帧时钟弹簧 + 脉冲进度环；消息源统一经 `IActivitySource` 接入活动服务 | 第二套自绘动画表面、动画放回 WPF `Storyboard`、各包自建通知浮层、第二套主题令牌 | 动效/轮廓/抑制/来源退订单测；空闲零重绘由帧时钟自动退订保证 |

## 流程

1. 新增关注点：在本表登记一行（关注点 / 选用 / 禁止 / 护栏），同一变更落地。
2. 替换已登记机制：**新开 ADR**，不得静默替换。
3. 登记即承诺：登记「禁止」清单后，对应护栏必须在门禁中可执行，否则在表中如实标注「暂无机检」。

## 附录 A · M4 白名单（旧仓可借鉴的结构体布局）

| 条目 | 来源（旧仓文件） | ABI 核对状态 |
|---|---|---|
| （空） | 尚无任何条目获准 | — |

> 每借用一条，先在此登记并完成「逐条核对 ABI」的物证，才能进入新仓代码。
