# Cairo 开发计划 · 免宿主右键快捷功能（双路原生扩展）

> Task: 让「桌面控制」「格式转换」两个快捷功能在**主程序未运行**时也能从资源管理器右键菜单直接可用，**同时进入 Windows 11 新版右键菜单第一层与经典菜单**（形态对齐压缩软件），支持多选批量；显示内容由主程序配置、未来新增功能不改原生代码。
> 证据基于**工作树**（本仓库尚无 commit，`git status` = No commits yet）。
> 技术力文档命中：`72-右键菜单/shell-menu-injection`、`context-menu-declaration-registry`、`windows-context-menu-registry-model`、`shell-thirdparty-handler-management`；**未命中**：① 原生 in-proc 菜单处理程序（`IShellExtInit`/`IContextMenu`）实现范式；② 稀疏包（sparse MSIX）+ `IExplorerCommand` 进 Win11 新菜单 —— 两者均需**新建**技术力文档。
> 形式：full（架构改动类）。

## 1. Objective

主程序不运行时：

| 表面 | 期望 |
|---|---|
| Win11 新版右键菜单（第一层，不需点"显示更多选项"） | 文件/文件夹右键出现「格式转换」；文件夹背景右键出现「桌面控制」 |
| Win11 "显示更多选项" / Win10 经典菜单 | 同上（兜底路径） |
| 多选 | 一次性拿到选中全集，批量执行（不逐文件弹 N 次） |
| 执行 | 点击后不依赖任何常驻进程；菜单打开无延迟 |
| 配置 | 菜单里出现什么、哪项显示/隐藏，由主程序控制；新增功能不改原生 DLL、不重编 |

## 2. Current Behaviour（[verified]）

| 菜单项 | 注册命令 | 无宿主真实结果 |
|---|---|---|
| 切换到自绘桌面 | `Cli --toggle-desktop` | 可用（唯一） |
| 桌面控制 | `Cli --menu-cmd desktop-controls` | `Classify` 无分支 → 「未知的 BetterDesktop 命令」 |
| 格式转换 | `Cli --menu-cmd convert-more "%1"` | 命中 `NeedsHostActions` → 「需要 BetterDesktop 正在运行」 |
| 压缩到 / 解压到 | 2026-09-11 起 `UnregisterArchive` | 键已删除（而 `compress-zip`/`unzip-here` 本身 headless 可执行） |

根因三条：
1. `HeadlessExecutor`（`BetterDesktop.Cli/HeadlessExecutor.cs:27-76`）已具备 ConvertTo/Compress/Unzip 免宿主执行能力，但**注册层没有任何入口指向它**——85 类扩展级联（原本每项直指 `convert-to-<fmt>`）于 2026-09-11 删除，换成必须宿主的 `convert-more`。
2. 「桌面控制」走 `desktop-controls`，`Bootstrap` 分支弹自绘 WPF 二级菜单 → 天然依赖宿主。
3. 注册表静态 verb 只支持 `%1`：多选时 explorer **逐文件调用 N 次**，拿不到选中全集；且静态 verb 无法按上下文动态决定显示内容——这正是「主程序控制显示 + 未来扩展」的阻塞点。**此天花板靠注册表无解，只能走 COM。**

## 3. Relevant Architecture（[verified]）

- 现有注册唯一应用点：`DesktopPlugin.ApplyShellMenuRegistration()`（`packages/shell/shell-desktop/DesktopPlugin.cs:622-665`），由 `shellmenu.*` 设置键驱动，幂等。
- 命令入口单点：`MenuCommandPaths.GetCliPath() / GetHostPath()`（`packages/kernel/kernel/MenuCommandPaths.cs`）。
- 命令桥：`host/MenuCommandPipe.cs`（服务端）+ `kernel/MenuCommandPipeClient.cs`（客户端，`Connect(timeout:1500)`）。
- 转换能力：`ConversionMatrix`（静态表：`GetTargets(ext)` / `AllInputExtensions` / `ConversionTarget{Lossless,Category,Label,Format}`）+ `EngineRegistry.Resolve`（引擎就绪探测）；`ConversionService.ConvertAsync(IReadOnlyList<string>, format)` **已支持多路径**。
- 原生工程先例（均不进 slnx，走独立构建脚本）：`packages/shell/shell-status/Native/`（CMake + `build_native.ps1`）；`scripts/build-explorertap.ps1`（VS2022 MSBuild + vcvars64 编译 C++ DLL）。
- 原生产物分发：csproj `<None Include="native\*.dll" CopyToOutputDirectory="PreserveNewest" Link="native\..." />` → `scripts/publish.ps1` 合并后落 `dist/<ver>/native/`（`dist/*/native/ExplorerTAP.dll` 实证）。
- 设置分区已有「重启桌面」能力可复用：`MenuManagerSection.RestartExplorerAsync()`（`Sections/MenuManagerSection.cs:134-154`）。
- **仓库内无任何 MSIX / AppxManifest / 打包签名基础设施**（全仓 grep `fileExplorerContextMenus` / `AppxManifest` 零命中）——本批从零建立。

