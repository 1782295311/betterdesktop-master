# 计划:品牌图标 + 初始化弹窗集成(host)

日期:2026-09-04
仓库:better-desktop-cordis(HEAD 0ecd841)
类别:功能开发(compact)
范围:host(BetterDesktop.Host)仅此一处;不动任何插件包。

## 目标(场景语言)

- 启动 exe 时,图标(任务栏/Alt-Tab/资源管理器)显示品牌四叶图标(BetterDesktop.ico)。
- 程序启动先弹出居中初始化弹窗:播放品牌动画(splash-anim-v5.mp4,16:9 回正定格版),动画播完(或点击跳过)后进入主程序装配,弹窗自动关闭。

## 技术库检索结论

- `TECH-KNOWLEDGE/索引.md` 检索:`启动/splash/品牌` → 无现成启动画面资产;**新建**。
- 05-图标(501-506)为「提取第三方程序图标」机制,与本任务(嵌入自有品牌图标)不同,不命中。
- 1407 自绘托盘图标:Shell_NotifyIconW 方案,本任务不涉及托盘,仅记录。

## 改动摘要(实现体)

1. **资源落位**:`docs\BetterDesktop.ico`、`docs\splash-anim-v5.mp4` → 复制进 `host\Assets\`(csproj 以 Resource 嵌入,pack URI 访问)。
2. **csproj**(`host\BetterDesktop.Host.csproj`):
   - `<ApplicationIcon>Assets\BetterDesktop.ico</ApplicationIcon>`(exe 图标 → 任务栏/Alt-Tab/资源管理器)
   - `<ItemGroup><Resource Include="Assets\BetterDesktop.ico" /><Resource Include="Assets\splash-anim-v5.mp4" /></ItemGroup>`
3. **SplashWindow**(新增 `host\Views\SplashWindow.xaml` + `.cs`):
   - 无边框透明窗口,800×450(16:9),屏幕居中、Topmost、ShowInTaskbar=false、不可调。
   - 圆角(18px)深色卡片 + `MediaElement` 播放 `pack://application:,,,/Assets/splash-anim-v5.mp4`,Stretch=Fill。
   - 关闭时机(任一先到):`MediaEnded`(动画播完)/ 鼠标点击 / 加载失败 `MediaFailed` / 10s 超时兜底。
4. **App.xaml.cs 集成**:
   - `OnStartup` 中:单实例互斥、异常钩子(保持原样)→ **提前设 `ShutdownMode.OnExplicitShutdown`**(避免 splash 关闭时应用退出)→ `new Views.SplashWindow()` + `Closed += Bootstrap.Build()` + `Show()`。
   - 原 try/catch 包 `Bootstrap.Build()` 的逻辑移至 splash.Closed 处理器(行为不变)。

## 关键约束(红线)

- Bootstrap.Build() 是 UI 线程同步阻塞(插件窗口创建);因此**必须先播完动画再装配**,不做并行(避免 UI 冻结黑屏)。
- 不改任何插件包代码;不动 Bootstrap 内部装配顺序。
- 动画播放失败(解码/资源缺失)必须能自动跳过,不得阻塞启动。
- 单实例互斥在 splash 之前执行(不变);watchdog 拉起路径同样经过 OnStartup,自动获得 splash。

## 验证(DoD)

1. `dotnet build host\BetterDesktop.Host.csproj -c Debug` 通过(TreatWarningsAsErrors=true 下零告警)。
2. 反汇编/资源检查:exe 内含 BetterDesktop.ico 与 splash-anim-v5.mp4 资源;exe 图标正确(exe 文件图标可读)。
3. 场景走查(人工):启动 → 弹窗居中播放动画 → 5s 后自动关闭 → 主界面正常装配;点击弹窗可立即跳过;断网/无解码器时弹窗不阻塞。
4. 回归:原启动链路(Bootstrap 装配顺序、ShutdownMode、异常钩子)不受影响。

## 风险

- MediaElement 解码依赖系统 Media Foundation(h264 mp4 Windows 10+ 原生支持,风险低)。
- splash 播放 5s 使「画面出现」晚 5s:可点击跳过缓解;不引入设置项(克制)。

## §14 交接节

- **注入清单**:本计划 + `docs\BetterDesktop.ico`(七档 ico)+ `docs\splash-anim-v5.mp4`(1280×720/5s/h264)。
- **模式判定**:宿主增强型(host 收口),插入式改动,无插件 API 变更,无外部契约影响。
- **适配参数**:splash 尺寸 800×450;圆角 18;超时兜底 10s;资源名 `Assets\splash-anim-v5.mp4`、`Assets\BetterDesktop.ico`。
- **DoD 核销表**:见上「验证(DoD)」4 条,实现完成逐条核销。
- **交接对象**:实现由 MainAgent 直接执行(单 agent 会话,计划即实现),完成后按 DoD 自检。
