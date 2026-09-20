# extension-rules.md — 扩展性与社区契约

> 地位：让 Better Desktop Cordis 像 DeepSeek Harness 一样**高扩展、可社区**的对外契约（MECHANISMS.md M8 的唯一真相源）。
> 分工：本文件管契约（清单字段/扩展点声明/兼容策略）；替换、隔离、熔断、回退等**行为面**见 `docs/pluginization.md`（M14）。
> 目标形态：第三方开发者只读本文件与各包 README，就能写插件、提贡献，不需要问任何人。

## 一、扩展点显式化

1. 每个包的 README「扩展点」节必须逐项声明可扩展接口（门禁 `package-readme` 已强制该节存在；内容真实性靠评审）。
2. 内核级扩展点就是服务图：插件声明 `inject` 依赖服务、`provide` 提供服务；不再有第二套扩展机制（M5）。
3. 不允许「隐式扩展点」（未声明的接口被外人继承/钩挂）；发现即要求补声明或封闭接口。

## 二、插件清单草案（plugin.json v1，P1 由 ADR-002 冻结）

| 字段 | 含义 | 必填 |
|---|---|---|
| `id` | 反向域名唯一标识 | 是 |
| `name` / `version` | 显示名 / 语义化版本 | 是 |
| `entry` | 入口程序集与类型 | 是 |
| `config` | 配置默认值与 schema 引用 | 否 |
| `inject` / `provide` | 依赖服务 / 提供服务声明 | 否 |
| `optional` | 可选依赖（缺省不阻塞加载） | 否 |
| `license` / `homepage` | 社区分发信息 | 否 |
| `contextMenus` | 系统右键菜单声明（M3.1 v1 可选，见下） | 否 |

**contextMenus 字段（M3.1 统一注册体系 v1）**：一个功能声明一次，三路输出同源——① 自绘菜单项（各主体产出 `MenuItemDef` 时填 `Action` 对齐标识）；② 系统右键注册表 verb（`ContextMenuRegistry` 按 `scene` 注入 HKCU，命令 = CLI `--menu-cmd <action>`）；③ CLI 动作路由键（`Action` 即 CLI 的 `--menu-cmd` 参数：headless 直执行 / 需宿主提示 / `plugin:<id>:<action>` 分派）。

```jsonc
"contextMenus": [{
  "scene": "file | directory | background | all",   // 场景，默认 file
  "verb": "BetterDesktop.ConvertToPdf",             // 注册表键名（同场景唯一）
  "title": "转换为 PDF",                            // MUIVerb，≤80 字符红线
  "icon": "optional-path",                          // 可选
  "action": "plugin:<pluginId>:<actionName>",       // CLI 分派标识（内置动作如 convert-to-pdf 亦可）
  "args": ["%1"],                                   // %1=文件 %V=目录 背景=空
  "extended": false                                 // Shift 扩展项（Win10 语义），可选
}]
```

- 装配期注入 `HKCU\Software\Classes\<scene根>\shell\<verb>`（键名避让、`%1`/`%V` 后缀、MUIVerb≤80、幂等重写——红线见 `TECH-KNOWLEDGE/72-右键菜单/`）。
- CLI 分派：action 前缀 `plugin:` → 定位插件 → ALC 加载 → 调能力接口（`IContextMenuActionHandler`，第二期骨架）；内置动作（convert-to-*/compress-*/unzip-*）走 headless 直执行。

## 三、API 兼容与弃用

1. 语义化版本：Major 破坏性 / Minor 新增 / Patch 修复；公开契约 = Contracts 命名空间与内核服务。
2. 破坏性变更：必须新开 ADR，并给弃用期（旧契约保留一个 Minor 周期）；**禁止 `[Obsolete]` 同名垫片**（ADR-001 R 系列教训）。
3. 插件运行时隔离：第三方插件进程内崩溃不得拖垮外壳（内核 PluginGuard，P1）；重资源/不可信插件走进程外模式（P2 规划）。

## 四、文档与示例义务（社区自助的前提）

1. 每个包 README 四小节（职责/依赖/扩展点/Known Limitations）——已有机检。
2. 关键扩展点必须附最小示例，放 `docs/cookbook/`；示例过时即红（P1 起 gen --check）。
3. 新能力合入前必须同步更新：包 README、术语表（如需新术语）、决策记录。

## 五、社区贡献

1. 贡献入口：根目录 `CONTRIBUTING.md`（流程、门禁、决策记录义务）。
2. PR 最低门槛：门禁全绿 + 决策记录 + 门禁单测齐备（改门禁时）。
3. 行为准则与 issue 模板：P2 随社区开放落地。

## 六、打包与分发（P2 落地）

1. 插件包 = 压缩包 + `manifest.json` + 完整性哈希 + 可选签名；布局：清单与 `plugin/` 内容并存包根。
2. 安装即校验：哈希 / 签名不通过拒绝安装；来源显示于状态中心。
3. 审核分级：签名可信 → 免确认；未签名 → 用户显式确认；权限滥用 → 下架并熔断。
