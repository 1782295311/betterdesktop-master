# coding-standards-core-rust.md — core（Rust）编写规则

> 适用范围：`core/` 及未来的 Rust 常驻件（`bd-infer`、`bd-world` 的原生部分、安全扫描器）。
> C# 侧规则见 [coding-standards.md](coding-standards.md)；层边界与禁止清单见 [architecture/六层模型与未来扩展点.md](architecture/六层模型与未来扩展点.md)。

**为什么单独一份而不是并进 coding-standards.md**：那份的标题与全部条目都是 **C# 专用**（包结构、DI、WPF、nullable、CA 规则），Rust 的规则（所有权与 `unsafe`、`Result` vs 异常、`Drop` 配对、crate 边界）与它没有一条重叠；硬塞进去会让两边都读不清，也会让那份文档超出其字数预算。

## 一、层边界（第一优先，其他规则都让位于它）

依赖**单向向下**（见架构文档第三节）。core 的三条硬边界：

1. core **不认任何扩展/表面的名字、类型、程序集** —— 它只读组件表（`components.json`）与 `settings.json` 的扁平键。想在 core 里写 `if name == "shell"` 就已经错了，应该查表。
2. core **不持有业务状态**（不缓存应用列表、不存剪贴板内容、不存索引）。
3. core **不渲染 UI**（除托盘图标与原生菜单；不得引入任何 UI crate）。

## 二、目录与文件

- `core/src/` 一文件一主题：`main.rs`（进程入口/消息循环）、`tray.rs`、`components.rs`、`settings.rs`、`log.rs`，后续 `pipe.rs`、`supervisor.rs`、`power.rs`、`security.rs`。
- **禁止** `utils.rs` / `helpers.rs` / `common.rs` —— 这类文件是无归属代码的垃圾场。想不出归属名，说明职责还没想清。
- **一文件一主题**：同一文件里不应出现两个不相关的公开概念。

## 三、公开项注释模板（七项）

仓库既有纪律是"注释即契约：因果结论附可复现命令"。Rust 侧把它具体成七项 —— **公开项（`pub fn` / `pub struct` / `pub enum` / `pub const`）必须覆盖前四项，涉及资源或实测的补后三项**：

1. **做什么**：一句话，动词开头。
2. **为什么**：因果结论，不是复述代码。写"为什么必须这样"（例如"必须先探活再对差集动手，否则把睡眠冻结误判为崩溃"）。
3. **契约**：参数与返回值语义、错误条件（`Err` 表示什么、调用方该怎么处理）。
4. **边界与失败路径**：非法输入、超时、缺失文件、解析失败分别怎么走；绝不写"不适用"。
5. **资源与生命周期**：谁分配谁释放、句柄配对（如 `RegisterHotKey`/`UnregisterHotKey` 必须同线程配对）、是否 `Drop`。
6. **调用方约束**：线程要求（如"必须在拥有消息队列的线程调用"）、调用顺序。
7. **可复现证据**：结论来自实测的，附命令（如 `powercfg /requests`、`Get-Counter`），让下一个人能复核而不是只能相信。

## 四、`unsafe`

- `unsafe` 块必须**最小化且紧贴 Win32 调用**，每个块上方一行注释说明"为什么这里安全"（不变式是什么）。
- **禁止**为了省事把整个函数标 `unsafe`。edition 2024 的 `unsafe_op_in_unsafe_fn` 绑定：`wnd_proc` 这类 `unsafe extern "system" fn` 内部仍需显式 `unsafe { }` 包裹调用 —— 这是特性不是麻烦，它标出了真正的不安全边界。
- 句柄一律用类型化包装（`HWND`、`HICON`），禁止裸 `isize`/`usize` 转换跨函数传递；必须转时（如窗口过程需要）加注释说明。

## 五、错误处理

- **生产路径禁止 `unwrap()` / `expect()`**（唯一例外：`log` 互斥量中毒，且必须写成 `unwrap_or_else(|e| e.into_inner())`）。
- 库内函数失败返回 `Result` / `Option`；**禁止 panic 跨越 FFI 边界**（panic 穿过 `extern "system"` 是未定义行为）。
- 失败**不得正常化**（仓库既有纪律）：初始化失败要么显式 `log::error` 后退出，要么记录后继续并在启动日志里可见。禁止"静默吞掉"。
- 日志分级：`error` = 用户可感知的功能失败；`warn` = 配置异常但已降级；`info` = 生命周期与决策（拉起/跳过/退避）；单条请求级细节用 `info` 但只在排查需要时加。
- 错误信息要能被搜索到具体原因（带上下文值），不要只写"failed"。

## 六、资源

- 用 RAII 表达所有权：句柄包装在结构体里并在 `Drop` 里释放（范例：`tray::TrayIcon` 在 `Drop` 中移除托盘图标并 `DestroyIcon`）。
- 需要区分"共享资源不可释放"与"自有资源必须释放"时，用显式布尔字段标明（范例：`owns_icon`），不要靠猜测。
- 计时器/线程在退出路径上必须能被停掉；长驻循环要可中断。

## 七、单测

- 测试与代码同文件（`#[cfg(test)] mod tests`），**不为测试单独建 crate**。
- 每个非平凡逻辑（分支、循环、解析、状态机）至少覆盖三态：**正常 / 边界 / 异常**。
- 优先把逻辑抽成**纯函数**以便测试（范例：`components::auto_start`、`resume_discipline_violation`），而不是靠启动进程去断言。
- **测试不得产生副作用**：不得写生产日志（由 `log::ENABLED` 闸门保证）、不得依赖真机进程状态、不得弹窗。
- 一个测试一个断言主题；测试名用中文或英文皆可，但必须说清"在什么条件下期望什么"。

## 八、命名

- 类型 `UpperCamelCase`，函数/变量 `snake_case`，常量 `SCREAMING_SNAKE_CASE`。
- 布尔字段用 `is_` / `has_` / `owns_` 前缀（`owns_icon`）。
- 枚举取值表达**语义**而非实现（`ResumeAction::ProbeAndReinit` 而非 `Action3`）。
- 运行时资产（exe / 管道 / 留痕 / 日志 / 配置键）的命名规则见 [architecture/六层模型与未来扩展点.md](architecture/六层模型与未来扩展点.md) 第七节。

## 九、依赖

- **不新增第三方依赖**，除非：标准库与 `windows` crate 都做不到，且写明理由。`windows` crate 版本面与 `engine/` 保持一致（当前 0.58）。
- 能复用仓库既有 Rust 实现就必须复用（`engine/src/hotkey.rs` 的 `parse_spec`、`settings.rs` 的读取范式、`log.rs` 的落盘范式），不重写第二份。
