# 托盘 / 更新 / 功能解耦 落地路线（对齐"草特码工具"形态）

> 目标（用户原始诉求）：
> ① 做出与 CTM 一样的**更新功能**；② 做出**系统托盘功能**；③ **让大部分功能在退出主程序后仍能正常运行**。
>
> 本文件是执行路线，不是设想：每一项都标注了**现有可复用件**（文件:行）与**验收方式**。

---

## 一、参照物：CTM 的组件形态（实测安装目录）

```
草特码工具.exe            3.4MB  ← 只是 UI 壳（火山视界 + WebView2）
Resource\CTM\
  ExplorerTAP.dll         260KB  ← 注入 explorer 的 DLL（★ 退出主程序后功能仍活的真正原因）
  ResourcemonitoringCtrip.exe 542KB ← 常驻资源监控进程
  Safetycenteroperation.exe   387KB ← 独立功能进程
  Renewal.exe             394KB  ← 更新/续期程序（独立进程）
  CTMSettings.exe         340KB  ← 设置 UI（按需拉起）
  ProgramLog.dll          383KB  ← 日志组件
  Settings.dll            5.5MB  ← 设置资源
```

**结论：CTM 的"退出主程序后功能仍在"不是靠主程序后台常驻，而是靠"功能不在主程序里"** —— 功能被放在三种地方：
1. 注入 explorer 的 DLL（生命周期 = explorer，与主程序无关）；
2. 独立常驻进程（监控/安全）；
3. 按需拉起的独立 exe（设置/更新）。

---

## 二、我们的现状（勘探结论摘要）

| 维度 | 现状 | 证据 |
|---|---|---|
| 系统托盘 | **完全没有**。菜单栏里那个 `SystemTrayIcon` 是"接管式自绘"，随宿主进程销毁 | `shell-menu-bar/Status/MenuBarStatusStrip.cs:1704,1775,2032` |
| 更新 | **完全没有**。无 version.json、无 publish 脚本、无 CHANGELOG、无 `--version/--update` 命令 | `Directory.Build.props:12`；`docs/build-release.md:36`（发布门禁未落地）|
| 功能承载 | **全在宿主进程内**（WPF 窗口 + 钩子 + DispatcherTimer），宿主退出即全部停止 | `Bootstrap.cs:74-190`、`DesktopPlugin.cs` |
| 已有常驻件 | 仅 `watchdog`（外部看门狗，会在 ~11s 内把宿主重新拉起）与 `recovery`（人工兜底） | `watchdog/Program.cs:34-60` |
| 已有注入件 | **有**：`ExplorerTAP.dll` + `ExplorerTapBridge`（移植 TTB 方案）—— ③ 的关键资产 | `shell-taskbar/Native/ExplorerTapBridge.cs:11` |
| 已有下载能力 | `EngineDownloader`：多镜像回退 + 大小/SHA256 校验 + 单实例门控 + 半包清理（可复用骨架） | `shell-convert/Services/Engines/EngineDownloader.cs:38,74,140` |
| 已有重启能力 | `HostWatchdog.RestartFromFatal`（拉新进程 + Exit） | `host/HostWatchdog.cs:49-73` |
| 部署约束 | 单文件自包含优先、插件禁 AOT、**CLI 必须与 Host 同目录**、`packages.lock.json` 锁定依赖 | `docs/build-release.md:26-28` |

---

## 三、目标架构（三层解耦）

```
┌─ UI 壳：BetterDesktop.Host.exe ────────── 可随时退出/重启，退出不影响下面两层
│    设置中心 / 自绘桌面 / 菜单栏 / Dock / 状态面板（WPF）
│
├─ 常驻 Agent：BetterDesktop.Tray.exe ★本轮已落地 ── 独立进程、零包引用、开机自启
│    托盘图标 + 控制菜单（功能开关 / 更新入口 / 应急恢复 / 组件状态）
│    通过既有 CLI 命令契约驱动一切（宿主在→管道热切；宿主不在→直写设置）
│
└─ 功能组件（可脱离宿主独立活）
     ├─ ExplorerTAP.dll        注入 explorer → 任务栏外观（生命周期=explorer）
     ├─ 桌面图标服务（待下沉）   钩子 + ShowWindow，无 WPF 依赖 → 独立小进程
     └─ BetterDesktop.Updater.exe（待落地）→ 独立进程，替换/回滚/重启
```

