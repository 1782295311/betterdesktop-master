using BetterDesktop.Capture.Contracts;

namespace BetterDesktop.Shell.Capture.Core;

/// <summary>
/// 采集后端契约：一次调用采集「虚拟屏像素矩形」为帧（不包含光标，由服务层统一合成）。
/// 各后端内部保证资源释放；失败必须给出可读原因（fail-visible），不得抛异常冒泡。
/// </summary>
public interface IBackendCapture : System.IDisposable
{
    CaptureBackend Kind { get; }

    string Name { get; }

    /// <summary>
    /// 采集 <paramref name="bounds"/>（虚拟屏物理像素矩形，可为负坐标）为 BGRA8 / FP16 帧。
    /// 成功：<paramref name="frame"/> 非空且所有权转移给调用方。
    /// 失败：<paramref name="frame"/> 为 null，<paramref name="error"/> 为可读原因。
    /// </summary>
    bool Capture(PixelRect bounds, out RawFrame? frame, out string error);
}
