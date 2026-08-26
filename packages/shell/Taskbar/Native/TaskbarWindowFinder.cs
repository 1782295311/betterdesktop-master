using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace BetterDesktop.Shell.Taskbar.Native;

/// <summary>
/// 枚举原生任务栏窗口句柄。
/// 核心路径原样搬运 TranslucentTB：主任务栏类名 <c>Shell_TrayWnd</c>，
/// 副任务栏（多显示器）类名 <c>Shell_Secondary_TrayWnd</c>。
/// </summary>
public static class TaskbarWindowFinder
{
    private const string PrimaryTaskbarClass = "Shell_TrayWnd";
    private const string SecondaryTaskbarClass = "Shell_Secondary_TrayWnd";

    /// <summary>枚举所有任务栏（主 + 副）句柄。</summary>
    public static IReadOnlyList<IntPtr> FindAll()
    {
        var list = new List<IntPtr>();
        var handle = GCHandle.Alloc(list);
        try
        {
            EnumWindows(EnumProc, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return list;
    }

    private static bool EnumProc(IntPtr hwnd, IntPtr lParam)
    {
        var list = (List<IntPtr>)GCHandle.FromIntPtr(lParam).Target!;
        var sb = new System.Text.StringBuilder(256);
        if (GetClassName(hwnd, sb, sb.Capacity) > 0)
        {
            var className = sb.ToString();
            if (className == PrimaryTaskbarClass || className == SecondaryTaskbarClass)
            {
                list.Add(hwnd);
            }
        }
        return true;
    }

    public static IReadOnlyList<IntPtr> FindPrimary()
    {
        var all = FindAll();
        var result = new List<IntPtr>();
        foreach (var h in all)
        {
            var sb = new System.Text.StringBuilder(256);
            if (GetClassName(h, sb, sb.Capacity) > 0 && sb.ToString() == PrimaryTaskbarClass)
            {
                result.Add(h);
            }
        }
        return result;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
}
