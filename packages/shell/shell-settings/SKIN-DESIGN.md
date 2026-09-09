# 皮肤系统增强设计稿（Skin Subsystem）

> 范围：图片皮肤参数化 + 配色皮肤（无图）+ 皮肤管理器 + **插件化扩散（影响范围全覆盖）**。
> 设计原则（铁律，不可违背）：
> 1. **令牌驱动、声明式绑定**——一切复用现有 `IAppearanceService`（`appearance.*` 键 + `Changed` 广播），不新增全局状态。
> 2. **不破坏 DWM 生命周期**——背景仍由 `ShellWindow.ApplyAppearance` 把 `BackgroundBrush` 赋给 `Window.Background` 与根 `ChromeBorder.Background`；圆角走 DWM 系统默认圆角，绝不用 `SetWindowRgn`/region。
> 3. **绝不"给所有窗口贴图覆盖"**——皮肤只作用于窗口根背景层；面板/内容层保持半透明叠层（保证文字可读），与现有 `IThemeTokens.PanelBackground/ContentBackground` 语义一致。
> 4. **零破坏现有契约**——不改 `IAppearanceService`/`IThemeTokens` 现有成员签名；新参数走 `appearance.skin.*` 子键，新增只读派生不影响旧消费者。
> 5. **【新增·插件化责任】皮肤系统对"未来所有功能界面"负责**——影响范围不能只困在位置（窗口根背景）内：
>    - 内置窗口：继承 `ShellWindow` → 自动吃皮肤（根背景 + 基类订阅重绘）。
>    - 插件窗口：继承 `PluginHostWindow`（即 `ShellWindow`）→ 外壳自动吃皮肤；**插件内部内容**经 `ThemeResourceProvider.GlobalDictionary` 的令牌引用皮肤，而非画死颜色。
>    - 扩散机制复用现有 `GlobalDictionary` 推送范式（与 `CardBorderBrush` 完全一致），不发明新范式。

---

## 1. 现状（已落地，代码核实）

| 能力 | 现状 | 落点 |
|------|------|------|
| 外观引擎 | `AppearanceService` 已实现令牌化：Accent / WindowTint / WindowOpacity / ContentOpacity / CornerRadius / SpacingScale / FontScale / Material / Mode / Border* / SkinPath | `shell-settings/Services/AppearanceService.cs` |
| 皮肤入口 | 仅"选择背景图片 / 清除皮肤"两个按钮，写 `appearance.skinPath` | `shell-settings/Sections/ThemeSection.cs` `SkinRow` |
| 皮肤渲染 | `SkinPath` 非空时 `BackgroundBrush` 返回 `ImageBrush(UniformToFill, Freeze)`；基类 `ApplyAppearance` 把它铺到所有 Shell 窗口根背景 | `AppearanceService.BackgroundBrush` + `ShellWindow.ApplyAppearance` |
| 内置窗口订阅 | `ShellWindow` 构造期订阅 `IAppearanceService.Changed`，`SkinChanged` 触发即重绘 | `ShellWindow.OnAppearanceChanged` |
| 插件令牌扩散 | `SyncAppResources` 已把 `CardBorderBrush`/`ThemeForeground` 等推入 `GlobalDictionary`，插件 XAML `DynamicResource` 引用 | `ThemeResourceProvider` + `PluginHostWindow.OnChromeBorderReady` |
| **【缺口·关键】** | **`BackgroundBrush`/`SkinPath` 未推入 `GlobalDictionary`** —— 插件内容层无法引用皮肤图，只能在自己内容区画死颜色；皮肤影响范围只困在窗口根 Border 内 | 见 §4 扩散设计 |

**缺口总结**：

1. 皮肤只有"选一张图铺满"这一种玩法；无参数（模糊/暗化/拉伸）、无配色皮肤、无多皮肤切换/管理。
2. **影响范围未扩散**：皮肤图 Brush 没进 `GlobalDictionary`，未来插件/功能界面的内部面板拿不到皮肤，无法做协调（如面板透出皮肤图、卡片描边随皮肤主色）。

---

## 2. 目标架构（含插件化扩散）

