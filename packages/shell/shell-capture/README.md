# BetterDesktop.Capture（截图入口面）

第三个入口面 exe（与剪贴板面板同构）：常驻注册全局截图热键（默认 `Win+Shift+B`）+ 托盘图标，
按需创建覆盖层/标注编辑器/OCR 面板/贴图四类窗口；不依赖宿主，壳退出后截图仍可用。
采集后端运行在本进程内（单帧 4K BGRA ≈ 33MB，不做跨进程帧搬运）；产物走
采集 → PNG 落盘(temp) → 写系统剪贴板 → 引擎捕获入库。

## 职责

- **覆盖层**（`UI/OverlayWindow` + `Core/SelectionController`）：按显示器各建一窗，暗化本屏并挖孔选区，跨屏拼接；拖选定稿后**覆盖层保留**，底部出现功能栏（完成/标注/OCR/贴图/取消），点空白处完成、Esc 取消。
- **标注编辑器**（`UI/EditorWindow`）：画笔/箭头/矩形/文字 + 马赛克/模糊（破坏性像素编辑，位图快照撤销）+ 撤销/重做；完成时 RTB 合成 → PNG → 写剪贴板。
- **OCR 面板**（`UI/OcrPanelWindow` + `Ocr/WindowsMediaOcr`）：后台识别不卡 UI；行列表可多选 → 复制全部 / 整体保存（按行序拼一条）/ 逐条保存（每行独立进历史）。
- **贴图**（`UI/StickerWindow`）：置顶图片、拖拽、角部缩放、透明度、点击穿透（穿透时另开一个小面板提供退出通道）。
- **设置**：`%LOCALAPPDATA%\BetterDesktop\capture\settings.json`（`hotkey` / `stickerTopmost`，见 `Core/CaptureSettings`）。
- **CLI**（headless）：`--capture [--region X,Y,W,H | --window HWND] [--save PATH] [--no-cursor] [--no-tonemap]`。

## 主题与视觉口径（2026-09-15 定稿）

主题令牌与设计刻度来自 **shell-core**（`EntryTheme` / `ThemeBrushes`，与剪贴板面板**同一份实现与同一套数字**），
启动时在 `App.OnStartup` 里 `EntryTheme.Load()` → `ApplyToAppResources()` → `SlimScrollBar.Install(this)`，顺序不可颠倒（样式模板引用令牌）。

界面代码一律走 `UI/CaptureUi`，**禁止就地写色值/字号/间距**：

- **两套表面**：① *窗口表面*（编辑器/OCR 面板 —— 普通窗口）跟随主题深浅色；② *HUD 表面*（选区信息条/放大镜标签/覆盖层功能栏/贴图穿透面板 —— 浮在截图或桌面之上）**恒为深色**。HUD 叠在任意内容上（可能是白底文档、也可能是深色 IDE），跟随浅色主题反而会在亮图上失去对比度；只有强调色（主按钮/选区描边）跟随主题。
- **按钮**：`PillButton` / `IconTextButton` / `IconButton`，面风格 `CaptureUi.Face`（Surface / Primary / Selected / Hud / HudPrimary）。`Face.Selected` 用于单选组（当前工具/颜色），`Primary` 表示"执行"，两者语义不混。
- **间距口径**：`CaptureUi.ButtonGap`（= SpaceM = 12px）—— 相邻按钮两两 12px、胶囊外缘内侧 12px、纵向 12px。8px 实测仍显挤（用户两轮反馈），不要再回退。
- **动效**：状态切换一律 `Scale.MotionFastMs`（150ms）+ `MotionEase()`，禁止 0ms 瞬变；颜色过渡的画刷必须**逐按钮自带**（用共享令牌画刷做动画会改到所有按钮并永久改写令牌）。
- **图标**：Segoe MDL2 字形（禁止 emoji 当图标，见 `CaptureUi.IconFont`）。

### 两个必须知道的坑

1. **自定义 `ControlTemplate` 不会自动应用 `Button.Padding`** —— WPF 自带模板是靠 `Margin="{TemplateBinding Padding}"` 手动转发才生效的。截图首版所有胶囊按钮挂的 `Padding(16,0,16,0)` 被静默丢弃 → 文字左右零留白（用户实测"胶囊长度不够"）。`CaptureUi.BuildPillTemplate` 现在把 `Padding` 转发到 Border，**改模板时别删这一行**。
2. **HUD 容器与其中的按钮必须同色且不透明** —— 胶囊与按钮都用半透明（0xEB）时，按钮会在胶囊上叠出"更深的圆角色块"（把已被胶囊挡掉的桌面又挡一次）。`HudSurface` 现为不透明色，按钮静止态与胶囊完全同色，只在 hover/选中时浮现底色。

## 依赖

- `BetterDesktop.Api`、`BetterDesktop.Shell.Clipboard.Ipc`、**`BetterDesktop.Shell.Core`**（主题令牌/刻度/细滚动条；与 Api+IPC 依赖同源，不引入新的传递依赖）
- `SkiaSharp`（PNG 编解码）；TFM 覆盖为 `net8.0-windows10.0.22621.0`（19041 的投影缺 WGC 所需的 `TryCreateFromWindowId`/`IsBorderRequired`）

## Known Limitations

- WGC 采集在本机不可用（缺 `windows.graphics.directx.direct3d11.interop.dll`）→ 自动降级 BitBlt/DXGI；DXGI 在多适配器机器上还会因 `E_NOINTERFACE` 跳过适配器，日志有明确降级说明。
- 覆盖层为**每显示器一个窗口**，拖选跨屏靠 `SelectionController` 汇总；高 DPI 混合屏下坐标换算按各屏 `DpiX` 分别处理。
- 贴图重启不恢复（计划 §7 默认）。
- CLI 无 OCR 入口（OCR 只在交互流程里）。
