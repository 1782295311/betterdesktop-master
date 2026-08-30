// BetterDesktop.Shell.Status — 互操作安全调用帮助（M10 降级策略）
// 系统管理对象属性在特定驱动/环境下可能抛 COM 或平台异常，统一在此收口为安全默认值。

using System.Runtime.CompilerServices;

namespace BetterDesktop.Shell.Status.Native;

/// <summary>
/// 对容易抛异常的系统取值做安全收敛：失败返回默认值，绝不外抛。
/// </summary>
internal static class InteropGuard
{
    /// <summary>安全执行一个取值的委托，异常时返回 fallback。</summary>
    public static T SafeInvoke<T>(Func<T> read, T fallback, [CallerMemberName] string? member = null)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            _ = ex;
            return fallback;
        }
    }
}