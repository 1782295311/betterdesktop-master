# 内存治理器「把常态当异常」修正

## 完成内容 针对「程序加载一分多钟后崩溃 / 内核管理阻止初始化」的定位与修正。

### 关键结论

- **崩溃无 crash.log、无 restart.count** → 进程不是走托管崩溃重启路径（`RestartFromFatal` 必然写 crash.log），而是原生层崩溃或外部终止；但治理器此前确实存在「把常态当异常」的设计缺陷，按用户判断已修正。
- **已确认安全的点**：`PluginHandle` 的插件加载异常都被 catch 吞成 Failed 状态，不会变成未观察任务异常导致进程崩；治理器 Timer 采样路径异常也有 try/catch。

### 改动文件

1. `packages/kernel/kernel/Core/ResourceGovernor.cs`
   - `DetectProcessCritical`（进程级兜底）新增 **warmup 保护**：启动后前 `BaselineWarmupMs`（默认 30s）内绝不评估，避免启动早期内存波动误判。
2. `host/Bootstrap.cs`
   - 进程级 `OnProcessCritical` 由「直接 `Environment.Exit(1)` 杀进程」改为**仅记录日志**，不再把内存偏高当成致命异常主动自杀。真正的进程重启仍由 `AppDomain.UnhandledException` / `DispatcherUnhandledException`（出错管理）负责。
   - 新增环境变量开关 `BD_DISABLE_GOVERNOR=1`：临时完全关闭内存治理器，便于二分验证。

### 配合前文已落地的修复（本次会话早些时候）

- 内存采样口径：`GC.GetTotalMemory` → `Process.GetCurrentProcess().WorkingSet64`，数值与任务管理器一致（解决「显示 11MB 而非 148MB」的口径混淆）。
- 基线 + 相对涨幅预警：稳定窗口采集基线，按基线 × 系数（1.5/2/3）预警，相对值与绝对上限取较小者。

## 验证方案（重要）

无法仅从静态分析定位「无声崩溃」，建议你在真机用二分法确认：

1. **默认启动**（治理器启用，但已不再自杀）——看是否还崩。
2. **`BD_DISABLE_GOVERNOR=1 BetterDesktop.Host.exe`** 启动——若不再崩，则坐实治理器是元凶；若仍崩，则排除治理器、转向原生层（DWM/WebView/COM）。

## 后续

- 若确认治理器是元凶，进一步把「单插件内存阈值干预」也收敛为「仅在插件真正 Failed/崩溃时动作」，彻底贯彻「出错管理」定位。
- 若排除治理器，需要增强启动/运行日志（DebugLog 早期崩溃丢日志）以定位原生层崩溃。 </content_policy> </think:6124c78e><tool_calls:6124c78e> <tool_call:6124c78e>TaskUpdate<tool_sep:6124c78e> <arg_key:6124c78e>taskId</arg_key:6124c78e> <arg_value:6124c78e>27
