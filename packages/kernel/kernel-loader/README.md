# BetterDesktop.Kernel.Loader

Cordis 风格插件树加载器（ADR-002 的 loader 落地）：读取 cordis.yml 声明式插件树，按启用标记装配插件并生成加载报告。

## 依赖

- `YamlDotNet`（唯一第三方依赖，版本锁定；配置格式 = YAML，对应 PLAN 的 cordis.yml）
- `BetterDesktop.Kernel`（插件与运行时契约）

## 扩展点

- `LoaderOptions.Factories`：内置插件工厂注册表（`name → Func<IPlugin>`），由宿主装配；loader 不做程序集加载（P2 HMR 范畴）

## Known Limitations

- v1 仅装配**已注册的内置插件工厂**：不支持从插件目录动态加载程序集（ALC 隔离加载属 P2）
- 不支持配置注入（intercept 语义 P2）：插件配置节暂不传给插件
- 不支持文件监听热重载（HMR P2）
- 未知工厂名按 fail-closed 处理：该条目记入报告并报错，loader 本身继续运行
