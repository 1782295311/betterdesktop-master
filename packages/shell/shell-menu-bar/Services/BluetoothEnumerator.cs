// BetterDesktop.Shell.MenuBar — 蓝牙设备枚举（真实系统数据源，零硬编码）。
// 策略：经典 bthprops.cpl 枚举（BluetoothFindFirstRadio + BluetoothFindFirstDevice），
// 返回系统"已配对/已记住/已连接"的蓝牙设备，含真实名称与连接状态。
// 比 WinRT DeviceInformation 更可靠（无需应用权限，兼容老设备）。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace BetterDesktop.Shell.MenuBar.Services;

/// <summary>蓝牙设备快照。带 48 位蓝牙地址（BTH_ADDR），用于连接/断开定位设备。</summary>
public sealed record BluetoothDeviceItem(
    ulong Address,            // 48 位蓝牙地址，0 = 未知（仅扫描所得但读不到地址时）
    string Name,              // 设备名（系统配对时显示的名称）
    string DeviceIconGlyph,   // Segoe MDL2 Assets 图标（系统图标加载失败时的降级）
    string DeviceTypeName,    // 设备类型名称（如"耳机"、"键盘"、"鼠标"），用于 UI 显示
    bool IsConnected,         // 是否已连接
    bool IsPaired)            // 是否已配对/认证
{
    /// <summary>系统自带的设备图标（异步加载，可能为 null）。</summary>
    public System.Windows.Media.ImageSource? IconSource { get; set; }

    /// <summary>最后一次看到该设备的时间（用于过时清理，仅附近设备使用）。</summary>
    public DateTime LastSeen { get; set; } = DateTime.Now;
}

/// <summary>蓝牙开关状态。</summary>
public enum BluetoothRadioState
{
    NoAdapter,   // 无蓝牙适配器 / 服务或驱动不可用
    Off,         // 有适配器但无线电关闭
    On           // 有适配器且无线电开启
}

/// <summary>
/// 蓝牙设备枚举：经典 bthprops.cpl API。
/// Enumerate() 只读已配对/已记住/已连接（快速，不发起查询）；DiscoverNearby() 主动扫描附近新设备。
/// </summary>
internal static partial class BluetoothEnumerator
{
    public static IReadOnlyList<BluetoothDeviceItem> Enumerate()
    {
        var devices = new List<BluetoothDeviceItem>(capacity: 8);
        try
        {
            // 1) 打开第一个无线电（回调句柄 BlFindRadio，无线电句柄 hRadio）。
            // 2) 用该无线电枚举已配对/已记住/已连接设备。
            // 3) 关闭无线电，继续找下一个无线电（一般只有一个）。
            var radioParams = default(BLUETOOTH_FIND_RADIO_PARAMS);
            radioParams.dwSize = Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>();
            IntPtr findRadio = BluetoothFindFirstRadio(ref radioParams, out IntPtr hRadio);
            if (findRadio != IntPtr.Zero)
            {
                do
                {
                    try
                    {
                        EnumerateDevicesOnRadio(hRadio, devices, includeUnknown: false, withInquiry: false);
                    }
                    finally
                    {
                        _ = CloseHandle(hRadio);
                        hRadio = IntPtr.Zero;
                    }
                } while (BluetoothFindNextRadio(findRadio, out hRadio));
                _ = BluetoothFindRadioClose(findRadio);
            }
        }
        catch
        {
            // 蓝牙服务/驱动不可用时返回空列表，由 UI 显示占位，避免崩溃。
        }
        return devices;
    }

    /// <summary>当前蓝牙开关状态：无适配器 / 关闭 / 开启。</summary>
    public static BluetoothRadioState GetRadioState()
    {
        try
        {
            var radioParams = default(BLUETOOTH_FIND_RADIO_PARAMS);
            radioParams.dwSize = Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>();
            IntPtr findRadio = BluetoothFindFirstRadio(ref radioParams, out IntPtr hRadio);
            if (findRadio == IntPtr.Zero)
            {
                return BluetoothRadioState.NoAdapter;
            }
            try
            {
                if (hRadio != IntPtr.Zero)
                {
                    _ = CloseHandle(hRadio);
                    hRadio = IntPtr.Zero;
                }
            }
            catch { /* ignore */ }
            _ = BluetoothFindRadioClose(findRadio);
            return BluetoothRadioState.On;
        }
        catch
        {
            return BluetoothRadioState.NoAdapter;
        }
    }

