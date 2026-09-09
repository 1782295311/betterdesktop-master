# BetterDesktop.Shell.Clipboard

剪贴板历史核心实现（公共 API 契约 + 捕获/存储/面板/热键/消费方）。

## 能力

- **公共 API 契约**（`packages/api/Clipboard/IClipboardService.cs`）：复制/粘贴/收藏/暂停/合并/按序粘贴/分段写回等，供消费方按接口调用，不依赖实现程序集。
- **捕获与存储**：剪贴板变更监听（Win11 广播不可用时 500ms 序列号轮询兜底）、去重、分类（文本/代码/图片/文件/HTML/RichText）、容量/收藏/图片预算/保留天数运行时设置。
- **历史面板**（`ClipboardHistoryWindow.cs`）：搜索/类型筛选/来源筛选/日期分组/多选/合并粘贴/按序粘贴/拖拽导出/收藏视图。
- **热键**：Ctrl+Shift+V 打开面板、Ctrl+Shift+P 收藏视图、Ctrl+Shift+Backspace 暂停/恢复。
- **消费入口**：菜单栏「📋」按钮（I6）、搜索弹窗最近复制区块（I7）、系统右键 4 场景「剪贴板历史…」（I10）、`--menu-cmd clipboard-history` 命令桥。

## 配置键（`extensions.clipboard-history.*`）

`enabled`（默认 false）、`capacity`（默认 10000）、`pinned-limit`（默认 200）、`max-image-mb`、`max-total-image-mb`、`retention-days`（默认 90）。

## Known Limitations

- I9 自绘右键「剪贴板历史…」项未实现：自绘右键管线已于 2026-09-05 退役（文件条目右键收口为系统原生菜单），由 I10 系统右键 4 场景注册覆盖同语义入口。
- WM_CLIPBOARDUPDATE 广播在部分 Win11 版本（如 26200）不可用，回退 500ms 序列号轮询，捕获延迟 ≤1s。
- 存储文件（`%LOCALAPPDATA%\BetterDesktop\clipboard_history.json`）以 DPAPI 加密（CBENC1 头），无法人工直接阅读，验证需走服务接口/日志。
- 历史面板为纯代码 UI，无 XAML 设计时支持；视觉/交互走查依赖真机人工验证（自动化截图受环境限制）。