```
┌─────────────────────────────────────────────────────────────┐
│  ThemeSection（设置 UI，shell-settings 内置分区）              │
│  ├─ 皮肤卡片：管理器列表 + 参数面板                            │
│  └─ 点击 → SkinManager.Apply(skin) / SetParam(...)           │
└───────────────┬─────────────────────────────────────────────┘
                │ 调用
┌───────────────▼─────────────────────────────────────────────┐
│  SkinManager（新增，纯逻辑，无 UI 依赖）                      │
│  - 枚举内置配色皮肤 + 用户图片皮肤（目录扫描）                │
│  - Apply(skinId)：写 appearance.skin.* / appearance.* 键      │
│  - 内置预设皮肤 = 一组 appearance.* 值的"主题包"             │
└───────────────┬─────────────────────────────────────────────┘
                │ 写键 + 广播（复用现有 Changed 机制）
┌───────────────▼─────────────────────────────────────────────┐
│  AppearanceService（扩展 BackgroundBrush 推导 + 扩散推送）    │
│  - 读 appearance.skin.* 子键                                  │
│  - 图片皮肤：按 stretch/blur/darken 推导 ImageBrush          │
│  - 配色皮肤：驱动 WindowTint+Accent+Border（无图）           │
│  - SyncAppResources 新增：推 SkinBackgroundBrush / SkinMeta   │
│    到 Application.Current.Resources + GlobalDictionary        │
│  - 仍经现有 setter → Changed 广播 → 订阅方重绘               │
└───────────┬───────────────────────────────┬────────────────┘
            │ BackgroundBrush（不变接口）     │ GlobalDictionary 令牌
            ▼                                ▼
┌──────────────────────────┐   ┌──────────────────────────────────────┐
│ ShellWindow.ApplyAppearance│   │ 插件/未来功能界面（两种接入契约）    │
│ 根背景 = BackgroundBrush  │   │ ① 继承 ShellWindow → 自动吃根背景   │
│ 面板/内容 = 半透明叠层    │   │ ② DynamicResource 绑 SkinBackground │
│ （内置窗口自动）          │   │    Brush / CardBorderBrush 等令牌    │
└──────────────────────────┘   └──────────────────────────────────────┘
```

---

## 3. 数据模型（全部 `appearance.skin.*` 子键，扁平持久化）

| 键 | 类型 | 默认 | 说明 |
|----|------|------|------|
| `appearance.skin.active` | string | `""` | 当前激活皮肤 ID。`""`=无（仅半透明托盘）；`img:<path>`=图片皮肤；`preset:<name>`=内置配色皮肤 |
| `appearance.skin.imgStretch` | int | `0` | 0=UniformToFill(铺满裁切) / 1=Uniform(完整居中留边) / 2=Fill(拉伸变形) / 3=Tile(平铺) |
| `appearance.skin.imgBlur` | double | `0` | 皮肤图上方叠的模糊强度 0–1（0=不模糊；>0 时叠半透明层保证文字可读） |
| `appearance.skin.imgDarken` | double | `0.25` | 皮肤图暗化 0–1（叠加半透明黑层；保证亮色文字可读，默认轻暗化） |
| `appearance.skin.imgOpacity` | double | `1.0` | 皮肤图自身不透明度 0–1（与 WindowOpacity 叠加控制透出程度） |

**配色皮肤（预设）不落 `skinPath` 图片键**，而是把一组 `appearance.*` 值打包，激活时整体写入：

| 预设名 | 写入的键 | 说明 |
|--------|----------|------|
| `preset:graphite` | WindowTint=#1F1F22, Accent=#0A84FF, BorderStyle=2, BorderStrength=0.5 | 默认石墨深灰（=现状默认值） |
| `preset:midnight` | WindowTint=#0B1020, Accent=#5E5CE6, BorderStyle=1 | 午夜蓝紫 |
| `preset:sakura` | WindowTint=#2A1A22, Accent=#FF375F, BorderStyle=2 | 樱粉 |
| `preset:mint` | WindowTint=#10231C, Accent=#30D158, BorderStyle=0 | 薄荷绿 |

> 预设皮肤**不依赖图片**：直接驱动现有 `WindowTint`/`Accent`/`Border*`。与"图片皮肤"正交，用户择一。

---

