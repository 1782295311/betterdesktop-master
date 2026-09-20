using BetterDesktop.Kernel.Contracts;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.ContextMenus.Contracts;
using BetterDesktop.Shell.ContextMenus.Services;
using BetterDesktop.Shell.Settings.Contracts;

namespace BetterDesktop.Shell.ContextMenus;

// ============================================================
// 【白话导航 · 右键菜单域】任何 AI 接手时，凭白话需求定位到精确文件：
//   "我想禁用/删掉某个右键菜单项（如某软件加的项）"   → Services/MenuManagerToggle.cs（启停/删除，写前备份）
//   "我想新建/注入一个自己的右键菜单项"             → Services/MenuManagerCreate.cs（写 HKCU，键名前缀 UserMenu）
//   "我想改右键菜单样式（Win10 / Win11 经典）"       → Services/MenuManagerStyle.cs（CLSID 样式开关）
//   "我想按来源整体关掉一类菜单项"                 → Services/MenuManagerGroups.cs（BetterDesktop/ShellEx/系统 分组开关）
//   "我想看/管理注册表里所有右键菜单项"             → Services/MenuManagerService.cs（枚举入口）+ Sections/MenuManagerSection.cs（设置 UI）
//   "桌面图标/空白处右键"                           → 已回归 shell-desktop 自绘（DesktopIconsControl.ShowMenu → DesktopMenuPopup）；跨进程委托 2026-09-10 移除
//   "开始菜单条目的右键菜单"                       → shell-start-menu/Services/AppItemActions.cs（AttachNative）
//   "dock 图标/应用提取器的右键菜单"               → shell-dock/Services/DockMenuPopup.cs + Templates/DockItemTemplate.cs（dock 自管例外）
//   "右键菜单里加压缩/解压/回收站还原/新建文件"    → ZipOps.cs / RecycleRestore.cs / ShellNewCatalog.cs
//   "第三方压缩工具/终端入菜单（7-Zip/WinRAR/WT…）" → Services/ToolCatalog.cs（工具发现与命令模板）
//   "菜单项图标/显示名/访问键"                     → MenuItemIconCache.cs / MenuText.cs / MenuAccessKeys.cs / ResourceRef.cs / GuidInfo.cs
//   "注册表写前备份 / 系统项夺权"                  → RegTreeBackup.cs / RegTakeover.cs
//   "系统原生菜单的渲染通道（HMENU→TrackPopupMenuEx）" → Services/NativeMenuPopup.cs + StaComWorker.cs
//   "某个扩展把程序搞崩了 / 想不用人工去屏蔽它"          → Services/HandlerCrashGuard.cs（连续 3 次自动停用）
// ============================================================

/// <summary>
/// 右键菜单插件（context-menu）——2026-09-07 架构现状：
/// ①桌面/图标右键自绘菜单（2026-09-07 用户拍板回归：DesktopIconsControl 构建 + DesktopMenuPopup
///   渲染，WPF ContextMenu 零 IContextMenu 依赖——系统原生接入稳定性不达标：拖动关联崩溃、
///   菜单类型错乱、非 explorer 进程聚合第三方扩展 0xC0000005 实锤）；
/// ②系统原生菜单接管（NativeMenuPopup：开始菜单/文件管理器/未申明自绘菜单的表面统一交给系统）；
/// ③系统右键菜单管理器（MenuManager：注册表层枚举/启停/新建/注入/备份）。
/// 历史沿革：2026-09-05 曾全面退役自绘右键（桌面统一走系统原生），2026-09-07 用户拍板回归
/// 自绘——桌面空白与图标条目一律自绘菜单（仅基础操作，不含第三方 shell 扩展项，代价已确认）。
/// </summary>
public sealed class ContextMenuPlugin : IPlugin
{
    public string Name => "context-menu";

    public IReadOnlyList<Type> Inject { get; } = [];

    public Task LoadAsync(IContext context, CancellationToken cancellationToken = default)
    {
        // 2026-09-07 回归自绘：桌面/图标右键由 shell-desktop 自绘（DesktopIconsControl +
        // DesktopMenuPopup），context-menu.mode 不再参与行为判定（旧值 custom 残留已移除，
        // 设置键保留兼容旧设置文件）。

        context.Provide<IFileClassifier>(new FileClassifier());

        // 预热 ShellNew 缓存（注册表扫描移出同步段；ShellNewCatalog 独立扫注册表，不走已删除的 RegistryVerbs）。
        _ = Task.Run(ShellNewCatalog.Enumerate);

        // 设置分区（「右键菜单」：菜单来源总开关 + 样式 + 场景扩展 + 新建 + 备份；
        // 2026-09-05 更名合并——原 ContextMenuSection（功能状态+控制）并入本分区，分区名与「菜单栏」区分。
        context.Get<ISettingsSectionRegistry>()?.Register(new Sections.MenuManagerSection());

        // 第三方 COM handler 崩溃熔断（4.1）：
        //   ① Attach 设置服务（ambient）——运行中 broker 报回"某 handler 把 broker 搞死了"时也要能写可见状态；
        //   ② ApplyPending 消费已记账的熔断，达阈值自动停用（走既有 Toggle 通道，写前备份、可逆）。
        HandlerCrashBreaker.Attach(context.Get<ISettingsService>());
        HandlerCrashBreaker.ApplyPending();

        return Task.CompletedTask;
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