## 4. Technical-Knowledge Findings

**已有资产（直接复用）**
- `shell-menu-injection`（L2）：注册表注入红线——MUIVerb ≤80、按场景追加 `%1`/`%V`、背景场景不追加、`HKCU\Software\Classes` 免夺权、同场景键名唯一、子菜单标题不带 ▸。
- `context-menu-declaration-registry`（L2）：三路同源不变式（自绘 `MenuItemDef.Action` = 注册表/COM action = CLI 路由键）；红线 4/5（CLI 必须 headless、不偷偷拉起宿主）；红线 8（系统菜单只放无损项）。
- `windows-context-menu-registry-model`：场景根路径与可见性判定链。
- `shell-thirdparty-handler-management`：枚举/启停语义。

**外部权威依据（本批新引入，Microsoft Learn，均 2026 年更新）**
- 《将文件资源管理器上下文菜单命令添加到打包的桌面应用》（2026-07-17）：三个组成部分 = ① 原生 COM DLL 实现 `IExplorerCommand`；② 清单注册 `windows.comServer`（`com:SurrogateServer` + `com:Class Id=CLSID Path=DLL ThreadingModel=STA`）；③ 清单注册 `windows.fileExplorerContextMenus`（`desktop4:FileExplorerContextMenus` → `desktop5:ItemType Type=* | Directory | Directory\Background` → `desktop5:Verb Id Clsid`）。
  - **决定性原文**：「仅使用『显示更多选项』查看**旧版**上下文菜单扩展。通过 `windows.fileExplorerContextMenus` 注册并使用 `IExplorerCommand` 实现的命令会**直接出现在 Windows 11 的上下文菜单**中。」
  - 明确支持**未打包 Win32 应用**：安装**稀疏包**提供包标识，复用同一 DLL 与同一清单注册。
  - 性能红线：「必须保持 `GetTitle`/`GetIcon`/`GetState` 及其他菜单构建方法**高效**，不要在 UI 路径上做昂贵工作；耗时操作放到 `Invoke` 被调用之后。」
  - 未打包应用的清单要求：`uap10:AllowExternalContent=true`、`uap10:TrustLevel="mediumIL"`、`uap10:RuntimeBehavior="win32App"`、`rescap:runFullTrust` + `rescap:unvirtualizedResources`。
- 《以外部位置打包方式手动授予包标识》（2026-04-18）：`MakeAppx.exe pack /o /d <dir> /nv /p out.msix` → `SignTool.exe sign /fd SHA256 /a /f cert.pfx /p pwd` → `Add-AppxPackage -Path <msix> -ExternalLocation <installDir>`；自签证书必须把公钥 `.cer` 导入 `Cert:\CurrentUser\TrustedPeople` 否则 `0x800B0109`；`Identity/@Publisher` 必须与证书 Subject 一致；同名同版本重复注册报 `0x80073CF9`（须先卸载）；`AllowExternalContent` 需 `10.0.19041.0`+。
- 《使用 IExplorerCommand 接口创建级联菜单》：级联靠 `EnumSubCommands` + `IEnumExplorerCommand`，父命令 `GetFlags` 必须返回 `ECF_HASSUBCOMMANDS`。
- `desktop5:ItemType/@Type` 的 schema 页为 TODO 占位（数据类型名 `ST_FileTypeOrStarWithDirectory`）；`Directory\Background` 取值以集成文档表格为准（schema 页无法佐证）→ 需真机验证。

**未命中（需新建文档）**
1. `TECH-KNOWLEDGE/72-右键菜单/native-context-menu-handler.md`：in-proc `IShellExtInit`+`IContextMenu` 薄实现范式（多选枚举、背景场景、防御式 COM 边界）。
2. `TECH-KNOWLEDGE/72-右键菜单/sparse-package-explorer-command.md`：稀疏包 + `IExplorerCommand` 进 Win11 新菜单的清单契约、签名/注册/版本化与踩坑。

## 5. Constraint Findings

