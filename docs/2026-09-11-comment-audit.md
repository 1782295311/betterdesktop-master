# 危险注释审计报告（2026-09-11）

> 触发背景：`DesktopPlugin` 里一条"**禁止回退 ShowWindow**"的注释，把一次**错误归因**固化成事实，
> 直接封死了正确方案，导致"双击隐藏桌面图标"这件事排查了近 12 小时。
> 本报告按同一标准（**注释是否把未验证的因果当成事实**）扫描全仓，分四类：
> **A 伪造因果 / B 注释与代码不符 / C 绝对化禁令 / D 无从验证的具体数据**。

---

## 0. 铁证级发现：同一批崩溃，两处互相矛盾的归因

同一天（`2026-09-11`）的那批 explorer 崩溃（`comctl32.dll 0xc0000005 @0x71f2d`），在两个文件里被归因成**两个不同的原因**，且都以"实测/实证/根治"的口吻写下：

| 文件 | 当时的归因 |
|---|---|
| `packages/shell/shell-desktop/DesktopPlugin.cs`（旧版头注释） | 「ShowWindow / WM_COMMAND 0x7402 触发 comctl32 渲染路径崩溃 → 正确做法 = SetParent 摘除法」 |
| `packages/shell/shell-taskbar/Services/TaskbarAppearanceEngine.cs:41-46` | 「每秒 20+ 次 SetTaskbarAppearance 跨进程 COM 轰炸 explorer → comctl32 0xc0000005 崩溃（当日 9 次）」 |

**实际真凶（今日差分对照 + 事件日志实证）：跨进程 `LVM_HITTEST`。** 复现方法：

```powershell
# 只要发 1 次即崩；事件日志随后出现 Explorer.EXE / comctl32.dll / 0xc0000005
DesktopIconProbe.exe --op=hittest --count=1
```

> 教训：**同一条现象在两个模块被各自归因，且没人去交叉比对** —— 这是"注释写因果"最典型的破产方式。
> 结论：崩溃/根因类知识必须有**单一权威登记点**，禁止各模块自建一套解释。

---

## 1. 要命级（可能再次带偏方向）

| # | 类型 | 位置 | 危险点 | 处置 |
|---|---|---|---|---|
| 1 | A/B/C | `shell-desktop/DesktopPlugin.cs`（约 12 处：文件头、方法文档、字段注释） | 文件头整段以"崩溃根治/正确做法/实测零崩溃"口吻陈述**已被推翻**的 SetParent 摘除法；`SetNativeIconsVisible` 的 XML 文档写着「禁止回退 ShowWindow」，而方法体**已经在用 ShowWindow** | **本次已修**：改为「已证伪存档」+ 显式标注教训 + 附可复现命令 |
| 2 | A | `shell-taskbar/Services/TaskbarAppearanceEngine.cs:41-46` | 「20+ 次跨进程 COM 轰炸 → explorer 崩溃（当日 9 次）」——与 #0 矛盾；崩溃签名与今日实证同源，但**归因未做对照** | **待复核**：见 §3 复核方案。其所做的"幂等去重 + 事件防抖"本身是有效防御，**保留**；只需把因果降级为"疑似" |
| 3 | A/C | `shell-taskbar/Native/ExplorerTapBridge.cs:11` | 「必须向 explorer.exe 注入 DLL」（移植 TranslucentTB 的结论）——绝对化，前提一变就封死无注入方案 | 补"注入非唯一解，TTB 方案仅作参考"；标注是否在本机独立验证过 |
| 4 | A | `shell-desktop/Services/DesktopMenuPopup.cs:4-6` | 「本机任何非 explorer 进程 GetUIObjectOf 聚合第三方扩展**必然崩溃**（0xC0000005 实锤）——IContextMenu 管线不可在本进程触碰」 | 降级为"曾观察到崩溃"，保留重试/隔离重试的空间 |
| 5 | A/D | `shell-dock/Native/MultitaskingViewVisibilityService.cs:9` | 「契约**完全对齐** TTB …方法序…**[verified]**」——undocumented COM 方法序错一位即崩，却用"完全对齐/已验证"封住复核 | 附来源快照（TTB 提交/文件名）；`[verified]` 必须写**怎么验的** |
| 6 | A/D | `shell-start-menu/Services/PowerCommands.cs:224` | 「34 个方法序逐项对照 Open-Shell…**禁止臆造方法序**」 | 同上：附 Open-Shell 版本/快照 + 一个运行期自检 |
| 7 | C/A | `shell-status/Native/KeyboardLayoutInterop.cs:6` | 「**绝不能**用低 16 位…这正是"点了输入法却没反应"的**根因**」 | 弱化为"经验上优先用完整 32 位"，附复现步骤 |
| 8 | C | `shell-menu-bar/Windows/MenuBarWindow.cs:159`、`shell-menu-bar/Native/AppBarReservation.cs:78` | 「**绝不**读 WorkArea…坏工作区会…**无法自愈**，即根因」——同源禁令跨文件重复固化 | 合并为一条，写明**适用边界**与解除条件 |
| 9 | A/D | `shell-window-tracker/Thumbnail/ThumbnailWindow.cs:24`、`DwmThumbnail.cs:14` | 「之前多次"全黑"的**根因不在分层与否**」——排除式结论，让后来者不敢再怀疑分层透明窗这条正确方向 | 改为"当前证据指向 ①②"，保留分层为待排查项 |
| 10 | A | `scripts/build-explorertap.ps1:99-100` | 「host bin 根目录的那份若被 explorer 锁定（注入中）无法覆盖，**属正常**」——**把失败正常化**，与本次"构建因进程占用而静默失败、跑的是旧 exe"完全同构 | 覆盖失败一律判构建失败 + 部署后校验目标 DLL 时间戳/哈希 |

