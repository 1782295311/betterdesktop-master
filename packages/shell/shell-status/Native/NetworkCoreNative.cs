// NetworkCore.dll 的 C# Interop 薄封装（纯转发，无业务逻辑）。
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterDesktop.Shell.Status.Native;

public readonly record struct NetworkPrimaryNative(
    uint IfIndex,
    string Name,
    string TypeName,
    string IpV4,
    ulong RxBytes,
    ulong TxBytes,
    bool Ok);

/// <summary>NetworkCore.dll 的薄封装。仅转发，不做任何业务判断。</summary>
public static class NetworkCoreNative
{
    private delegate int NetReadPrimaryCounters(
        out uint ifIndex,
        [Out] ushort[] name, int nameCch,
        [Out] ushort[] typeName, int typeCch,
        [Out] ushort[] ipAddr, int ipCch,
        out ulong rxBytes,
        out ulong txBytes,
        out int ok);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void NetChangeCallback();
    private delegate int NetSetChangeCallback(NetChangeCallback cb);
    private delegate int NetShutdownChangeNotify();

    private static readonly NetReadPrimaryCounters? _read;
    private static readonly NetSetChangeCallback? _setChangeCb;
    private static readonly NetShutdownChangeNotify? _shutdown;

    // 原生回调通过函数指针调用此委托；必须持有引用，防止被 GC 回收。
    private static NetChangeCallback? _changeCallback;

    static NetworkCoreNative()
    {
        _read = NativeLoader.GetExport<NetReadPrimaryCounters>("NetworkCore.dll", "Net_ReadPrimaryCounters");
        _setChangeCb = NativeLoader.GetExport<NetSetChangeCallback>("NetworkCore.dll", "Net_SetChangeCallback");
        _shutdown = NativeLoader.GetExport<NetShutdownChangeNotify>("NetworkCore.dll", "Net_ShutdownChangeNotify");
    }

    public static bool IsAvailable => _read is not null;

    /// <summary>启用事件驱动：地址/路由变化时调用 <paramref name="onChanged"/>（后台线程触发）。</summary>
    public static bool SetChangeCallback(Action onChanged)
    {
        if (_setChangeCb is null)
        {
            return false;
        }
        try
        {
            _changeCallback = () => onChanged();
            int hr = _setChangeCb(_changeCallback);
            return hr == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>关闭网络变更监听并释放后台线程。</summary>
    public static void Shutdown()
    {
        try
        {
            _shutdown?.Invoke();
        }
        catch
        {
            // 忽略清理异常
        }
        _changeCallback = null;
    }

    public static NetworkPrimaryNative ReadPrimary()
    {
        if (_read is null) return default;
        try
        {
            var name = new ushort[128];
            var type = new ushort[32];
            var ip = new ushort[64];
            int hr = _read(out uint idx, name, name.Length, type, type.Length, ip, ip.Length,
                out ulong rx, out ulong tx, out int ok);
            if (hr != 0) return default;
            return new NetworkPrimaryNative(idx, Trim(name), Trim(type), Trim(ip), rx, tx, ok != 0);
        }
        catch
        {
            return default;
        }
    }

    private static string Trim(ushort[] buf)
    {
        var sb = new StringBuilder(buf.Length);
        foreach (var c in buf)
        {
            if (c == '\0') break;
            sb.Append((char)c);
        }
        return sb.ToString();
    }
}
