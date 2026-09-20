# Cairo 修复计划 · 表情包：从「内容分类」到「独立标记」

> 用户两次反馈（2026-09-13）：
> ① "我们加表情包的方式有问题，你就没有考虑过有些表情包已经被我们的剪贴板历史给记录了吗？"
> ② "但不是所有的表情包都是 gif，还有的就是一张图啊…**我的意思是跟收藏一样的机制。这样就不管是图片还是颜文字都可以了**"
>
> 第一次反馈暴露了判重缺陷；第二次指出的是**更根本的模型错误**：我把表情包当成了一种"内容类型"，而它本质是一个**用户标记**。

## 1. 问题（两阶段）

### 阶段一：判重口径分裂 → 同一张图留两条
- 图片条目键 = `sha256("image:{像素hash}")`，表情包条目键 = `sha256("sticker:{文件字节hash}")` —— **两个键永不相同**；
- 且导入侧的判重**只查"表情包集合"**，既拦不住自己、也发现不了历史；
- 结果：先复制过某张图（历史里是图片条目），之后再导入为表情包 → 列表里**两条同内容**。

### 阶段二：把"标记"当成了"类型"（更根本）
- 表情包是 `Category::Sticker` **分类**，且**只能靠"导入本地文件"产生**；
- 直接后果：**文字颜文字永远无法成为表情包**（它没有文件可导入），静态图与动图被迫走上"文件副本"这条重路径；
- 用户的口径很清楚：**"跟收藏一样的机制"** —— 收藏（`is_pinned`）是打在任意条目上的独立布尔，表情包同理。

## 2. 最终设计（标记制）

```
is_sticker: bool   —— 与 is_pinned 同级的独立标记，任意条目可标记
                     （文字颜文字 / 静态图 / 动图 / 文件一视同仁）
```

**标记带来的两件事**（不含第三件 —— 它不改变条目内容）：
1. 出现在「表情包」筛选里；
2. 与收藏一样**豁免驱逐**与**「清理未收藏」**。

| 位置 | 改动 |
|---|---|
| `model.rs` | 新增 `is_sticker`；`Category::Sticker` 降级为**遗留值**（仅为旧数据兼容） |
| `engine.rs::cmd_set_sticker` | **新增 IPC**：对任意条目打/取消标记（面板行内按钮直接调） |
| `engine.rs::cmd_add_sticker` | 命中已有条目 → **只打标记**，不再改 `content_type`/`file_paths`/分类 |
| `engine.rs::QueryFilter` | 新增 `sticker` 条件；`category == 5`（旧调用方）自动翻译为 `sticker = true` |
| `store.rs` | `evict` / `clear_unpinned` 豁免判据由"分类"改为"标记"；`remove_entry_files` 同理 |
| `store.rs::migrate_sticker_flag` | 启动迁移：旧 `category == Sticker` → `is_sticker = true` + 分类按**实际内容**重判 |
| 面板 | 行内新增 🎴/😀 按钮；筛选走 `sticker` 参数；预览形态按"内容类型 + 标记"共同判定 |
| legacy `ClipboardManager` | 同步：`IsSticker` 标记 + `SetSticker`/`ToggleSticker` + 各处豁免判据 |

**动图的特殊处理（唯一需要碰内容的情形）**：库里的图片条目是引擎重编码的 PNG（只剩首帧）。
若导入的是 GIF/WebP/APNG，则必须改指**原文件副本**才放得出动画 —— 这是标记制下唯一保留的"内容替换"路径，
且仅在"命中的是图片条目 **且** 导入的是动图"时触发。

**指纹**：`ItemKind::Files` 的判重条件由 `category == Sticker` 放宽为 `!content_hash.is_empty()` ——
指纹不该跟着标记走（标记是用户意图，指纹是内容身份）。

## 3. 真机验证

