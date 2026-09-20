// BetterDesktop.Shell.Core — Win32 Native 统一平台层（唯一权威互操作层）
//
// 【目的】全库 ~90 处散落的 P/Invoke 重复声明收口于此（计划 2026-09-07-common-capability-extraction.md P0-1）。
//   迁移纪律：签名/语义与原始声明保持行为等价（不重命名、不改 CharSet/MarshalAs/结构体布局）。
//   迁移来源：shell-status/Native、shell-window-tracker/Native、shell-taskbar/Native、
//     shell-menu-bar（MenuBarPopupWindow / MenuBarScreen / MenuBarWindow / AppBarReservation）等。
// 【纪律】7434（声明纪律）/ 7435（WinEventHook 泵线程）/ 7437（互操作错误）/ 7438（退订配对）。
//   结构体一律 LayoutKind.Sequential；涉及 bool 返回的 P/Invoke 按原声明保留 [return: MarshalAs] 或 bool 默认。
//   新增互操作声明必须先收口到本文件（verify-native-convergence 门禁强制）。

using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BetterDesktop.Shell.Core.Native;

/// <summary>
/// Win32 互操作统一声明（按 DLL 分区）。全部公开，供各 shell 包引用；
/// 业务封装（枚举器/读取器等）保留在各包自己的 Native 目录，只收口声明。
/// </summary>
public static partial class NativeMethods
{
    // ===================== 常量 =====================

    // WinEvent（user32）— 7435 纪律
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_OBJECT_CREATE = 0x8000;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_REORDER = 0x8008;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    public const int OBJID_WINDOW = 0;
    public const int CHILDID_SELF = 0;

    // 消息泵
    public const uint WM_QUIT = 0x0012;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_SETTINGCHANGE = 0x001A;

    /// <summary>DWM 合成状态变化（透明效果开关、显卡驱动重载、远程会话切换都会发）。
    /// 【2026-09-18】Win11 上窗口从最小化恢复/切换虚拟桌面后 accent 模糊会静默失效，
    /// 收到本消息必须重新应用一次材质，否则一次丢失就永久丢失。</summary>
    public const int WM_DWMCOMPOSITIONCHANGED = 0x031A;

    /// <summary>SetWindowPos 标志：不改尺寸/位置/Z 序，只让 DWM 重算非客户区与合成（强制刷新 backdrop）。</summary>
    public const uint SWP_NOSIZE = 0x0001;

    public const uint SWP_NOMOVE = 0x0002;

    public const uint SWP_NOZORDER = 0x0004;

    public const uint SWP_NOACTIVATE = 0x0010;

    public const uint SWP_FRAMECHANGED = 0x0020;

    // 低级鼠标钩子（WH_MOUSE_LL）
    public const int WH_MOUSE_LL = 14;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_MBUTTONDOWN = 0x0207;

    // 低级键盘钩子（WH_KEYBOARD_LL，2026-09-12 新增：按序粘贴把 Ctrl+V 重定向为"粘下一条"）
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;
    public const int VK_V = 0x56;
    public const int VK_CONTROL = 0x11;
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;

    // 窗口枚举 / 状态
    public const uint GW_OWNER = 4;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    // 多显示器
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    // ===================== Delegate（必须字段强引用，7435/7437 纪律） =====================

