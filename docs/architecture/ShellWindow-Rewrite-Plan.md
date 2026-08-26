# ShellWindow 基类重写方案

## 一、现状病灶诊断

当前 `ShellWindow`（`packages/shell/shell-core/Surface/ShellWindow.cs`）经过多轮补丁，
已经不是「设计」而是「补丁堆叠」。根本矛盾是：

> **WPF `WindowChrome`（非客户区合成圆角）与 `SetWindowRgn`（任意半径 region 裁剪）
> 是两套互斥的圆角方案。** 前者由 WPF `WindowChromeWorker` 在消息循环里反复重设自己的
> region（含 DPI 换算），后者是我们手动设的 region。两者争抢同一个 HWND 的可见区，
> 必然出现「内圆角 + 外直角」双重窗口感。

为压住这个矛盾，基类推出来一堆互相打补丁的逻辑：

| 补丁 | 位置 | 解决什么 | 引入的副作用 |
|------|------|----------|--------------|
| `SetWindowRgn` + `DisableDwmRound` | `RefreshWindowRegion` | 任意半径圆角 | 必须 DPI 换算（DIP×DpiScale），否则高 DPI 下 region 比窗口小 |
| `SourceInitialized` 挂 `WM_WINDOWPOSCHANGED` hook | `WndProcHook` | WindowChromeWorker 反扑重设 region | 每次窗口变化都 Render 延迟重裁，逻辑耦合进消息钩子 |
| `SizeChanged`/`StateChanged` Render 延迟重裁 | 构造函数 | 缩放/还原时取到旧尺寸 | 多处延迟调用，时序脆弱 |
| `InvalidateVisual()` | `RefreshWindowRegion` 末尾 | layered 窗口缩放后旧位图残留（模糊方块） | 全量重绘，性能代价 |
| 原地改 `WindowChrome.CornerRadius` 而非重建 | `ApplyWindowChrome` | 重建 WindowChrome 触发 worker 重置 | 仍依赖 WindowChrome 存在，妥协方案 |
| `WM_NCHITTEST` 自建 resize | `WndProcHook` | 移除 WindowChrome 后失去 resize 命中区 | 自己算边角命中，又一层逻辑 |

**结论**：这些补丁每一个单独看都有道理，合在一起让基类：
1. 同时侍奉两套圆角方案（WindowChrome + SetWindowRgn），职责不清；
2. 圆角「单一来源」名存实亡——WindowChrome 的 CornerRadius 和 SetWindowRgn 的半径必须手动同步；
3. 消息钩子里既管 region 反扑又管 resize 命中，违反单一职责；
4. DPI/时序/旧位图残留全靠「延迟 + 重裁 + 重绘」三连兜底，不可测试、不可推理。

派生窗口契约目前是：`ChromeBorder` 根 Border + `ResizeMode`/`Topmost` 等属性覆盖。
这些契约本身没问题，问题在基类内部实现。

---

## 二、重写设计目标

1. **圆角单一来源**：彻底不用 `WindowChrome`。所有窗口形状（圆角、描边、阴影）
   100% 由基类 `SetWindowRgn` 掌控，不再有任何 WPF 非客户区圆角参与。
2. **职责拆分**：把「外观订阅」「窗口形状（region）」「输入命中（resize/drag）」
   「生命周期」拆成清晰的方法，不塞进一个 `WndProcHook`。
3. **DPI 一致**：region 尺寸与圆角半径统一走 `VisualTreeHelper.GetDpi` 换算，
   与 WPF 内部 `DpiHelper.LogicalPixelsToDevice` 一致，消除高 DPI 错位。
4. **可 resize 不自管 WindowChrome**：用 `WM_NCHITTEST` 自建命中区（已验证可行），
   抽成独立方法，边界清晰。
