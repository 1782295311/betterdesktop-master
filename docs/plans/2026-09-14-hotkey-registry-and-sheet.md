# 热键注册表与热键侧板实施计划（2026-09-14）

> 用户需求（2026-09-14 拍板）："热键面板需要能够显示在此时的界面下可以使用的热键功能，不分来源；我们还要能对这些热键做修改，让用户管理来源不同的热键之间不会重复和覆盖。""也可以让用户选择忽略部分的热键，比如这些热键是常用不会混淆，或者某个功能实在用不到。""和剪切板一样的侧板隐藏窗口，平时常驻透明窗口，内部显示当前可以用的实时热键表，并写明其对应的功能；使用长按右 alt 键加鼠标点击唤起窗口的可操作性，放开即恢复只显示而不被触摸。"
> 状态：设计已定，待实施。总索引见 [2026-09-14-remaining-features-index.md](2026-09-14-remaining-features-index.md)。

## 0. 判位

热键注册表是**平台服务**（shell 层基础设施），热键侧板只是它的一个可视面。顺序不能倒过来：先有唯一真相源，侧板显示的"实时热键表"才是真的；否则侧板必然是手抄清单，三天后就开始骗人。

同一个模型同时解决用户提的两件事：**作用域**回答"此刻可用"，**注册期冲突检测**回答"不重复不覆盖"。

## 1. 现状锚点（源码验证）

| 层 | 位置 | 事实 |
|---|---|---|
| 系统热键注册 | `packages/shell/shell-clipboard-panel/PanelMainWindow.cs:2324` | 面板在自己的窗口上 `RegisterHotKey`（"粘回"热键），注销在 `:2351`；**全仓唯一一处系统热键注册** |
| 引擎热键 | `engine/src/hotkey.rs` | 剪贴板引擎在 **Rust 进程内**注册三个全局热键（含 `0x581` 冲突处置记录），与宿主侧互不知情 |
| 键盘钩子封装 | `packages/shell/shell-core/Native/KeyboardHook.cs:44` | `WH_KEYBOARD_LL` 统一封装（低层钩子装/卸） |
| 钩子内的键位判定 | `packages/shell/shell-start-menu/Services/StartKeyHook.cs:79`、`packages/shell/shell-desktop/DesktopPlugin.cs:843`、`agent/Capabilities/DesktopIconsCapability.cs:115` | 键位逻辑**硬编码在各自钩子里**；改键无从生效 |
| 键位解析 | `packages/shell/shell-clipboard-ipc/HotkeySpec.cs:7` | 已有唯一解析器（其注释已写明"两边各写一遍解析必然漂移"） |
| 独占能力机制 | `agent/Capabilities/HostPresenceWatcher.cs:10` | 已登记"两边各装一个 `WH_MOUSE_LL` → 一次双击切换两次 = 净效果为零"的问题与门控解法 |
| 注册表服务 | `packages/api` | **不存在** `IHotkey*` 任何契约 |
| 无焦点浮窗先例 | `packages/shell/shell-core/Windowing/WindowStyleHelper.cs:11`、`packages/shell/shell-core/Windows/PopupWindowBase.cs:75`、`packages/shell/shell-clipboard-panel/EdgeHandleWindow.cs:51` | `WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW` 已成体系 |
| 点击穿透 | 全仓 | **零先例**：`WS_EX_TRANSPARENT` 与 `HTTRANSPARENT` 均无使用 |

## 2. 模型

```csharp
public enum HotkeySource { SystemHotkey, LowLevelHook }        // 实现机制
public sealed record HotkeyScope(string Id);                    // "Global" | "Surface.ClipboardPanel" | ...

public sealed record HotkeyBinding(
    string Id,                 // 稳定标识：来源.动作，如 clipboard.paste-next、desktop.toggle-icons
    HotkeyChord Chord,         // 复用 HotkeySpec 解析出的键位
    HotkeyScope Scope,
    string Description,        // 中文功能说明，侧板正文就是它
    HotkeySource Source,
    HotkeyChord DefaultChord,  // 用于"恢复默认"
    string Owner,              // 注册方（包名 / exe 名 / 引擎名）
    bool Editable = true,      // 系统保留或与系统冲突的项可置 false
    string? Shadows = null);   // 声明"在本质作用域内接管某条全局绑定"
```

