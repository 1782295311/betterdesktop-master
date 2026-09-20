using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BetterDesktop.Shell.Clipboard.Contracts;
using BetterDesktop.Shell.Clipboard.Ipc;

namespace BetterDesktop.Clipboard.Panel;

/// <summary>
/// 入口装配：按 <c>extensions.clipboard-history.entry-style</c>（sidebar|off）装配快捷入口
/// 并持有与引擎的唯一 IPC 连接。
/// 2026-09-12 用户实测否决悬浮球（'悬浮球并没有用，还是用侧边栏好一点'）：
///   - 唯一入口 = 侧边栏（EdgeHandleWindow 右缘「›」手柄，**点击**滑出完整面板，可拖动移动）；
///   - 悬浮球实现（FloatingOrbWindow/OrbPanel）已于 2026-09-12 **删除**（无调用点 + 继承裸 Window
///     违反统一基类纪律）；历史配置值 orb/both 静默按 sidebar 处理；
///   - off = 不装配任何入口。
/// 事件转发：ClipboardChanged（保留事件链供未来订阅者，如灵动岛）。
/// </summary>
public sealed class EntryHost : IDisposable
{
    private readonly ClipboardIpcClient _client;
    private EdgeHandleWindow? _edge;
    private bool _disposed;

    public ClipboardIpcClient Client => _client;

    /// <summary>复制事件（引擎 clipboard_changed）→ 订阅者（灵动岛/搜索框）。</summary>
    public event Action<ClipboardChangedInfo>? ClipboardChanged;

    public static EntryHost Create()
    {
        var config = PanelTheme.ExtensionConfig();
        if (!config.Enabled)
        {
            PanelLog.Trace("扩展中心 clipboard-history 未启用（enabled=false），面板不装配入口");
            // CA2000：transport 生命周期转交 ClipboardIpcClient（Dispose 释放），创建后立即托管
#pragma warning disable CA2000
            return new EntryHost(new ClipboardIpcClient(new NamedPipeTransport()), startEntries: false);
#pragma warning restore CA2000
        }

#pragma warning disable CA2000
        var host = new EntryHost(new ClipboardIpcClient(new NamedPipeTransport()), startEntries: true);
#pragma warning restore CA2000
        host._client.ClipboardChanged += info =>
        {
            try { host.ClipboardChanged?.Invoke(info); } catch (Exception e) { PanelLog.Trace($"ClipboardChanged 分发异常: {e.Message}"); }
        };
        host._client.Reconnected += () =>
        {
            try { host._client.GetLastCopiedContent(); } catch { /* 探活，重连后确认管道可用 */ }
        };
        return host;
    }

    private EntryHost(ClipboardIpcClient client, bool startEntries)
    {
        _client = client;
        if (!startEntries) return;

        var style = PanelTheme.ExtensionConfig().EntryStyle;
        switch (style)
        {
            case "off":
                PanelLog.Trace("entry-style=off，不装配任何快捷入口");
                break;
            case "orb":
            case "both":
                // 历史配置值：悬浮球已删除，一律按侧边栏装配（不报错、静默降级，避免用户改过配置后入口消失）
                PanelLog.Trace($"entry-style={style} 已废弃（悬浮球已移除），按侧边栏装配");
                _edge = new EdgeHandleWindow();
                break;
            default:
                _edge = new EdgeHandleWindow();
                break;
        }

        // 正向留痕：与上面那条"未装配"互为对照 —— 排障时一眼能看出本次到底装没装入口
        //（2026-09-17 真机踩坑：设置落盘 debounce 未 flush → 面板读到旧值 false → 静默不装配手柄，
        //  只看"没有错误日志"根本查不出来）。
        if (_edge is not null)
        {
            PanelLog.Trace($"侧边入口已装配（entry-style={style}）");
        }
    }

    public void ConnectAndProbe()
    {
        // 同步 Connect：引擎不在 → 拉起后由 IPC 客户端 500ms 指数退避重连兜底。
        try
        {
            _client.Connect();
        }
        catch (Exception e)
        {
            PanelLog.Trace($"首次连接引擎失败（将自动重连）: {e.Message}");
        }
    }

    /// <summary>面板进程前台启动入口（App 启动/单实例转发后调用）。</summary>
    public void EnsureFrontEntry()
    {
        if (_edge is null) return;
        _edge.ShowHandle();
    }

    /// <summary>
    /// 按**最新** entry-style 刷新入口显隐（2026-09-12 用户要求"修改实时生效"）：
    /// 设为 off 立即隐藏手柄、改回 sidebar 立即重新出现，无需重启面板进程。
    /// 注：'off' 之后用户仍可用热键/菜单唤起面板（面板自身就是入口的兜底）。
    /// </summary>
    public void RefreshEntryStyle()
    {
        if (_edge is null)
        {
            return;
        }

        if (string.Equals(PanelTheme.ExtensionConfig().EntryStyle, "off", StringComparison.OrdinalIgnoreCase))
        {
            _edge.Hide();
        }
        else
        {
            _edge.ShowHandle();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
    }
}
