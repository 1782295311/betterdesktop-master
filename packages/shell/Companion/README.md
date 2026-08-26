# Companion（插件设计稿 · 深度版）

> 状态：设计草稿（尚未实现）。取代 `分析稿/方案-Shell替代程序优秀特性集成.md` 中"Companion 桌宠系统"章节的浅层描述。

## 1. 目标与边界

**做什么**：陪伴式 AI 桌宠——本地大模型驱动的对话、表情/状态机驱动的行为、语音与手势输入。
**不做什么**：通用 Agent / 系统级自动化；不接管系统设置；不联网上传用户数据。

## 2. 架构

```
Companion (Extension)
├── PetBrainService    # 决策层：感知 → 意图 → 行为（本地 Ollama / 规则回退）
├── PetStageService    # 舞台层：WPF 窗体 + 精灵/3D 模型渲染
├── PetAIService       # 与 LLM 交互（Ollama 本地，离线优先）
├── InputRouter        # 语音(STT) / 手势 / 点击 → 归一为 Intent
└── WorkerDaemon       # 常驻后台脑循环（节流，不阻塞 UI）
```

依赖：`BetterDesktop.Shell.Core`（Surface / Vibrancy）、`BetterDesktop.Kernel`（后台任务调度）。

## 3. 领域模型

```csharp
enum PetState { Idle, Greeting, Thinking, Sleeping, Playing }
record Perception(string Source, string Raw, float Confidence);
record Intent(string Action, float Confidence, Dictionary<string,string> Slots);
```

## 4. 内核集成

- `Name = "companion"`，`Inject: [typeof(IVibrancyService)]`（真实内核契约：无 ExtensionId/Manifest，依赖用 `Inject` 声明）。
- `LoadAsync(IContext)`：注册 `IPetBrainService` / `IPetStageService` / `IPetAIService`；启动 `WorkerDaemon`（常驻，遵循 automations 的常驻任务范式）。
- `UnloadAsync`：停脑循环 → 隐藏舞台 → 释放模型句柄（CPU/GPU 占用归零）。

## 5. 公共契约（语义）

```csharp
interface IPetBrainService
{
    Task<Intent> DecideAsync(Perception p);        // 感知 → 意图；规则回退保证永不抛
}

interface IPetStageService
{
    void EnterState(PetState s);                   // 状态机切换（含过渡动画）
    void ShowEmote(string emoteId);                // 表情/动作
}

interface IPetAIService
{
    Task<string> ChatAsync(string prompt);          // 本地 Ollama，离线优先
}
```

- `DecideAsync` 必须永不抛异常：LLM 不可用时走关键词规则。

## 6. 配置

- `companion.ini`：`ollama.baseUrl`、`persona.prompt`、`gesture.map`、`voice.enabled`、`cpu.cap`。
- 人格 prompt 与频率控制，避免脑循环过度触发。

## 7. 数据流

```
感知(语音/手势/定时)
  → InputRouter 归一为 Intent
  → PetBrainService.DecideAsync
  → PetStageService.EnterState / ShowEmote
  → 行为动画渲染
```

## 8. 跨插件协作

- 与 ThemeCenter：宠物外观令牌化，随主题切换表情色调。
- 舞台窗口继承 `shell-core.Surface.ShellWindow` 统一基类（统一窗口属性 + 毛玻璃入口 + `OnLoadedCore` 钩子），不自行实现窗口壳。
- 与 Dock：可被停靠或一键隐藏，复用 shell-dock 的边缘热区。

## 9. 错误处理

- LLM 不可用：规则回退（关键词应答），脑循环不中断。
- 模型加载失败：宠物进入 `Idle` 并提示用户配置端点。
- STT/手势识别缺失：仅保留点击交互，不报错。

## 10. 性能与隐私

- 本地推理，数据不上传第三方；CPU/GPU 占用设硬上限。
- 脑循环节流（如 500ms 最小间隔），Idle 时休眠。

## 11. 验收

- [ ] 本地 Ollama 对话可达
- [ ] 状态机切换带动画
- [ ] LLM 失败时规则回退仍可用

## 12. 开放问题

- 是否纳入多模态（视觉）输入；手势识别精度与误触边界。
- 常驻进程在笔记本上的功耗表现需实测。