**判定原则**：一个功能是否该下沉，看它**是否需要 WPF 视觉**。
需要（自绘桌面/菜单栏/Dock/面板）→ 留在壳；不需要（图标显隐钩子、任务栏外观、状态采集）→ 下沉为独立进程或注入件。

---

## 四、切片 1：系统托盘（✅ 本轮完成）

新增 `tray/BetterDesktop.Tray.csproj`（WinExe / x64 / WinForms / **零包引用**）：

| 文件 | 职责 |
|---|---|
| `tray/Program.cs` | 单实例互斥（`Local\BetterDesktop.Tray.SingleInstance`）、异常兜底、`--selftest` 自检入口 |
| `tray/TrayApplicationContext.cs` | 托盘图标 + 菜单 + 全部行为（功能开关 5 项 / 检查更新 / 重启主程序 / 开机自启 / 日志目录 / 应急恢复 / 组件状态 / 关于 / 退出）|
| `tray/ProcessBridge.cs` | 启停宿主、调用 CLI 契约、拉起更新器/恢复程序 |
| `tray/SettingsBridge.cs` | **只读** settings.json（菜单勾选状态）+ `AutoStart`（HKCU\Run） |
| `tray/PipeProbe.cs` | 探测 `\\.\pipe\BetterDesktop.MenuCmd` 就绪（协议仍单点在 CLI/kernel 实现）|

三条实测纪律（都踩过坑才写进来）：
1. **发命令前必须等管道就绪**：CLI 在无宿主时对"需宿主动作"会弹原生对话框并阻塞约 10s（自检实测 9.8s）；
2. **自检必须在互斥锁之前**：否则托盘已在运行时无法排障；
3. **写入一律走 CLI**：托盘只读设置，避免与 CLI 出现两套写语义。

验收（已通过）：
```
BetterDesktop.Tray.exe --selftest      # 31ms 完成，无弹窗
  [OK] 部署目录 / 主程序 Host / 命令契约 CLI / 看门狗 / 应急恢复
  [!!] 更新器 Updater：未部署（切片 2）
  [OK] 命令契约实测：宿主未运行，跳过实测（CLI 就位即可）
```
常驻实测：进程存活、图标注册成功（`%LOCALAPPDATA%\BetterDesktop\logs\tray-<date>.log`）。

---

## 五、切片 2：更新功能（✅ 本轮完成并实测）

**版本单一源**：`scripts/publish.ps1` 从**构建产物**生成 `version.json`（`product/version/build/informational/channel/publishedAt`）
与 `manifest.json`（每个文件 `path/size/sha256` + `minHost/notes`）。版本号不再手抄，杜绝"源码版本与产物版本漂移"。

**`scripts/publish.ps1`（发布 = 合并 + 门禁 + 清单）**：
- 把 Host / Cli / Tray / Updater / Watchdog / Recovery 六个组件 publish 后**合并到同一个目录**
  （部署纪律：CLI 必须与 Host 同目录）；
- **完整性门禁**：六个 exe + `cordis.yml` 缺任一即 `PUBLISH FAILED` 且非零退出（半成品绝不发出去）；
- 生成 `version.json` + `manifest.json`；清理 `.pdb/.xml`；
- 实测：`dist\BetterDesktop-2026.09.11.0807`，151 个文件 / 89MB / `build=2026.09.11.0807`。

**`updater/BetterDesktop.Updater.csproj`（独立 exe，零包引用）**：

