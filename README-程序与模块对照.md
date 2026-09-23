# BetterDesktop 1.3.0 — 编译产物（按源码结构摆放）

本包**只有编译结果，没有源码**。目录名与源码仓库一一对应，
每个模块目录里放的是**该模块自己编译出来的东西**（程序集 + deps/runtimeconfig
+ 该工程自带的资源），不夹带第三方依赖，也不夹带 .NET 运行时。

## 两条必须知道的约束

1. **这份结构目录不能直接运行。** 产品要求 Host / Cli / DesktopControl /
   Settings / betterdesktop-core.exe / BetterDesktopShellMenu.dll 处于**同一个目录**
   （Cli 按 `AppContext.BaseDirectory` 定位同目录组件）。按模块拆开后这个前提就不成立。
   要运行请用 `_可运行成品（双击即用，与上面结构目录无关）` 里的 zip。
2. **已剔除 PDB 与 `*.runtimeconfig.dev.json`。** 前者内含绝对路径，
   后者内含 NuGet 全局缓存的绝对探测路径 —— 两者都是个人信息。
3. **已做路径脱敏**（Release 构建）：.NET 侧 `Directory.Build.props` 的 `PathMap` 
   把仓库根映射成 `/bd/`；Rust 侧 `.cargo/config.toml` 用 `--remap-path-prefix` 做同样的事。
   导出后用字节级扫描逐文件核对（脚本：仓库 `Temp\leak-scan.ps1`，自带"已知命中必须检出"自检）。

## 目录对照

| 目录 | 里面是什么 |
| --- | --- |
| `core\` | `betterdesktop-core.exe`（唯一常驻进程：托盘/热键/控制管道/监护/电源） |
| `engine\` | `betterdesktop-clipboard-engine.exe`（剪贴板引擎） |
| `engine-index\` | `betterdesktop-index-engine.exe`（索引） |
| `engine-ocr\` | `betterdesktop-ocr-engine.exe`（OCR） |
| `engine-translate\` | `betterdesktop-translate-engine.exe`（翻译） |
| `native\convert-engine\` | `convert-engine.exe`（格式转换，cargo 产物） |
| `native\convert-lite\` | `convert-lite.exe` |
| `packages\entry\launcher\` | **`BetterDesktop.exe`**（用户唯一入口；程序集名 `BetterDesktop.Launcher`） |
| `packages\entry\host\` | `BetterDesktop.Host.exe`（主程序外壳 + 插件清单 `cordis.yml`） |
| `packages\entry\cli\` | `BetterDesktop.Cli.exe`（跨进程契约入口，必须与 Host 同目录） |
| `packages\entry\updater\` | `BetterDesktop.Updater.exe` |
| `packages\entry\recovery\` | `BetterDesktop.Recovery.exe` |
| `packages\shell\shell-desktop-control\` | `BetterDesktop.DesktopControl.exe`（右键菜单的进程侧） |
| `packages\shell\shell-settings-host\` | `BetterDesktop.Settings.exe`（设置中心，独立进程） |
| `packages\shell\shell-capture\` | `BetterDesktop.Capture.exe`（程序集名 `BetterDesktop.Shell.Capture`） |
| `packages\shell\其他\` | 各壳组件的 `BetterDesktop.Shell.*.dll` |
| `packages\kernel\*\` | `BetterDesktop.Kernel*.dll`（内核、加载器、定时器、HMR） |
| `packages\api\` | `BetterDesktop.Api.dll` |
| `tools\` | 内部工具（GdiAudit、ShellComponentsPlayground、基准等） |
| `_运行前置（非编译产物）\` | `redist\`（VC++ 运行库，其中 `vcruntime140.dll` 是 core 的唯一真实导入，缺了整个产品不工作）＋ `BetterDesktop.ico` / `cordis.yml` / 安装卸载脚本 / `required-files.json` |
| `_可运行成品（双击即用，与上面结构目录无关）\` | 正式发布包的 zip（组件同目录，解压即用）：BetterDesktop-1.3.0-内测-2026.09.23-含可选引擎.zip |

## 未包含

- **源码**（本次刻意不发）。
- **测试工程**（`*.Tests`）与**已退役组件** `packages\entry\tray`
  （托盘已迁进 Rust core，再发一个 .NET 托盘会出现第二个托盘图标）。
- **安装器**：仓库里只有 `installer\BetterDesktop.iss` 脚本，编译需 Inno Setup。
- **可选引擎包**（OCR/转换的第三方模型与运行时，约 172 MB）：
  需要时另取 `dist\BetterDesktop-1.3.0-内测-*-含可选引擎.zip`。

## 生成方式

从当前工作区导出：.NET 侧取各工程 `bin\x64\Release\<tfm>\` 与 `bin\Release\<tfm>\`（两个根都扫，
正本优先、同优先级取最新；`win-x64\` 自包含副本仅作兜底）里
「属于本工程」的文件；Rust 侧取各 crate `target\release\<crate>.exe`。
导出脚本：`Temp\export-binaries.ps1`（不进 `scripts\`，避免与门禁/文档预算约定冲突）。

导出时间：2026-09-23 11:30