5. **最小化重绘**：不再无脑 `InvalidateVisual`，仅在尺寸真正变化且旧 region 失效时重裁。
6. **派生窗口零痛迁移**：保留 `ChromeBorder` 机制；去掉对 WindowChrome 的任何依赖。

---

## 三、新基类结构（草案）

```
abstract class ShellWindow : Window
{
    // ---- 外观（已有，保留） ----
    protected IVibrancyService? VibrancyService { get; set; }
    protected IAppearanceService? AppearanceService { get; set; }

    // ---- 窗口形状（新：单一来源） ----
    protected Border? ChromeBorder { get; set; }
    protected virtual Thickness ChromeMargin => new(0);
    private void ApplyWindowShape()        // 圆角 + 描边统一应用到 ChromeBorder
    private void RefreshRegion()           // SetWindowRgn，仅 Normal，DPI 换算，无 InvalidateVisual

    // ---- 输入命中（新：替代 WindowChrome 非客户区） ----
    protected virtual double ResizeBorderThickness => 6;
    private IntPtr HitTestResize(IntPtr lParam)   // WM_NCHITTEST 边角判定

    // ---- 消息钩子（只做一件事：region 反扑兜底） ----
    private IntPtr WndProc(IntPtr, int, IntPtr, IntPtr, ref bool)
        => WM_WINDOWPOSCHANGED: Render 延迟 RefreshRegion
        => WM_NCHITTEST:        HitTestResize（仅 CanResize 且无 WindowChrome）

    // ---- 生命周期（保留但精简） ----
    SourceInitialized -> 挂 HwndSource + 首次 RefreshRegion
    Loaded           -> ApplyMaterial + ApplyAppearance + ApplyWindowShape + 首次 FontScale
    SizeChanged/StateChanged -> Render 延迟 RefreshRegion
}
```

**与现状的关键差异**：
- `ApplyWindowChrome` 改名为 `ApplyWindowShape`，**不再碰 WindowChrome**（因为彻底不用了）。
- `RefreshRegion` 去掉 `InvalidateVisual`；旧位图残留问题改用「resize 时只重裁 region、
  不强制全量重绘」+ 必要时由 WPF 自身合成处理（实测模糊方块主要来自 WindowChrome 双层，
  移除后应消失；若仍残留再针对性处理，不预支成本）。
- `WM_NCHITTEST` 命中逻辑独立成 `HitTestResize`，不再和 region 反扑混在一段 if 里。

---

## 四、派生窗口迁移

| 窗口 | 当前 | 迁移动作 |
|------|------|----------|
| `SettingsWindow` | 已移除 WindowChrome | 无需改结构，验证 resize/drag 正常 |
| `DockWindow`/`LaunchpadWindow`/`AppGrabberWindow` | XAML + `ChromeBorder` | 保持，确认无 WindowChrome 引用 |
| `MinimalDockWindow`/`NewAppsNotificationWindow` | 纯代码 + `ChromeBorder` | 保持 |
| 所有窗口 | 依赖 `ResizeMode` 触发 `WM_NCHITTEST` | 基类自动处理，无需各自写 |

**不破坏的契约**：`ChromeBorder`、`AppearanceService`、`VibrancyService`、
`OnLoadedCore` 扩展点全部保留。

---

## 五、待你确认的点

1. 是否同意「彻底放弃 WindowChrome，圆角 100% 走 SetWindowRgn」这一根本决策？
   （我已据此把 SettingsWindow 的 WindowChrome 移除并验证编译通过，方向上已无回退必要。）
2. 新基类是否就叫 `ShellWindow`（原地重写），还是新建 `FluentWindow`/`AcrylicWindow`
   另立类名、旧名标记 `[Obsolete]` 逐步迁移？（推荐原地重写，派生窗口改动最小。）
3. 模糊方块若移除 WindowChrome 后仍出现，是否接受「针对性补 `InvalidateVisual`」
   而不是现在每帧都调？（推荐按需，不预支性能。）

确认后我开始实现（任务 #52）。