| 参数 | 行为 | 实测结果 |
|---|---|---|
| `--check` | 读本地 `version.json`，拉远端 `manifest.json`，比对 **build 时间戳**（同版本号下的正确判据） | 同版本 → 退出码 **0**；模拟旧安装 → 退出码 **10** 且提示"发现新版本" |
| `--download` | 多源回退（`--source A;B;C`，支持 HTTP(S) 与本地目录/UNC）→ 逐文件 **SHA256 校验** → 落地暂存目录 + 安装计划 | 退出码 **11**，暂存含 `update-test.txt` + `manifest.json` |
| `--apply` | 等宿主退出（60s）→ 备份清单内文件到 `backup-<stamp>` → 替换（占用文件先改名 `.old-<stamp>`）→ 重启宿主；失败自动回滚 | 退出码 **12**，文件内容由 `OLD-CONTENT` 变为 `NEW-CONTENT`，哈希精确匹配 |
| `--rollback` | 从最近备份还原并重启 | 退出码 **13**，内容与哈希精确还原为 `OLD-CONTENT` |

其他约定：
- `--no-restart`（脚本化/CI 用：只替换不重启）、`--quiet`（托盘拉起时不闪控制台）、`--staging <目录>`；
- 退出码是对外契约：`0/10/11/12/13` 正常语义，`2 参数错误 / 3 源不可达 / 4 清单无效 / 5 应用失败（已尝试回滚）`；
- 结果写 `%LOCALAPPDATA%\BetterDesktop\update-status.json`，**托盘读它弹气泡**（与更新器零 IPC 耦合）；
- 更新源配置：组件同目录 `update.config.json`（`{"source":"https://... 或 \\\\share\\dir"}`），支持分号分隔多镜像。

**两条被实现约束逼出来的关键机制**：
1. **不能覆写正在运行的 exe** → 采用"重命名旧的 + 落新的"；本更新器自身也在被替换目录里，它的旧 exe 留到**下次启动清理**（`Applier.CleanupOldArtifacts` 在每次启动先跑）；
2. **必须先确保宿主退出**（它持有窗口/钩子/文件句柄）→ `WaitHostExit(60s)`；超时则**一个文件都不动**并明确报错。

**托盘联动**：菜单新增「下载并安装更新…」，由**托盘编排**"下载 → 主动退出主程序 → 替换 → 自动重启"（宿主正常退出才能走图标/任务栏恢复），更新器内部仍会再等一次宿主退出作为双保险。

**尚未补齐（非阻塞）**：`verify-release.ps1`（发布门禁脚本）、`CHANGELOG.md`、`update.config.json` 样例随包分发。

### 补跑验证记录（2026-09-11 19:1x，代码复核后的修复验证）

| 验证项 | 方法 | 结果 |
|---|---|---|
| **回滚不留半更新** | 合成清单（`update-test.txt` 已存在 + `new-file.txt` 新增）跑 download→apply→rollback | 退出码 **11 / 12 / 13**；rollback 后 `update-test.txt` 精确还原为 `OLD-CONTENT`，**`new-file.txt` 被删除**；日志：`登记本次新增文件 1 个（回滚时删除）` → `已删除本次新增的文件 1 个` |
| **清单无 BOM** | `publish.ps1` 产出后读前 3 字节 | `manifest.json` / `version.json` = **123,13,10**（`{`CRLF），首字符 `{`，`ConvertFrom-Json` 可解析，`fileCount=156` |
| **完整性门禁** | 发布输出组件清单 | 7 个 exe 齐全（新增 `BetterDesktop.Agent.exe`） |
| **HTTP 更新通道**（此前因 BOM 必失败） | 本地 `python -m http.server` 服务 dist 当源 | 同版本 → **0**；旧版本安装 → **10**；状态文件正确写出 |
| **进程归属判定** | 补跑中发现并修复的缺陷 | `WaitHostExit` 原按**进程名全局**判定 → 会等别处（开发 bin/另一份安装）的 Host/Agent，**空等 60s 后拒绝更新**；改为按**映像路径归属**（`IsRunningFrom`），超时提示列出目标实例 `pid + 路径`。修复后实测：`目标目录的 Host 与 Agent 均已退出（等待 14ms）` |