## 4. 插件化扩散设计（核心：影响范围全覆盖）【本节为对上一版的修正补强】

### 4.1 问题定义 当前 `SyncAppResources` 推送 `GlobalDictionary` 的键：`Control*` / `Theme*` / `CardBorderBrush` / `CardShadowEffect` / `BackgroundBrush`。 注意最后一项 `BackgroundBrush` 虽被推入，但它是**无皮肤时**的色调托盘 Brush；**有皮肤时 `BackgroundBrush` = 皮肤图 ImageBrush，确实被推了**——但**插件并不知道"当前是不是皮肤模式、皮肤主色是什么、暗化参数多少"**，无法在内部面板做协调（如让卡片透出皮肤图、描边取皮肤主色）。

### 4.2 扩散方案（复用现有 GlobalDictionary 范式，零新机制） 在 `SyncAppResources` 内新增推送以下键（同步写入 `Application.Current.Resources` 与 `GlobalDictionary`）：

| 新增键 | 类型 | 说明 |
|--------|------|------|
| `SkinBackgroundBrush` | Brush | = `BackgroundBrush`（皮肤图或色调托盘，与根背景完全一致），供插件内容层 `DynamicResource` 引用——让插件面板可透出同一张皮肤图 |
| `SkinIsActive` | bool | 当前是否处于皮肤模式（`active` 非空），插件据以决定是否走"透出皮肤"还是"中性托盘" |
| `SkinDarkenOpacity` | double | 皮肤暗化强度，插件内部覆盖层可复用同一暗化值保持视觉一致 |
| `SkinAccentFromSkin` | Color | 配色皮肤激活时的主色（=Accent），插件自定义绘制取色用 |

> 推送方式与现有 `CardBorderBrush` 完全一致（`app.Resources[key]=...` + 复制到 `ThemeResourceProvider.GlobalDictionary`），**不改推送范式、不新增广播维度**。

### 4.3 插件契约扩散（SDK 只读快照） `ThemeSnapshot`（shell-plugin-sdk）新增只读字段，让有自定义绘制的插件能读皮肤状态做协调，**不暴露修改权**（保持 SDK 最小面）：

```csharp
// IShellPluginWindow.OnThemeChanged(ThemeSnapshot) 载荷扩展（向后兼容，init 新增字段）
public bool SkinIsActive { get; init; }        // 是否皮肤模式
public Brush? SkinBackgroundBrush { get; init; } // 皮肤背景 Brush（无皮肤为 null）
public Color SkinAccentColor { get; init; }     // 皮肤/主题主色
```

插件两种适配路径（二选一即可自动跟进皮肤）：

- **路径 A（推荐，零代码）**：插件 XAML 用 `DynamicResource SkinBackgroundBrush` / `CardBorderBrush` 等令牌——`GlobalDictionary` 刷新时自动重绘，**无需实现 `OnThemeChanged`**。
- **路径 B（自定义绘制）**：实现 `OnThemeChanged`，读 `ThemeSnapshot.SkinBackgroundBrush` 等做协调绘制。

### 4.4 责任边界（对未来功能界面的承诺）

> **皮肤系统保证**：任何新功能界面，只要遵循以下两条接入契约之一，即自动获得皮肤适配，无需皮肤系统逐个适配：
> 1. 继承 `ShellWindow`（或经 `PluginHostWindow` 托管）→ 根外壳自动吃皮肤 + 基类订阅重绘；
> 2. 内部视觉元素用 `DynamicResource` 绑定 `GlobalDictionary` 令牌（`SkinBackgroundBrush` / `Theme*` / `CardBorderBrush` 等）→ 随皮肤/主题一键刷新。
>
> 皮肤系统**不负责**也无法触及：插件在内容区硬编码的颜色/画刷（违反路径 A/B 即自担视觉断裂）。这点在插件开发规范文档中须明确告知。

---

## 5. 图片皮肤渲染推导（`BackgroundBrush` 扩展）

现有 `BackgroundBrush` 在 `SkinPath` 非空时只做 `ImageBrush(UniformToFill)`。扩展后：

