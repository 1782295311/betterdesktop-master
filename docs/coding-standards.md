# coding-standards.md — C# 编写规则（P1 起生效）

> 地位：内核与所有包代码的编写标准。本文规则先于代码存在；违反本文的代码不得合入。
> 与 `docs/TERMINOLOGY.md` 的关系：命名以术语表为准；本文只规定结构、风格与禁止事项。

## 一、工程基线（P1 全部包适用）

- 目标框架：`net8.0-windows`（ADR-001 D2 冻结，禁止任何包写其它 TFM）。
- `<Nullable>enable</Nullable>`、`<ImplicitUsings>enable</ImplicitUsings>`、`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`、`<Platforms>x64</Platforms>`。
- 依赖选型：**内核基础包（kernel/kernel）原则上零第三方运行时依赖**；确需引入的依赖（含 MS DI 等）必须在 ADR-002 或对应决策记录中写明理由与边界。
- 文件编码 UTF-8；换行 LF（与文档一致）。
- 每包一个 csproj + 同目录 README.md（门禁 `package-readme` 强制，含 `## Known Limitations`）。

## 二、目录与命名

- 目录 → 命名空间映射、包命名规则见 `docs/TERMINOLOGY.md` 第二节，逐字遵守。
- 每类型一个文件；XAML 与 code-behind 成对；自动生成文件后缀 `.g.cs`。
- 标识符用英文（PascalCase 公共、`_camelCase` 私有字段）；注释与文档用中文。

## 三、内核代码禁止清单（红线级，来自 ADR-001 §2.3 与 Cordis 源码教训）

1. 禁止静态单例 / ServiceLocator / 静态可变全局状态——一切能力走 Context 服务图。
2. 禁止透明代理与反射式服务解析——服务读取用显式 `ctx.Get<T>()`，内核内部反射一次后缓存委托。
3. 禁止跨程序集裸 C# `event`——跨插件通信走内核事件服务（五种分发）。
4. 禁止在 UI 线程之外触碰任何 WPF 对象——一律经内核 Dispatcher 服务封送 STA 线程。
5. 禁止吞异常（`catch {}`）与 `Console.WriteLine`——崩溃走崩溃可见机制，日志走内核日志服务。
6. 禁止 `[Obsolete]` 同名垫片与「搬家留垫片」——迁移即改调用方（老仓 R4 教训）。
7. 禁止复制旧仓 `cairoshell原版` 的任何逻辑代码；结构体布局仅限 `docs/MECHANISMS.md` M4 白名单。
8. 插件卸载必须可完成：effect 注册的清理器必须释放全部句柄与事件订阅（HMR 的前提）。
9. 禁止 UI 硬编码：字体 / 颜色 / 圆角 / 窗口属性一律走统一地基令牌，禁止自建 Window 派生体系（`docs/ui-foundation.md`）。
10. 禁止复制粘贴与重复实现：复用先于新建，共享逻辑必须上移（`docs/reuse-rules.md`）。

## 四、包结构与测试规范

- 包内布局：`Services/`（服务实现）、`Plugins/`（插件入口）、`Contracts/`（对外接口，可选）、`Resources/`（资产）。
- 测试：xUnit，工程名 `*.Tests`；**契约测试优先**——先写「服务图解析/epoch 去重/effect 逆序清理」三类内核契约测试，再写实现。
- 内核三机制（服务图、依赖驱动重载、托管清理）必须有独立测试物证（对应 P1 验收判据）。
- 包粒度与拆包判据见 `docs/reuse-rules.md`；对外扩展点声明义务见 `docs/extension-rules.md`。

## 五、诚实标注原则

每条禁止项要么有对应门禁/测试（标注名称），要么如实标注「暂无机检，靠评审」。禁止把纸面规则写成像有机检的样子（老仓「假绿」教训，ADR-001 R4 配套）。
