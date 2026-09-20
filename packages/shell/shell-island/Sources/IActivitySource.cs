// BetterDesktop.Shell.Island — 消息来源统一契约
//
// 三个来源（剪贴板 / 转换 / 媒体）行为同构：订阅 → 映射成活动 → 退订。
// 用同一个接口收口，装配侧就能用一行"起停对称"的循环管理它们，而不是三段 if/else
// （设置里的三个来源开关正是靠这个接口实现的）。

namespace BetterDesktop.Shell.Island.Sources;

/// <summary>一个消息来源（可起停；起停必须幂等且对称）。</summary>
internal interface IActivitySource
{
    /// <summary>开始监听（幂等）。</summary>
    void Start();

    /// <summary>停止监听（幂等；必须与 Start 配对退订，7438）。</summary>
    void Stop();
}