| 作用域 | 语义 | 现有实例 |
|---|---|---|
| `Global` | 任何界面下都生效 | 引擎的三枚全局热键、截图热键 |
| `Surface.ClipboardPanel` | 仅面板活跃时生效 | `Ctrl+V` → 粘下一条、`Esc` 收起、按格粘会话键 |
| `Surface.StartMenu` | 仅开始菜单活跃 | 搜索框内键、方向键导航 |
| `Surface.Desktop` | 仅桌面模式 | 双击切换原生图标 |
| `Surface.MenuBar` / `Surface.Island` / `Surface.CaptureOverlay` / `Surface.Settings` | 各自表面的键 | 待登记 |
| `Surface.Any` | 任一 BetterDesktop 表面活跃即生效 | 切换类全局键（如唤出侧板） |

## 3. 上下文栈与"此刻可用"

定义：**显示集合 = 全部 `Global` ∪ 当前上下文栈链上的 `Surface.*`**。上下文栈由各表面生命周期上报（弹层 show/hide、模式开关如自绘/原生桌面、前台窗口归属），不由用户手动切换。

没有任何表面活跃时，只显示全局键，并给一行"当前无界面上下文"的说明——空表会让人以为功能坏了。

被遮蔽的项不能消失，要标出来（这是"不重复不覆盖"的用户可见证据）：

```
Ctrl+V   粘下一条（按序粘贴）        剪贴板面板 · 接管全局粘贴
Ctrl+V   粘贴                       全局 · 面板打开期间被接管 ⚠
```

## 4. 服务面

```csharp
public sealed record RegistrationResult(bool Ok, string? ConflictWithId, string? Reason);
public sealed record HotkeyView(HotkeyBinding Binding, bool Enabled, bool Visible, bool ShadowedBy, bool OsConflict);

public interface IHotkeyRegistryService
{
    RegistrationResult Register(HotkeyBinding binding);
    void Unregister(string id);
    RegistrationResult Rebind(string id, HotkeyChord chord);
    void SetEnabled(string id, bool enabled);
    void SetVisible(string id, bool visible);            // 「忽略」= 不显示，不改功能
    void ResetToDefault(string id);
    void SetActiveScopes(IReadOnlyList<string> scopeIds); // 上下文栈上报

    IReadOnlyList<HotkeyView> GetActive();                // 侧板渲染
    IReadOnlyList<HotkeyView> GetAll();                   // 设置中心
    IReadOnlyList<Conflict> GetConflicts();
    event Action? Changed;
}
```

实现落 `packages/shell/shell-core/Hotkeys/HotkeyRegistryService.cs`（shell 层基础设施，所有 shell 包都已依赖 shell-core，零循环依赖），并承担两件事：

- **一个 `HWND_MESSAGE`（消息专用）窗口**统一注册全部系统热键，`WM_HOTKEY` 按 Id 分派给回调。改键 = 一处 `UnregisterHotKey` + `RegisterHotKey`；"当前占用表"永远可枚举。
- **单一键盘钩子**（扩展 `KeyboardHook`）在回调里查键表（键位 → 绑定），取代各包硬编码的键位判断；查表命中且作用域活跃才消费。

## 5. 冲突策略（用户可管理）

| 冲突类型 | 检测时机 | 处置 |
|---|---|---|
| 同作用域同键位、不同属主 | 注册期 | **拒绝**（fail-closed），返回占用者 Id；UI 提供"改我的"或（属主可改时）"改他的" |
| 跨作用域同键位 | 注册期 | 允许，但必须显式声明 `Shadows`；未声明即视为冲突。侧板与设置中心标注"接管中 / 被接管" |
| 被系统或其他程序占用 | 注册期（`RegisterHotKey` 返回 false） | 标记「被其他程序占用」并提示改键；不静默失败（PowerToys 与输入法是常见对手） |
| 跨进程（壳 / Agent / 面板 exe / 截图 exe / 引擎） | 启动期与归属交接 | 复用已有独占能力机制（`HostPresenceWatcher` + `ExclusiveCapabilityHost`）：同一热键同时只允许一个进程持有，交接时先让后取 |
| 引擎侧三枚全局热键 | 声明期 | 引擎在注册表内声明占用（Owner = `clipboard-engine`）；改键统一写 `settings.json`，引擎经既有 `apply_settings` 通道生效 |

持久化：全部落 `settings.json` 的 `hotkeys` 节（键 = 绑定 Id，值 = `chord`/`enabled`/`visible`），默认值在代码里的 `DefaultChord`，schema 变更按 `docs/product-quality.md` 的设置迁移纪律。注册表是唯一读写者。