| 约束 | 来源 | 规划含义 |
|---|---|---|
| Win11 新菜单**只接受有包标识的 `IExplorerCommand`**，传统 handler 只会落"显示更多选项" | MS 集成文档原文 [verified] | 「做到最好」= 双路：A 路（稀疏包 + IExplorerCommand）进新菜单，B 路（`ContextMenuHandlers`）覆盖 Win10/经典菜单 |
| **`IExplorerCommand` 的菜单构建方法必须极快**，重活只能在 `Invoke` 后 | MS 集成文档原文 [verified] | `GetTitle/GetIcon/GetState/EnumSubCommands` 全部读**内存缓存**；配置解析落在首次加载，按 mtime 失效 |
| B 路 in-proc handler 崩溃 = explorer 崩溃 = 桌面全掉 | [inferred] 通用铁律 | B 路 DLL 全方法 try/catch 不抛、零阻塞 IO；A 路预期由 `com:SurrogateServer` 在 dllhost 承载（进程外兜底）——真机确认 |
| `Identity/@Publisher` 必须等于签名证书 Subject；同版本重复注册报 `0x80073CF9` | MS 授予标识文档 [verified] | 证书与 `Identity` 单点维护；`Identity/@Version` 必须**每构建递增** |
| 自签证书须把公钥导入 `Cert:\CurrentUser\TrustedPeople`，否则 `0x800B0109` | MS 授予标识文档 [verified] | 安装脚本必须含证书信任步骤（每用户，无需管理员） |
| `AllowExternalContent` 需 Windows 10.0.19041.0+ | MS 授予标识文档 [verified] | 安装程序做 OS 版本检查，低于 19041 不注册 A 路（退 B 路） |
| DLL 被 dllhost/explorer 锁定，运行中不可覆盖 | [verified] 仓库既有记录（ExplorerTAP MSB3061） | 升级需先停止宿主扩展加载（重启 explorer / 结束 dllhost）；发布脚本不得假设可覆盖 |
| 注册/注销扩展需重启 explorer 才生效 | MS 集成文档「测试扩展」[verified] | 设置分区必须给「立刻重启桌面」出口 |
| 系统菜单只放**无损且引擎就绪**的目标 | `context-menu-declaration-registry` 红线 8 | 「格式转换」只放 Lossless；有损项不进系统菜单（本批明确不含） |
| 命令入口必须是轻量 CLI，绝不启动 WPF Application/Dispatcher；不偷偷拉起静默宿主 | `context-menu-declaration-registry` 红线 4/5 | 派发目标固定 `BetterDesktop.Cli.exe`；两个功能都必须落在 headless 能力内 |
| 改动必须保持既有 action 标识与设置键兼容 | 工具型项目侧重（对外契约兼容） | `convert-to-<fmt>` / `toggle-key` / `shellmenu.*` 全部沿用；`--menu-cmd` 等三入口语义不变 |

## 6. Proposed Changes

### 6.0 双路总览

```
┌─ Route A（Win11 新菜单第一层）──────────────────────────────┐
│  sparse MSIX  →  windows.comServer(SurrogateServer)          │
│                + windows.fileExplorerContextMenus            │
│  CLSID 激活 →  IExplorerCommand 实现（同一 DLL）             │
└──────────────────────────────────────────────────────────────┘
┌─ Route B（Win10 / Win11「显示更多选项」）────────────────────┐
│  HKCU\Software\Classes\...\shellex\ContextMenuHandlers       │
│  → InprocServer32 → IShellExtInit + IContextMenu（同一 DLL）  │
└──────────────────────────────────────────────────────────────┘
          ↓ 两条路共用 ↓
   配置快照 shellmenu.json  →  菜单树（渲染）  →  Invoke/InvokeCommand
          ↓
   落临时批文件 %TEMP%\bdt-menu-*.json  →  分离启动 CLI  →  立即返回
          ↓
   BetterDesktop.Cli.exe --menu-batch <file>  →  HeadlessExecutor  →  干完退出
```

**同一枚 DLL、同一份配置、同一条执行链**；两路只在「注册方式 + 实现的 COM 接口」上分叉。

### 6.1 原生 DLL（C++，一个 DLL 两个接口）
目录 `packages/shell/shell-context-menu/native/`，`BetterDesktopShellMenu.vcxproj`（x64 Release，纯 Win32 + `shlobj.h`/`shobjidl.h`/`shlwapi.h`，无 ATL/WIL/C++/WinRT）：

- `dllmain.cpp` — `DllGetClassObject` / `DllCanUnloadNow`
- `ClassFactory.cpp` — 两个 CLSID 的 `IClassFactory`
- `ShellMenuHandler.cpp` — **B 路**：`IShellExtInit::Initialize`（`SHCreateShellItemArrayFromDataObject` 取选中全集；`pidlFolder` 判定背景场景）+ `IContextMenu::QueryContextMenu` / `InvokeCommand` / `GetCommandString`（不做 owner-draw，故不需 `IContextMenu2/3`）
- `ExplorerCommand.cpp` — **A 路**：`IExplorerCommand`（`GetTitle` / `GetIcon` / `GetToolTip` / `GetCanonicalName` / `GetState`（可返回 `ECS_HIDDEN`/`ECS_DISABLED`）/ `GetFlags`（级联返回 `ECF_HASSUBCOMMANDS`）/ `Invoke(IShellItemArray*)` / `EnumSubCommands`）+ `IEnumExplorerCommand`（`Next`/`Skip`/`Reset`/`Clone`）
- `MenuConfig.cpp` / `MenuBuilder.cpp` — 配置快照解析 + 菜单树构建（**纯函数**，可被 smoke exe 直接调用）
- `Launcher.cpp` — 临时批文件落盘 + `CreateProcessW` 分离启动 CLI（`CREATE_NO_WINDOW`，立即 `CloseHandle`）
- `Diagnostics.cpp` — `%TEMP%\bdt-shellmenu.log` 追加式最小日志（失败静默）