> 说明：表中第一项在**修复前**那一次是「退出码 5、未改动任何文件」——即"拒绝半更新"的逻辑本身是对的，
> 正是这次失败暴露了上面的进程归属缺陷。**补跑的价值就在这里：两个缺陷都是跑出来的，不是想出来的。**

---

## 六、切片 3：让功能在退出主程序后仍运行（待落地）

按"是否需要 WPF 视觉"分批下沉，**优先级从高到低**：

1. **桌面图标服务**（最高优先，因为实现已被实证且无 WPF 依赖）
   - 现状：钩子 + 显隐逻辑在 `DesktopPlugin`（宿主内），宿主退出即停；
   - 目标：抽成 `BetterDesktop.DesktopIconService.exe`（或并入 Tray 进程内的一个后台线程），含：
     WH_MOUSE_LL 自判定双击 → 类名白名单 → `ShowWindow(SysListView32)`；**点击路径零跨进程消息**（今日实证：1 次 `LVM_HITTEST` 即崩 explorer）；
   - 状态与主程序共享：只读写 `settings.json` 的 `desktop.iconsHidden`，与宿主同键同语义；
   - 由托盘负责拉起/停止（托管在 `HKCU\Run` 或托盘启动时带上）。
2. **任务栏外观**：继续走 `ExplorerTAP.dll` 注入 —— 注入代码活在 explorer 内，**天然满足"退出主程序仍在"**；需要补的是"注入后如何被托盘按需卸载/重装"。
3. **状态采集（电池/CPU/内存）**：若要在主程序退出后仍能从托盘看到，需把采集下沉到 Tray 进程（只读 WMI/性能计数器，无 WPF）。
4. **必须留在壳里的**：自绘桌面、菜单栏、Dock、各类面板（需要 WPF 视觉）—— 用户退出壳时它们停止是**正确行为**，但托盘要能一键拉回。

验收：关闭/结束宿主进程后 → ① 托盘仍在并能开关功能；② 双击桌面仍能切换图标显隐；③ 任务栏外观保持；④ 托盘"打开设置中心"能把壳重新拉起。

---

## 七、纪律（引用本仓库今日教训，避免重复踩）

1. **跨进程窗口消息禁令**：桌面窗口链上不得发 `LVM_*`/`WM_COMMAND`/`SendMessageTimeout`（实证 1 次 `LVM_HITTEST` 即打崩 explorer，comctl32 0xc0000005 @0x71f2d）。新功能若需"命中测试"，用消息自由方案。
2. **失败不得正常化**：更新/替换/构建失败必须显式失败 + 可发现（参见 `build-explorertap.ps1:99` 的反面例子）。
3. **注释即契约**：因果结论必须附可复现命令（`docs/2026-09-11-comment-audit.md` §4）。
4. **单点实现**：设置写入只经 CLI；管道协议只在 kernel/CLI；版本号只在 `version.json`。

---

## 八、落地顺序与进度

1. ✅ **切片 1 托盘** —— 已完成，`--selftest` 通过，常驻运行中；
2. ✅ **切片 2 更新**（`publish.ps1` 版本单一源 + `manifest.json` + `BetterDesktop.Updater.exe` 四动作）—— 已完成并逐项实测（0/10/11/12/13 全部符合预期）；
3. ⏳ **切片 3 功能解耦**：桌面图标服务下沉 + 托盘托管（直接兑现"退出主程序后功能仍运行"）；
4. ⏳ 收尾：`verify-release.ps1` 门禁、`CHANGELOG.md`、`update.config.json` 样例。
