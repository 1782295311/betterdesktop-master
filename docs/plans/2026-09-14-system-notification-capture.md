# 系统通知采集实施计划（2026-09-14）

> 用户需求（2026-09-14 拍板）："我们的通知这些一直没有实现获取到系统的信息，在设计灵动岛的时候一并捎上，毕竟灵动岛也是需要这些消息提示的。"
> 状态：设计已定，待实施。消费方见 [2026-09-14-dynamic-island.md](2026-09-14-dynamic-island.md)；总索引见 [2026-09-14-remaining-features-index.md](2026-09-14-remaining-features-index.md)。

## 0. 判位

本功能补的是**读**的一侧：把系统通知中心里的通知（含其他应用产生的）读进来，成为灵动岛与通知中心面板的消息源。它与 `shell.notification` 现有职责（**发**本地新装应用弹窗）方向相反，因此新增独立契约，不扩展现有接口语义。

## 1. 现状锚点（源码验证）

| 层 | 位置 | 事实 |
|---|---|---|
| 现有契约 | `packages/api/Notification/INotificationService.cs:11` | 只有 `ShowNewAppsNotification` 一类消费者；**无读取系统通知的接口** |
| 现有实现 | `packages/shell/shell-notification/Services/NotificationService.cs:21` | 订阅 `IAppSourceService.AppSourceChanged` → 检测新增应用 → 弹窗；与系统通知中心无关 |
| 扩展点声明 | `packages/shell/shell-notification/README.md` | 明写"未来若需通用通知（标题/正文/图标/回调），可在 Contracts 增加 Descriptor 与通用 Show 方法"——**本计划走"读"的独立契约，不动这句** |
| 面板现状 | `packages/shell/shell-menu-bar/Windows/NotificationCenterWindow.cs:7` | "系统通知源（WinRT UserNotificationListener）仍待接入；当前面板呈现的是转换进度与完成消息" |
| 状态条现状 | `packages/shell/shell-menu-bar/Status/MenuBarStatusStrip.cs:1011` | 通知中心图标为占位（未接系统通知） |
| 稀疏包基础设施 | `packages/shell/shell-context-menu/native/AppxManifest.xml`、`scripts/pack-shellmenu-msix.ps1` | 已有"清单 + 外部位置 + 打包脚本 + 发布者=证书 Subject"的完整先例与实测坑记录 |
| 未落地项 | `docs/architecture/STATUS.md` | "免宿主双路原生扩展（稀疏包与签名链路）"列为未落地 |

## 2. 技术路径

主路径是 WinRT `Windows.UI.Notifications.Management.UserNotificationListener`：

| 步骤 | API | 说明 |
|---|---|---|
| 申请授权 | `UserNotificationListener.Current.RequestAccessAsync()` | 返回 `Allowed` / `Denied` / `Unspecified`；用户可在系统设置里撤销 |
| 枚举 | `GetNotificationsAsync(NotificationKinds.Toast)` | 返回 `UserNotification` 列表（含已有历史） |
| 变更 | `UserNotificationChanged` 事件 | 参数给出 `ChangeKind`（新增/移除）与 `UserNotification` |
| 取内容 | `UserNotification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric)` | 再经 `GetTextElements()` / `GetImageElements()` 取标题、正文、图标；部分应用绑定不同（`Binding.Name` 需回退） |
| 元信息 | `UserNotification.AppInfo`、`Id`、`CreationTime` | 应用名、AUMID、时间 |
| 关闭 | `DismissNotificationAsync(Id)` | 岛/面板可"清除"该通知（等价于在通知中心里清掉） |

**硬前置：包标识 + `userNotificationListener` 能力。** 该 API 对未打包应用通常直接返回 `Denied`，因此必须给承载进程一个 MSIX 标识并声明能力——复用稀疏包链路（清单 + 外部位置 + 打包脚本 + 签名），GUID 书写形式、版本递增、`Identity/@Publisher` 必须等于签名证书 Subject 等坑已在 `packages/shell/shell-context-menu/native/AppxManifest.xml` 头部有实测记录。

**这是本计划的阻塞项**：签名链路未落地（`docs/architecture/STATUS.md` 未落地节；更新签名同属 `docs/MECHANISMS.md` M11）。在签名与包标识打通之前，本功能只能运行在开发模式注册的路径上，不能进发布链路。

## 3. 降级与失败语义（fail-visible）

| 状态 | 行为 |
|---|---|
| 未授权 / 被拒 / 无包标识 | 岛与通知中心面板显示一行明确说明（"系统通知未授权"）+ 一键打开系统设置对应页；**不假装没有通知**，也不弹二次诱导 |
| 运行中授权被撤销 | 停止监听、清空来源标记、回到上一条的显示；下次启动重新申请 |
| 单个应用的通知无法解析 | 该条退化为"应用名 + 时间"最小卡片；其余通知不受影响 |
| API 整体不可用（系统版本/策略） | 记录一次诊断（版本 + HRESULT），转"仅本地活动"模式（转换进度、剪贴板、更新等由岛自产的消息仍可用） |

## 4. 契约与数据

