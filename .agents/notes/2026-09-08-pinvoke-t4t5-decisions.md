# P/Invoke 收口 T4/T5 范围决策记录

> 2026-09-08 · 主线：T1（桶 B 20 函数/46 处收口）✅ → T2（签名漂移 packages 内归零 + host 收口）✅ → T3（slnx 构建 + run-gates 全量）→ T4/T5 决策（本文档）
> 数据源：`Temp/dup_decl_compare.py`（本轮权威 86 函数名 / 模块声明 126 / 桶 A 28 / 桶 B 0 / 桶 C 92 / 门禁 89≤281 PASS）

## T4 — 非 packages 45 处（现 28 处桶 A + 豁免）范围决策

| 范围 | 现状 | 决策 | 理由 |
|---|---|---|---|
| poc/system-status-poc | 桶 A 12 处（EnumWindows/Wlan*/GetSystemPowerStatus/WindowInterop 等）+ NativeDwm*（桶 C） | **豁免**（参考实现，不收口不改引） | 参考演示工程，非生产装配；门禁只覆盖 packages；改动收益低、风险高（poc 无引用 core） |
| docs/ 草案 | 桶 A 8 处（EnumWindows/FindWindow/GetClassName/MonitorFromPoint/MonitorFromWindow/SetWindowCompositionAttribute/TaskbarAppearanceManager-草案.cs） | **不处理**（草案不编译） | 文档草案文件，非编译产物 |
| tools/GdiAudit | 桶 A 2 处（GetProcAddress SAME/GetModuleHandle DIFF 等价） | **豁免**（独立诊断工具，无 core 引用） | 工具性质、不改依赖拓扑；若后续并入主仓再收口 |
| recovery/Program.cs | 桶 A 3 处（EnumWindows/GetClassName/ShowWindow） | **豁免**（独立恢复程序，无 core 引用） | 单文件独立程序；引 core 需加依赖，风险大于收益 |
| kernel/SystemMemoryProbe.cs | 桶 A 1 处（GlobalMemoryStatusEx SAME） | **豁免**（kernel 层不引 shell-core——反向依赖违反分层） | kernel 是 shell 的下层，不能反向引用；签名已与权威一致（SAME） |
| shell-app-source | 桶 A 4 处（CoTaskMemFree/GetProcAddress SAME + GetModuleHandle/ExtractIconEx DIFF）+ 维持 | **豁免**（既定裁决：不改依赖拓扑，csproj 仅引 kernel） | 此前已裁决；权威声明对 app-source 不可见，保留局部声明（签名已对齐权威口径） |
| shell-desktop DesktopPlugin.cs:530 SendMessage(LVHITTESTINFO) | 桶 A DIFF 但真实业务签名 | **豁免**（业务专用变体，非重复） | LVM_HITTEST 消息需 `ref LVHITTESTINFO` 传参，权威通用 SendMessage(uint,IntPtr,IntPtr) 无法表达；保留局部声明，语义独立 |

## T5 — 桶 C 92 个唯一声明审计（上提/保留）

- **本轮已上提（1）**：`WindowFromPoint`（user32）——shell-desktop DesktopPlugin 唯一调用，P/Invoke 统一平台层原则 → 权威 NativeMethods.cs + 改引。
- **保留在模块（桶 C 常态）**：业务投影/专用语义——WlanInterfaceInfoNative/WlanInterfaceState（status/menu-bar）、ACCENT_POLICY/MARGINS（taskbar/core）、LVHITTESTINFO（desktop）、MemoryStatusEx 业务读取器封装、各枚举/常量簇。
- **候选上提（后续按需，未立项）**：KeyboardLayoutInterop 的 Native 声明簇、menu-bar Bluetooth*/Power* 大簇（23 项）、context-menu 17 项、status 11 项——上提前需确认：跨模块出现 ≥2 次（变桶 B）才强制；唯一声明维持现状最稳。
- **决策规则**：桶 C 唯一声明**不主动上提**（避免无收益改动）；仅在（a）后续出现第二模块使用（自动变桶 B）或（b）函数属高频通用（WindowFromPoint 案例）时上提。

## 门禁口径（记录）

- `verify-native-convergence.ps1` 只统计 **packages/** 内非 shell-core/Native 的 DllImport 总数（现 **89 ≤ 281** PASS）。
- 桶 B/C 统计由 `Temp/dup_decl_compare.py`（全仓含 poc/docs/tools/host/recovery/kernel）独立提供，两者口径互补：门禁保证 packages 收敛趋势，compare 提供全仓重复可见性。

## 遗留（明确未做）

- poc NativeDwm*（桶 C）与窗口缩略图簇权威化差异：poc 用 NativeDwm* 命名 + DwmThumbnailNativeProperties struct，window-tracker 已改权威 Dwm*——两者互不引用，poc 维持原状（参考实现口径）。
- `GetSystemPowerStatus`（poc ref SYSTEM_POWER_STATUS vs 权威 out SystemPowerStatus）真差异——poc 豁免；若 poc 后续纳入生产再统一。
