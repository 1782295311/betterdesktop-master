# BetterDesktop.Host

macOS 风格美化桌面的 WPF 宿主（WinExe）：以 cordis.yml 声明式插件树装配内核与 shell 部件，呈现「渐变壁纸 + 毛玻璃基底 + 顶部毛玻璃栏 + 底部毛玻璃 Dock」。

## 依赖

- `BetterDesktop.Shell.Core` / `BetterDesktop.Shell.Bar` / `BetterDesktop.Shell.Dock`
- `BetterDesktop.Kernel` / `BetterDesktop.Kernel.Loader` / `BetterDesktop.Kernel.Timer`

## 启动流程

- `App.OnStartup` → `Bootstrap.Build()`：先 Provide 主窗口句柄，再 Plugin 计时器与 `VibrancyService`（先于 loader），最后 Plugin loader 按 `cordis.yml` 的 `builtin.shell-*` 工厂装配。
- `cordis.yml` 经 csproj 的 `None Update` 拷贝到输出目录，运行时按相对路径读取。

## Known Limitations

- 桌面窗口以 Topmost 覆盖主屏，未做「置于原生桌面之下」的层级处理（P1）
- 多显示器下仅在主屏呈现（P1）
