# ThemeCenter（插件设计稿 · 深度版）

> 状态：设计草稿（尚未实现）。本文取代 `分析稿/方案-Shell替代程序优秀特性集成.md` 中"主题中心"章节的浅层描述。

## 1. 目标与边界

**做什么**：统一 Cairo/Cordis 桌面的视觉令牌（Design Token）来源与分发，支持实时切换、离屏预览、导入导出、条件切换。 **不做什么**：不负责具体控件的绘制逻辑；不持有业务状态；不直接读写控件属性。控件通过订阅令牌自行重绘。

**设计原则**：令牌驱动（Token-driven）。一切外观都由令牌表达，主题只是令牌集合的命名版本。

## 2. 架构

```
ThemeCenter (Extension)
├── ThemeEngine          # 加载/解析 ThemePack，维护 ActiveTheme，广播 ThemeChanged
│   ├── ThemeLoader      # 从 .theme 包反序列化（JSON/YAML），校验命名空间
│   └── TokenCompiler    # 令牌 → WPF ResourceDictionary / WebView2 CSS 变量
├── ResourceBridge       # 令牌映射到 WPF / WebView2 双通道
├── PreviewRenderer      # 离屏渲染主题预览位图
└── Watchdog             # 文件变更热重载（开发期）
```

依赖：`BetterDesktop.Kernel`（事件总线 / ExtensionContext）、`BetterDesktop.Shell.Core`（Vibrancy 复用、窗口服务）。

## 3. 领域模型

```csharp
enum TokenType { Color, Font, Spacing, Radius, Opacity, Shadow }

record DesignToken(string Key, TokenType Type, string Value, string? Scope = null);

class ThemePack
{
    string Id; string Name; string Author;
    bool IsDark;
    string? ParentId;              // 继承父主题，差量覆盖
    List<DesignToken> Tokens;
}

class ThemeManifest                  // 随包分发，声明契约
{
    string Id; Version Version;
    string[] RequiredNamespaces;    // 声明的令牌命名空间，缺则校验失败
}
```

令牌命名空间约定：`shell.dock.*`、`shell.menu.*`、`shell.taskbar.*`、`desktop.widget.*`，避免跨插件冲突。

## 4. 内核集成

- `Name = "theme-center"`，`Inject: [typeof(IVibrancyService)]`（真实内核契约：无 ExtensionId/Manifest，依赖用 `Inject` 声明）。
- **加载顺序**：必须在所有 UI 扩展（Dock / MenuBar / Taskbar）之前加载，因为它们启动时需要读令牌。内核按依赖拓扑排序启动。
- `LoadAsync(IContext)`：注册 `IThemeService` / `IThemePreviewService` / `IThemeResourceProvider`；从持久化设置读取上次主题并 `ApplyAsync`；订阅内核 `SettingsChanged` 以响应条件切换（如系统明暗变化）。
- `UnloadAsync`：先恢复内置 `default` 主题，再释放资源，避免残留透明/错位窗口。

## 5. 公共契约（语义）

```csharp
interface IThemeService
{
    ThemePack Active { get; }
    Task<ThemePack> LoadAsync(string packPath);          // 解析 + 校验 + 编译
    Task ApplyAsync(string themeId);                     // 实时切换，绝不重启进程
    Task<bool> ValidateAsync(ThemePack pack);            // 校验缺失令牌 / 命名空间冲突
    event EventHandler<ThemeChangedArgs> Changed;         // 广播差量令牌集合
}

interface IThemePreviewService
{
    Task<Bitmap> RenderPreviewAsync(ThemePack pack, Size px);  // 离屏渲染，不阻塞 UI
}

interface IThemeResourceProvider
{
    object? Resolve(string tokenKey);                    // 控件按需取令牌，缓存命中
}
```

> 注：`ApplyAsync` 必须是事务性的——校验失败则回滚到上一 Active，不部分生效。

## 6. 配置

- 主配置 `themes/<id>/theme.ini`：`[colors]`、`[fonts]`、`[icons]`、`[animation]` 分组。
- 切换策略：`realtime`（默认）/ `scheduled(HH:mm)` / `conditional(system-light|system-dark)`。
- 切换事务化：失败自动回滚到上一主题并写诊断事件。

## 7. 数据流

```
ApplyAsync(themeId)
  → ValidateAsync（失败→回滚 default）
  → TokenCompiler 计算差量（只更新变化的令牌，dedupe）
  → ResourceBridge 一次性替换 ResourceDictionary / 注入 WebView2 CSS 变量
  → 广播 Changed(diff)
  → 订阅控件（Dock / MenuBar / Taskbar / Desktop）按 diff 重绘自身
```

## 8. 跨插件协作

- Dock / MenuBar / Taskbar / DynamicDesktop 通过 `IThemeResourceProvider.Resolve(key)` 取令牌，不直接读文件。
- 与 FrostedShell 的冲突已规避：玻璃模糊参数由 `shell-core.Vibrancy` 提供，ThemeCenter 仅通过令牌控制其强度/色调，不替换外壳。
- 所有外壳窗口继承 `shell-core.Surface.ShellWindow` 统一基类（窗口属性 + `ApplyWindowMaterial` 毛玻璃入口 + `OnLoadedCore` 钩子），材质参数从本插件令牌读取，不自造窗口样式。

## 9. 错误处理与韧性

- 主题包损坏 / 缺失令牌：回退内置 `default`，诊断事件记录具体哪个 key 缺失。
- 控件取不到令牌：回退中性色（`#808080` 或等价），不抛异常、不崩。
- 热重载失败（Watchdog）：保留内存中当前主题，仅警告。

## 10. 性能

- 合并字典一次性替换，避免逐控件遍历；切换走差量更新。
- 预览渲染在后台线程，主线程只接收结果位图。
- 启动时延迟到首次显示前完成，避免首帧闪烁。

## 11. 验收

- [ ] 加载 `theme.ini` 成功并渲染
- [ ] 实时切换无需重启
- [ ] 预览图正确反映令牌
- [ ] 缺失令牌时控件不崩、回退中性色
- [ ] 卸载后恢复默认主题、无残留

## 12. 开放问题

- 与 upstream Cairo 主题命名空间是否需要对齐以保证兼容？
- WebView2 侧的 CSS 变量注入通道（经 HtmlShell 桥）尚未定义，需与 shell-core 共同确认。
