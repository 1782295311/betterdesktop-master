using Xunit;

// WPF Application 单例 + SettingsService 共享 %APPDATA%\BetterDesktop\settings.json。
// xUnit 默认跨测试类并行，会导致重复建 Application、XAML 资源字典并发改、settings.json 跨测试污染。
// 以可靠性为先，禁用跨类并行。详见 2026-08-23 修复记录。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
