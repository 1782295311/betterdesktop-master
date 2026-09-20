# 错误码表（BDMC1 控制管道）

> 上游：[六层模型与未来扩展点.md](六层模型与未来扩展点.md)。协议的线格式与共享向量：[protocols/bdmc1-test-vectors.json](../../protocols/bdmc1-test-vectors.json)。

## 一、为什么现在就要有错误码

协议一旦发布，"错误是字符串"就会烂成几十种写法：`Unknown component` / `unknown_component` / `no such component`。调用方只能靠字符串匹配，改一个字就断。
现在加代价为零；以后加要兼容两套。所以**错误码是结构化字段，不是人类可读文本**——人类可读的那份放在 `message` 里，**客户端不得依赖它的内容**。

## 二、传输形态

控制请求 `BDMC1|@ctl|<verb>|<arg>` 的响应是**单行紧凑 JSON**：

```json
{"ok":true,"verb":"status","data":{...}}
{"ok":false,"verb":"start","error":"unknown-component","message":"no such component: desktp"}
```

失败时 `error` 取下表字面量。**响应绝不换行**（按行分帧；换行会被转义，见两侧实现与单测）。

## 三、码表

| 码 | 何时出现 | 调用方该怎么处理 |
|---|---|---|
| `payload-too-large` | 消息超过 1 MiB（`maxMessageBytes`） | 不要重试；这是编程错误，缩短输入 |
| `unknown-verb` | `@ctl` 后的词不在命令表内 | 不要重试；检查调用的 verb 拼写或版本 |
| `unknown-component` | `start` / `stop` / `toggle` 的组件名不在组件表内 | 不要重试；用 `status` 列出可用组件名 |
| `gate-closed` | 组件开关为 false，拒绝拉起 | 不要重试；这是**用户主动关掉**的东西，不应被自动复活 |
| `forbidden` | 调用者校验失败（SID 或会话不匹配） | 不要重试；记日志告警（可能是冒充尝试） |
| `internal-error` | 内部异常（I/O 失败、锁中毒等） | 可重试一次；仍失败则降级并记日志 |

## 四、两个**不是错误码**的幂等结果

`already-running` 与 `not-running` 刻意**不作为错误码返回**——它们是幂等成功：

- `start <已在跑的组件>` → `{"ok":true,"data":{"component":"shell","running":true,"changed":false}}`
- `stop <没在跑的组件>` → `{"ok":true,"data":{"component":"shell","running":false,"changed":false}}`

理由：如果把"本来就满足"报成错误，调用方就必须区分"失败"与"无事可做"，而**幂等命令的语义恰恰是不区分**。需要区分时看 `data.changed`。
（这两个名字保留在本文件中，是为了解释**为什么不返回它们**；避免下一个人"顺手补上"。）

## 五、命名风格

一律 **kebab-case**（`unknown-component`），与协议其余枚举值一致（`on-demand` / `probe-and-reinit` / `user-triggered`）。不采用 snake_case，以免同一协议里出现两种风格。

## 六、跨语言契约

这些字面量是**契约的一部分**：

- Rust：`core/src/protocol.rs` 的 `ErrorCode` 枚举 + `error_code_literals_are_locked` 单测；
- C#：`packages/kernel/kernel-tests/Bdmc1ProtocolContractTests.cs` 的解码路径。

**改名即破约**：两侧单测会同时红。
