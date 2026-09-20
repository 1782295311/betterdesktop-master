using System.Threading;
using System.Threading.Tasks;

namespace BetterDesktop.Capture.Contracts;

/// <summary>
/// 屏幕采集服务契约（截图 = 剪贴板扩展功能；产物是 PNG 路径，不暴露帧字节）。
/// <para>实现位于 capture exe（<c>packages/shell/shell-capture</c>），本包仅承载公共契约，
/// 供 CLI / 托盘 / 面板 / 外部扩展按同一接口消费。</para>
/// </summary>
public interface IScreenCaptureService
{
    /// <summary>
    /// 执行一次截图：按降级链（WGC → DXGI → BitBlt）采集 → HDR 色调映射（如需）→ 裁剪 → PNG 编码落盘。
    /// </summary>
    /// <param name="request">截图请求（模式/目标/光标/HDR）。</param>
    /// <param name="ct">取消令牌（支持耗时采集的中断取消）。</param>
    /// <returns>
    /// 成功：<see cref="CaptureResult.Success"/> = true，<see cref="CaptureResult.PngPath"/> 指向临时 PNG。
    /// 失败：<see cref="CaptureResult.Success"/> = false，<see cref="CaptureResult.Error"/> 为可读原因
    /// （受保护内容 / 后端不可用 / 采集失败 / 超时 / 取消），不产出半成品文件。
    /// </returns>
    Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken ct);
}