### 阶段一（判重口径统一，当时实测）
```
1) 捕获为历史条目：contentType=1(Image) category=3(Image)
3) add_sticker → added=0 upgraded=1 skipped=0        ← 原地升级，未新增
5) entries 13 → 13                                   ← 没有出现第二条
```

### 阶段二（标记制，2026-09-13 实测）
```
1) 文字颜文字捕获为文字条目：contentType=0 isSticker=False
2) set_sticker(true) → isSticker=True
3) query sticker=true → total=1，包含这段颜文字        ← **颜文字真的成了表情包**（旧模型不可能）
4) set_sticker(false) → 立刻从筛选消失                 ← 双向可控
5) 图片捕获：contentType=1(Image)
6) add_sticker(同一张图) → added=0 upgraded=1          ← 只打标记
7) 条目 contentType 仍是 1(Image) isSticker=True；entries 15 → 15   ← 不改类型、不新增
8) query sticker=true → 包含该图
9) cleanup removed=2；entries 13（恢复原状）
```

## 3.5 改造后的代码审查（2026-09-13）

用户随后要求"检查有没有 bug 和程序问题"，做了一次独立代码审查 ——
**抓到 1 个高危功能回归（`capture.rs` 的表情包写回判据未随模型改造更新，导致只认位图的应用粘不出东西）
+ 5 个中等问题**（含跨后端哈希口径冲突、取消标记后副本不回收、文字表情包显示图片图标等）。
详见 `docs/audits/2026-09-13-sticker-mark-review.md`；教训见
`TECH-KNOWLEDGE/13-剪贴板/1301-clipboard-history.md`「语义变更必须做全库判据审计」。

## 4. 测试

| 层 | 用例 |
|---|---|
| Rust `store` | `sticker_flag_protects_text_emoji`（**文字颜文字**受驱逐豁免 —— 旧模型不可能的场景）、`migrate_sticker_flag_converts_legacy_category`（旧分类 → 标记 + 分类重判，幂等）、`dedupe_merges_image_into_sticker_and_keeps_sticker`（保留策略按**标记**）、`evict_never_drops_stickers` |
| Rust `model` | `image_and_sticker_share_fingerprint_for_same_content_hash`、`sticker_fingerprint_ignores_file_paths` |
| 全量 | Rust **91 passed** / IPC **22** / 契约 **87**；`dotnet build BetterDesktop.slnx` 0 警告 0 错误 |

## 5. 迁移与兼容

- **旧数据**：`category == Sticker` 的条目在引擎启动时自动转为标记（`migrate_sticker_flag`），
  分类按扩展名重判（图片 → `Image`，其余 → `File`）；幂等。
- **旧客户端**：仍传 `category = 5` 的调用方由引擎自动翻译为 `sticker = true`（不需同步升级就能用）。
- **`content_hash` 口径**：表情包条目的该字段已由"文件字节 sha256（64 hex）"改为"像素 hash（16 hex）"，
  迁移会自动重算；**不要再用它做文件完整性校验**。

## 6. 取舍与遗留

1. **动图按首帧判同**：两个"首帧相同、后续不同"的动图会被视为同一张。罕见，换来"同一张图只有一条"的可预期行为；
   动图副本仍按原文件字节保存，动画不受影响。
2. **`Category::Sticker = 5` 保留**：仅为旧数据与 C# 枚举兼容，引擎不再产生该值。
   若将来清理，需同时确认没有旧版本客户端在读它。
3. **legacy 后端**：已同步实现标记与豁免（`IsSticker` + `SetSticker`），但其 `AddStickers` 仍用文件字节哈希去重
   （无像素解码能力），故"导入已在历史的图"这条路径在 legacy 下不保证命中 —— 默认后端（引擎）完整支持。
4. **多选批量标记**：目前只能逐条标记。若需要"勾选多条 → 一键设为表情包"，可复用现有的多选操作条与 `set_sticker` 循环调用（待用户确认是否需要）。