CLSID：A 路 `{7B2E9C41-3D58-4F0A-9E6B-1A4C8D2F5E70}`、B 路 `{7B2E9C41-3D58-4F0A-9E6B-1A4C8D2F5E71}` [assumed：新建 GUID，落地时生成并确认唯一]。
线程模型：A 路 `STA`（清单声明）；B 路 `Apartment`。
构建脚本：`scripts/build-shellmenu.ps1`（工具链常量照抄 `scripts/build-explorertap.ps1:16-20`），产物 → `packages/shell/shell-context-menu/native/BetterDesktopShellMenu.dll`。
分发：`BetterDesktop.Shell.ContextMenu.csproj` 补 `<None Include="native\BetterDesktopShellMenu.dll" CopyToOutputDirectory="PreserveNewest" Link="native\..." />`。

### 6.2 配置快照通道（主程序 → 扩展；「主程序控制显示 + 未来扩展」的载体）
- 新增 `%APPDATA%\BetterDesktop\shellmenu.json`（**独立于 settings.json**：内容是"已渲染好的菜单树"）。写入方 `packages/shell/shell-context-menu/Services/ShellMenuConfigWriter.cs`，读 `ISettingsService` + `ConversionMatrix` + `EngineRegistry.Resolve`；schema v1：
```jsonc
{
  "version": 1,
  "extensionEnabled": true,
  "items": [
    { "id": "desktopControls", "title": "桌面控制", "kind": "submenu", "scenes": ["background"],
      "children": [
        { "id": "icons",   "title": "桌面图标显隐", "kind": "toggle", "checked": true,  "action": "toggle-key", "args": ["icons"] },
        { "id": "taskbar", "title": "隐藏任务栏",   "kind": "toggle", "checked": false, "action": "toggle-key", "args": ["taskbar"] },
        { "id": "doubleclick", "title": "双击隐藏图标", "kind": "toggle", "checked": true, "action": "toggle-key", "args": ["doubleclick"] }
      ] },
    { "id": "convert", "title": "格式转换", "kind": "submenu", "scenes": ["files", "directory"],
      "filter": { "extensions": [".docx", ".md", ".png", "…"], "selection": "sameExtension" },
      "children": [
        { "id": "convert-to-pdf", "title": "转为 PDF", "action": "convert-to", "args": ["pdf"], "highlight": true, "default": true },
        { "id": "convert-to-md",  "title": "转为 Markdown", "action": "convert-to", "args": ["md"], "highlight": true }
      ] }
  ]
}
```
- **C++ 侧只做「渲染 + 派发」，不懂业务** → 未来新增快捷功能 = 主程序多写一段 items + CLI 多一个 action 分支，原生 DLL 不重编、不需重启 explorer（配置热生效）。
- 写入时机：`ApplyShellMenuRegistration` 同点（启动、`shellmenu.*`/`components.*` 变更、引擎探测完成后），幂等重写。

### 6.3 原生侧读配置（性能红线落地）
`MenuConfig.cpp` 用 `GetFileAttributesExW` 取 `ftLastWriteTime + nFileSizeLow` 作缓存键；未变则复用已解析的菜单树（**A 路 `GetTitle/GetState` 全程走内存**）。变化才重新解析。首次读取失败/文件缺失 → 不显示扩展项（宁可不显示也不阻塞 explorer）。

### 6.4 多选执行通道
`Invoke`（A 路，直接拿到 `IShellItemArray`）与 `InvokeCommand`（B 路）统一：不拼命令行（32767 上限 + 引号转义），写 `%TEMP%\bdt-menu-<pid>-<ticks>.json`：
```jsonc
{ "action": "convert-to", "args": ["pdf"], "paths": ["C:\\a.docx","C:\\b.docx"] }
```
再 `CreateProcessW` 启动 `"<installDir>\BetterDesktop.Cli.exe" --menu-batch "<临时文件>"`，`CREATE_NO_WINDOW`，`CloseHandle` 后立即返回。残留：CLI 读完即删；Host 启动时清理 >1h 的历史残留。

### 6.5 CLI 侧（对外契约向后兼容）
- `BetterDesktop.Cli/Program.cs` 新增 `--menu-batch <file>` 分支 → `HeadlessExecutor.RunBatch(action, args, paths)`；**既有 `--menu-cmd <action> <path>` / `--toggle-desktop` / `--toggle-key` 三入口全部保留原语义**。
- `HeadlessExecutor` 增 `RunBatch` + 批量重载 `ConvertTo(paths, format)`（`ConversionService.ConvertAsync` 已支持多路径）。
- batch 路径**不走管道转发**（避开 1.5s 等待）；退出码沿用 `ExitCodes`，batch 输入错误 → `Usage`。

### 6.6 Route B 注册（经典菜单/兜底）
新增 `packages/shell/shell-context-menu/Services/ComShellExtensionRegistrar.cs`：
- `HKCU\Software\Classes\CLSID\{B-CLSID}\InprocServer32` = DLL 全路径 + `ThreadingModel=Apartment`
- 处理程序键：`*\shellex\ContextMenuHandlers\BetterDesktop`、`Directory\shellex\...`、`Directory\Background\shellex\...`、`DesktopBackground\shellex\...` = `{B-CLSID}` [assumed：`DesktopBackground` 一处需真机确认]
- 注销 = 键树删除（`throwOnMissingSubKey:false`），幂等
- 接线到 `DesktopPlugin.ApplyShellMenuRegistration()`，替换现有「桌面控制」「格式转换」两条静态注册并清理历史键。

