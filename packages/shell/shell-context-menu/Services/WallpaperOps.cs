// BetterDesktop.Shell.ContextMenus — 设为桌面壁纸（消费 FileCapabilities.SetAsWallpaper）
// 用 SystemParametersInfo(SPI_SETDESKWALLPAPER)，与系统设置同一入口：
// 桌面壁纸由 explorer 渲染，我们只下指令，不自己画（桌面生死线 #1）。

using System.Runtime.InteropServices;

namespace BetterDesktop.Shell.ContextMenus.Services;

/// <summary>壁纸设置（SPI_SETDESKWALLPAPER）。</summary>
public static class WallpaperOps
{
    /// <summary>把图片设为桌面壁纸（拉伸铺满）；失败返回 false。</summary>
    public static bool Set(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !System.IO.File.Exists(imagePath))
        {
            return false;
        }

        try
        {
            // fWinIni: SPIF_UPDATEINIFILE(0x01) | SPIF_SENDWININICHANGE(0x02) —— 立即生效并持久化
            return SystemParametersInfo(SpiSetDeskWallpaper, 0, imagePath, 0x01 | 0x02);
        }
        catch
        {
            return false;
        }
    }

    private const uint SpiSetDeskWallpaper = 0x0014;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string pvParam, uint fWinIni);
}
