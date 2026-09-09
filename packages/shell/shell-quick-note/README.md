# shell-quick-note

快速笔记插件：由扩展中心开关（`extensions.quick-note.enabled`）驱动启停的常驻浮窗，提供一键便签速记能力。

## 职责

- `QuickNotePlugin`：插件入口，作为扩展中心开关的**观察者**——LoadAsync 时按当前键位决定是否激活浮窗；订阅 `ISettingsService.Changed`，用户在任何地方（含扩展中心）切换开关即实时启停。
- `QuickNoteLauncher`：桌面悬浮启动器（小图标），点击切换笔记窗口显隐，右键可禁用自身（将开关置 false，经 Changed 回到 Deactivate，闭环一致）。
- `QuickNoteWindow`：笔记编辑浮窗，文本持久化到 `extensions.quick-note.text` 设置键，毛玻璃效果由 `IVibrancyService` 提供。
- 默认关闭：外部扩展是可选能力，不主动在桌面添加元素；需用户在扩展中心手动启用。

## 依赖

- `BetterDesktop.Kernel`
- `BetterDesktop.Shell.Core`（外观 `IAppearanceService`、毛玻璃 `IVibrancyService`）
- `BetterDesktop.Shell.Settings`（设置服务，开关与文本持久化）

## 扩展点

- 启动器外观与位置可通过 `IAppearanceService` 主题令牌适配。
- 笔记窗口内容可扩展为富文本或 Markdown（当前为纯文本）。
- 开关键 `extensions.quick-note.enabled` 与文本键 `extensions.quick-note.text` 为扩展中心标准约定，其他插件可读取状态。

## Known Limitations

- 仅支持单条笔记，无多笔记管理、标签或分类功能。
- 笔记文本持久化到 ISettingsService（设置存储），非独立文件；大文本或高频写入可能影响设置服务性能。
- 依赖 `IVibrancyService`，缺失时降级为 `NullVibrancy`（窗口不变毛玻璃，但不崩溃）。
- 启动器位置固定，不支持拖拽自定义位置。
- 窗口创建/销毁通过 `Application.Current.Dispatcher.Invoke` 封送到 UI 线程，极端高负载下可能有短暂延迟。