### 6.7 Route A 打包与注册（进 Win11 新菜单）
- 新增 `packages/shell/shell-context-menu/native/AppxManifest.xml`（稀疏包清单）：
  - `Identity`：`Name="BetterDesktop.ShellMenu"`、`Publisher="CN=BetterDesktop"`（= 证书 Subject）、`Version` 每构建递增、`ProcessorArchitecture="neutral"`
  - `Properties`：`uap10:AllowExternalContent=true`、DisplayName/Logo
  - `Dependencies/TargetDeviceFamily`：`Windows.Desktop` MinVersion `10.0.19041.0`、MaxVersionTested `10.0.26100.0`
  - `Applications/Application`：`uap10:TrustLevel="mediumIL"`、`uap10:RuntimeBehavior="win32App"`、`VisualElements AppListEntry="none"`
  - `com:Extension Category="windows.comServer"` → `com:SurrogateServer` → `com:Class Id={A-CLSID} Path="native\BetterDesktopShellMenu.dll"`（**相对外部位置的路径**）、`ThreadingModel="STA"`
  - `desktop4:Extension Category="windows.fileExplorerContextMenus"` → `desktop5:ItemType Type="*"` 与 `Type="Directory"` → `desktop5:Verb Id="BetterDesktopConvert" Clsid={A-CLSID}`；`Type="Directory\Background"` → `desktop5:Verb Id="BetterDesktopDesktopControls" Clsid={A-CLSID}` [assumed：桌面背景覆盖范围待真机确认]
  - `Capabilities`：`rescap:runFullTrust`、`rescap:unvirtualizedResources`
- 新增 `scripts/pack-shellmenu-msix.ps1`：证书自举（`New-SelfSignedCertificate` → 导出 `.pfx` + `.cer` → 导入 `Cert:\CurrentUser\TrustedPeople`）→ `MakeAppx.exe pack /o /d ... /nv /p ...` → `SignTool.exe sign /fd SHA256 /f ... /p ...`
- 版本映射：`Identity/@Version` = `1.3.<yyyyMMdd 的天序号取模 65535>.<HHmm>`（MSIX 版本四段各 ≤65535；避免同版本重复注册 `0x80073CF9`）
- 新增 `packages/shell/shell-context-menu/Services/SparsePackageRegistrar.cs`：`Register()` = `Add-AppxPackage -Path <msix> -ExternalLocation <installDir>`；`Unregister()` = `Get-AppxPackage BetterDesktop.ShellMenu | Remove-AppxPackage`；`IsRegistered()`；先做 OS 版本检查（<19041 直接跳过 A 路）
- 接入 `publish.ps1`：产出 `.msix` 并纳入 `dist/<ver>/` 与 manifest.json

### 6.8 退役死路径（避免两套实现漂移）
删 `DesktopSystemMenuRegistrar` 的 `EnsureUiTogglesRegistered`/`UnregisterUiControls`/`EnsureConvertRegistered`/`UnregisterConvert`/`EnsureUiSub`/`DeleteUiSub`；删 `Bootstrap` 的 `case "desktop-controls"` 与 `ShowConvertMenu`；删 `DesktopControlMenu.cs`；从 `HeadlessExecutor.NeedsHostActions` 移除 `convert-more`/`convert`。**保留** `EnsureRegistered`（「切换到自绘桌面」）。

### 6.9 设置分区出口
「右键菜单」分区补：扩展总开关、A 路（新菜单）/B 路（经典菜单，Win11 默认关、Win10 强制开）分别开关、「立刻重启桌面」（复用 `RestartExplorerAsync` 模式）、诊断区（DLL 是否存在、CLSID 键、包是否已注册、`Get-AppxPackage` 状态）。

## 7. Implementation Sequence

1. **B 路最小可弹**（先走熟路验证加载链）：C++ 工程 + `IShellExtInit`/`IContextMenu` + 硬编码一项 + 手工注册 → 重启 explorer → 经典菜单出现。
2. **配置通道**：`ShellMenuConfigWriter`（C#）+ `MenuConfig`/`MenuBuilder`（C++ 纯函数）→ 菜单内容由 JSON 驱动（含缓存/mtime 失效）。
3. **CLI 批量**：`--menu-batch` + `HeadlessExecutor.RunBatch` + 批量重载 + 单测。
4. **派发接线**：`Launcher` → 临时 JSON → CLI 分离启动 → 真机验证多选转换。
5. **A 路 `IExplorerCommand`**：同 DLL 补实现 + 级联（`EnumSubCommands`/`ECF_HASSUBCOMMANDS`）→ 先靠**手工注册**（`HKCU\...\*\shell\<verb>\ExplorerCommandHandler`）验证接口正确性。
6. **稀疏包打包与注册**：`AppxManifest.xml` + `pack-shellmenu-msix.ps1` + `SparsePackageRegistrar` → 真机确认进入 Win11 新菜单第一层。
7. **注册器收口**：`ComShellExtensionRegistrar` + `ApplyShellMenuRegistration` 双路接线 + 按 OS 默认策略 + 删旧静态项。
8. **退役死路径**（6.8）+ 设置分区开关（6.9）。
9. **构建/分发/诊断**：csproj `<None Include>`、`publish.ps1` 纳入 `.msix`。
10. **单测 + 真机走查 + 两份技术力文档新建**。

