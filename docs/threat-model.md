# threat-model.md — 威胁模型与负空间（core 现状）

> 回答"**防谁 / 不防谁 / 为什么**"。措施细节在各实现文件的模块注释里，这里只给**指针**（文件名 + 测试名；不用行号——行号会腐烂）。
> 与 `security.md` 分工：那份写"我们要求什么"，本份写"当前防住、不防什么"。文件名避开 `SECURITY.md`：它与 `security.md` 仅差大小写，Windows 上会**互相覆盖**。

## 一、信任边界

| 对象 | 判定 |
|---|---|
| 当前用户 SID | **可信**（管道 DACL 只放行它 + 连接后校验 SID/会话） |
| 其他用户 / 网络 / 匿名 | **不可信**（DACL 显式 DENY；**DENY 须排在 GRANT 之前**） |
| 组件 exe（安装根 / 数据目录） | **假定未被篡改**（安装目录非用户可写 + 解析后前缀断言，C19） |
| `settings.json` / `components.json` | **可信**（用户数据目录，仅当前用户可写） |
| 未来第三方模型 / 插件 | **假定可能被篡改**（C20 留位，S5.5 起用） |

## 二、防谁

| # | 威胁（场景） | 措施 | 证据 |
|---|---|---|---|
| T1 | 同机其他用户连控制管道发 `stop desktop` | ACL + 连接后校验 SID/会话，**读第一个字节前**完成 | `security.rs::validate_client`；跨账户实测 `scripts/test-pipe-acl.ps1`（该脚本记着：`CreateRestrictedToken` 会给出**假绿色**，必须换另一个用户账户） |
| T2 | `components.json` 的 exe 被改成绝对路径或 `..\evil.exe` | ① `is_bare_name` 在 `join` **之前**拒非裸名；② 解析结果须落在允许目录（按**路径段**比较） | `process.rs::is_bare_name`、`security.rs::validate_component_exe`（测试 `resolve_exe_refuses_absolute_path_even_if_it_exists`） |
| T3 | 10000 层嵌套 JSON / 超大管道消息 | `serde_json` 递归上限**不得关闭**；消息长度上限 + 单连接最长 30s | `components.rs::parse`（测试 `deeply_nested_json_is_rejected_not_stack_overflow`）、`pipe.rs::read_line_bounded` |
| T4 | 参数里塞引号拼出额外命令 | 可执行体传绝对路径、参数**逐项构造**（禁拼接） | `process.rs::spawn_detached`（测试 `command_line_quotes_exe_and_appends_args`） |
| T5 | 兼容期内 C# 与 core 同写配置 ⇒ 旧快照覆盖新值 | 同名互斥 + 互斥覆盖**整个读-合并-写** + 解析失败**拒写** + 原子替换 | `settings.rs::SAVE_MUTEX_NAME`（测试 `save_mutex_name_is_pinned`）、`settings.rs::set_flat_at` |
| T6 | 计划任务 / 开机自启指向可替换的开发目录，**或被同一台机器上的另一份 core 抢走** | 用户级 + 写权三态：dev 目录**拒绝**；产品目录里的副本只能"用"不能"写" | `ownership.rs::of`（测试 `a_second_copy_on_the_same_machine_is_only_a_tenant`） |
| T7 | 边界随时间漂移 | 棘轮：**未登记即红，登记但已失效也红** | `scripts/verify-architecture-guard.ps1`、`scripts/manifests/architecture-allowlist.json` |

**C# 侧（core 之外）**——此前只有 core 有统一判据，2026-09-20 起 C# 侧同样有机器门禁（`scripts/verify-security.ps1`）：

| # | 威胁（场景） | 措施 | 证据 |
|---|---|---|---|
| C1 | 同用户进程连 Host / 桌面服务管道投递命令；或「只写不换行」吃满内存并钉死唯一实例槽 | 有界读 + 总超时 + magic 握手，实例数限 1 | `BoundedPipeLine`；规则 1 |
| C2 | 外部数据（注册表 `UninstallString`、本进程命令行）拼进子进程参数 | `ArgumentList` 逐项传参，禁字符串拼接 | 规则 2（三处 `cmd /c` 已改） |
| C3 | 外部输入的超深 JSON 打成栈深炸弹 | 管道 / HTTP / 跨进程 stdio 显式 `MaxDepth`，禁依赖库默认值 | 规则 3（外部 5 处，硬红线） |
| C4 | 相对名 `LoadLibrary` 被当前目录劫持 | 只接受绝对路径变量 | 规则 4 |
| C5 | 空 `catch` 让安全检查静默失效 | 块内至少留注释说明「为何可忽略」 | 规则 6（棘轮，存量 34 处） |

## 三、不防谁（**带触发条件才不会腐烂成借口**）

| 威胁 | 为什么不防 | 触发条件 |
|---|---|---|
| 管理员级攻击者 | 可直接替换文件、读内存；防它等于改产品形态 | **永久不防**（本行**有意**不带条件：它不可能变成过时的借口） |
| 同账户其它进程 | 用户级产品里它等价于用户本人（C1 的 magic 握手与实例数限制只能提高门槛，**不构成防线**；per-session token 同理——同用户可读） | **开放第三方插件生态时** |
| 安装目录可写的攻击者 | 用户级 Run 键已有同等能力，计划任务未引入新信任边界 | **引入 Authenticode 签名时** |
| 第三方模型 / 插件文件被替换 | C20 留位未实现 | **S5.5 起用 C20 时** |
| 屏幕内容（截图 / OCR / 翻译）外泄 | 无云端通路，全在本机 | **bd-infer 立项时** |
| 供应链投毒（crates / NuGet） | 依赖最小化已做，但不做运行时校验 | **出现 CVE 时**（详见 `security.md` §二） |

## 四、什么时候回来改

1. **引入代码签名** ⇒ "安装目录可写"从"不防"移到"防"。
2. **`bd-infer` / `bd-world` 立项** ⇒ 新增 GPU、模型文件、屏幕内容威胁。
3. **开放第三方扩展** ⇒ 新增"恶意扩展"（当前仅 schema 与 C20 留位）。
4. **兼容期结束（S7）** ⇒ T5 改述为"唯一写者 + 写入可观测（谁 / 什么键 / 何时）"。
5. **任何措施被删除时** ⇒ 先回本表找它对应的威胁；**找不到 = 它可能是装饰性的**，应删而不是留。
