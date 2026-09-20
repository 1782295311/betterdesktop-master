# 剪贴板文档导航（时间序索引 · 早的在前）

> **阅读纪律（2026-09-10 定）**：查剪贴板任何问题，必须**按下表顺序**通读——先立项、后阶段、最后核销。
> 只看中间某一份或只看归档蓝图，会得出过时结论（实例：09-10 核销曾误判 K5 未做，实为实施拍板降级只读展示）。
> 每份文档头部都有状态横幅标注「已收口/现行」；**冲突时以后面的文档为准**。

## 文档链（按时间升序）

| 序 | 日期 | 文档 | 状态 | 一句话定位 |
|---|---|---|---|---|
| 1 | 2026-09-08 | [`archive/2026-09-08-clipboard-history-extension.md`](archive/2026-09-08-clipboard-history-extension.md) | 🗄 已归档（蓝图仍有效） | 立项：功能全集 A-N（v1≈55/v1.1≈8/未来≈8）、三方融合矩阵、生死线、DoD D1-D9。**功能目录以它为唯一权威**，但架构决策（契约同程序集）已被 #2 升级取代 |
| 2 | 2026-09-09 | [`2026-09-09-clipboard-public-api.md`](2026-09-09-clipboard-public-api.md) | ✅ 已收口（Phase A） | 公共 API 信息源：契约升格进 `packages/api/Clipboard/`（BetterDesktop.Api），实现留 shell-clipboard，服务常驻、开关只控监控活性 |
| 3 | 2026-09-09 | [`2026-09-09-clipboard-phase-b.md`](2026-09-09-clipboard-phase-b.md) | ✅ 已收口（Phase B = v1.3 终态） | 用户拍板 v1.3 一次收口：面板 UI/热键/自动分段/按类写回/消费板块（I6-I10）/按序粘贴（L 组提前）/G6 配置化；K5 实施拍板降级只读展示 |
| 4 | 2026-09-10 | [`../audits/2026-09-10-clipboard-plan-completion-review.md`](../audits/2026-09-10-clipboard-plan-completion-review.md) | 📋 现行（随代码演进更新） | 对照核销：v1 55/55 ✅、v1.1 6✅+1 语义变更+1 降级、UI 三轮重设计超越立项 |
| 5 | 2026-09-13 | [`2026-09-13-clipboard-tiez-p1p2.md`](2026-09-13-clipboard-tiez-p1p2.md) | ✅ 已实施（D1/D2/D4 待人工） | TieZ 对标剩余项：P1-4 命名格式透传 / P1-5 按应用清洗规则 / P2×4（渐进富文本探测·敏感遮罩·临时粘贴·标签补全）。核销与设计见文；来源 = [`../audits/2026-09-13-tiez-comparison.md`](../audits/2026-09-13-tiez-comparison.md) §7 |
| 6 | 2026-09-13 | [`2026-09-13-clipboard-cell-sequential-paste.md`](2026-09-13-clipboard-cell-sequential-paste.md) | ✅ 已实施（D1-D3 待人工） | 用户新需求：Excel 表格**逐格**粘进业务系统（「按格粘」）。纯客户端 + 面板、**引擎零改动**；粘后自动按键由用户选（Tab/Enter/不自动） |

## 关键事实速查（免重读全链）

- 终态版本：**v1.3**；单测 85 例全绿；构建门禁 0 警告 0 错误。
- 热键三件套：Ctrl+Shift+V / Ctrl+Shift+P / Ctrl+Shift+Backspace；**不可配置**（设置分区只读展示，Manager 常量锁定）。
- I9 自绘右键入口：随自绘菜单管线退役（2026-09-05），由 I10 系统右键 4 场景覆盖同语义。
- 面板 UI：2026-09-09 三轮重设计（卡片化/真实 Shell 图标/暂停横幅），2026-09-09 迁入 `PopupWindowBase` 统一弹窗基类（含搜索框 → `UseNoActivateWindowStyle=false`）。
- 实现包 README：`packages/shell/shell-clipboard/README.md`（2026-09-10 已同步 v1.3 终态）。
- 技术力库：`TECH-KNOWLEDGE/13-剪贴板/1301-clipboard-history.md`（2026-09-10 已补 C# 变体指针）。