## 8. Test Strategy

新增 C# 单测（`packages/shell/shell-context-menu-tests/`）：
- `ShellMenuConfigWriterTests`：无损过滤（`Lossless=false` 必须不出现）、引擎不就绪不出现、场景字段、勾选态镜像 settings、MUIL 文本 ≤80。
- `ComShellExtensionRegistrarTests`：注册/幂等/注销/4 处场景路径/CLSID 键 + `ThreadingModel`（写真实 HKCU，独立 GUID 测后清理——沿用 `ContextMenuRegistryTests` 模式）。
- `SparsePackageRegistrarTests`：OS 版本门控（<19041 不注册）、版本号映射（`yyyyMMdd`→MSIX 四段、越界回绕）、未注册时的 `IsRegistered=false`。

新增 CLI 单测（`BetterDesktop.Cli.Tests/`）：
- `MenuBatchTests`：正常 JSON / 空 paths / 坏 JSON / 路径不存在 / 混合扩展名 / 未知 action → 退出码各自正确；batch 文件被删除。

C++ 侧：
- 新增 `ShellMenuSmoke.exe`：对 `MenuConfig` 解析 + `MenuBuilder` 纯函数断言（正常/空/超长文本截断/未知 kind 忽略/缺字段不崩/`scenes` 过滤），打印结果供人读。

验证命令（真实存在）：
- `dotnet build better-desktop-cordis/BetterDesktop.slnx -c Debug`
- `dotnet test better-desktop-cordis/packages/shell/shell-context-menu-tests/BetterDesktop.Shell.ContextMenu.Tests.csproj`
- `dotnet test better-desktop-cordis/BetterDesktop.Cli.Tests/BetterDesktop.Cli.Tests.csproj`
- `powershell -File scripts/build-shellmenu.ps1`
- `powershell -File scripts/pack-shellmenu-msix.ps1`

## 9. Risk and Impact Analysis

- **最高风险：B 路 in-proc 崩溃拖垮 explorer**（桌面全掉）。缓解：全方法 try/catch 不抛、零阻塞 IO、配置走内存缓存、`SHCreateShellItemArrayFromDataObject` 失败即返回空菜单；A 路预期由 `com:SurrogateServer` 进程外承载（**真机确认**，若否则升级为高风险并考虑退到 A 路单用）。
- **签名与信任链**：自签证书须导入 `TrustedPeople`；`Identity/@Publisher` 与证书 Subject 不一致（`0x80073D54`）、同版本重复注册（`0x80073CF9`）都有明确对策但必须自动化，否则用户侧安装会失败。
- **DLL 锁定**：dllhost/explorer 持有 DLL 时无法覆盖 → 升级/卸载需先重启 explorer；`publish.ps1`/安装器不得假设可写。
- 下游消费者：`DesktopPlugin`（注册唯一应用点）、`Bootstrap`（命令桥分支）、`BetterDesktop.Cli`（入口契约）、`publish.ps1`（发布产物）。全部需保持向后兼容。
- 兼容性：`--menu-cmd` 单文件路径继续可用；`context-menu.mode` 等旧设置键保留不动；Win10 用户走 B 路，行为不退化。
- 性能：B 路 `QueryContextMenu` 增量目标 ≤20ms；A 路菜单构建方法只读内存。点击后 CLI 冷启动为已知成本（本批不做 AOT 瘦身，§12 deferred）。
- 可观测性：`%TEMP%\bdt-shellmenu.log`（DLL 加载/查询项数/派发命令）+ 既有 `bdt-cli.log` + 设置分区诊断区。

## 10. Files Expected to Change

