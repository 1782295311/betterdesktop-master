# 应用提取器：程序表与过滤规则反推（2026-09-13）

> **数据来源**：索引引擎 `list_apps`（真机 **1951** 项候选，构建 214ms，2026-09-13）。
> **判定口径**：保留/过滤 = 直接调用生产代码 `AppSourceService.ResolveFromPath(path)`（权威，不重写逻辑）；原因归因 = 复刻其私有谓词的规则表逐条定位。**两者 mismatch = 0**（1951 项逐项一致），故下表可信。
> **附表**：`2026-09-13-app-index-program-table.tsv`（1951 行；列 = verdict / reason / authoritative / engineSource / resolvedName / resolvedTarget / path）
> **复现**：见文末 §七。
> **落地状态（2026-09-14）**：§7.4 的**过滤层**已实现 —— `packages/shell/shell-app-source/Services/AppFilterRules.cs`（规则表，纯函数）+ `AppSourceService.ApplyAllProgramsFilter`（接入全程序模式的**引擎与本地两条产出路径**）。开关 `BETTERDESKTOP_APP_FILTER` **默认关**（过滤改变条目集属行为变更，需真机对比两档后再定默认）；生效时记 Info「过滤生效：N → M 项」。**§7.4 的范围层**也已实现（口袋目录 `app-source.extra-roots` + 桌面快捷方式 `app-source.scan-desktop-shortcuts` 默认纳入 + 手动添加应用）。文档化为 `TECH-KNOWLEDGE/05-图标/507-app-filter-rules.md`。**实现期修正 3 处**：① `jre`/`jdk` 裸前缀会误杀 `Jreport`/`Jdkeeper` → 改版本化前缀；② 安装根在浅目录下退化为盘根（`D:\Gone` 与 `D:\Other` 同根）→ 盘根不算根；③ 裸 `runtime` 不滤（§6.1 的谨慎项已确认）。

---

## 一、结论速览

| 指标 | 数值 |
|---|---|
| 引擎候选（磁盘两源） | **1951** |
| 通过过滤链（**可显示**） | **1487** |
| 被过滤 | **464** |

**三条反推结论**：

1. **漏网 111 项**（保留项里不该出现在应用提取器的）：服务/守护进程 43、崩溃与遥测 22+11、控制台工具 9、驱动 9、运行库分发 6、调试 7、补丁 3、中文「修复/辅助/日志」9。现有五道闸门**一条都盖不住**这些模式 → 需新增过滤维度。
2. **误杀风险**（子串匹配打在 lnk 描述上）：`IsExcludedName` 用**无边界子串包含**匹配 `displayName`，而 `ResolveLnk` 把 **lnk 的 `GetDescription` 当显示名**——本机实测 **4 例**显示名是整句文案（「选择特殊字符并且复制到文档中。」等）。本机结果可接受，但机制上无保护：任何 lnk 描述含「帮助/说明/更新/安装/下载/许可/教程/隐私」的**真实应用**都会被静默滤掉。
3. **结构性问题**：保留项中 **1341 项来自 Program Files（1280 个裸 `.exe`）**，只有 **146 项来自开始菜单（149 个 `.lnk`）**。即磁盘盘点列表 **90% 是 Program Files 内部实现文件**。用户可感知的「应用」几乎等价于开始菜单 `.lnk` 集合。

> **口径说明（重要）**：应用提取器 `AppGrabberWindow` 有**两种模式**（`AppGrabberWindow.cs:2`、`DockAppsService.cs:375-498`）：
> - **干净模式** = `ScanStartMenu()`（开始菜单）+ 注册表已安装 → 本表 **start-menu 保留 146 项** ≈ 该模式输入，**本身已经干净**；
> - **全程序模式** = `ScanAllPrograms()`（磁盘盘点）→ 本表 **program-files 保留 1341 项**，第三节的漏网项全部落在这里。
>
> 故「该过滤什么」的主战场是 **全程序模式**；干净模式的问题在第四节（误杀风险）。

