# 剪贴板功能 · 立项对照核销报告

> 状态：已归档快照——核销时点为引擎化之前的宿主内 v1.3 实现。引擎化后的现行事实见 `docs/plans/2026-09-11-clipboard-engine-rust-ipc.md` 与 `packages/shell/shell-clipboard/README.md`。

日期：2026-09-10（2026-09-10 二轮修正：补全文档链视角、修正 K5 定性）。

文档链（时间序，早的在前，导航 = `docs/plans/clipboard-docs-index.md`）：

- ① `archive/2026-09-08-clipboard-history-extension.md`（立项蓝图，功能目录唯一权威）
- ② `plans/2026-09-09-clipboard-public-api.md`（Phase A：契约入公共 API 包）
- ③ `plans/2026-09-09-clipboard-phase-b.md`（Phase B = **v1.3 终态收口**，v1.1 功能经用户拍板全部提前并入）
- ④ 本报告（核销）

现状依据（核销时点）：`packages/shell/shell-clipboard/`（README 已同步 v1.3 终态 + ClipboardSection + ClipboardManager + ClipboardHistoryWindow）、单测 85/85 绿、构建 0 警告 0 错误。

> ⚠️ 方法论教训（本文初版的错误）：初版只对照了①归档蓝图，漏读②③，把 Phase B 计划内的 K5 误判为"未做"（实为实施拍板降级只读展示），也漏掉了"v1.3 一次收口"这一定版事实。**核对文档必须沿时间序通读全链，冲突时以后者为准。**

## 结论

**v1 全量落地（55/55）；v1.1 落地 6 项 + 2 项有意调整（I9 语义变更 / K5 降级只读展示），无实质缺口；未来/可选 8 项按计划未启动。终态版本 v1.3（Phase B 用户拍板一次收口）。** 面板 UI 经三轮重设计后已明显超出立项 J 组标准。

## v1 清单逐组核销（55/55 ✅）

| 域 | 项 | 状态 | 备注 |
|---|---|---|---|
| A 监控捕获 | A1-A6 | ✅ 全 | Win11 26200 无广播 → 500ms 序列号轮询兜底（延迟 ≤1s，README 已记录） |
| B 历史管理 | B1-B7 | ✅ 全 | 去重置顶/收藏/删除/标签/CopyCount/来源/日期分组 |
| C 分类分析 | C1-C4 | ✅ 全 | 代码检测（缩进+关键字密度）、混合内容（图/表） |
| D 粘贴输出 | D1-D8 | ✅ 全 | 自动分段（250ms 间隔、失败即停）、按类别合适粘贴、合并、拖拽导出 |
| E 搜索筛选 | E1-E4 | ✅ 全 | 来源 Top8 按计数排序（GetSourceApps） |
| F 隐私安全 | F1-F3 | ✅ 全 | 双表黑名单、暂停 60s、DPAPI+CBENC1 加密（明文旧格式回退兼容） |
| G 容量生命周期 | G1-G5 | ✅ 全 | 10000/200/5MB/200MB 预算/90 天+孤儿清理 |
| H 存储 | H1-H4 | ✅ 全 | System.Text.Json（beyond-1 已纳入强制 scope 并落地） |
| I 服务契约 | I1-I5 | ✅ 全 | IClipboardService 注册内核服务图，关开关历史仍可查 |
| J 面板 UI | J1-J6 | ✅ 全+超越 | 详见下节 |
| K 热键 | K1-K4 | ✅ 全 | 0x581 捕获+配对释放、冲突降级不崩 |
| M OCR 契约 | M1-M3 | ✅ 全 | ImportEntries 批量/分段录入 + ContentAnalyzer 分类（契约就绪，OCR 插件未来接） |
| N 便签预留 | N1 | ✅ | 数据访问保证已有 |

## v1.1（8 项 → 6 ✅ + 2 有意调整，无实质缺口）

| 项 | 状态 | 说明 |
|---|---|---|
| G6 参数配置化 | ✅ | capacity/pinned-limit/max-image-mb/max-total-image-mb/retention-days 全部进设置分区（ClipboardSection） |
| I6 菜单栏按钮 | ✅ | ClipboardMenuBarExtension（「📋」直开面板） |
| I7 搜索弹窗联动 | ✅ | SearchPopupWindow 最近复制区块 + 历史检索（接 IClipboardService） |
| L1-L3 按序粘贴 | ✅ | Phase B 用户拍板提前并入 v1.3：队列状态机（Remaining/Cancel）+ 面板按序条 + Ctrl+Shift+V 逐条 |
| I10 系统右键 | ✅ | 注册表 4 场景（文件/目录/空白/桌面）+ `--menu-cmd clipboard-history` 命令桥 |
| I6-I10 其余 | ✅ | 消费板块随 Phase B 全量接线 |
| **I9 自绘右键接管** | ⚠️ 语义变更 | 自绘右键管线 2026-09-05 整体退役，I9 不再适用；同语义入口由 I10 系统右键覆盖（README Known Limitations 已记录） |
| **K5 热键可改** | ⚠️ 有意降级 | Phase B 已实施"配置化"：设置分区「快捷键」卡**只读展示**三组合键，改键能力由实施拍板锁定（组合键由 Manager 常量锁定，避免热键冲突面失控，见 ClipboardSection.cs 头注释）——非未做 |

## 未来/可选（8 项，按计划全部未启动 ⬜）

C5 语言识别、E5 搜索历史、F4 敏感模式识别、H5 LMDB、I8 状态栏指示器、N2/N3/N4 便签/工作站（N1 数据访问已预留）。

## 超出立项的部分

1. **面板 UI 三轮重设计（2026-09-09）**：卡片化条目 + 真实 Shell 文件图标 + 真实文件名预览 + 96px 图片缩略图 + 暂停横幅 + MacButton/令牌全量收口——立项 J 组只要求"能用 + 主题适配"。
2. **统一弹窗基类**：ClipboardHistoryWindow 迁入 PopupWindowBase（外点收起钩子/前景传导/chrome 统一），超出立项的 ShellWindow 直继方案。
3. **多选模式快捷入口**：Ctrl+N/Ctrl+M（原实现无入口，属死功能，重设计中补齐）。
4. **公共 API 独立契约包**：`packages/api/Clipboard/IClipboardService.cs`（Phase B 拆分，消费方零实现依赖）。

## 残余差距（建议）

| # | 差距 | 建议 |
|---|---|---|
| 1 | beyond-3「常用来源」快捷筛选未做 | GetSourceApps 已按计数排序，面板加一排常用来源 chip 即可 |
| 2 | 立项 DoD D1-D4/D7-D9 真机走查 | UI 相关场景需人工确认（面板已重设计，走查表可直接复用） |
| 3 | I9 语义变更 / K5 降级 | 均为实施拍板，维持 README + Phase B 横幅记录即可，无需动作 |