| File | Symbols | Reason |
|---|---|---|
| `packages/shell/shell-context-menu/native/*`（新增） | `BetterDesktopShellMenu.dll` 全部源 | 双路 COM 扩展本体 |
| `.../native/BetterDesktopShellMenu.vcxproj`（新增） | — | 原生构建 |
| `.../native/AppxManifest.xml`（新增） | — | 稀疏包清单（进新菜单） |
| `packages/shell/shell-context-menu/Services/ShellMenuConfigWriter.cs`（新增） | 菜单树快照生成 | 主程序控制显示 |
| `.../Services/ComShellExtensionRegistrar.cs`（新增） | 注册/注销/IsRegistered（B 路） | 经典菜单注册 |
| `.../Services/SparsePackageRegistrar.cs`（新增） | Register/Unregister/IsRegistered（A 路） | 新菜单包注册 |
| `packages/shell/shell-context-menu/BetterDesktop.Shell.ContextMenu.csproj` | `<None Include>` | 原生产物分发 |
| `packages/shell/shell-desktop/DesktopPlugin.cs` | `ApplyShellMenuRegistration` | 双路接线替换 |
| `packages/shell/shell-desktop/Services/DesktopSystemMenuRegistrar.cs` | 删 6 个方法 | 退役静态项 |
| `packages/shell/shell-desktop/Services/DesktopControlMenu.cs` | 整体删除 | 退役宿主侧菜单 |
| `host/Bootstrap.cs` | 删 `desktop-controls` / `ShowConvertMenu` | 退役命令桥分支 |
| `BetterDesktop.Cli/Program.cs` | `--menu-batch` | 批量入口 |
| `BetterDesktop.Cli/HeadlessExecutor.cs` | `RunBatch` + 批量重载 | 多选执行 |
| `scripts/build-shellmenu.ps1`、`scripts/pack-shellmenu-msix.ps1`（新增） | — | 原生构建 + 包签名 |
| `scripts/publish.ps1` | 纳入 `.msix` | 发布产物 |
| 设置分区（`shell-context-menu/Sections/*`） | 双路开关 + 重启桌面 + 诊断 | 控制出口 |

## 11. Reusable Implementation Context

- 注册表注入红线逐条照 `72-右键菜单/shell-menu-injection.md`（MUIVerb ≤80、场景参数、HKCU 免夺权、幂等重写）。
- action 标识不得新造：复用 `convert-to-<fmt>` / `toggle-key`（`context-menu-declaration-registry.md` 三路同源）。
- 原生构建工具链常量直接抄 `scripts/build-explorertap.ps1:16-20`（VS2022 Community、MSVC 版本、ASCII 暂存目录规避中文路径）。
- 原生产物分发照 `packages/shell/shell-taskbar/BetterDesktop.Shell.Taskbar.csproj` 的 `<None Include="native\..." Link="native\...">`。
- 转换目标与无损判定：`ConversionMatrix.GetTargets(ext)` / `ConversionTarget.Lossless`；引擎就绪：`EngineRegistry.Resolve([path], target)`。
- 多路径执行：`ConversionService.ConvertAsync(IReadOnlyList<string>, format)`。
- 单测写真实 HKCU 的范式：`shell-context-menu-tests/ContextMenuRegistryTests.cs`。
- A 路清单契约与稀疏包步骤：MS Learn《将文件资源管理器上下文菜单命令添加到打包的桌面应用》+《以外部位置打包方式手动授予包标识》（原文要点已固化于本计划 §4/§6.7）。

## 12. Assumptions and Open Questions

- `[assumed]` **`desktop5:ItemType Type="Directory\Background"` 是否覆盖「桌面」背景**（若只覆盖文件夹背景，"桌面控制"在 Win11 新桌面菜单可能不可达）——**首要真机验证项**。兜底：桌面控制保留 B 路 `DesktopBackground\shellex\ContextMenuHandlers`（经典菜单）+ 宿主运行时自绘菜单入口。
- `[assumed]` **两个一级项是否会被 Win11 合并进同一个"应用浮出控件"**（MS 文档提到"同一应用中的命令可分组"）——真机确认；若被合并，需调整为一个一级项 + 内部分组，或接受合并形态。
- `[assumed]` `com:SurrogateServer` 确实在 dllhost（进程外）激活 —— 真机用进程树确认；若不成立，B 路/A 路都成为 explorer 内进程代码，崩溃风险等级上调。
- `[assumed]` `DesktopBackground\shellex\ContextMenuHandlers` 可被桌面空白右键加载 —— 真机确认；不成立退 `Directory\Background\shellex\...`。
- `[assumed]` 用 C++ 而非 C#：MS 明确不建议托管代码写 in-proc shell 扩展（CLR 版本冲突/无法卸载锁定）；仓库已有 VS2022 C++ 构建先例。
- `[assumed]` 不给 `BetterDesktop.Host.exe` 加 fusion 清单 `<msix>` 元素——本批不需要进程自身包标识（不用后台任务/Toast），避免 `0x80073D54` 类风险；未来若需要再补。
- `[assumed]` 桌面控制勾选态取 Host 写入的快照（宿主离线时可能陈旧）。可选增强：`桌面图标显隐` 由扩展在 explorer/dllhost 内直接 `IsWindowVisible(SysListView32)` 判定 —— 标 beyond。
- Deferred（用户未要求 / 后续批次）：CLI 冷启动瘦身（拆薄入口/AOT/单文件）；「压缩到 / 解压到」补注册（配置通道已通用）；MSIX 生产证书（当前自签 + TrustedPeople）；`IExplorerCommand` 之外再补 `AppListEntry`/Share 集成。
- 需新建技术力文档 2 份：`native-context-menu-handler.md`、`sparse-package-explorer-command.md`（按积累 skill 三段式）。

## 13. Definition of Done