> **更根本的语义口径（见 §七）**：本表枚举的是「磁盘上有哪些可执行文件」，**不是**「用户会双击启动的应用」。
> 实测：保留项里 **536 项（36%）** 是 CLI / 工具形迹（`\bin\`、`\Scripts\`、jdk、Python），其余 951 项仍含大量
> 服务 / 开发工具 / 驱动；而**便携与脚本类工具（MAA / OneDragon 一类）根本不在索引范围**——三个入口都看不见。

---

## 一之二、⚠️ 口径修正（2026-09-13 二次实测，务必先读）

本表用 `ResolveFromPath` 判「可显示/被过滤」，但二次实测发现 **`ScanAllPrograms`（全程序模式）根本不调用它**：

| 扫描入口 | 是否过 `ResolveFromPath` | 实测条目 |
|---|---|---|
| `ScanStartMenu` → `ScanDirectory`（`:551-577`） | **是**（每个文件走 `ResolveFromPath`） | 开始菜单 .lnk 过滤后 **146** |
| `ScanInstalledApps`（注册表） | 是（`:213-219` 内建过滤 + `ResolveFromPath`） | 65ms，未细算 |
| **`ScanAllPrograms` → `EnumerateExecutablesRecursive`（`:777-838`）** | **否**——只有 `IsSupportedFile` 扩展名闸门，然后 `Resolve` + **无条件 Add** | **1789 项原样** |

**实测对照**（同批候选）：
```
本地 ScanAllPrograms（原样）                 = 1789
同批候选经 ResolveFromPath                   = 1476   → 313 项会被拒
```
**结论**：**全程序模式今天几乎不过滤**（uninstall.exe / WinProcessListHelper.exe / SUPInstall.exe 等全部照显）。这正是它「条目多但全是垃圾」的根因。

> 因此本表 `filtered/kept` 列描述的是 **`ResolveFromPath` 语义**（= 干净模式现状 + 全程序模式「若启用过滤链」的结果），**不是全程序模式的现状**。第三节的 111 项是**连 `ResolveFromPath` 也拦不住**、需新增规则的部分；313 项是**现有链能拦但全程序模式没启用**的部分。

---

## 二、当前过滤链（五道闸门）与实测命中

判定顺序即下表顺序（`AppSourceService.ResolveFromPath`，`:478-515`）：

| # | 闸门 | 判据 | 实现位置 | 实测命中 |
|---|---|---|---|---|
| 1 | 扩展白名单 | 扩展 ∈ {exe,bat,cmd,com,msc,appref-ms,url,lnk} | `ShellLinkResolver.cs:16-26` | **0**（引擎已与之一致） |
| 2 | 排除名 | 显示名**子串包含** 72 个词 | `AppSourceService.cs:73-85`、`:672-689` | **311** |
| 3 | 文档目标 | 目标扩展 ∈ 17 种文档格式 | `:88-93`、`:691-720` | **4** |
| 4 | 可执行目标 | 目标扩展 ∉ {exe,bat,cmd,com,msc} | `:722-740` | **44** |
| 5 | 系统目录 | 目标以 11 个系统路径开头 | `:742-773` | **105** |

> 闸门 4 的实际作用：`ResolveLnk` 对**目标缺失**的 lnk 回退到自身路径（`.lnk`），于是被闸门 4 判为非可执行 → 丢弃。即「损坏/失效快捷方式」由闸门 4 兜住（44 项大头在此）。

---

## 三、反推①：漏网 —— 连过滤链也拦不住的 111 项

> 定位：这 111 项**通过了 `ResolveFromPath` 全部五道闸门**（即「现有过滤链也无法识别」），因此无论走
> 干净模式还是（未来启用过滤链的）全程序模式，都需要**新增规则**才能滤掉。
> 另有 **313 项**是现有闸门**本来就能拦**、但全程序模式**没启用**过滤链而漏出的（见 §一之二），
> 那部分不需要新规则，只需要让全程序模式接入过滤（或改用引擎 + `ResolveFromPath`）。

按关键词归因（同一项可能命中多词，故合计 > 111）：

| 模式 | 命中 | 典型样例（显示名 → 路径） |
|---|---|---|
| 后台服务 / 守护 | 43 + 6 | `ARMOURY CRATE Service`、`Steam Client Service`、`vmware-authd.exe`、`Tomcat11.exe`（Commons Daemon）、`gpg-agent.exe` |
| 崩溃 / 遥测上报 | 11 + 11 | `crashreport.exe`（原神）、`UnityCrashHandler64.exe`、`crashpad_handler.exe`（微信）、`VBoxBugReport.exe`、`VBoxCpuReport.exe` |
| 控制台工具 | 9 | `7-Zip Console`（7z.exe，3 处）、`Info-ZIP Zip for Win32 console`、`Console`（Tesseract） |
| 驱动 / 辅助 | 9 + 6 | `AsIO3 Driver`、`docker-machine-driver-vmware`、`360安全卫士 系统修复图标扫描模块`、`输入法修复器` |
| 运行库 / 分发 | 6 | `Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33130`（→ `vc_redist.x64.exe`，多处） |
| 运行时（非应用） | 7 | `Node.js JavaScript Runtime`（node.exe）、`Battle.net Overlay Runtime` |
| 调试 | 7 | `docker-debug`、`blender_debug_gpu_glitchworkaround.cmd`（3 个变体） |
| 补丁 / 构建工具 | 3 | `hpatchz.exe`（原神热更）、`WiX Toolset Patch Builder` |
| 中文：修复/辅助/日志 | 6+1+2 | `360安全卫士 日志中心模块`、`Office 遥测日志`、`360杀毒 辅助程序` |

**观察**：这些几乎全部来自 **Program Files 内部实现文件**（服务宿主、崩溃处理器、运行库），不是用户会主动启动的「应用」。它们之所以漏网，是因为显示名里恰好没有 72 个排除词中的任何一个。

---

## 四、反推②：误杀风险 —— 子串匹配打在 lnk 描述上

**机制**：`ResolveLnk`（`ShellLinkResolver.cs:92-97`）用 `IShellLink.GetDescription()` 作为显示名，**仅在描述为空时**才退回文件名。而很多 lnk 的描述是**整句文案**。

**本机实测 4 例**（显示名即整句，被 `帮助`/`文档` 子串命中）：

| 显示名 | 命中的排除词 |
|---|---|
| `帮助你与电脑交互，并通过语音听写文本。` | 帮助 |
| `读出屏幕上的文本、对话框、菜单以及按钮(如果扬声器或者声音输出设备已安装的话)。` | 安装 |
| `创建精美的文档、轻松地与其他人共用并体验阅读。` | 文档 |
| `选择特殊字符并且复制到文档中。` | 文档 |

**判定**：这 4 例都是 Windows 辅助功能/Office 工具目录项，被滤掉的结果**可接受**；但**原因是偶然的**——换一个「描述句里带『下载』的真实应用」就会被静默误杀。

**风险等级**：中高。机制上**无边界保护**（`lowerName.Contains(exclude)`，`:682`），任何名称**任意位置**出现该词即命中。

---

## 五、反推③：结构性问题 —— 列表 90% 是 Program Files 裸 exe

| 保留项分组 | 数量 | 占比 |
|---|---|---|
| program-files（`.exe` 1280 / `.bat` 31 / `.cmd` 25 / `.com` 2） | 1341 | **90.2%** |
| start-menu（`.lnk` 149） | 146 | 9.8% |

即应用提取器**全程序模式**里，**每 10 个条目有 9 个是安装目录里的内部实现文件**（含第三节列出的服务/崩溃处理器/运行库）。这解释了为什么该列表体量大但可用性低——而干净模式（开始菜单 + 注册表）已天然规避了这个问题。

---

## 六、建议规则（供后续 `filters.ini` 外置参考）

### 6.1 新增硬过滤（补齐第三节 111 项，建议按**文件名/目标路径**判定，而非显示名）

| 维度 | 模式 | 备注 |
|---|---|---|
| 后台组件 | `service`、`daemon`、`svc`、`agent`（谨慎：`agent` 易误杀） | 优先用目标路径判定 |
| 崩溃遥测 | `crash`、`crashpad`、`bugreport`、`crashreport`、`telemetry`、`日志` | |
| 运行库分发 | `redist`、`vcredist`、`runtime`、`dotnet`、`vcruntime` | |
| 驱动辅助 | `driver`、`helper`、`修复`、`辅助`、`patch` | |
| 控制台 | 目标名含 `console` 或位于 `\bin\` 且为 `.exe` | 建议**降权**而非硬滤（少数用户确实要 7z.exe） |
| 调试 | `debug`、`dump`、`dumpbin` | |

### 6.2 匹配方式修正（修第四节的机制缺陷）

1. **优先按文件名/目标路径判定**，`lnk` 描述仅作**次要**信号（描述是文案，文件名才是标识）。
2. 若必须匹配显示名：改为 **token 边界匹配**（按空白/标点切词后整词比较），而非 `Contains`。
3. 出现「整句描述」（含 `。`/长度异常）时**不参与排除判定**，改用文件名。

### 6.3 结构性建议（修第五节）

- **默认列表 = 开始菜单 `.lnk`（146 项）**；Program Files 裸 `.exe` 移入「全部程序（高级）」或需显式开启。
- 若保持单列表：对 Program Files 项**要求存在同名开始菜单快捷方式**才默认显示（去重后即「有快捷方式的应用」），无快捷方式者降权。

### 6.4 边界（不要修）

- 闸门 4（可执行目标）承担的「失效快捷方式过滤」是**有意行为**，建议保留（否则开始菜单会出现一堆打不开的残留项）。
- 506「跳 `\startup`」红线：**当前 betterdt 未落实**（`ScanDirectory:452` 无 startup 过滤），引擎为结果集平价也未跳。若要补，需 C#/引擎同批改。

---

## 七、用户视角复核 —— 「可执行文件」≠「用户会双击启动的应用」

> 前六节是**计数视角**（只统计保留/过滤与命中数，未逐条看条目）。本节按用户提问补做**逐条复核**。
> 结论：本表（以及现有过滤链）回答的是「磁盘上有哪些可执行文件」，**不是**「用户会双击启动的应用」——
> 这个口径差是列表可用性差的根本原因。

### 7.1 保留项里有多少是「程序员在环境里用、没人双击」的

保留 1487 项按安装形迹归类（实测）：

| 形迹 | 数量 | 典型条目 |
|---|---|---|
| `\bin\*.exe`（任意 bin 目录） | 456 | `getopt.exe`、`curl.exe` … |
| 其中 `\usr\bin\`（Git / MSYS2） | 246 | grep / sed / awk / ssh 一类 |
| `jre/jdk*\bin\` | 72 | `javac` / `jar` / `keytool` / `jshell` |
| `\Python*\` 安装目录 | 52 | `python.exe` / `pythonw.exe` |
| `\Scripts\*.exe`（pip 控制台脚本） | 48 | `fastapi.exe`、`uvicorn.exe`、`pip3.14.exe`、`f2py.exe`、`ttx.exe`、`torchrun.exe`、`pygmentize.exe` |
| `\nodejs\` | 5 | `node.exe` / `npm.cmd` |
| **去重并集** | **536（36%）** | |

**且「非 CLI 形迹」的 951 项里仍混着大量套件附属二进制**——但这里**必须区分「主程序」与「工具链/附件」**（2026-09-13 用户修正：整块按供应商目录滤是错的）：

| 类型 | 示例 | 干净模式该不该留 |
|---|---|---|
| **套件主程序** | `Ssms.exe`（SQL Server Management Studio）、`devenv.exe`（VS 2022）、`vmware.exe` | **该留** |
| 编译 / 构建链 | `Roslyn\csc.exe`、`MSBuild.exe`、`VC\Tools\MSVC\*\bin\`（`cl.exe`/`link.exe`）、`Windows Kits\10\bin\*`（`signtool`/`makeappx`） | **该滤** |
| 套件附属工具 | `Common7\IDE\ControlService.exe`、`ssbdiagnose.exe`、`Tesseract-OCR\tesseract.exe`、`Docker\cli-plugins\*` | **该滤**（多数是 CLI/服务） |
| 驱动 / 服务 | `NVIDIA Corporation\*`、`ASUS\*Service.exe`、`ldplayer9box\*` | **该滤** |

→ 正确粒度是**同一套件内区分主程序与工具链/附件**，而不是「整个 `Program Files\<Vendor>` 一律滤掉」。
§7.1 表里的 `SQL Server… 33`、`Windows Kits 17` 正是这类混装目录（主程序有用、编译器无用）。

### 7.2 便携 / 压缩包下载的工具类：机制上不可能被覆盖

索引根集 = `X:\Program Files*` + 开始菜单（用户 + 公共）+ `%LocalAppData%\Programs`，**此外一律不扫**。
因此任何「解压即用、不安装」的应用（**MAA**、**OneDragon** 一类）：

| 条件 | 后果 |
|---|---|
| 放在 `D:\SomeTool\`、`Desktop\SomeTool\`、`Downloads\SomeTool\` 等位置 | **不在任何根集** → 全程序模式扫不到 |
| 没有注册表卸载项（未安装） | 干净模式的注册表分支找不到 |
| 没有开始菜单快捷方式 | 干净模式的 lnk 分支找不到 |

→ **三处（干净模式 / 全程序模式 / 索引引擎）皆不可见**；且**这不是过滤规则能修的**（属扫描范围）。

> 本机未命中 `MAA` / `OneDragon` 本体（用户外部举例）；上述结论由根集定义直接推出，不依赖具体样例。
> （注：`Desktop\fangzhoujiaoben`、`Desktop\自动化脚本` 等目录是**用户自建的实验项目**，不属本议题样例，已从证据中移除。）

**次生缺口**：**桌面快捷方式也不参与索引**——实测存在 `Desktop\自动化脚本\网易云音乐.lnk`。
而桌面恰是用户最主要的手动启动入口，「用户主动放桌面」本身是极强的「我在意这个应用」信号，目前完全没用上。

### 7.3 「正经安装的程序也没做到百分百」的机制原因

干净模式输入 = 开始菜单 `.lnk` + 注册表 Uninstall 项，漏两类：
1. **安装时不建开始菜单快捷方式**的程序 → 只能靠注册表 `DisplayIcon` / `InstallLocation` 兜（`ResolveInstalledExecutable`）；
   这两个值缺失或无效（`DisplayIcon` 指向不存在文件很常见）→ 该程序彻底漏掉。
2. **绿色/便携但对用户是常规应用**的（解压即用工具）→ 与 7.2 同因，完全不在扫描范围内。

### 7.4 建议（三层：注意「过滤」解决不了「范围」）

| 层 | 手段 | 解决 |
|---|---|---|
| 过滤层（治标） | CLI 形迹硬过滤：`\bin\`、`\Scripts\`、`jre/jdk*\bin`、`\usr\bin`、`\nodejs`、`Windows Kits`、`Tesseract-OCR`、`Docker` 等 | §7.1 的 536 项起步 |
| **范围层（治本）** | ① 设置中心加「口袋目录」自定义根；② **桌面/下载快捷方式纳入扫描**；③ 复用已有 `UserAdded` 手动添加通道 | §7.2 便携 / 脚本类看不见 |
| 信号层（提升识别率） | 注册表兜底加强（`InstallLocation` 顶层唯一 exe + `App Paths`）；桌面 lnk → 解析目标并合并去重 | §7.3 未建快捷方式的安装程序 |

---

## 八、复现方式

1. 构建引擎：`& "$env:USERPROFILE\.cargo\bin\cargo.exe" build --release`（cwd = `engine-index`）。
2. 取程序表：临时控制台工程引用 `shell-index-ipc` + `shell-app-source`，`EnsureEngine` → `ListAppsAsync` → 逐项 `ResolveFromPath` 判定 + 规则归因 → 写 TSV（本表生成时 mismatch = 0）。
3. 本文件 §三/§五 的统计由该 TSV 直接分组得出。

> 生成工具为一次性分析工具，已按仓库临时文件纪律删除（未入库）；程序表与本报告为交付物。