    /// <summary>SetWinEventHook 回调（OUTOFCONTEXT：在注册线程消息泵上下文执行）。</summary>
    public delegate void WinEventProc(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    /// <summary>WH_MOUSE_LL 低级鼠标钩子回调。</summary>
    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>WH_KEYBOARD_LL 低级键盘钩子回调。</summary>
    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>EnumWindows 枚举回调。</summary>
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // ===================== user32.dll =====================

    // ---- WinEvent 钩子（7435 泵线程纪律）----

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    // ---- 消息泵三件套 ----

    /// <summary>PeekMessage 的 wRemoveMsg：取出后从消息队列移除。</summary>
    public const uint PM_REMOVE = 0x0001;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostThreadMessage(int idThread, uint msg, IntPtr wParam, IntPtr lParam);

    // ---- 低级鼠标钩子 ----

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    /// <summary>
    /// 键盘版 SetWindowsHookEx。**必须另起名字**：与鼠标版的参数个数/返回类型完全相同，
    /// 若同名重载会 C# 重载歧义（两个委托类型签名一致，无法区分）。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
    public static extern IntPtr SetWindowsHookExKeyboard(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    // 双击时间基准（毫秒）：用户系统「鼠标 → 双击速度」设置，默认 500ms。
    // 对齐草特码工具 GetDoubleClickTime() 用法（2026-09-11 A 组）。
    [DllImport("user32.dll")]
    public static extern uint GetDoubleClickTime();

    // ---- 窗口 / 几何 ----

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    // ---- 系统电源 ----

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemPowerStatus(ref SYSTEM_POWER_STATUS lpSystemPowerStatus);

    // ---- 多显示器 ----

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // ---- 窗口信息 / 样式（P1 桶 B 收口）----

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>系统度量（SM_REMOTESESSION 等）。</summary>
    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    /// <summary>是否远程会话（远程桌面 / 部分虚拟机会置位）。DWM 模糊与合成在此类会话下通常整体失效。</summary>
    public const int SM_REMOTESESSION = 0x1000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// 设置前台窗口。**只有当前台进程调用时才被系统接受**（否则静默失败并返回 false）——
    /// 故"归还前台"必须在收起面板**之前**（此刻本进程仍是前台进程）执行，见 PopupWindowBase.OnBeforeHide。
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>把窗口提到 Z 序顶部（配合 AttachThreadInput 使用，见 WindowActivator）。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    // ---- 全局热键（2026-09-13：面板可选的「粘贴回原窗口」用户热键）----

    /// <summary>WM_HOTKEY：注册热键后系统把按键投递到该窗口过程的消息号。</summary>
    public const int WmHotKey = 0x0312;

    /// <summary>
    /// 注册全局热键。组合被占用时失败，Win32 错误码为 0x581
    /// （`ERROR_HOTKEY_ALREADY_REGISTERED`）。
    /// <para>
    /// 【本项目分工】引擎负责**内置**热键（`Ctrl+Shift+V/P/Backspace`，全程常驻）；
    /// 面板负责**用户自定义**的那一个 —— **仅在面板可见期间注册、收起即注销**，
    /// 故面板既不长期占用全局热键，也不会与其它程序长期冲突。
    /// </para>
    /// </summary>
    /// <param name="fsModifiers">MOD_* 位组合，含 `MOD_NOREPEAT`（见 <c>HotkeySpec</c>）。</param>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, uint vk);

    /// <summary>注销热键。必须与 <see cref="RegisterHotKey"/> 成对（否则窗口销毁时会漏）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---- 热键注册表中心窗口（2026-09-15：HWND_MESSAGE 消息专用窗口 + 键状态查询）----

    /// <summary>消息专用窗口的父句柄（HWND_MESSAGE）：不属任何桌面窗口树，仅收消息。</summary>
    public const int HWND_MESSAGE = -3;

    /// <summary>GetAsyncKeyState 高位 = 键当前按下。</summary>
    public const int KeyStateDown = unchecked((int)0x8000);

    public const int VK_SHIFT = 0x10;
    public const int VK_MENU = 0x12;
    public const int VK_LSHIFT = 0xA0;
    public const int VK_RSHIFT = 0xA1;
    public const int VK_LMENU = 0xA4;
    public const int VK_RMENU = 0xA5;
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;

    /// <summary>窗口类注册（Unicode）。返回的 atom 非零即成功。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    /// <summary>窗口过程默认处理（未消费的消息必须回传）。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>查询虚拟键当前状态（高位 0x8000 = 按下）。钩子回调内查修饰键状态的标准手段。</summary>
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    /// <summary>窗口过程委托（必须字段强引用，否则窗口类注册后回调被 GC → 异常）。</summary>
    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>WNDCLASSEX（RegisterClassEx 载荷；CharSet Unicode）。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetShellWindow();

    // ---- 窗口树操作（2026-09-11：桌面图标 SetParent 摘除法，见 desktop-progman-embed.md）----

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    // GA_ROOT = 2：返回顶层祖先（覆盖 Wallpaper Engine RenderWindow 等悬浮层归属判定）
    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    // ===================== kernel32.dll =====================

    [DllImport("kernel32.dll")]
    public static extern int GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    // ===================== ole32.dll =====================

    [DllImport("ole32.dll", EntryPoint = "CoCreateInstance", CallingConvention = CallingConvention.StdCall)]
    public static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    // ===================== 图标 / Shell（P1 扩展批） =====================

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetProcAddress(IntPtr hModule, IntPtr lpProcName); // 序数（MAKEINTRESOURCE）

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
    public static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr ILCreateFromPath(string pszPath);

    [DllImport("shell32.dll")]
    public static extern void ILFree(IntPtr pidl);

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    public static extern int DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    public static extern int GetMenuItemCount(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, StringBuilder? pvParam, uint fWinIni);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    /// <summary>DWMWA_EXCLUDED_FROM_PEEK：窗口在 Aero Peek（悬停缩略图预览 / Alt-Tab）时保持渲染，
    /// 不被 DWM 透明化。dock 置顶使用中悬停缩略图（DwmActivateLivePreview）时靠它豁免透明化，
    /// 与系统任务栏在 Aero Peek 时始终可见同效。</summary>
    public const int DwmWindowAttributeExcludedFromPeek = 12;

    // ===================== Shcore.dll =====================

    public const int MDT_EFFECTIVE_DPI = 0;

    [DllImport("Shcore.dll", SetLastError = true)]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ===================== 结构体 =====================

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>WH_KEYBOARD_LL 回调载荷。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // ---- 键盘注入（带自注入标记）----

    /// <summary>
    /// 自注入粘贴的标记值（写入 `SendInput` 的 `dwExtraInfo`）。
    /// <para>
    /// 低级键盘钩子据此**精确识别**"这一发 Ctrl+V 是我们自己发的"从而放行 —— 取代按时间猜的
    /// 抑制窗口。时间窗有两个致命缺陷（2026-09-12 真机双双踩到）：
    /// ① 窗口期内**用户的真实按键也被放行** → 少粘/多粘；
    /// ② 窗口过期后**自注入的按键回到钩子** → 再次消费 → **自激**，一次按键连粘好几条
    ///（用户原话："后面又被一股脑卸出来了"）。
    /// </para>
    /// </summary>
    public const uint PasteInjectionTag = 0xBD5A9E01;

    /// <summary>
    /// <see cref="PasteInjectionTag"/> 的 <see cref="IntPtr"/> 形态。
    /// **注入端与钩子比较端必须共用这一个值** —— 常量值超过 `int` 范围，直接强转会在 32/64 位下
    /// 产生不同的符号扩展结果，两边写法不一致就会出现"标记认不出来"的诡异问题。
    /// </summary>
    public static readonly IntPtr PasteInjectionTagPtr = new(unchecked((int)PasteInjectionTag));

    public const uint InputKeyboard = 1;
    public const uint InputKeyUp = 0x0002;
    public const ushort InputVkControl = 0x11;
    public const ushort InputVkV = 0x56;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    /// <summary>`INPUT` 的联合体（显式布局，保证与 native 结构同尺寸同偏移）。</summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>
    /// 注入一次 Ctrl+V，并给四个按键事件都打上 <see cref="PasteInjectionTag"/> 标记。
    /// 面板的低级键盘钩子见到该标记即放行 —— 实现"自注入 vs 用户输入"的精确区分，
    /// 不再需要任何时间窗口。
    /// </summary>
    public static void SendPasteInjection()
    {
        var tag = PasteInjectionTagPtr;
        var inputs = new INPUT[4];
        inputs[0].type = InputKeyboard;
        inputs[0].u.ki = new KEYBDINPUT { wVk = InputVkControl, dwExtraInfo = tag };
        inputs[1].type = InputKeyboard;
        inputs[1].u.ki = new KEYBDINPUT { wVk = InputVkV, dwExtraInfo = tag };
        inputs[2].type = InputKeyboard;
        inputs[2].u.ki = new KEYBDINPUT { wVk = InputVkV, dwFlags = InputKeyUp, dwExtraInfo = tag };
        inputs[3].type = InputKeyboard;
        inputs[3].u.ki = new KEYBDINPUT { wVk = InputVkControl, dwFlags = InputKeyUp, dwExtraInfo = tag };

        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    // 与 Windows SDK 定义严格一致（16 字节）：4×BYTE + 3×DWORD。
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag; // Windows 10+ 第 0 位 = 省电模式开启
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
        public uint Reserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT32 rcMonitor;
        public RECT32 rcWork;
        public uint dwFlags;
    }
    // ===================== dwmapi（DWM 缩略图簇，window-tracker 权威） =====================
    [DllImport("dwmapi.dll", EntryPoint = "DwmRegisterThumbnail")]
    public static extern int DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr phThumbnailId);

    [DllImport("dwmapi.dll", EntryPoint = "DwmUnregisterThumbnail")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DwmUnregisterThumbnail(IntPtr hThumbnailId);

    [DllImport("dwmapi.dll", EntryPoint = "DwmUpdateThumbnailProperties")]
    public static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnailId, ref DwmThumbnailProperties ptnProperties);

    [DllImport("dwmapi.dll", EntryPoint = "DwmQueryThumbnailSourceSize")]
    public static extern int DwmQueryThumbnailSourceSize(IntPtr hThumbnailId, out DwmSize psize);

    [DllImport("dwmapi.dll", EntryPoint = "DwmIsCompositionEnabled")]
    public static extern int DwmIsCompositionEnabled(out bool pfEnabled);

    /// <summary>
    /// 激活/关闭窗口的 Aero Peek 实时预览（DWM 合成器层，**免疫 UIPI 完整性限制**——
    /// explorer（中完整性）对管理员窗口做任务栏预览正靠它，替代 SetWindowPos 抬窗的 UIPI 缺口）。
    /// ⚠️ 入口点是**按序号 #113**（未命名导出）：本机 dwmapi.dll 导出表中没有
    /// "DwmActivateLivePreview" 名字（119 导出仅 44 个有名字，75 个按序号导出），
    /// 按名字调用会 EntryPointNotFoundException。序号来自 ManagedShell 0.0.344 反射
    /// （ManagedShell.Interop.NativeMethods，EntryPoint="#113"，与 cairoshell 任务栏同源）。
    /// </summary>
    /// <param name="enable">1=开始预览，0=关闭预览。</param>
    /// <param name="targetHwnd">被预览的窗口句柄（关闭预览时传 Zero）。</param>
    /// <param name="callingHwnd">调用者窗口句柄（任务栏/dock 自身；关闭预览时传 Zero）。</param>
    /// <param name="type">AeroPeekType：0=Default，1=Desktop，3=Window（值来自 ManagedShell 0.0.344 反射，勿臆改）。</param>
    /// <param name="unknown">Win8.1+ 预留参数，传 IntPtr.Zero。</param>
    /// <returns>HRESULT，0=成功。</returns>
    [DllImport("dwmapi.dll", EntryPoint = "#113", SetLastError = true)]
    public static extern int DwmActivateLivePreview(
        uint enable, IntPtr targetHwnd, IntPtr callingHwnd, int type, IntPtr unknown);

    [StructLayout(LayoutKind.Sequential)]
    public struct DwmSize
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DwmThumbnailProperties
    {
        public uint dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fSourceClientAreaOnly;
    }

    // ===================== wlanapi（Wlan 簇，menu-bar/status 权威） =====================
    [DllImport("wlanapi.dll")]
    public static extern int WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    public static extern int WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    public static extern void WlanFreeMemory(IntPtr pMemory);

    [DllImport("wlanapi.dll")]
    public static extern int WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    // ===================== advapi32（特权簇，context-menu/start-menu 权威） =====================
    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeProcessHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool LookupPrivilegeValue(string? systemName, string privilegeName, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges, ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [StructLayout(LayoutKind.Sequential)]
    public struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges;
    }

    // ===================== shell32（SH 簇，context-menu/desktop/dock/menu-bar 权威） =====================
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHFileOperation(ref ShFileOpStruct lpFileOp);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(IntPtr pidl, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll")]
    public static extern IntPtr SHAppBarMessage(uint dwMessage, ref AppbarData pData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AppbarData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public int lParam;
    }

    // ===================== user32 =====================

    /// <summary>
    /// 设置窗口合成属性（<c>WCA_ACCENT_POLICY</c> 等；user32 未公开导出）。
    /// <para>
    /// <b>返回 BOOL：非零 = 成功，不是 HRESULT</b>。此处按 BOOL 声明，杜绝"按 HRESULT 判 0 成功"的误读 ——
    /// 旧写法把成功（0x1）当失败，导致每次设置都记一条失败日志并**无条件叠加降级材质**
    ///（2026-09-14 修正，日志里 1000+ 条 "失败 hr=0x00000001"、连 `Disable` 也"失败"是决定性反证）。
    /// </para>
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowCompositionAttribute(IntPtr hWnd, ref WINCOMPATTRDATA pAttrData);

    [StructLayout(LayoutKind.Sequential)]
    public struct WINCOMPATTRDATA
    {
        public int nAttribute;
        public IntPtr pData;
        public int ulSize;
    }

    // ===================== kernel32 / user32 / imm32 =====================
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetKeyboardLayoutNameW(StringBuilder pwszKLID);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT p);

    [DllImport("imm32.dll")]
    public static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);

    // ===================== shell32（用户通知状态簇，island 抑制判定权威） =====================

    /// <summary>
    /// 查询当前用户通知状态（<c>QUNS_*</c>）。灵动岛的"全屏/游戏/演示模式不弹出"判定用它
    /// （对照 Windows 系统通知的同一判据，避免自造规则）。
    /// 返回 <c>S_OK</c> 时 <paramref name="state"/> 才是有效值；调用失败时调用方按"可通知"处理（保守不抑制）。
    /// </summary>
    [DllImport("shell32.dll")]
    public static extern int SHQueryUserNotificationState(out int state);

    // QUERY_USER_NOTIFICATION_STATE（取值与 Windows SDK 严格一致，勿按记忆改）
    public const int QUNS_NOT_PRESENT = 1;
    public const int QUNS_BUSY = 2;
    public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    public const int QUNS_PRESENTATION_MODE = 4;
    public const int QUNS_ACCEPTS_NOTIFICATIONS = 5;
    public const int QUNS_QUIET_TIME = 6;
    public const int QUNS_APP = 7;
}