| # | 判据 | 验证方式 |
|---|---|---|
| D1 | **关闭主程序**，Win11 文件资源管理器右键（**第一层，不点"显示更多选项"**）出现「格式转换」 | 真机走查（先 `Stop-Process BetterDesktop.Host`） |
| D2 | 关闭主程序，桌面/文件夹背景右键第一层出现「桌面控制」（若 §12 首条假设不成立，则判据改为"经典菜单可见"并记录） | 真机走查 |
| D3 | 关闭主程序，选中 **3 个同类型文件** → 「格式转换 ▸ 转为 PDF」→ 生成 3 个 PDF，且**只发生一次派发** | 真机走查 + `%TEMP%\bdt-shellmenu.log` 仅 1 条派发 |
| D4 | 关闭主程序，点「桌面控制 ▸ 桌面图标显隐」→ explorer 桌面图标真的隐藏/显示且设置持久化 | 真机走查 + 查 `settings.json` |
| D5 | 混合类型多选 → 「格式转换」按规则隐藏；有损目标不出现；引擎缺失目标不出现 | 单测 `ShellMenuConfigWriterTests` + 真机抽查 |
| D6 | 稀疏包可安装/卸载/重装：`Add-AppxPackage -ExternalLocation` 成功；卸载后新菜单项消失；重复安装同版本报错可被脚本自动处理 | 真机走查 + `Get-AppxPackage BetterDesktop.ShellMenu` |
| D7 | Win10 或"显示更多选项"经典菜单同样可用（B 路） | 真机走查（或在 Win11 展开"显示更多选项"验证） |
| D8 | explorer / dllhost 在菜单弹出、点击、取消全过程不崩（连续 20 次右键 + 5 次点击） | 真机压力走查 + 事件日志排查 |
| D9 | 既有 `--menu-cmd <action> <path>` / `--toggle-desktop` / `--toggle-key` 行为不变 | `dotnet test BetterDesktop.Cli.Tests` |
| D10 | 全部单测绿 + 全仓构建 0 警告 0 错误 | `dotnet build BetterDesktop.slnx -c Debug` + 三个测试工程 |
| D11 | 发布包含 DLL 与 `.msix` | `scripts/publish.ps1` 后核对 `dist/<ver>/native/BetterDesktopShellMenu.dll` 与随包 `.msix` |
| D12 | 设置里可分别开关 A/B 路，改完重启桌面后生效 | 真机走查 + 注册表/包状态核对 |

## 14. Handoff to 技术力应用（交接节）

| 项 | 内容 |
|---|---|
| **模式判定** | ① 注册/C# 侧（`ComShellExtensionRegistrar`/`SparsePackageRegistrar`/`ShellMenuConfigWriter`）：**标准文档注入**——命中 `shell-menu-injection` + `context-menu-declaration-registry`（L2 红线直接可用）。② 原生 COM 实现（`IShellExtInit`+`IContextMenu`、`IExplorerCommand`+`IEnumExplorerCommand`）与稀疏包清单：**无匹配 → 工程代码权威**，按本计划 §4/§6.1/§6.7 的 MS 契约实现，完成后回写 2 份新文档。③ CLI 批量：**命中但需扩展**——`context-menu-declaration-registry` 覆盖单文件路由，批量为其超集，不得破坏原契约。 |
| **注入清单** | `TECH-KNOWLEDGE/72-右键菜单/shell-menu-injection.md`、`.../context-menu-declaration-registry.md`、`.../windows-context-menu-registry-model.md`、`.../shell-thirdparty-handler-management.md`、本计划 §3/§4/§5/§6/§11（§4 已固化 MS 官方清单契约与稀疏包步骤，实现侧无需再联网调研） |
| **适配参数** | 语言：C++（Win32 SDK，无 ATL/WIL/C++WinRT）、C#（net8.0-windows10.0.19041.0）；命名空间 `BetterDesktop.Shell.ContextMenus.Services`；CLSID：A `{7B2E9C41-3D58-4F0A-9E6B-1A4C8D2F5E70}`、B `{...E71}`（生成后确认唯一）；包标识 `Name="BetterDesktop.ShellMenu"` / `Publisher="CN=BetterDesktop"`；配置 `%APPDATA%\BetterDesktop\shellmenu.json`；批文件 `%TEMP%\bdt-menu-<pid>-<ticks>.json`；构建 `scripts/build-shellmenu.ps1`、打包 `scripts/pack-shellmenu-msix.ps1`。 |
| **禁区** | 禁止在 WPF 宿主进程内导入 `IContextMenu`/`GetUIObjectOf`/`CreateViewObject`（已实证 `0xC0000005`）；禁止在 A 路 `GetTitle/GetIcon/GetState/EnumSubCommands` 内做 IO 或解析大 JSON（必须走内存缓存）；禁止在 B 路 COM 边界抛异常；禁止系统菜单出现有损目标；禁止改 `--menu-cmd` 既有语义；禁止复活 `DesktopMenuDelegation`/跨进程 DefView 转发；禁止把 `.msix` 版本固定不变（必触发 `0x80073CF9`）。 |
| **DoD 核销表** | D1–D12 逐条勾销，凭据 = 真机走查记录（截图/日志片段/`Get-AppxPackage` 输出）+ 测试输出 + 构建/打包输出。 |
