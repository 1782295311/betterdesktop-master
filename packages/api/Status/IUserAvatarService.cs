using System.Threading.Tasks;

namespace BetterDesktop.Shell.Status.Contracts;

/// <summary>
/// 用户头像服务契约（7444 契约 B：shell32 序数 #261）。
/// 独立接口、不并入 IStatusMonitor（计划禁区③：对外接口签名不变）。
/// 消费方：未来用户卡片/开始菜单用户区（本轮只提供能力）。
/// </summary>
public interface IUserAvatarService
{
    /// <summary>取当前用户头像图片文件路径；系统不支持/失败返回 null。</summary>
    Task<string?> GetUserPicturePathAsync();
}
