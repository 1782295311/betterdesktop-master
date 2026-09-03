// BetterDesktop.Shell.Notification.Tests — 构造冒烟（W3-2）。
// NotificationService 对必需依赖做了 arg-null 守卫：冒烟钉住守卫行为
//（appSource 为 null 时必须快速失败，而不是半构造出一个订阅了事件却不工作的服务）。

using BetterDesktop.Shell.Notification.Services;
using Xunit;

namespace BetterDesktop.Shell.Notification.Tests;

public class NotificationServiceSmokeTests
{
    [Fact]
    public void Ctor_NullAppSource_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new NotificationService(appSource: null!, pinning: null, iconService: null,
                vibrancy: null, appearance: null, logger: null!));
    }
}