    /// <summary>
    /// 主动扫描附近未配对的新设备。会发起 Inquiry（默认约 5 秒），返回附近设备；
    /// 与已配对列表由调用方合并、去重。驱动/服务不可用时返回空列表。
    /// </summary>
    public static IReadOnlyList<BluetoothDeviceItem> DiscoverNearby()
    {
        return DiscoverNearby(timeoutMultiplier: 4);
    }

    /// <summary>主动扫描附近未配对的新设备，可指定 Inquiry 超时倍数（每单位约 1.28 秒）。</summary>
    public static IReadOnlyList<BluetoothDeviceItem> DiscoverNearby(byte timeoutMultiplier)
    {
        var discovered = new List<BluetoothDeviceItem>(capacity: 8);
        try
        {
            var radioParams = default(BLUETOOTH_FIND_RADIO_PARAMS);
            radioParams.dwSize = Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>();
            IntPtr findRadio = BluetoothFindFirstRadio(ref radioParams, out IntPtr hRadio);
            if (findRadio == IntPtr.Zero) return discovered;
            try
            {
                EnumerateDevicesOnRadio(hRadio, discovered, includeUnknown: true, withInquiry: true, timeoutMultiplier: timeoutMultiplier);
            }
            finally
            {
                if (hRadio != IntPtr.Zero) _ = CloseHandle(hRadio);
                _ = BluetoothFindRadioClose(findRadio);
            }
        }
        catch
        {
            // 驱动异常时返回空列表，UI 显示"无法扫描"
        }
        return discovered;
    }

    // 连接/断开标志（BluetoothSetServiceState 的 dwServiceFlags）
    private const uint BLUETOOTH_CONNECT_FLAG_CONNECT = 0x00000001;
    private const uint BLUETOOTH_CONNECT_FLAG_DISCONNECT = 0x00000002;

    /// <summary>连接已配对设备。返回 true 表示调用成功（并非保证立即连上，等服务 SDP 才能生效）。</summary>
    public static bool ConnectDevice(BluetoothDeviceItem device)
    {
        return SetDeviceServiceState(device, BLUETOOTH_CONNECT_FLAG_CONNECT);
    }

    /// <summary>断开已连接设备。返回 true 表示调用成功。</summary>
    public static bool DisconnectDevice(BluetoothDeviceItem device)
    {
        return SetDeviceServiceState(device, BLUETOOTH_CONNECT_FLAG_DISCONNECT);
    }