---

## 2. 中低危（影响可控，但建议顺手清理）

- `shell-desktop/Windows/DesktopWindow.cs:11`、`shell-core/Native/NativeMethods.cs:198`：仍称原生模式 = SetParent 摘除法（现两模式统一 ShowWindow）。
- `shell-core/Surface/ShellWindow.cs:112`：断言旧机制"是卡顿根因"（无证据路径）。
- `host/HostWatchdog.cs:32-35`：`Release()` 自身已 try/catch，「否则会崩」与现码不符。
- `host/App.xaml.cs:86-88`：`--menu-cmd-hosted` 静默装配已无触发方，注释描述死分支。
- `host/App.xaml.cs:133-135`：「只有 AppDomain 致命异常才自重启」——与启动期失败同样重启不符（绝对化）。
- `host/MenuCommandPipe.cs:4-6`：「命名管道默认 DACL 仅允许当前用户」——未显式设 `PipeSecurity`，安全结论建立在未验证前提上。
- `host/IconRestoreSentinel.cs:59-61`：DefView 现在根本不会被隐藏，该因果场景已不存在。
- `tools/GdiAudit/Program.cs`、`scripts/verify-native-convergence.ps1:5`、`VlanEnumerator.cs:584` 等：写死跨版本偏移/结构体布局/历史计数（281/311/30），建议标注实测环境或改由运行期自检。

---

## 3. 建议的复核方案（给 #2 TaskbarAppearanceEngine）

不猜、不重写，用**同一套差分方法**复验（今天已验证这套方法有效）：

1. **关掉变量**：临时禁用任务栏外观刷新（或把跨进程 COM 调用率降到 0），正常用机 1 天，统计事件日志 explorer 崩溃次数。
2. **只留变量**：恢复任务栏外观、但确保桌面图标钩子不装（`components.desktop=true` 自绘模式，钩子不安装），同样跑 1 天。
3. **对照**：两天数据一比，即可判定"跨进程 COM 轰炸"是不是真因。
4. 复核前，注释先降级为「疑似」；复核后再写结论 + 附命令。

---

## 4. 注释纪律（建议写入 CONTRIBUTING / AGENTS.md）

1. **因果必须有复现方法**：写"X 会导致 Y"时，必须同时给出可执行命令/步骤与期望现象；给不出就写"疑似"。
2. **禁令必须有边界与解除条件**：禁止"绝不可 / 唯一 / 禁止回退"式无限期禁令，改为"当前证据下优先 A；若出现 Z 现象则重新评估 B"。
3. **已证伪的结论要显式归档**：标注「已证伪存档」，并写清"当时为什么错"；不得以"实证/根治/正确做法"的口吻继续留存。
4. **不写无从验证的具体数据**：偏移、方法序、次数、百分比、日期要么给来源（版本/文件/快照），要么删掉。
5. **失败不得正常化**：任何"覆盖失败属正常""静默降级"的注释，都要写明**如何发现它发生了**（校验/告警）。
6. **注释与代码同步**：删除机制时必须同步删注释——本次 `DesktopPlugin` 里 4 处 `LVM_HITTEST` 残留注释与代码直接矛盾，就是漏同步。
7. **根因单点权威**：崩溃/根因类结论只在一处登记（如 `docs/known-issues.md`），各模块引用而非各自解释。

> 可选落地：加一条 CI 检查——对 `禁止|绝不可|唯一|实测崩溃|根因` 等词命中的注释，要求同一注释块内出现"复现/命令/步骤/疑似"之一，否则告警。

---

## 5. 本次改动清单

- **已修**：`packages/shell/shell-desktop/DesktopPlugin.cs`（12 处危险/过时注释：文件头改「已证伪存档」、方法文档去掉"禁止回退 ShowWindow"、删 4 处 LVM_HITTEST 残留描述、`_pendingTogglePoint` 用途改为"仅日志"、去掉不存在的 `DesktopIconHitTester` 引用、"三个开关值"改"两个"）。
- **未动**：其余文件仅提出建议，等复核结论后再改（避免把"建议"又写成一批未经核实的新注释）。
