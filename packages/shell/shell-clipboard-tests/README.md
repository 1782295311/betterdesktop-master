# BetterDesktop.Shell.Clipboard.Tests

剪贴板核心逻辑单元测试项目（85+ 例全绿，Theory 展开后总数略多）。

## 覆盖范围

- 捕获/去重/过滤/隐私（前台应用隐私位）
- 清理/容量淘汰/图片预算/保留天数
- 分段（`ClipboardSegmenter`：HTML 块级切分/栈配对/不平衡降级/纯文本写回）
- 暂停/恢复、合并粘贴文本构建、按序粘贴状态机、CF_HTML 头偏移
- 服务契约（`IClipboardService` 可加扩充项）

## 运行

```powershell
dotnet test packages/shell/shell-clipboard-tests/BetterDesktop.Shell.Clipboard.Tests.csproj --no-restore -v q
```

## Known Limitations

- 测试针对内核逻辑（捕获/分段/状态机/契约），不覆盖面板 GUI 视觉与热键系统级行为——这些依赖真机人工走查。
- 测试文件读写走 `%TEMP%\bdt-clipboard-tests\{guid}` 临时目录，运行后自动清理。