    /// <summary>
    /// 发起与未配对设备的配对请求。使用 BluetoothAuthenticateDevice（Just Works / PIN 配对）。
    /// 该 API 是同步阻塞式弹窗，必须在有消息泵的线程（UI 线程）调用，并传入真实父窗口句柄，
    /// 否则对话框无归属、容易弹不出来导致线程被无限占住。
    /// 返回 true 表示配对请求已发出（实际配对结果由系统对话框/后续状态反映）。
    /// </summary>
    public static bool PairDevice(ulong address, string deviceName, IntPtr hwndParent = default)
    {
        if (address == 0) return false;
        IntPtr hRadio = IntPtr.Zero;
        IntPtr findRadio = IntPtr.Zero;
        try
        {
            var radioParams = default(BLUETOOTH_FIND_RADIO_PARAMS);
            radioParams.dwSize = Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>();
            findRadio = BluetoothFindFirstRadio(ref radioParams, out hRadio);
            if (findRadio == IntPtr.Zero || hRadio == IntPtr.Zero) return false;

            var bdi = new BLUETOOTH_DEVICE_INFO
            {
                dwSize = Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>(),
                Address = address,
                szName = deviceName ?? string.Empty
            };

            // BluetoothAuthenticateDevice：传空 PIN 走 Just Works 配对，系统会弹出必要的确认对话框。
            // hwndParent 传真实窗口句柄，对话框能正确归属/居中，避免后台线程 + NULL 父窗口导致的挂起。
            uint result = BluetoothAuthenticateDevice(hwndParent, hRadio, ref bdi, null, 0);
            return result == 0; // ERROR_SUCCESS = 0
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hRadio != IntPtr.Zero) _ = CloseHandle(hRadio);
            if (findRadio != IntPtr.Zero) _ = BluetoothFindRadioClose(findRadio);
        }
    }

    /// <summary>
    /// 对设备的每个已安装服务调用 BluetoothSetServiceState，改其连接状态。
    /// 拿不到地址（扫描的未配对设备）或无适配器时直接返回 false，不做任何写入。
    /// </summary>
    private static bool SetDeviceServiceState(BluetoothDeviceItem device, uint flags)
    {
        if (device.Address == 0) return false;
        try
        {
            var radioParams = default(BLUETOOTH_FIND_RADIO_PARAMS);
            radioParams.dwSize = Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>();
            IntPtr findRadio = BluetoothFindFirstRadio(ref radioParams, out IntPtr hRadio);
            if (findRadio == IntPtr.Zero) return false;
            try
            {
                var bdi = new BLUETOOTH_DEVICE_INFO
                {
                    dwSize = Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>(),
                    Address = device.Address,
                    szName = device.Name
                };

                // 两段式读取该设备已安装的服务 GUID 列表：先取数量再取内容
                uint count = 0;
                _ = BluetoothEnumerateInstalledServices(hRadio, ref bdi, ref count, null);
                if (count == 0)
                {
                    // 无已安装服务（个别老设备）——退化为直接对该设备发出连接请求，
                    // 仍调 SetServiceState 但服务 GUID 为空列表时以 false 结束，避免误报成功。
                    return false;
                }

                var guids = new Guid[count];
                if (!BluetoothEnumerateInstalledServices(hRadio, ref bdi, ref count, guids))
                {
                    return false;
                }

                bool any = false;
                for (int i = 0; i < guids.Length; i++)
                {
                    if (BluetoothSetServiceState(hRadio, ref bdi, ref guids[i], flags))
                    {
                        any = true;
                    }
                }
                return any;
            }
            finally
            {
                if (hRadio != IntPtr.Zero) _ = CloseHandle(hRadio);
                _ = BluetoothFindRadioClose(findRadio);
            }
        }
        catch
        {
            return false;
        }
    }

    private static void EnumerateDevicesOnRadio(
        IntPtr hRadio, List<BluetoothDeviceItem> result,
        bool includeUnknown, bool withInquiry, byte timeoutMultiplier = 2)
    {
        var searchParams = new BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            fReturnAuthenticated = true,  // 已认证（配对）
            fReturnRemembered = true,     // 已记住
            fReturnUnknown = includeUnknown, // 主动扫描时需要包含附近未配对设备
            fReturnConnected = true,      // 已连接
            fIssueInquiry = withInquiry,  // 是否发起主动查询
            cTimeoutMultiplier = timeoutMultiplier, // Inquiry 超时（每单位约 1.28 秒）
            hRadio = hRadio
        };
        searchParams.dwSize = Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>();

        var deviceInfo = default(BLUETOOTH_DEVICE_INFO);
        deviceInfo.dwSize = Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>();

        IntPtr findDevice = BluetoothFindFirstDevice(ref searchParams, ref deviceInfo);
        if (findDevice == IntPtr.Zero) return;

        try
        {
            do
            {
                var name = deviceInfo.szName ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name)) name = "(未知蓝牙设备)";
                var (glyph, typeName) = ClassifyIcon(deviceInfo.ulClassofDevice, name);
                result.Add(new BluetoothDeviceItem(
                    deviceInfo.Address,
                    name,
                    glyph,
                    typeName,
                    deviceInfo.fConnected,
                    deviceInfo.fAuthenticated));
            } while (BluetoothFindNextDevice(findDevice, ref deviceInfo));
        }
        finally
        {
            _ = BluetoothFindDeviceClose(findDevice);
        }
    }

    /// <summary>公开的设备图标分类入口（供 DeviceWatcher 等外部调用方使用）。</summary>
    public static (string Glyph, string TypeName) ClassifyIconPublic(ulong classOfDevice, string name)
        => ClassifyIcon(classOfDevice, name);

    /// <summary>
    /// 依据蓝牙"Class of Device"(CoD) 的 Major/Minor 分类设备；拿不到时回退按名称关键词匹配。
    /// 返回 (图标字形, 设备类型中文名)。
    /// Major：0x1 电脑、0x2 电话、0x4 音频/视频、0x5 外设、0x6 可穿戴、0x7 成像、0x9 健康、0xA 玩具。
    /// </summary>
    private static (string Glyph, string TypeName) ClassifyIcon(ulong classOfDevice, string name)
    {
        // CoD 布局：bit[0..1] 格式，bit[2..7] Minor，bit[8..12] Major，bit[13..23] 服务类。
        int major = (int)((classOfDevice >> 8) & 0x1F);
        int minor = (int)((classOfDevice >> 2) & 0x3F);

        // 电脑（Major 0x1）：minor 1=台式、2=服务器、3=笔记本、4=掌上电脑、5=掌上PC、6=可穿戴、7=平板
        if (major == 0x1)
        {
            if (minor == 3) return ("\uE977", "笔记本电脑");
            if (minor == 1) return ("\uE977", "台式电脑");
            if (minor == 7) return ("\uE70A", "平板电脑");
            return ("\uE977", "电脑");
        }
        // 电话（Major 0x2）：minor 1=蜂窝、2=无绳、3=智能手机、4=有线调制解调器、5=ISDN
        if (major == 0x2)
        {
            if (minor == 3) return ("\uE787", "智能手机");
            return ("\uE787", "手机");
        }
        // 音频/视频（Major 0x4）：minor 1=免提、2=头戴、3=耳机、4=扬声器、5=便携音频、6=车载音响、7=混音器、8=麦克风、9=扬声器盒、10=会议、11=游戏玩具、12=电话
        if (major == 0x4)
        {
            if (minor == 4 || minor == 5 || minor == 9) return ("\uE767", "扬声器");
            if (minor == 8) return ("\uE720", "麦克风");
            if (minor == 1 || minor == 12) return ("\uE85D", "免提设备");
            if (minor == 2 || minor == 3) return ("\uE85D", "耳机");
            if (minor == 6) return ("\uE85D", "车载音响");
            return ("\uE85D", "音频设备");
        }
        // 外设（Major 0x5）：minor 1=摇杆、2=游戏手柄、3=遥控器、4=传感、5=数位板、6=读卡器、7=数码笔、8=键盘、9=鼠标、10=多模式、16=键盘+鼠标
        if (major == 0x5)
        {
            if (minor == 8 || minor == 16) return ("\uE945", "键盘");
            if (minor == 9) return ("\uE95F", "鼠标");
            if (minor == 1 || minor == 2) return ("\uE7E8", "游戏手柄");
            if (minor == 3) return ("\uE787", "遥控器");
            if (minor == 5) return ("\uE700", "数位板");
            if (minor == 7) return ("\uE700", "触控笔");
            return ("\uE700", "外设");
        }
        // 可穿戴（Major 0x6）：minor 1=手表、2=眼镜、3=徽章、4=手环、5=项链、6=头盔
        if (major == 0x6)
        {
            if (minor == 1 || minor == 4) return ("\uE916", "智能手表");
            if (minor == 2) return ("\uE7E8", "智能眼镜");
            if (minor == 6) return ("\uE916", "头盔");
            return ("\uE916", "可穿戴设备");
        }
        // 成像（Major 0x7）：minor 1=显示器、2=摄像头、3=扫描仪、4=打印机、5=投影仪
        if (major == 0x7)
        {
            if (minor == 2) return ("\uE722", "摄像头");
            if (minor == 4 || minor == 3) return ("\uE7B8", "打印机/扫描仪");
            if (minor == 1) return ("\uE7F4", "显示器");
            if (minor == 5) return ("\uE7F4", "投影仪");
            return ("\uE722", "成像设备");
        }
        // 健康（Major 0x9）：minor 1=血压计、2=体温计、3=体重秤、4=血糖、5=脉搏、6=数据记录
        if (major == 0x9)
        {
            if (minor == 1) return ("\uE7E2", "血压计");
            if (minor == 2) return ("\uE7E2", "体温计");
            if (minor == 3) return ("\uE7E2", "体重秤");
            return ("\uE7E2", "健康设备");
        }
        // 玩具（Major 0xA）：minor 1=机器人、2=交通工具、3=娃娃、4=控制器、5=游戏
        if (major == 0xA)
        {
            if (minor == 4 || minor == 5) return ("\uE7E8", "游戏控制器");
            if (minor == 1) return ("\uE916", "机器人");
            return ("\uE7E8", "玩具");
        }

        // CoD 不可靠时回退名称关键词。
        var hay = (name ?? string.Empty).ToUpperInvariant();
        if (hay.Contains("GAMEPAD") || hay.Contains("CONTROLLER") || hay.Contains("JOYSTICK")) return ("\uE7E8", "游戏手柄");
        if (hay.Contains("WATCH") || hay.Contains("BAND") || hay.Contains("FITBIT")) return ("\uE916", "智能手表");
        if (hay.Contains("CAMERA")) return ("\uE722", "摄像头");
        if (hay.Contains("PRINTER") || hay.Contains("SCANNER")) return ("\uE7B8", "打印机/扫描仪");
        if (hay.Contains("HEART") || hay.Contains("BLOOD") || hay.Contains("SCALE")) return ("\uE7E2", "健康设备");
        if (hay.Contains("KEYBOARD")) return ("\uE945", "键盘");
        if (hay.Contains("MOUSE") || hay.Contains("MICE")) return ("\uE95F", "鼠标");
        if (hay.Contains("HEADPHONE") || hay.Contains("EARBUD") || hay.Contains("HEADSET") || hay.Contains("AIRPOD")) return ("\uE85D", "耳机");
        if (hay.Contains("SPEAKER") || hay.Contains("SOUNDBAR")) return ("\uE767", "扬声器");
        if (hay.Contains("TV") || hay.Contains("TELEVISION")) return ("\uE7F4", "电视");
        if (hay.Contains("CAR") || hay.Contains("AUTO")) return ("\uE85D", "车载设备");
        return ("\uE700", "蓝牙设备");
    }

    // ---------------- 结构体 & P/Invoke ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_FIND_RADIO_PARAMS
    {
        public int dwSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_DEVICE_SEARCH_PARAMS
    {
        public int dwSize;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnAuthenticated;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnUnknown;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }

    // Windows SYSTEMTIME（16 字节），不能用 long 代替，否则 BLUETOOTH_DEVICE_INFO 尺寸不对、
    // 蓝牙 API 校验 dwSize 失败导致枚举不到任何设备。
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTimeCompat
    {
        public ushort wYear;
        public ushort wMonth;
        public ushort wDayOfWeek;
        public ushort wDay;
        public ushort wHour;
        public ushort wMinute;
        public ushort wSecond;
        public ushort wMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BLUETOOTH_DEVICE_INFO
    {
        public int dwSize;
        public ulong Address;         // BTH_ADDR (64-bit)
        public uint ulClassofDevice;
        [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
        public SystemTimeCompat stLastSeen;   // SYSTEMTIME：16 字节
        public SystemTimeCompat stLastUsed;   // SYSTEMTIME：16 字节
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
    }

    [DllImport("bthprops.cpl", EntryPoint = "BluetoothFindFirstRadio", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, out IntPtr phRadio);

    [DllImport("bthprops.cpl", EntryPoint = "BluetoothFindNextRadio", SetLastError = true)]
    private static extern bool BluetoothFindNextRadio(IntPtr hFind, out IntPtr phRadio);

    [DllImport("bthprops.cpl", EntryPoint = "BluetoothFindRadioClose", SetLastError = true)]
    private static extern bool BluetoothFindRadioClose(IntPtr hFind);

    [DllImport("bthprops.cpl", EntryPoint = "BluetoothFindFirstDevice", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS pbtdsp, ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport("bthprops.cpl", EntryPoint = "BluetoothFindNextDevice", SetLastError = true)]
    private static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport("bthprops.cpl", EntryPoint = "BluetoothFindDeviceClose", SetLastError = true)]
    private static extern bool BluetoothFindDeviceClose(IntPtr hFind);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothEnumerateInstalledServices(
        IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi,
        ref uint pcNumServices, [In, Out, MarshalAs(UnmanagedType.LPArray)] Guid[]? pGuidServices);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothSetServiceState(
        IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi, ref Guid pGuidService, uint dwServiceFlags);

    [DllImport("bthprops.cpl", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint BluetoothAuthenticateDevice(
        IntPtr hwndParent, IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi,
        [MarshalAs(UnmanagedType.LPWStr)] string? pszPasskey, uint ulPasskeyLength);
}