## 6. 两个正交维度：生效与显示

| 维度 | 用户意图 | 效果 |
|---|---|---|
| `Enabled` | "这个功能我用不到" | 释放键位、不再占用、不再冲突 |
| `Visible` | "这个我熟，别占视线" | 仅不显示，功能照常 |

红线：**隐藏 ≠ 静音**。隐藏只过滤"一切正常"的行；以下情况**无论是否隐藏都必须提示**——与另一绑定冲突、被其他程序占用、注册失败、被高优先级作用域长期接管。这与 `docs/runtime-health.md` 的 fail-visible 纪律同源。

恢复路径：侧板底部固定一行"已忽略 N 项 · 管理"，一键进入并逐条恢复；设置中心支持按来源整组忽略/恢复（"某个功能用不到"通常是整块）。新建绑定默认 `Visible = true`，升级不会继承他人的隐藏状态。

## 7. 改键生效路径

| 来源 | 动作 | 生效 |
|---|---|---|
| `SystemHotkey` | 中心窗口 `UnregisterHotKey` → `RegisterHotKey` | 立即 |
| `LowLevelHook`（壳内） | 更新键表 | 立即 |
| 引擎（Rust） | 写 `settings.json` → `apply_settings` | 引擎热更新 |
| Agent 内（桌面图标、任务栏） | Agent 自己的注册表实例 + 独占归属交接 | 按壳在场状态交接 |

## 8. 侧板的两态窗口（新窗口能力）

| 态 | 窗口样式 | 行为 |
|---|---|---|
| 只读态（平时） | 常驻透明、`WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW` + **`WS_EX_TRANSPARENT`**（或 `WM_NCHITTEST → HTTRANSPARENT`） | 可见但完全穿透：点击/滚动落到下方应用 |
| 可操作态 | 去掉穿透位，保留不抢焦点 | 可点击条目、录新键、隐藏/停用 |

门控：按住**右 Alt** 期间 + 鼠标点击侧板区域 → 切到可操作态；松开右 Alt → 立即回只读态。实现用已有的 `KeyboardHook`（`WH_KEYBOARD_LL`）跟踪 `VK_RMENU` 起落，配合鼠标点击判定。

**AltGr 冲突（必须处理的真机风险）**：欧语布局下 AltGr 由 `Ctrl + 右 Alt` 合成（输入 `@`、`{`、`}` 都要按住右 Alt）。因此门控必须满足"右 Alt 按住 **且** 鼠标点击落在侧板上"两个条件同时成立才切换，不允许"一按右 Alt 就切态"；门控键可配置（右 Ctrl / 右 Shift / 仅当 BetterDesktop 表面为前台时生效）。

该能力落 `packages/shell/shell-core/Windowing/`（与 `WindowStyleHelper`/`PopupWindowBase` 同层），并登记 `docs/MECHANISMS.md`——这是全仓首个"点击穿透两态窗口"机制。

## 9. 迁移清单（本项真实工作量）

| 现有热键 | 位置 | 迁移动作 |
|---|---|---|
| 引擎三枚全局热键 | `engine/src/hotkey.rs` | 注册表内声明 + 改键走 `apply_settings` |
| 面板"粘回"热键 | `PanelMainWindow.cs:2324` | 交中心窗口注册 |
| 面板内按键（`Ctrl+V` 重定向、`Esc`、按格粘） | `PanelMainWindow.cs` 钩子与状态机 | 登记为 `Surface.ClipboardPanel` 绑定，声明接管全局 `Ctrl+V` |
| 开始菜单 Win 键 | `StartKeyHook.cs:79` | 键位改为查表，作用域 `Global`（可后迁 Agent） |
| 桌面双击切换 | `DesktopPlugin.cs:843` + `agent/DesktopIconsCapability.cs:115` | 登记 + 独占归属（现已是独占能力） |
| 截图热键 | 见截图文档 | 首个使用新注册表登记的项 |

## 10. 界面分工

| 界面 | 职责 |
|---|---|
| 热键侧板（常驻透明窗） | 只读态 = **当前可用热键单表**（不分来源分段，来源仅作次要标注；冲突/被接管行置顶高亮）；可操作态 = 就地录新键 / 隐藏 / 停用；底部"已忽略 N 项 · 管理" |
| 设置中心 → 热键管理 | 全量列表（可按来源筛选）、冲突解决、启停、显示开关、恢复默认、按来源整组忽略 |