```
if active 以 "img:" 开头:
    path = active[4:]
    base = 解码 BitmapImage (DecodePixelWidth/Height 上限 2560, CacheOption.OnLoad, Freeze)
    brush = ImageBrush(base) { Stretch = MapStretch(imgStretch) }
    if imgStretch==Tile: brush.TileMode=Tile + Viewport 控制密度
    if imgOpacity<1: brush.Opacity = imgOpacity
    return brush   // 暗化/模糊不写进此 Brush（保持冻结缓存契约）
```

暗化/模糊层：`ShellWindow` 在根 `ChromeBorder` 内新增一个 `IsHitTestVisible=false` 的覆盖层（与现有 `ApplyGlassOverlay` 机制同思路、独立、默认 `Visibility.Collapsed`），参数变化时只刷新该层，不动已冻结图。覆盖层厚度/不透明度由 `appearance.skin.imgDarken`/`imgBlur` 驱动。

---

## 6. 配色皮肤激活流程（`preset:` 路径）

```
SkinManager.Apply("preset:midnight"):
    preset = BuiltinPresets["midnight"]
    appearance.WindowTint   = preset.Tint      // 触发 WindowTintChanged
    appearance.Accent       = preset.Accent    // 触发 AccentChanged
    appearance.BorderStyle  = preset.BorderStyle
    appearance.BorderStrength = preset.BorderStrength
    appearance.SkinActive   = "preset:midnight"   // 记录来源，不写图
    // 各 setter 各自广播 Changed → ShellWindow/GlobalDictionary 自动重绘（现有机制）
```

不引入新广播维度——复用 `WindowTintChanged`/`AccentChanged`/`BorderChanged`，**零新接口**。

---

## 7. 皮肤管理器（UI + 逻辑）

### 7.1 逻辑层 `SkinManager`（新增类，无 UI 依赖）

