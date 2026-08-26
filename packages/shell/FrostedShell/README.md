# FrostedShell（插件设计草稿 · 已废弃 / 复盘）

> ⚠️ **状态：已废弃 / 已翻车（P0）**
> 本次尝试于 2026-08-18 实施，相关改动见 `botched-frosted-20260818.diff`，
> WebView2 移除尝试见 `_trash_webview2_removal_20260817`。**当前仓库已回退到 HtmlShell / WebView2 路线，本插件不再实现。**
> 本文为复盘稿（post-mortem），保留供后续架构决策参考。

## 1. 背景与目标（当时）

- 用 WPF/DWM 原生磨砂玻璃（`DwmExtendFrameIntoClientArea`）替换 HtmlShell / WebView2 渲染层。
- 预期收益（**未经基准测试，不可采信**）：内存减少 ~50%、启动 3-5s → 1s 内。仓库无 before/after 实测日志。

## 2. 原方案

```csharp
public class FrostedShellExtension : IPlugin   // 真实内核契约：IPlugin（非 ICordisExtension）
{
    public string Name => "frosted-shell";
    public IReadOnlyList<Type> Inject => Array.Empty<Type>();
    public Task LoadAsync(IContext context, CancellationToken ct = default)
    {
        context.Provide<IFrostedGlassService>(new FrostedGlassService());
        context.Provide<ITransparentPanelService>(new TransparentPanelService());
        context.Provide<IWindowService>(new FrostedWindowService());
        return Task.CompletedTask;
    }
    public Task UnloadAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public class FrostedGlassService : IFrostedGlassService
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMargins);
    public void EnableFrostedGlass(IntPtr hWnd)
    {
        var margins = new MARGINS(-1);
        DwmExtendFrameIntoClientArea(hWnd, ref margins);
    }
}
```

## 3. 翻车根因（复盘）

1. **架构层级错误**：把"外壳渲染层"当成"一个插件"来替换，等于让插件去替换内核负责的窗口宿主，破坏了 HtmlShell 已建立的 WebView2 桥与 UI 通道。插件应为"能力扩展"，而非"外壳替换"。
2. **与 HtmlShell 强冲突**：WebView2 承载的 UI 无法在不重写全部视图的前提下被原生 WPF 面板替代，导致双渲染层并存、消息循环竞争、崩溃。
3. **性能假设未验证**：内存/启动收益来自估算，`botched-frosted-20260818.diff` 显示引入了大量边界条件回归（DPI、全屏、多显示器），实际收益为负。
4. **缺少灰度与回滚**：一次性大改，无特性开关（env gate），回退只能靠 trash 恢复。

## 4. 回退决策

- 保留 HtmlShell / WebView2 为主路线（已在 `cairoshell原版` 落地）。
- 磨玻璃能力降级为 `shell-core` 的 **Vibrancy** 服务（见 `packages/shell/shell-core/Vibrancy`），由 Taskbar / DynamicDesktop 等插件按需复用，不再单独成插件替换外壳。

## 5. 教训（供后续）

- **插件内核铁律**：插件提供"能力"，不替换"宿主"。外壳/渲染层属于内核职责。
- **任何架构层替换必须先做 before/after 基准测试**，否则不记入"收益"。
- **特性开关优先**：大改必须 env/flag gate，保证可秒级回滚。
- 本复盘已沉淀为 `plugin-doc-deep` 规范的反面案例。

## 6. 替代方案

- 毛玻璃 = `shell-core.Vibrancy.Enable(...)`，由各 UI 插件调用，无需 FrostedShell 插件。