新增 `packages/api/Notification/INotificationFeedService.cs`（读侧独立于既有 `INotificationService` 写侧）：

```csharp
public enum NotificationAccessState { Allowed, Denied, Unspecified, Unsupported }

public sealed record SystemNotification(
    uint Id, string AppUserModelId, string AppName, string Title, string Body,
    DateTimeOffset CreatedAt, string? IconPngPath, bool HasActions);

public interface INotificationFeedService
{
    NotificationAccessState AccessState { get; }
    Task<NotificationAccessState> RequestAccessAsync(CancellationToken ct);
    Task<IReadOnlyList<SystemNotification>> GetRecentAsync(int limit, CancellationToken ct);
    Task DismissAsync(uint id, CancellationToken ct);
    event Action<SystemNotification>? Arrived;
    event Action<uint>? Removed;
}
```

落点：常驻 Agent（与 OCR 同层：都是"能力"，壳退出后仍应工作，托盘与岛按需消费）；宿主内的通知中心面板经服务读取。实现归属 `packages/shell/shell-notification/Feeds/`（同包，避免新包只为一个类），契约进 `packages/api`。

隐私与控制：

- 通知正文属敏感信息：默认**不写诊断日志正文**；历史只保留有界环形缓冲（上限可配，默认 100 条），可设置"仅标题模式"与"不保留历史"。
- 按应用屏蔽：用户可以屏蔽某应用的通知进岛（与屏蔽进面板分开），屏蔽表落 `settings.json`。
- 关闭能力总开关：`extensions.notification.system-feed`（默认关，首次使用引导授权）。

## 5. 与灵动岛、通知中心面板的接线

| 消费方 | 接线 |
|---|---|
| 灵动岛 | 通知到达 → 以 `ActivityItem` 提交给活动服务（来源 = 应用名，优先级低于媒体与截图结果，高于转换进度）；点击展开详情、可清除 |
| 通知中心面板 | 在既有"活动/消息区"之上追加系统通知列表（面板注释已按此预留位置）；与转换进度同区展示但分组区分 |
| 气泡/托盘 | 托盘图标徽标计数（可选，默认关） |
| 点击行为 | 尽力按 AUMID 激活对应应用（`IApplicationActivationManager`）；激活失败时只做面板内展开，不静默无反应 |

## 6. 红线

- **不伪造**：未授权就是未授权，不用本地活动冒充系统通知。
- **不回写系统**：除 `DismissNotificationAsync` 外不修改系统通知中心的任何状态；不代发通知（发送走既有 `INotificationService`）。
- **不读用户输入**：只读 toast 的文本与图片元素，不读 `UserInput` 之类的交互内容。
- **不落正文到磁盘**（默认档）：历史驻内存；如需持久化必须走设置显式开启，并进 `docs/security.md` 的敏感信息纪律。
- **不做**：通知回复、快捷动作自定义、跨设备同步、通知内容上传。

## 7. 实现顺序

1. 稀疏包标识与能力声明打通（含签名与开发模式注册路径）——**先做，否则后续全在未授权态**。
2. `INotificationFeedService` 契约 + 实现（授权、枚举、事件、解析、降级四态）。
3. 通知中心面板接列表（含未授权说明与一键跳设置）。
4. 接入 `IActivityService` → 灵动岛显示与清除。
5. 屏蔽表、仅标题模式、历史上限等设置项 + 设置分区卡片。
6. 真机走查（三种授权态 × 常见应用 × 通知密集场景）。

## 8. DoD

功能 DoD（真机）：

- D1：首次使用 → 引导授权 → 授权后现有历史通知在面板可见；岛对**新到**通知即时提示。
- D2：在系统设置里撤销授权 → 面板与岛显式提示未授权，程序不崩、不刷日志。
- D3：某应用发通知 → 内容（标题/正文/应用名/图标）正确；点击可展开，清除按钮与系统通知中心状态一致。
- D4：被屏蔽的应用不再进岛（但仍可在未授权说明之外的面板列表里按设置处理）。
- D5：无包标识 / 无签名环境下启动 → 明确提示"需要包标识"，不误报"没有通知"。

机制 DoD（机检）：

- T1：授权状态机四态转移（含撤销后重建）用假 feed 断言。
- T2：toast XML → `SystemNotification` 的映射纯函数（含缺绑定、多文本框、只有图标、超长正文截断）。
- T3：屏蔽表与历史上限的边界（达上限驱逐、屏蔽优先级高于历史）。

构建门禁：`dotnet build BetterDesktop.slnx` 0 警告 0 错误；`packages/shell/shell-notification` 及其测试工程全绿。

## 9. 交接节

**注入文档**：本计划；[2026-09-14-dynamic-island.md](2026-09-14-dynamic-island.md)（活动模型与仲裁）；`packages/shell/shell-context-menu/native/AppxManifest.xml`（稀疏包实测坑）。

**模式判定**：新能力 + 既有能力宿主扩展；新增一个 api 契约、一个读取实现、一个持久化设置节。

**强制 scope 边界**：只做"读系统 toast → 归一化 → 面板/岛展示 → 清除与屏蔽"；不做代发、不做回复、不做云、不做通知动作自定义。