- `IEnumerable<SkinEntry> List()`：扫描内置预设 + 用户图片（`%APPDATA%\BetterDesktop\skins\` 目录图片，或历史选过的 `img:` 记录）。
- `Apply(string id)`：按 `img:`/`preset:` 前缀分发。
- `Remove(string id)`：仅对用户图片皮肤有效（删注册记录 + 可选删文件，需确认）。
- `SkinEntry`：`Id` / `Name` / `Kind(img|preset)` / `Thumbnail`（预设用色块，图片用缩略图）。

### 7.2 UI（`ThemeSection` 内重做 SkinRow → SkinCard）

- 顶部：当前皮肤名 + "管理"展开。
- 列表：网格缩略图（预设色块 / 图片缩略图），点击即 `Apply`。
- 选中图片皮肤时，下方展开参数面板：拉伸方式（ComboBox）、暗化（Slider 0–0.8）、模糊（Slider 0–1）、不透明度（Slider 0.3–1）。
- 底部："选择背景图片"（原功能保留，选后 `Apply("img:<path>")`）、"清除皮肤"。

---

## 8. 边界与错误处理

| 场景 | 处理 |
|------|------|
| 皮肤图片路径失效/被删 | `BackgroundBrush` 现有 `try/catch` 回退半透明托盘；管理器 `List()` 扫描时剔除失效项并清理 `appearance.skin.active` |
| 图片解码失败 | 同上回退；不抛异常中断 UI |
| 超大图（>8K）内存 | 解码限制 `DecodePixelWidth/Height`（上限 2560）后再 Freeze，避免显存爆炸 |
| 平铺(Tile)模式 | `ImageBrush.TileMode=Tile` + `Viewport` 控制密度；非 UniformToFill 时不裁切 |
| 预设皮肤与手动调参冲突 | 激活预设时整体覆盖对应键；用户后续手动调 Accent/Opacity 即视为"自定义覆盖"，`skin.active` 仍标记该预设（仅来源记录），不强制回滚 |
| 多窗口并发重绘 | 复用现有 `Changed` 单线程广播 + 各窗口独立 `ApplyAppearance`，无新增竞态 |
| 插件硬编码颜色不跟随 | **不在皮肤系统处理**——属插件违反接入契约（§4.4），由插件开发规范约束 |
| 设置 JSON 读写 | 沿用 `ISettingsService` 扁平键值 + 临时文件替换落盘，线程安全 |

---

## 9. 验收标准

1. **不回归**：现有"选一张图铺满"行为不变（默认 `imgStretch=0` 等价于原 `UniformToFill`）。
2. **图片参数化**：切换拉伸/暗化/模糊/不透明度，所有 Shell 窗口根背景实时变化，面板/内容层文字仍可读。
3. **配色皮肤**：点击预设即整体换色（WindowTint+Accent+描边），无需重启，所有窗口跟随。
4. **管理器**：列表显示内置预设 + 历史图片；点击切换；删除用户图片后列表更新且不再回退崩溃。
5. **【新增·扩散验收】插件内容层跟随**：新建一个测试插件窗口，其面板用 `DynamicResource SkinBackgroundBrush` 绑定——切换皮肤/配色时该面板自动透出对应背景，无需插件写重绘逻辑；实现 `OnThemeChanged` 的插件能读到 `SkinIsActive`/`SkinBackgroundBrush` 并协调绘制。
6. **持久化**：重启后 `appearance.skin.active` 及参数恢复，首屏即应用（复用 `AppearanceService.Initialize` → `SyncAppResources`）。
7. **构建**：slnx 全量 `dotnet build -warnaserror` 0 警告 0 错误。
8. **零破坏**：`IAppearanceService` / `IThemeTokens` 现有成员签名不变；`ThemeSnapshot` 仅 `init` 新增字段（向后兼容）；`ShellWindow` 仅新增可选覆盖层（默认不激活），不影响无皮肤窗口。

---

## 10. 实施拆分（✅ 已落地于 2026-08-23）

1. ✅ `AppearanceService`：扩展 `BackgroundBrush` 读 `appearance.skin.*`（stretch/darken/blur/opacity + 解码尺寸上限 2560）；新增 `SkinActive`/`SkinKind`/`SkinImage*` 成员；`SyncAppResources` 推送 `SkinBackgroundBrush`/`SkinIsActive`/`SkinDarkenOpacity`/`SkinAccentFromSkin` 到 App 资源 + `GlobalDictionary`；**预设应用下沉到 `SkinActive` setter（单一真相源）**。
2. ✅ `shell-plugin-sdk`：`ThemeSnapshot` 新增 `SkinIsActive`/`SkinBackgroundBrush`/`SkinAccentColor`（init 字段，向后兼容）；`SkinKind` 枚举移至 SDK（跨层共享）。
3. ✅ `ShellWindow`：根 `ChromeBorder` 内新增暗化/模糊覆盖层（IsHitTestVisible=false，默认 Collapsed），受 `SkinChanged` 触发；不动已冻结图。
4. ✅ `SkinManager`（新类）：4 套内置预设（graphite/midnight/sakura/mint）+ 用户图片扫描（`%APPDATA%\BetterDesktop\skins\`）+ `Apply/Remove`。
5. ✅ `ThemeSection`：`SkinRow` 重做为 `SkinCard`（网格缩略图列表 + 参数面板：拉伸/暗化/模糊/不透明度 + 选择/清除按钮）。
6. ✅ 测试：新建 `shell-settings-tests` 工程（注册进 slnx），11 个单测覆盖激活模型/推导/SkinManager/预设下沉；headless 验证通过。

### 实施中修复的既有 bug

- **`SetColor/SetDouble/SetString` 首次写入失效**：原实现用 `value` 作 fallback，键未设置时 `cur==value` 恒成立导致跳过写入（如首次设 `WindowTint` 默认值不被持久化）。已改为"键未设置即视为首次写入"，修复后预设覆盖/参数持久化正常。

---

## 11. 明确不做（防"乱搞"）

- ❌ 不给"所有控件/按钮/文本"单独贴皮肤图——皮肤只作用于窗口根背景层 + 经令牌扩散到插件内容层（由插件自行决定是否引用）。
- ❌ 不引入第二套主题引擎或替换 `AppearanceService` 单一真相源。
- ❌ 不用 region/SetWindowRgn 裁切皮肤图（圆角交给 DWM）。
- ❌ 不做皮肤市场/在线下载（本期仅本地图片 + 内置预设）。
- ❌ 不实现深/浅色自动跟随系统（现有 `Mode` 手动三态保留）。
- ❌ 不修改 `IAppearanceService`/`IThemeTokens` 现有成员签名（仅扩展派生与推送）。
