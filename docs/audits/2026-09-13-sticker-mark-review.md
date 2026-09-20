# 审查报告 · 表情包「标记制」改造（2026-09-13）

> 触发：用户要求"检查有没有 bug 和程序问题"。
> 范围：本轮全部改动 —— 表情包由**分类**改为**标记**（与收藏同级）、多选批量设为表情包、条目文件大小显示。
> 方法：独立代码审查（交叉验证）+ 静态核对 + 全量测试。
> 结论：**发现 1 个高危功能回归、5 个中等问题、若干低危隐患**，除明确标注"不修"者外均已修复并回归通过。

## 一、已修复

| # | 严重度 | 问题 | 证据 | 修法 |
|---|---|---|---|---|
| H1 | **高（功能回归）** | 表情包写回剪贴板时**只写 CF_HDROP**，丢掉 PNG 原字节与 CF_DIB 首帧兜底 → **只认位图的应用（不少聊天输入框）粘不出东西**，用户感知为"点了复制却粘不上" | `capture.rs:618` 判据仍是 `entry.category == Category::Sticker`；而导入后 `category` 已是 `Image/File`、旧数据也被迁移重判 → 该分支**永不成立**，`set_sticker_entry` 成死代码 | 判据改为 `entry.is_sticker`（并移除因此不再使用的 `Category` 导入） |
| M1 | 中 | 公共契约 `StatsText` 对表情包退化成"1 个文件"（不再显示"GIF · 300×300"） | `ClipboardEntry.cs:154` 同因（旧分类判据永不成立） | 判据改为 `IsSticker || Category == Sticker`（后者作遗留兜底） |
| M2 | 中 | **跨后端重复导入**：legacy 后端把 `sha256` **截断成 16 hex** 落盘，与像素哈希同长 → 引擎误判"已是像素口径"而**永不重算** → "legacy 导入过 → 切回引擎再导同一张图"仍会新增重复条目 | `ClipboardManager.cs:853`（`[..16]`）vs `store.rs` 的 `content_hash.len() != 16` 判据 | ① legacy 改为写**完整** 64 hex；② 迁移判据追加"无尺寸"（引擎写入必带 `image_width`，legacy 不写）：`len != 16 \|\| image_width == 0` |
| M3 | 中 | 取消标记后再删除条目 → `stickers\` 副本**不被回收**（孤儿文件）→ 磁盘占用虚高、存储横幅长期误报 | `store.rs:352` 把回收判据从"分类"改成"**标记**"，而标记是用户可随时取消的 | 回收改回**路径判据**（无条件遍历 `file_paths` 删 `parent() == stickers_dir` 者）；安全红线不变 |
| M4 | 中 | **文字表情包**行首显示"🖼"（照片图标），与同一行 `TypeGlyph` 精心区分的 😀/🖿 自相矛盾 | `RecentStrip.cs:320` `if (isImage \|\| isSticker) LoadImageThumbnail(...)` —— 标记可打在文字条目上，此时无图可加载 → 回退成图片图标 | 与 `BuildContentPanel` 对齐：`isImage \|\| (isSticker && isFile)` |
| L1 | 低 | `{category:5, sticker:false}` 会被翻译成"全部非表情包"（语义反转） | `engine.rs:549` 兼容翻译 | 改为 `sticker = sticker.or(Some(true))`：**不覆盖**调用方的显式参数 |
| L4 | 低 | `storage_status.unpinned` 把表情包算进"未收藏"，与 `clear_unpinned` 不删表情包的口径不一致 | `engine.rs:610` | 改为 `!e.is_pinned && !e.is_sticker` |
| L5 | 低 | 命中"已是表情包"的条目、但因保动画而**替换了内容**时，仍计 `skipped`（调用方/日志无从得知内容已变） | `engine.rs:941` | 内容被替换时计 `upgraded` |
| L8 | 低 | 可访问名不含"表情包/已收藏" → 读屏用户无从得知标记状态 | `RecentStrip.BuildAccessibleName` | 前缀加入"已收藏且表情包、/已收藏、/表情包、" |
| — | 中（待确认项） | `RefreshRow` 用 `Items[i] = row` 替换**当前选中项**时，WPF `Selector` 可能清空 `SelectedItem` → 之后 Enter/Delete/Space **静默失效**（用户感觉"键盘忽然不灵"） | `PanelMainWindow.RefreshRow` | 替换后恢复选中下标；本次新增的第三个调用方（批量标记）最易撞上 |
| L7 | 低（覆盖缺口） | 表情包标记这条**核心写路径此前完全没有测试** | 无 | 新增 4 个 IPC 用例：`SetSticker` 转发 true/false、`Upgraded` 解析、`sticker` 筛选参数 |

## 二、已核对无问题（逐条确认过，非"没看")

1. **迁移不改索引是安全的**：`fingerprint()` 的所有分支只看 `content_type`/`content_hash`/`file_paths`/`content`，**不看 `category`**；`migrate_sticker_flag` 只改 `category` → 指纹不变、索引无需重建。
2. **迁移顺序正确**：`migrate_sticker_flag` 在 `dedupe_images` **之前**；若颠倒，旧条目 `is_sticker` 仍为 false → 不参与合并 → "图片+表情包同像素两条"的重复会残留。
3. **`cmd_add_sticker` 借用无冲突**：不可变借用（`find_by_image_hash`）的最后使用早于可变借用，NLL 下成立；标记打的确实是命中的那条。
4. **判据统一性**：引擎侧"仍按分类判断表情包"的生产代码**只剩 H1 一处**（已修）；驱逐、清理、级联删除、查询均已统一为 `is_sticker`。
5. **接口实现完整**：`IClipboardService` 仅 2 个实现（IPC 客户端、legacy），均实现新方法；测试替身继承 legacy 自动获得。
6. **`Upgraded` 构造点全部同步**：4 处构造全部为 4 参；legacy 恒为 0 且在代码中注明原因（非遗漏）。
7. **序列化对齐**：引擎 `#[serde(rename_all = "camelCase")]` → `isSticker`；C# 侧 `CamelCase + PropertyNameCaseInsensitive` → 正确匹配（真机探针已实测 `isSticker=True` 可读）。
8. **事件订阅完整**：面板两处创建 `RecentStrip` 的地方都接上了全部 5 个事件（含新增的 `StickerRequested`）—— 这是本项目踩过的坑（漏接会让重建行后交互永久失效）。
9. **批量标记状态一致**：`marking`（数据方向）与按钮文案（视图方向）互为取反，"已选 N 项"/行内 😀/🎴/按钮文案同源，不会不同步。
10. **`_stickerBatchBtn` 无 NRE 风险**：赋值于 `BuildMultiSelectBar` 内，且使用处有双重判空守卫；调用链上 `RefreshSelectionUi` 只可能在 `BuildContent` 之后进入。
11. **`FormatSize` 边界安全**：`<= 0` 返回空串（不显示"0 B"），极大值走 GB 分支，先转 double 不会溢出。
12. **键盘勾选无冲突**：`PreviewKeyDown` 挂在列表上，焦点在列表内才触发（搜索框数字键不受影响）；Space 被 `Handled` 后不再冒泡给 ListBoxItem 的默认语义；chip 的 Enter/Space 各自独立。
13. **`QueryFilter` 兼容翻译不误命中**：`category=5` 只被兼容分支吃掉，`kind`/`pinned` 等互不干扰。