## 11. 红线

- **不静默**：注册失败、被占用、冲突、被接管，四类都要有可见位置（侧板高亮 + 设置中心条目）。
- **不偷偷改功能**：`Visible` 与 `Enabled` 是两个独立动作，任何"隐藏"操作不得改变键位注册状态。
- **不改系统**：不写其他应用的注册表、不劫持系统保留键（如 `Ctrl+Alt+Del`、`Win+L`），不可用时明确告知。
- **不双注册**：同一绑定在两个进程同时持有属于 bug（有 `HostPresenceWatcher` 的先例作回归项）。
- **不做**：宏录制、按键脚本、跨设备同步、热键云端配置。

## 12. 实现顺序

1. 模型 + 冲突判定 + 作用域过滤纯逻辑（无窗口、无钩子）+ 单测。
2. `HWND_MESSAGE` 中心窗口 + 单钩子查表 + `settings.json` 持久化。
3. 迁移清单里风险最低的一项（截图热键登记）验证闭环。
4. 点击穿透两态窗口原语 + 右 Alt 门控（含 AltGr 处置）。
5. 侧板 UI（只读表 → 可操作态 → 隐藏/停用/恢复）。
6. 设置中心热键管理分区。
7. 逐项迁移既有热键（面板 → 开始菜单 → 桌面 → 引擎声明），每项迁移后回归其功能。
8. 真机走查：冲突、占用、改键、隐藏、跨进程、AltGr 布局。

## 13. DoD

功能 DoD（真机）：

- D1：打开剪贴板面板 → 侧板出现面板热键并标注"接管全局 Ctrl+V"；关闭 → 恢复。
- D2：让两个包注册同一 `Global` 键 → 第二个被拒，设置中心可见占用者。
- D3：侧板改键 → 旧键立即失效、新键立即生效；重启后保持。
- D4：注册一个被其他程序占用的键 → 标记"被其他程序占用"，不静默。
- D5：壳与 Agent 同时运行 → 无双注册（以"一次双击切换两次"为反例回归）。
- D6：隐藏某键 → 侧板不再显示但功能照常；重启后仍隐藏。
- D7：点"已忽略 N 项"可逐条恢复；设置中心可按来源整组忽略。
- D8：停用某键 → 键位释放，可被其他绑定占用。
- D9：隐藏其中一条冲突后，冲突仍可见且双方都列出。
- D10：只读态下点击侧板 → 事件穿透到下方应用；按住右 Alt 点击 → 可操作；松开 → 立即回到穿透态。

机制 DoD（机检）：

- T1：同作用域/跨作用域冲突判定与 `Shadows` 解析纯函数。
- T2：作用域过滤（上下文栈 → 可用集合）与"冲突不受隐藏影响"断言。
- T3：键表查表命中与消费判定（不装真实钩子，用假键事件驱动）。
- T4：`settings.json` 往返与默认值回退（缺字段、损坏值、升级后新增绑定）。
- T5：`RegisterHotKey` 失败路径（假注册器注入失败）→ 冲突可见而非抛异常。

构建门禁：`dotnet build BetterDesktop.slnx` 0 警告 0 错误；新增服务与侧板相关测试全绿。

## 14. 开放问题

1. 门控键默认值（右 Alt）是否改为右 Ctrl，取决于用户是否会用到 AltGr 字符输入。
2. 改键入口是否只保留侧板就地改键（设置中心仅管理），或两处都可改。
3. 侧板位置与尺寸：默认右侧中部还是贴近菜单栏；是否需要用户可拖拽并记忆位置。
4. 是否需要"只显示我改过键的项 / 只显示冲突项"这类过滤器（当前倾向放设置中心）。

## 15. 交接节

**注入文档**：本计划；`packages/shell/shell-clipboard-ipc/HotkeySpec.cs`（键位解析唯一来源）；`packages/shell/shell-clipboard-panel/README.md`（侧板形态先例）；`agent/Capabilities/HostPresenceWatcher.cs`（独占能力先例）。

**模式判定**：新平台服务 + 新视觉包 + 新窗口能力（点击穿透两态）+ 既有热键迁移；新增一个 api 契约。

**强制 scope 边界**：只做"注册表 + 冲突治理 + 改键/隐藏/停用 + 侧板渲染 + 两态门控 + 既有热键迁移"；不做宏/脚本、不做跨设备同步、不做第二套钩子机制、不接管系统保留键。
