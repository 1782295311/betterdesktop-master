namespace BetterDesktop.Shell.Core.Vibrancy;

/// <summary>毛玻璃风格（高层语义）。</summary>
public enum VibrancyStyle
{
    /// <summary>透亮模糊（blur-behind）：只做高斯模糊、不叠任何色调，背景透亮无色。
    /// 这是全局窗口的默认毛玻璃（appearance.material 默认值）。</summary>
    Transparent,

    /// <summary>系统亚克力（Acrylic）：DWM 系统亚克力，自带一层暗色调，偏暗、可读性更好。</summary>
    Acrylic,

    /// <summary>无磨砂（None）：关闭一切 DWM 模糊，普通透明窗。
    /// 专用于 dock「清晰」档（dock.material=clear），与全局默认 Transparent(毛玻璃) 区分。</summary>
    None
}

/// <summary>窗口毛玻璃服务。由宿主或插件通过内核 Provide，bar/dock 仅 Inject 依赖后调用。</summary>
public interface IVibrancyService
{
    /// <summary>对指定窗口应用毛玻璃。</summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="style">毛玻璃风格。</param>
    /// <param name="roundCorners">是否启用圆角。</param>
    /// <param name="smallRadius">圆角半径是否为小号（仅 roundCorners 为真时生效）。</param>
    void Apply(IntPtr hWnd, VibrancyStyle style, bool roundCorners = true, bool smallRadius = false);

    /// <summary>关闭毛玻璃，恢复普通透明窗。</summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    void Disable(IntPtr hWnd);
}