## 三、已知遗留（明确不修，非遗漏）

| # | 差异 | 为什么不修 |
|---|---|---|
| M5 | legacy 导入缺少单文件大小上限（引擎有 `file-copy-max-mb` 校验） | legacy 仅 `backend=legacy` 紧急回退路径使用；补它需要给 `ClipboardManager` 注入设置依赖（改动构造链与全部测试替身），收益与风险不成比例。**默认后端（引擎）已有校验。** |
| L2 | legacy 表情包条目无缩略图/尺寸 → 面板回退解码原图，大 GIF 滚动可能卡顿 | 同上（legacy 专属） |
| L6 | legacy 写回无表情包多格式分支（无 CF_DIB/PNG 兜底） | 同上（H1 修好后引擎侧已正常） |
| L3 | legacy `SetSticker` 会广播 `history_changed`（面板整页重建、丢滚动位置），引擎刻意不广播 | 与 legacy 自身其它操作（`PinEntry` 等）保持一致更重要；两个后端的表现差异已在此记录 |

## 四、回归结果

| 层 | 结果 |
|---|---|
| Rust | **91 passed / 0 failed**（0 警告） |
| IPC | **26 passed / 0 failed**（新增 4 个标记路径用例） |
| 契约 | **87 passed / 0 failed** |
| 构建 | `dotnet build BetterDesktop.slnx` **0 警告 0 错误** |
| 部署 | 引擎（release）与面板均已更新并重启 |
