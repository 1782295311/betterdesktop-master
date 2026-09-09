// 真实键盘布局/输入法枚举与管理：从 Windows 注册表 Keyboard Layouts 读取已注册的键盘布局和输入法，
// 从 HKCU\Keyboard Layout\Preload 读取用户"实际启用"的顺序；用 GetKeyboardLayoutList() 拿系统已加载的
// HKL，用 ImmGetOpenStatus / ImmGetConversionStatus 判断当前激活。
//
// 重要：KLID 一律使用"完整 32 位"（8 位十六进制，如 "00000409" 美式键盘、"E0200804" 微软拼音）。
//   绝不能用低 16 位（& 0xFFFF / 取后 4 位），否则会丢掉 TSF 输入法的高位标识，
//   ActivateKeyboardLayout 会切到错误的基底布局而不是真正的输入法 —— 这正是"点了输入法却没反应"的根因。
//
// 管理部分（对应"系统设置里替代的输入法管理"）：读/写 HKCU\Keyboard Layout\Preload，
//   支持 添加 / 删除 / 排序 / 设默认，改完调用 LoadKeyboardLayout 让当前会话立即生效。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using BetterDesktop.Shell.Core.Native;
using Microsoft.Win32;

namespace BetterDesktop.Shell.Status.Native;

// ── 本文件方法级白话索引（白话 → 方法）──
//   "枚举所有键盘布局/输入法（带缓存）" → Enumerate（底层 EnumerateViaInputDll 走 input.dll 表）
//   "当前激活的布局句柄 HKL / KLID"     → GetActiveHkl / GetActiveTsKlid
//   "中/英文切换"                       → ToggleChineseEnglish
//   "读当前是中文还是英文状态"          → ReadConversionNativeMode / ReadConversionModeViaImeWindow
//   "循环切换一次输入法"                → CycleOnce（C10 相关：Reorder 插入位置）
//   "当前激活的 TSF 输入法 / 立即激活 TSF 配置" → GetActiveTsKlid / ActivateTsProfile（B6 已修：同一 RCW 只 Release 一次）
//   "激活指定 TSF 配置 / 加载布局"      → ActivateTsProfile；HKL→KLID 换算 HklToKlid
//   "布局显示名/语言代码/CLSID 解析"    → ResolveTsDisplayName、GetLanguageCode、TsClsidToHex、ParseLayoutOrTip
//   "标记列表里哪个是当前激活项"        → ApplyActiveFlags / ComputeIsActive
//   "释放 GDI 图标句柄"                 → ReleaseIcon
//   文件尾部 ImeNaming：输入法显示名规范化。TSF 文本输入处理见 TsfInputProcessor.cs。
// ────────────────────────────────────

/// <summary>一个真实键盘布局/输入法项。KlidHex 为完整 32 位（8 位十六进制，大写）。</summary>
public sealed record KeyboardLayoutItem(
    string KlidHex,        // 完整 8 位十六进制，如 "00000409" / "E0200804"；TSF 时为 CLSID 去掉 {} 的 32 位
    string LayoutName,     // 来自注册表 Layout Text 或 TSF Description，如 "中文(简体)-美式键盘" / "微软拼音"
    string LayoutFile,     // .kbd / .ime / .dll 文件名；TSF 时为空
    bool IsIme,            // Layout File 以 .ime 结尾 = 输入法（IMM）
    bool IsActive,         // 是否当前激活
    bool IsTs = false,     // true = TSF 文本服务（纯 TSF 输入法），false = 传统键盘布局/IMM
    int LangId = 0,        // 语言 ID（如 0x0804=中文简体, 0x0409=英语美国），用于排序
    string? HklHex = null);// TSF 输入法在 SortOrder 注册表里的 KeyboardLayout 值（激活时的真实 HKL，
                           // 如微软拼音 0xE0200804，8 位十六进制）；非 TSF 或注册表无该值时 null。
                           // 用于激活判定：TSF 精确匹配不可用时，用 HKL 精确比对区分同语言多输入法。

public static partial class KeyboardLayoutInterop
{
    private const uint KLF_ACTIVATE = 0x00000001;
    private const uint KLF_SUBSTITUTE_OK = 0x00000002;
    private const uint KLF_NOTELLSHELL = 0x00000080;

    // 转换状态位：NATIVE 置位 = 中文输入；清除 = 英文（ASCII）输入。
    private const uint IME_CMODE_NATIVE = 0x00000002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetKeyboardLayoutList(int nBuff, IntPtr[]? lpList);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ActivateKeyboardLayout(IntPtr hkl, uint Flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint Flags);

    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ImmGetOpenStatus(IntPtr hKL);

    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ImmGetConversionStatus(IntPtr hKL, out uint lpdwConversion, out uint lpdwSentence);

    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ImmSetConversionStatus(IntPtr hKL, uint fdwConversion, uint fdwSentence);

    // ===== input.dll：TSF 输入法切换 =====
    // 枚举已弃用 EnumEnabledLayoutOrTip（该 API 在非 UI 线程回调从不被调用，且有堆损坏风险），
    // 改用 HKCU\Software\Microsoft\CTF\SortOrder\AssemblyItem 注册表枚举（Windows 输入法选择器的权威数据源）。
    // SetDefaultLayoutOrTip 仍用于切换 TSF 输入法，需要完整 "0x{LangID}:{CLSID}{Profile}" 字符串。
    [DllImport("input.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetDefaultLayoutOrTip(string? psz, uint dwFlags);

    // ===== TSF COM：ITfInputProcessorProfiles::ActivateProfile（立即激活 TSF 输入法） =====
    // SetDefaultLayoutOrTip 只设默认值，不立即切换当前前台窗口的输入法。
    // ActivateProfile 是 TSF 官方立即激活接口，按 vtable 顺序定义前 3 个方法即可。
    [ComImport]
    [Guid("3B8FE2A0-6571-4BA5-88E5-1704591D54B8")] // CLSID_TF_InputProcessorProfiles
    private class TfInputProcessorProfilesClass { }

    [ComImport]
    [Guid("71C6E74C-1E65-4D44-95B5-5AF87A3C4A6D")] // IID_ITfInputProcessorProfiles
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfInputProcessorProfiles
    {
        // vtable 顺序（IUnknown 3 个之后）：
        [PreserveSig] int EnumInputProcessorInfo(out IntPtr ppEnum);
        [PreserveSig] int GetDefaultLanguageProfile(ushort langid, out Guid pclsid, out Guid pguidProfile);
        [PreserveSig] int ActivateProfile(ref Guid rclsid, ushort langid, ref Guid guidProfile);
        // 以下为占位方法，保持 vtable 顺序到 GetActiveLanguageProfile
        [PreserveSig] int DeactivateProfile(ref Guid rclsid, ushort langid, ref Guid guidProfile);
        [PreserveSig] int IsEnabledLanguageProfile(ref Guid rclsid, ushort langid, ref Guid guidProfile, out int pfEnabled);
        [PreserveSig] int GetLanguageProfileDesc(ref Guid rclsid, ushort langid, ref Guid guidProfile, [MarshalAs(UnmanagedType.BStr)] out string pbstrDesc);
        [PreserveSig] int GetLanguageProfileInfo(ref Guid rclsid, ushort langid, ref Guid guidProfile, IntPtr pInfo);
        [PreserveSig] int EnumLanguageProfiles(ushort langid, out IntPtr ppEnum);
        // 第 9 个方法：获取当前激活的 TSF 配置文件
        [PreserveSig] int GetActiveLanguageProfile(ushort langid, out Guid pclsid, out Guid pguidProfile, out int pfModified);
    }

    /// <summary>
    /// 获取当前激活的 TSF 输入法的 KLID（CLSID 前 8 位十六进制），失败返回 null。
    /// 用 ITfInputProcessorProfiles.GetActiveLanguageProfile（Win11 上比 ThreadMgr 的
    /// ProfileMgr 更稳定），且 langid 取**前台窗口线程**的 HKL（而不是本线程）——
    /// 否则菜单栏/弹窗抢焦点后读到的是本线程的布局，导致"当前输入法识别不准"。
    /// </summary>
    private static string? GetActiveTsKlid()
    {
        try
        {
            // 前台窗口线程的 HKL 低 16 位 = 当前输入语言的 langid
            var hkl = (uint)GetActiveHkl().ToInt64();
            if (hkl == 0) return null;
            ushort langid = (ushort)(hkl & 0xFFFF);

            var obj = new TfInputProcessorProfilesClass();
            var profiles = (ITfInputProcessorProfiles)obj;
            var hr = profiles.GetActiveLanguageProfile(langid, out var clsid, out var profile, out _);
            // B6 修复：obj 与 profiles 指向同一 RCW（接口强转不产生新 RCW），只能 Release 一次。
            // 原先再 ReleaseComObject(obj) 必抛 InvalidComObjectException，被 catch 吞掉，
            // 导致本方法恒返回 null（TSF 活跃输入法识别从未生效）。对照 TsfInputProcessor 单次释放范式。
            Marshal.ReleaseComObject(profiles);
            if (hr < 0) return null;
            if (clsid == Guid.Empty) return null;
            return TsClsidToHex(clsid.ToString("B"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>用 TSF COM ActivateProfile 立即激活指定 TSF 输入法。返回 true 成功。</summary>
    private static bool ActivateTsProfile(Guid clsid, ushort langId, Guid profile)
    {
        try
        {
            var obj = new TfInputProcessorProfilesClass();
            var profiles = (ITfInputProcessorProfiles)obj;
            var hr = profiles.ActivateProfile(ref clsid, langId, ref profile);
            // B6 修复：同一 RCW 只 Release 一次（重复释放会抛异常被吞、使本方法恒返回 false）。
            Marshal.ReleaseComObject(profiles);
            return hr >= 0;
        }
        catch
        {
            return false;
        }
    }

    // ===== shlwapi.dll：解析 @input.dll,-5021 这类间接字符串 =====
    // ===== user32.dll：向前台窗口发切换请求（切的是前台应用的布局，不是本进程的） =====
    private const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
    private const uint INPUTLANGCHANGE_SYSCHARSET = 0x0001;

    // ===== 输入模拟：keybd_event 模拟 Win+Space（Windows 官方输入法循环切换方式） =====
    // 272e4a6 历史版本的关键实现："完美运行过"的输入法切换——用 keybd_event 模拟一次完整
    // Win+Space 按键序列，Windows 系统把它当真实热键处理，循环切换到下一个输入法，
    // **不会弹输入法选择器浮层**（与用户记忆"原来的程序模拟 Win+Space 是直接切换到下一个输入法，
    // 不会再出现额外的界面"完全吻合）。e40d64e 把它改成枚举直切后实机失败，现恢复。
    // 注意：调用方需要 StartKeyHook 放行注入键（isInjected=true 时直接 CallNextHookEx），
    // 否则钩子会吞掉我们注入的 Win DOWN，导致模拟无效。
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;
    private const ushort VK_SPACE = 0x20;
    private const ushort VK_LCONTROL = 0xA2;
    private const ushort VK_LSHIFT = 0xA0;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    // ===== 图标提取：从 DLL/EXE/IME 中提取图标（ExtractIconEx） =====
    /// <summary>释放通过 GetLayoutIconHandle 获取的图标句柄。</summary>
    public static void ReleaseIcon(IntPtr hIcon)
    {
        if (hIcon != IntPtr.Zero) NativeMethods.DestroyIcon(hIcon);
    }

    /// <summary>获取键盘布局的语言代码（如 "ENG"、"CHS"、"JPN"），用于无图标时的文本显示。
    /// 从 KLID 低 16 位 langid 获取 CultureInfo 的三字母 ISO 语言名。</summary>
    public static string GetLanguageCode(string klidHex)
    {
        try
        {
            if (string.IsNullOrEmpty(klidHex) || klidHex.Length < 4) return "???";
            // KLID 低 4 位十六进制 = langid（如 "00000409" → 0x0409 = en-US）
            var langPart = klidHex.Substring(klidHex.Length - 4);
            if (!int.TryParse(langPart, System.Globalization.NumberStyles.HexNumber, null, out var langId))
                return "???";
            var ci = System.Globalization.CultureInfo.GetCultureInfo(langId);
            return ci.ThreeLetterISOLanguageName.ToUpperInvariant();
        }
        catch
        {
            return "???";
        }
    }

    // ===== 线程附加：AttachThreadInput 让我们能在目标窗口线程上下文中激活 TSF 输入法 =====
    private const uint GW_HWNDNEXT = 2;

    // 注册表缓存：已注册布局 + 用户启用顺序几乎不变，避免频繁的全量注册表遍历。
    private static Dictionary<string, LayoutInfo>? _registryCache;
    private static List<string>? _preloadCache;
    private static long _registryCacheStamp;
    private const long RegistryCacheTtlMs = 30_000;

    // ======== 公开 API：快速切换 ========

    /// <summary>返回前台应用线程的激活键盘布局句柄（HKL）的完整 32 位值。前台不可判断时返回当前线程的布局。</summary>
    public static IntPtr GetActiveHkl()
    {
        try
        {
            var foreground = NativeMethods.GetForegroundWindow();
            uint threadId = 0;
            if (foreground != IntPtr.Zero)
            {
                threadId = NativeMethods.GetWindowThreadProcessId(foreground, out _);
            }
            return GetKeyboardLayout(threadId);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>读取指定 HKL 的中/英文转换状态：true=中文输入，false=英文(ASCII)输入；
    /// 该布局不是 IME 或读取失败时返回 null（调用方按"无区分/非输入法"处理）。</summary>
    public static bool? ReadConversionNativeMode(IntPtr hkl)
    {
        if (hkl == IntPtr.Zero) return null;
        try
        {
            // 先确认该布局上的输入法是否已打开（未打开时无"英文/中文"切换语义）
            if (!ImmGetOpenStatus(hkl)) return null;
            if (!ImmGetConversionStatus(hkl, out uint conversion, out _)) return null;
            return (conversion & IME_CMODE_NATIVE) != 0;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 经输入法窗口读取中 / 英文模式：NativeMethods.ImmGetDefaultIMEWnd(前台窗口) + WM_IME_CONTROL/IMC_GETCONVERSIONMODE。
    /// 这是设计稿指定的路径，对 TSF 输入法（搜狗 / 微软拼音）同样有效；
    /// 而 ImmGetConversionStatus 对 TSF 输入法经常返回失败，正是"模式未知"的来源。
    /// 返回 null 表示读不到（当前是纯键盘布局、或输入法窗口不响应）。
    /// </summary>
    public static bool? ReadConversionModeViaImeWindow()
    {
        try
        {
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero) return null;

            var imeWnd = NativeMethods.ImmGetDefaultIMEWnd(foreground);
            if (imeWnd == IntPtr.Zero) return null;

            // 只有 IME 处于打开状态（前台确实在文本输入）时，转换模式才有意义。
            // 前台是菜单栏/桌面这类非输入窗口时 IME 是关闭的，conversion 为 0 会被误读成"英文模式"——
            // 此处返回 null，让调用方按"当前输入语言"推断（中文输入法即显示"中"）。
            var open = (long)NativeMethods.SendMessage(imeWnd, WM_IME_CONTROL, (IntPtr)IMC_GETOPENSTATUS, IntPtr.Zero);
            if (open == 0) return null;

            var conversion = (long)NativeMethods.SendMessage(imeWnd, WM_IME_CONTROL, (IntPtr)IMC_GETCONVERSIONMODE, IntPtr.Zero);
            return (conversion & IME_CMODE_NATIVE) != 0;
        }
        catch
        {
            return null;
        }
    }


    private const uint WM_IME_CONTROL = 0x0283;
    private const int IMC_GETOPENSTATUS = 0x0001;
    private const int IMC_GETCONVERSIONMODE = 0x0002;

    /// <summary>秒切当前激活输入法的中/英文模式。
    /// IMM 输入法用 ImmSetConversionStatus 直接翻转 NATIVE 位；
    /// TSF 输入法（微软拼音等）降级为向前台窗口发 Shift 键（TSF 默认 Shift 切中/英）。
    /// 返回切换后的状态（true=中文 / false=英文），TSF 降级时返回 null（调用方应重新读取）。</summary>
    public static bool? ToggleChineseEnglish()
    {
        var hkl = GetActiveHkl();
        if (hkl == IntPtr.Zero) return null;

        // 优先 IMM 方式：直接翻转 NATIVE 位，精确可靠
        try
        {
            if (ImmGetOpenStatus(hkl) && ImmGetConversionStatus(hkl, out uint conversion, out uint sentence))
            {
                conversion ^= IME_CMODE_NATIVE;
                if (ImmSetConversionStatus(hkl, conversion, sentence))
                {
                    return (conversion & IME_CMODE_NATIVE) != 0;
                }
            }
        }
        catch
        {
            // IMM 方式失败，降级到 Shift 键
        }

        // 降级：向前台窗口发 Shift 按下+抬起（TSF 输入法如微软拼音默认 Shift 切中/英）
        try
        {
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground != IntPtr.Zero)
            {
                const uint VK_SHIFT = 0x10;
                const uint WM_KEYDOWN = 0x0100;
                const uint WM_KEYUP = 0x0101;
                NativeMethods.PostMessage(foreground, WM_KEYDOWN, (IntPtr)VK_SHIFT, IntPtr.Zero);
                NativeMethods.PostMessage(foreground, WM_KEYUP, (IntPtr)VK_SHIFT, IntPtr.Zero);
            }
        }
        catch
        {
            // 发送失败
        }
        return null; // TSF 降级方式无法确定结果，返回 null 让调用方重新读取
    }

    // ======== TSF / LayoutOrTip 解析辅助 ========

    /// <summary>解析 input.dll 返回的 "LangID:ID" 字符串，返回 (langId, id, isTs)。
    /// ID 含 { } = TSF 输入法（CLSID），否则 = 传统 KLID。解析失败返回 null。</summary>
    private static (string LangId, string Id, bool IsTs)? ParseLayoutOrTip(string layoutOrTip)
    {
        if (string.IsNullOrWhiteSpace(layoutOrTip)) return null;
        var colon = layoutOrTip.IndexOf(':');
        if (colon <= 0 || colon >= layoutOrTip.Length - 1) return null;
        var langId = layoutOrTip[..colon].Trim();
        var id = layoutOrTip[(colon + 1)..].Trim();
        if (string.IsNullOrEmpty(langId) || string.IsNullOrEmpty(id)) return null;
        bool isTs = id.StartsWith('{') && id.EndsWith('}');
        return (langId, id, isTs);
    }

    /// <summary>把 TSF CLSID（含或不含 { }）归一化为 32 位十六进制字符串（去掉 - 和 { }），用于 KlidHex 字段。</summary>
    private static string TsClsidToHex(string clsid)
    {
        var clean = clsid.Replace("{", "").Replace("}", "").Replace("-", "");
        return clean.Length >= 8 ? clean[..8].ToUpperInvariant() : clean.ToUpperInvariant();
    }

    /// <summary>解析 TSF 输入法的显示名：优先读注册表 LanguageProfile\Description，
    /// 间接字符串（@input.dll,-5021）用 SHLoadIndirectString 解析；失败时回退到 CLSID 前 8 位占位。
    /// 查找顺序：HKLM\CTF\TIP\LanguageProfile\langID → HKLM\CTF\TIP 根 → HKCU\CTF\TIP → HKLM\Classes\CLSID Default → 已知 CLSID 硬编码。</summary>
    private static string ResolveTsDisplayName(string clsid, string langId)
    {
        // 已知 TSF IME 的 CLSID → 显示名 硬编码（注册表 Description 经常为空）
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{E7EA138E-69F8-11D7-A6EA-00065B844310}"] = "搜狗拼音输入法",
            ["{81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E}"] = "微软拼音",
            ["{6A498709-E00B-4C45-A018-8F9E4081AE40}"] = "微软拼音",
            ["{03B5835F-F03C-411B-9CE2-AA23E1171E36}"] = "日语输入法",
            ["{A028AE76-01B1-46C2-99C4-ACD9858AE02F}"] = "韩语输入法",
        };
        if (known.TryGetValue(clsid, out var knownName)) return knownName;

        try
        {
            // 1. HKLM\CTF\TIP\{clsid}\LanguageProfile\{langId} 下的 Description
            var tipPath = $@"SOFTWARE\Microsoft\CTF\TIP\{clsid}\LanguageProfile\{langId}";
            using var key = Registry.LocalMachine.OpenSubKey(tipPath, writable: false);
            if (key is not null)
            {
                var desc = key.GetValue("Description") as string;
                if (!string.IsNullOrEmpty(desc))
                {
                    var resolved = ResolveIndirectString(desc);
                    if (!string.IsNullOrEmpty(resolved)) return resolved;
                }
            }
            // 2. HKLM\CTF\TIP\{clsid} 根节点 Description
            var tipRoot = $@"SOFTWARE\Microsoft\CTF\TIP\{clsid}";
            using var root = Registry.LocalMachine.OpenSubKey(tipRoot, writable: false);
            if (root is not null)
            {
                var desc = root.GetValue("Description") as string;
                if (!string.IsNullOrEmpty(desc))
                {
                    var resolved = ResolveIndirectString(desc);
                    if (!string.IsNullOrEmpty(resolved)) return resolved;
                }
            }
            // 3. HKCU\CTF\TIP\{clsid}（用户级安装的 TIP）
            using var hkcuTip = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\CTF\TIP\{clsid}", writable: false);
            if (hkcuTip is not null)
            {
                var desc = hkcuTip.GetValue("Description") as string;
                if (!string.IsNullOrEmpty(desc))
                {
                    var resolved = ResolveIndirectString(desc);
                    if (!string.IsNullOrEmpty(resolved)) return resolved;
                }
            }
            // 4. HKLM\SOFTWARE\Classes\CLSID\{clsid} 的 Default 值（搜狗拼音等第三方 IME 在这里注册名称）
            using var clsidKey = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\CLSID\{clsid}", writable: false);
            if (clsidKey is not null)
            {
                var defaultName = clsidKey.GetValue("") as string;
                if (!string.IsNullOrEmpty(defaultName)) return defaultName;
            }
        }
        catch
        {
            // 注册表读失败，回退到 CLSID 占位
        }
        return $"TSF 输入法 ({TsClsidToHex(clsid)})";
    }

    /// <summary>解析 @filename,-resourceID 这类间接字符串；不是间接字符串时原样返回。</summary>
    private static string? ResolveIndirectString(string source)
    {
        if (string.IsNullOrEmpty(source)) return source;
        if (!source.StartsWith("@")) return source;
        try
        {
            var sb = new System.Text.StringBuilder(512);
            var hr = NativeMethods.SHLoadIndirectString(source, sb, sb.Capacity, IntPtr.Zero);
            if (hr >= 0 && sb.Length > 0) return sb.ToString();
        }
        catch
        {
            // 解析失败
        }
        return null;
    }

    // SortOrder 注册表枚举结果缓存：枚举结果几乎不变，缓存避免重复注册表遍历。
    private static List<KeyboardLayoutItem>? _cachedInputDllResult;
    private static readonly object _inputDllCacheLock = new();

    /// <summary>从 HKCU\Software\Microsoft\CTF\SortOrder\AssemblyItem 枚举已启用的输入法/键盘布局。
    /// 这是 Windows 输入法切换器使用的权威数据源，包含 IMM 键盘布局和 TSF IME（搜狗拼音、微软拼音等）。
    /// 结构：SortOrder\AssemblyItem\{langID}\{assemblyKey}\{序号} → CLSID / Profile / KeyboardLayout
    /// - CLSID 非零 = TSF IME，用 TsClsidToHex 生成 KLID，ResolveTsDisplayName 查名称
    /// - CLSID 全零 = 纯键盘布局，KeyboardLayout 是 HKL，转换为 KLID 后查注册表名称
    /// 结果缓存，后续调用直接返回。返回 null 表示注册表无数据，调用方走降级。</summary>
    private static List<KeyboardLayoutItem>? EnumerateViaInputDll()
    {
        lock (_inputDllCacheLock)
        {
            if (_cachedInputDllResult is not null) return _cachedInputDllResult;
        }

        var activeKlid = ReadActiveKlid();
        var registryMap = _registryCache;
        var preloadOrder = _preloadCache ?? new List<string>();

        // 按语言分组收集，便于补充纯键盘布局和排序
        var langItems = new Dictionary<int, List<KeyboardLayoutItem>>();

        using var sortOrderKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\CTF\SortOrder\AssemblyItem");
        if (sortOrderKey is null) return null;

        foreach (var langName in sortOrderKey.GetSubKeyNames())
        {
            if (!langName.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(langName.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var langId)) continue;

            var items = new List<KeyboardLayoutItem>();

            using var langKey = sortOrderKey.OpenSubKey(langName);
            if (langKey is null) continue;

            foreach (var assemblyName in langKey.GetSubKeyNames())
            {
                using var assemblyKey = langKey.OpenSubKey(assemblyName);
                if (assemblyKey is null) continue;

                var itemNames = assemblyKey.GetSubKeyNames();
                Array.Sort(itemNames, StringComparer.Ordinal);

                foreach (var itemName in itemNames)
                {
                    using var itemKey = assemblyKey.OpenSubKey(itemName);
                    if (itemKey is null) continue;

                    var clsidVal = itemKey.GetValue("CLSID") as string;
                    var kbdLayoutObj = itemKey.GetValue("KeyboardLayout");

                    bool isTs = !string.IsNullOrEmpty(clsidVal) && clsidVal != "{00000000-0000-0000-0000-000000000000}";

                    string klidHex;
                    string layoutName;
                    string layoutFile;
                    bool isIme;
                    string? hklHex = null;

                    if (isTs)
                    {
                        klidHex = TsClsidToHex(clsidVal!);
                        layoutName = ResolveTsDisplayName(clsidVal!, "0x" + langId.ToString("X8"));
                        layoutFile = string.Empty;
                        isIme = true;
                        // TSF 输入法的 KeyboardLayout 值 = 该输入法激活时的真实 HKL（如微软拼音 0xE0200804）。
                        // 记录为 HklHex：TSF CLSID 与 HKL 无法互相推导，但 SortOrder 里两者并存，
                        // 激活判定时用"前台 HKL == 该项 HklHex"精确比对，同语言多输入法也能区分。
                        hklHex = kbdLayoutObj switch
                        {
                            int i2 => ((uint)i2).ToString("X8"),
                            uint u2 => u2.ToString("X8"),
                            _ => null
                        };
                    }
                    else
                    {
                        var hkl = kbdLayoutObj is int i ? (uint)i : kbdLayoutObj is uint u ? u : 0;
                        klidHex = HklToKlid(hkl);
                        if (registryMap is not null && registryMap.TryGetValue(klidHex, out var info))
                        {
                            layoutName = info.LayoutName;
                            layoutFile = info.LayoutFile;
                            isIme = info.IsIme;
                        }
                        else
                        {
                            layoutName = $"布局 {klidHex}";
                            layoutFile = string.Empty;
                            isIme = false;
                        }
                    }

                    bool isActive = string.Equals(klidHex, activeKlid, StringComparison.OrdinalIgnoreCase);
                    items.Add(new KeyboardLayoutItem(
                        KlidHex: klidHex,
                        LayoutName: layoutName,
                        LayoutFile: layoutFile,
                        IsIme: isIme,
                        IsActive: isActive,
                        IsTs: isTs,
                        LangId: langId,
                        HklHex: hklHex));
                }
            }

            if (items.Count > 0)
            {
                langItems[langId] = items;
            }
        }

        if (langItems.Count == 0) return null;

        // 按 Preload 顺序排序语言，同语言内保持 SortOrder 序号
        int LangSortIndex(int langId)
        {
            var plainKlid = "0000" + langId.ToString("X4");
            var idx = preloadOrder.FindIndex(k => string.Equals(k, plainKlid, StringComparison.OrdinalIgnoreCase));
            return idx >= 0 ? idx : 999;
        }

        var result = new List<KeyboardLayoutItem>(capacity: 16);
        foreach (var langId in langItems.Keys.OrderBy(LangSortIndex))
        {
            result.AddRange(langItems[langId]);
        }

        lock (_inputDllCacheLock)
        {
            _cachedInputDllResult = result;
        }
        return result;
    }

    /// <summary>将 HKL（输入区域标识符）转换为 KLID（键盘布局 ID）。
    /// 纯键盘布局的 HKL 高16位=低16位（如 0x04090409），KLID 为 "00000409"。
    /// IMM IME 的 HKL 高16位是设备句柄（如 0xE0200804），KLID 即 HKL 的8位十六进制。</summary>
    private static string HklToKlid(uint hkl)
    {
        var low = hkl & 0xFFFF;
        var high = (hkl >> 16) & 0xFFFF;
        if (high == low)
        {
            // 纯键盘布局
            return "0000" + low.ToString("X4");
        }
        // IMM IME 或其他
        return hkl.ToString("X8");
    }

    /// <summary>枚举系统已加载 + 用户实际启用的真实键盘布局/输入法列表（IMM + TSF），按系统启用顺序排序。</summary>
    public static IReadOnlyList<KeyboardLayoutItem> Enumerate()
    {
        // 确保注册表缓存已初始化
        RefreshRegistryCacheIfStale();

        // 优先用 SortOrder 注册表统一枚举（IMM 键盘布局 + TSF IME），这是 Windows 输入法选择器的权威数据源。
        // 列表本身来自注册表（可缓存），但"当前激活哪一项"是动态状态，必须每次现算。
        var items = EnumerateViaInputDll();
        if (items is not null && items.Count > 0) return ApplyActiveFlags(items);

        // ===== 降级：input.dll 不可用时，回退到旧的 Preload + GetKeyboardLayoutList 逻辑 =====
        var preloadOrder = _preloadCache!;
        var registryMap = _registryCache!;
        var loadedKlids = ReadLoadedKlidsFromApi();
        var activeKlid = ReadActiveKlid();

        var fallback = new List<KeyboardLayoutItem>(capacity: Math.Max(preloadOrder.Count, loadedKlids.Count));

        foreach (var klid in preloadOrder)
        {
            if (registryMap.TryGetValue(klid, out var info))
            {
                fallback.Add(new KeyboardLayoutItem(
                    KlidHex: klid,
                    LayoutName: info.LayoutName,
                    LayoutFile: info.LayoutFile,
                    IsIme: info.IsIme,
                    IsActive: string.Equals(klid, activeKlid, StringComparison.OrdinalIgnoreCase)));
            }
            else
            {
                fallback.Add(new KeyboardLayoutItem(
                    KlidHex: klid,
                    LayoutName: $"未知布局 ({klid})",
                    LayoutFile: string.Empty,
                    IsIme: false,
                    IsActive: string.Equals(klid, activeKlid, StringComparison.OrdinalIgnoreCase)));
            }
        }

        foreach (var klid in loadedKlids)
        {
            if (preloadOrder.Contains(klid, StringComparer.OrdinalIgnoreCase)) continue;
            if (registryMap.TryGetValue(klid, out var info))
            {
                fallback.Add(new KeyboardLayoutItem(
                    KlidHex: klid,
                    LayoutName: info.LayoutName,
                    LayoutFile: info.LayoutFile,
                    IsIme: info.IsIme,
                    IsActive: string.Equals(klid, activeKlid, StringComparison.OrdinalIgnoreCase)));
            }
        }

        if (fallback.Count == 0 && activeKlid is not null && registryMap.TryGetValue(activeKlid, out var fb))
        {
            fallback.Add(new KeyboardLayoutItem(
                KlidHex: activeKlid,
                LayoutName: fb.LayoutName,
                LayoutFile: fb.LayoutFile,
                IsIme: fb.IsIme,
                IsActive: true));
        }

        return fallback;
    }

    /// <summary>
    /// 重新计算激活标记。注册表枚举结果可以缓存，但"当前激活哪一项"是动态状态，
    /// 必须每次现算——否则缓存会让切换输入法后菜单栏永远停在首次枚举的结果上。
    /// </summary>
    private static List<KeyboardLayoutItem> ApplyActiveFlags(List<KeyboardLayoutItem> cached)
    {
        var activeHkl = unchecked((uint)GetActiveHkl().ToInt64());
        var activeKlid = activeHkl == 0 ? null : activeHkl.ToString("X8");
        // TSF 精确匹配（双来源）：Win11 上 ThreadMgr 的 ProfileMgr 常未注册（TryGetActiveClsidHex 返回 null），
        // 此时用 ITfInputProcessorProfiles.GetActiveLanguageProfile（GetActiveTsKlid）补充。
        // 两路都失败才降级到 HKL / 语言匹配——否则同语言多输入法（搜狗+微软拼音）恒指列表第一项，
        // 表现为"切到微软拼音仍显示搜狗图标"。
        var activeTsfClsid = TsfInputProcessor.TryGetActiveClsidHex() ?? GetActiveTsKlid();
        var activeLang = (ushort)(activeHkl & 0xFFFF);

        var result = new List<KeyboardLayoutItem>(capacity: cached.Count);
        // 语言级匹配只认第一项：同语言的多个输入法（搜狗 + 微软拼音）无法区分，
        // 若全部标记会让"激活项"不唯一，UI 上表现为多个"当前输入法"。
        var langMatched = false;

        foreach (var item in cached)
        {
            var isActive = ComputeIsActive(item, activeHkl, activeKlid, activeTsfClsid, activeLang, ref langMatched);
            result.Add(item with { IsActive = isActive });
        }
        return result;
    }

    /// <summary>单项激活判定：TSF 精确匹配 → 语言 ID 匹配 → KLID 精确匹配。</summary>
    private static bool ComputeIsActive(
        KeyboardLayoutItem item,
        uint activeHkl,
        string? activeKlid,
        string? activeTsfClsid,
        ushort activeLang,
        ref bool langMatched)
    {
        if (activeHkl == 0) return false;

        // ① TSF 精确匹配：只有能查到当前激活的 TSF profile 时才可用。
        //    此时激活项必为 TSF 输入法，非 TSF 的键盘布局直接排除（避免同语言的布局项被误判）。
        if (activeTsfClsid is not null)
        {
            return item.IsTs && string.Equals(item.KlidHex, activeTsfClsid, StringComparison.OrdinalIgnoreCase);
        }

        // ② HKL 精确匹配（v7，2026-08-30）：TSF 输入法在 SortOrder 注册表里带 KeyboardLayout 值
        //    （激活时的真实 HKL，如微软拼音 0xE0200804），与前台 HKL 完全一致即确认激活。
        //    根治"切换输入法后菜单栏图标不刷新"：TSF 精确匹配不可用（Win11 TF_ThreadMgr 常未注册）
        //    时，旧逻辑只能按语言 ID 匹配，同语言多输入法（微软拼音/搜狗）恒指列表第一项，
        //    图标永不跟随切换。HKL 匹配让每个输入法有唯一标识，切换后 active 立即正确。
        if (activeKlid is not null
            && !string.IsNullOrEmpty(item.HklHex)
            && string.Equals(item.HklHex, activeKlid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // ③ 语言 ID 匹配：TSF 输入法以 CLSID 标识，与 HKL 无法直接比对，只能回退到语言维度。
        //    这是 Win11（TF_ThreadMgr 未注册）下的实际生效路径。
        var itemLang = item.LangId != 0
            ? (ushort)(item.LangId & 0xFFFF)
            : TryParseHex16(item.KlidHex, 4) ?? 0xFFFF;
        if (itemLang == activeLang)
        {
            if (langMatched) return false;
            langMatched = true;
            return true;
        }

        // ④ KLID 精确匹配：纯键盘布局的常见形态（HKL=0x04090409 ↔ KLID=00000409）。
        return activeKlid is not null
            && string.Equals(item.KlidHex, activeKlid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>从 8 位十六进制串的指定位置解析 16 位值（用于从 KLID 取语言 ID）。</summary>
    private static ushort? TryParseHex16(string text, int startIndex)
    {
        if (startIndex < 0 || text.Length < startIndex + 4) return null;
        return ushort.TryParse(
            text.AsSpan(startIndex, 4),
            System.Globalization.NumberStyles.HexNumber,
            null,
            out var value)
            ? value
            : null;
    }

    /// <summary>
    /// 切换到下一个输入法/键盘布局：用 keybd_event 模拟按键热键，**单步切换**。
    ///
    /// 只用 Win+Space（Windows 官方全局输入法循环切换方式，272e4a6 历史"完美运行过"的路径）：
    ///   - 每调用一次**稳定切一步**，绝不连跳——杜绝"一次点击跳过中间输入法"（如搜狗被快速略过）。
    ///   - 全局切换（作用于所有窗口），对 IMM 与 TSF 输入法（含微软拼音/搜狗）均有效，
    ///     **不弹 CTF 输入法选择器浮层**。
    ///   - 不受用户自定义热键影响（无论用户配的是 Ctrl+Shift / Alt+Shift / 未分配，Win+Space 都生效）。
    ///
    /// 历史教训：
    /// - v7 (2026-08-30) 曾"先模拟 Ctrl+Shift、验证 HKL 未变再降级 Win+Space"：
    ///   **该策略在实机会连切两步**——TSF 输入法（尤其第三方如搜狗）切换后 HKL 更新可能慢于
    ///   验证 Sleep(120ms)，`GetActiveHkl()` 读到旧值被误判"没切换"，于是又补一次 Win+Space，
    ///   一步跳过中间输入法。且 Ctrl+Shift 是 per-window（切的是焦点窗口，焦点在菜单栏时切不到用户应用）。
    ///   故回归到单步 Win+Space。
    /// - 272e4a6：keybd_event 模拟 Win+Space（完美运行过，Playground 截图证实）。
    /// - e40d64e (v5)：枚举→ITfInputProcessorProfiles::ActivateProfile / PostMessage 直切——实机失败。
    /// - v6：回滚 Win+Space，与 StartKeyHook v6 配套（钩子对注入 Win 键 LLKHF_INJECTED 放行）。
    ///
    /// Sleep 是为给系统足够时间逐次消费按键（keybd_event 同步入队但系统异步处理）。
    /// </summary>
    public static bool CycleOnce()
    {
        // Win+Space 每调用一次全局切一步，不校验 HKL（校验会引入"连跳"风险，见上方历史教训）。
        return SimulateWinSpace();
    }

    /// <summary>模拟一次完整的 Ctrl+Shift 按键序列（Windows"在输入语言之间切换"热键）。
    /// 返回 false 表示模拟失败（异常）。</summary>
    private static bool SimulateCtrlShift()
    {
        try
        {
            NativeMethods.keybd_event((byte)VK_LCONTROL, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(50);
            NativeMethods.keybd_event((byte)VK_LSHIFT, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(50);
            NativeMethods.keybd_event((byte)VK_LSHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            System.Threading.Thread.Sleep(50);
            NativeMethods.keybd_event((byte)VK_LCONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            return true;
        }
        catch
        {
            // 异常时释放可能处于按下状态的修饰键，避免卡键
            try { NativeMethods.keybd_event((byte)VK_LCONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); } catch { }
            try { NativeMethods.keybd_event((byte)VK_LSHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); } catch { }
            return false;
        }
    }

    /// <summary>模拟一次完整的 Win+Space 按键序列（Windows 官方全局输入法循环切换方式，
    /// 272e4a6 历史"完美运行过"的实现）。StartKeyHook v6 对注入的 Win 键一律放行，模拟不会被吞。</summary>
    private static bool SimulateWinSpace()
    {
        try
        {
            byte vkLWin = (byte)VK_LWIN;
            byte vkSpace = (byte)VK_SPACE;
            NativeMethods.keybd_event(vkLWin, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(80);
            NativeMethods.keybd_event(vkSpace, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(60);
            NativeMethods.keybd_event(vkSpace, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            System.Threading.Thread.Sleep(60);
            NativeMethods.keybd_event(vkLWin, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            System.Threading.Thread.Sleep(150);
            return true;
        }
        catch
        {
            // 异常时尽量释放可能处于按下状态的修饰键，避免卡键
            try { NativeMethods.keybd_event((byte)VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); } catch { }
            try { NativeMethods.keybd_event((byte)VK_SPACE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); } catch { }
            return false;
        }
    }

    /// <summary>获取指定输入法/键盘布局的图标句柄（HICON）。调用方负责用 DestroyIcon 释放。
    /// TSF：从 HKLM\SOFTWARE\Microsoft\CTF\TIP\{CLSID}\LanguageProfile 读 IconFile/IconIndex 提取。
    /// IMM：从 .ime 文件提取第一个图标。
    /// 键盘布局：返回 shell32 默认键盘图标。
    /// 失败返回 IntPtr.Zero。</summary>
    public static IntPtr GetLayoutIconHandle(string klidHex, bool isTs)
    {
        try
        {
            if (isTs)
            {
                return ExtractTsIcon(klidHex);
            }
            // IMM 输入法或键盘布局：从 LayoutFile 提取
            RefreshRegistryCacheIfStale();
            if (_registryCache is not null && _registryCache.TryGetValue(klidHex, out var info) &&
                !string.IsNullOrEmpty(info.LayoutFile) &&
                info.LayoutFile.EndsWith(".ime", StringComparison.OrdinalIgnoreCase))
            {
                var imePath = System.IO.Path.Combine(Environment.SystemDirectory, info.LayoutFile);
                return ExtractFirstIcon(imePath);
            }
            // 纯键盘布局：Windows 11 语言栏显示语言代码文本（如 "ENG"），不显示图标。
            // 返回 IntPtr.Zero，由 UI 层降级为语言代码文本显示。
            return IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>从 TSF 注册表中读取 IconFile/IconIndex 并提取图标。</summary>
    private static IntPtr ExtractTsIcon(string klidHex)
    {
        var profileInfo = FindTsProfileByKlid(klidHex);
        if (profileInfo is null) return IntPtr.Zero;
        var (clsid, langId, profile) = profileInfo.Value;

        // 路径：HKLM\SOFTWARE\Microsoft\CTF\TIP\{CLSID}\LanguageProfile\0x{langid:X8}\{profile}
        // 注意：langid 是 8 位十六进制带 0x 前缀，如 "0x00000804"
        var regPath = $@"SOFTWARE\Microsoft\CTF\TIP\{{{clsid}}}\LanguageProfile\0x{langId:X8}\{{{profile}}}";
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(regPath);
            if (key is null)
            {
                // 回退：TIP 根键下可能有 IconFile
                using var tipKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\CTF\TIP\{{{clsid}}}");
                if (tipKey is null) return IntPtr.Zero;
                return ExtractIconFromKey(tipKey);
            }
            return ExtractIconFromKey(key);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>从注册表键读取 IconFile/IconIndex 并提取图标。</summary>
    private static IntPtr ExtractIconFromKey(RegistryKey key)
    {
        var iconFile = key.GetValue("IconFile") as string;
        if (string.IsNullOrEmpty(iconFile)) return IntPtr.Zero;

        // 解析间接字符串（如 @input.dll,-5021）
        if (iconFile.StartsWith("@"))
        {
            var sb = new System.Text.StringBuilder(512);
            if (NativeMethods.SHLoadIndirectString(iconFile, sb, sb.Capacity, IntPtr.Zero) == 0)
            {
                iconFile = sb.ToString();
            }
        }
        // 展开环境变量
        iconFile = Environment.ExpandEnvironmentVariables(iconFile);

        int iconIndex = 0;
        var idxVal = key.GetValue("IconIndex");
        if (idxVal is int i) iconIndex = i;
        else if (idxVal is string s && int.TryParse(s, out var si)) iconIndex = si;

        return ExtractIcon(iconFile, iconIndex);
    }

    /// <summary>从文件提取指定索引的图标（小图标，16x16）。</summary>
    private static IntPtr ExtractIcon(string filePath, int iconIndex)
    {
        if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath)) return IntPtr.Zero;
        var smallIcons = new IntPtr[1];
        uint count = NativeMethods.ExtractIconEx(filePath, (int)iconIndex, null, smallIcons, 1);
        if (count > 0 && smallIcons[0] != IntPtr.Zero) return smallIcons[0];
        return IntPtr.Zero;
    }

    /// <summary>从文件提取第一个图标。</summary>
    private static IntPtr ExtractFirstIcon(string filePath)
    {
        return ExtractIcon(filePath, 0);
    }

    /// <summary>
    /// 激活指定输入法/键盘布局（**直接激活，不弹系统输入法选择器 UI**）。
    /// 关键：切的是前台应用的布局，不是本进程的布局。
    /// v5 (2026-08-30)：TSF 输入法的 KlidHex 是 CLSID 前 8 位（如微软拼音 "E7EA138E"），
    /// **不等于**真实 HKL（微软拼音 HKL=0xE0200804），KlidToHkl 对 TSF 解析出的 HKL 无效，
    /// 这正是此前"点输入法没反应"的根因之一。因此 isTs=true 必须**优先**走
    /// TSF COM ITfInputProcessorProfiles::ActivateProfile（官方立即激活接口，无 UI），
    /// 仅 COM 失败才降级 PostMessage。IMM / 纯键盘布局走 NativeMethods.PostMessage(WM_INPUTLANGCHANGEREQUEST)。</summary>
    public static bool Activate(string klidHex, bool isTs)
    {
        if (isTs)
        {
            // TSF 输入法：CLSID 前 8 位 ≠ 真实 HKL，先走 TSF COM 立即激活
            var profile = FindTsProfileByKlid(klidHex);
            if (profile is not null &&
                ActivateTsProfile(profile.Value.clsid, (ushort)profile.Value.langId, profile.Value.profile))
            {
                return true;
            }
            // 降级：个别 TSF 输入法注册了可解析 HKL，尝试窗口消息
            var tsHkl = KlidToHkl(klidHex);
            return tsHkl != IntPtr.Zero && ActivateByHkl(tsHkl);
        }

        // IMM / 纯键盘布局：HKL 精确路径
        var immHkl = KlidToHkl(klidHex);
        return immHkl != IntPtr.Zero && ActivateByHkl(immHkl);
    }

    /// <summary>向后兼容：默认按 IMM 处理（ActivateKeyboardLayout 切本进程，不推荐）。
    /// 新代码请用 Activate(klidHex, isTs) 以切前台窗口。</summary>
    public static bool Activate(string klidHex) => Activate(klidHex, isTs: false);

    /// <summary>按 HKL 向前台窗口发 WM_INPUTLANGCHANGEREQUEST（Windows 原生切换，IMM/TSF 通用）。
    /// 用 GetRealForegroundWindow：点击菜单栏时前台可能正是我们的壳窗口，必须切用户正在用的应用。</summary>
    private static bool ActivateByHkl(IntPtr hkl)
    {
        if (hkl == IntPtr.Zero) return false;
        try
        {
            var foreground = GetRealForegroundWindow();
            if (foreground != IntPtr.Zero)
            {
                // wParam = INPUTLANGCHANGE_SYSCHARSET，lParam = HKL
                NativeMethods.PostMessage(foreground, WM_INPUTLANGCHANGEREQUEST, (IntPtr)INPUTLANGCHANGE_SYSCHARSET, hkl);
                return true;
            }
            // 取不到前台窗口时，降级到 ActivateKeyboardLayout（切本进程）
            return ActivateKeyboardLayout(hkl, KLF_ACTIVATE);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>IMM 路径已并入 Activate(klidHex, isTs:false)：KlidToHkl → ActivateByHkl。</summary>
    /// <summary>TSF 路径已并入 Activate(klidHex, isTs:true)：FindTsProfileByKlid → ActivateTsProfile（COM 立即激活，无 UI）。
    /// 已废弃旧实现"模拟 Win+Space 循环切换"：系统会把模拟按键识别为真实热键并弹出输入法选择器浮层（v5 修复）。</summary>

    /// <summary>获取真正的前台窗口。如果当前前台窗口是我们自己的弹窗，取 Z-order 下一个可见窗口。</summary>
    private static IntPtr GetRealForegroundWindow()
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero) return IntPtr.Zero;

        // 检查前台窗口是否属于本进程（我们的弹窗）
        uint fgProcId;
        NativeMethods.GetWindowThreadProcessId(fg, out fgProcId);
        uint ourProcId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        if (fgProcId != ourProcId) return fg; // 前台窗口不是我们的，直接用

        // 前台窗口是我们的弹窗，沿 Z-order 找下一个可见窗口
        var hwnd = NativeMethods.GetWindow(fg, GW_HWNDNEXT);
        while (hwnd != IntPtr.Zero)
        {
            if (NativeMethods.IsWindowVisible(hwnd))
            {
                uint procId;
                NativeMethods.GetWindowThreadProcessId(hwnd, out procId);
                if (procId != ourProcId) return hwnd;
            }
            hwnd = NativeMethods.GetWindow(hwnd, GW_HWNDNEXT);
        }
        return fg; // 找不到就用前台窗口
    }

    /// <summary>从 SortOrder 注册表中找到 klidHex 匹配的 TSF IME，返回 (CLSID, LangId, ProfileGUID)。</summary>
    private static (Guid clsid, int langId, Guid profile)? FindTsProfileByKlid(string klidHex)
    {
        try
        {
            using var sortOrderKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\CTF\SortOrder\AssemblyItem");
            if (sortOrderKey is null) return null;

            foreach (var langName in sortOrderKey.GetSubKeyNames())
            {
                if (!langName.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(langName.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var langId)) continue;

                using var langKey = sortOrderKey.OpenSubKey(langName);
                if (langKey is null) continue;

                foreach (var assemblyName in langKey.GetSubKeyNames())
                {
                    using var assemblyKey = langKey.OpenSubKey(assemblyName);
                    if (assemblyKey is null) continue;

                    foreach (var itemName in assemblyKey.GetSubKeyNames())
                    {
                        using var itemKey = assemblyKey.OpenSubKey(itemName);
                        if (itemKey is null) continue;

                        var clsidVal = itemKey.GetValue("CLSID") as string;
                        var profileVal = itemKey.GetValue("Profile") as string;

                        if (string.IsNullOrEmpty(clsidVal) || clsidVal == "{00000000-0000-0000-0000-000000000000}") continue;

                        var itemKlid = TsClsidToHex(clsidVal);
                        if (string.Equals(itemKlid, klidHex, StringComparison.OrdinalIgnoreCase))
                        {
                            return (Guid.Parse(clsidVal), langId, Guid.Parse(profileVal ?? "{00000000-0000-0000-0000-000000000000}"));
                        }
                    }
                }
            }
        }
        catch { /* 注册表读失败 */ }
        return null;
    }

    /// <summary>从 SortOrder 注册表中找到 klidHex 匹配的 TSF IME，构造 SetDefaultLayoutOrTip 需要的完整字符串。
    // ======== 公开 API：管理（添加 / 删除 / 排序 / 设默认 / 应用） ========

    /// <summary>所有已安装到系统的键盘布局/输入法（HKLM 注册表，IMM 为主；TSF 全量枚举因 API 不稳定暂不启用）。</summary>
    public static IReadOnlyList<KeyboardLayoutItem> GetRegisteredLayouts()
    {
        RefreshRegistryCacheIfStale();
        var result = new List<KeyboardLayoutItem>(capacity: _registryCache!.Count);
        foreach (var kv in _registryCache)
        {
            result.Add(new KeyboardLayoutItem(kv.Key, kv.Value.LayoutName, kv.Value.LayoutFile, kv.Value.IsIme, IsActive: false));
        }
        return result.OrderBy(x => x.LayoutName, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>当前启用顺序（HKCU\Keyboard Layout\Preload），KLID 为完整 8 位十六进制，第一项为默认。</summary>
    public static IReadOnlyList<string> GetActivePreloadOrder()
    {
        RefreshRegistryCacheIfStale();
        return _preloadCache!;
    }

    /// <summary>把指定布局/输入法加入 Preload 并立即加载。在首位则设为默认。</summary>
    public static bool AddLayout(string klidHex, bool setDefault = false)
    {
        var key = NormalizeKlid(klidHex);
        if (key is null) return false;
        try
        {
            using var preload = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Preload", writable: true);
            if (preload is null) return false;

            var names = preload.GetValueNames();
            var existing = new List<string>(capacity: names.Length + 1);
            foreach (var n in names)
            {
                if (TryReadKlid(preload, n, out var id))
                {
                    existing.Add(id!);
                }
            }
            // 已存在：若要求设默认，则移到首位后同样应用。
            bool alreadyThere = existing.Contains(key, StringComparer.OrdinalIgnoreCase);
            if (!alreadyThere)
            {
                existing.Add(key);
            }
            // C10 修复：非默认布局追加到末尾（existing.Count），而非倒数第二（existing.Count-1）。
            // Reorder 内部会钳制 index 不越界，末尾插入语义与注释「非默认则追加到末尾」一致。
            ApplyOrderToPreload(preload, Reorder(existing, key, setDefault ? 0 : existing.Count));

            Apply(); // 加载新布局，让当前会话生效
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>从 Preload 删除指定布局。返回是否真的删掉了。</summary>
    public static bool RemoveLayout(string klidHex)
    {
        var key = NormalizeKlid(klidHex);
        if (key is null) return false;
        try
        {
            using var preload = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Preload", writable: true);
            if (preload is null) return false;

            var names = preload.GetValueNames();
            var remaining = new List<string>(capacity: names.Length);
            bool removed = false;
            foreach (var n in names)
            {
                if (TryReadKlid(preload, n, out var id))
                {
                    if (string.Equals(id, key, StringComparison.OrdinalIgnoreCase)) { removed = true; continue; }
                    remaining.Add(id!);
                }
            }
            ApplyOrderToPreload(preload, remaining);
            return removed;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把指定布局移动到目标下标（0 = 设为默认，同时作为默认输入法）。</summary>
    public static bool MoveLayoutTo(string klidHex, int targetIndex)
    {
        var key = NormalizeKlid(klidHex);
        if (key is null || targetIndex < 0) return false;
        try
        {
            using var preload = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Preload", writable: true);
            if (preload is null) return false;
            var names = preload.GetValueNames();
            var order = new List<string>(capacity: names.Length);
            foreach (var n in names)
            {
                if (TryReadKlid(preload, n, out var id))
                {
                    order.Add(id!);
                }
            }
            var reordered = Reorder(order, key, targetIndex);
            ApplyOrderToPreload(preload, reordered);
            Apply();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把 NewLayout 移动到 list 中第 index 位（其余顺序保持）。</summary>
    private static List<string> Reorder(List<string> list, string newLayout, int index)
    {
        var result = new List<string>(capacity: list.Count);
        string? moved = null;
        foreach (var id in list)
        {
            if (string.Equals(id, newLayout, StringComparison.OrdinalIgnoreCase)) { moved = id; continue; }
            result.Add(id);
        }
        if (moved is null) moved = newLayout;
        if (index > result.Count) index = result.Count;
        result.Insert(index, moved);
        return result;
    }

    /// <summary>按给定顺序覆盖写 Preload 的 "1".."N" 值（值为 8 位十六进制 REG_SZ）。</summary>
    private static void ApplyOrderToPreload(RegistryKey preload, IReadOnlyList<string> order)
    {
        // 先清掉多余的旧值（从高下标到低，避免键名冲突）
        foreach (var n in preload.GetValueNames())
        {
            preload.DeleteValue(n, throwOnMissingValue: false);
        }
        for (int i = 0; i < order.Count; i++)
        {
            preload.SetValue((i + 1).ToString(), order[i].ToLowerInvariant(), RegistryValueKind.String);
        }
        _preloadCache = null; // 缓存失效，下次重新读
        _registryCacheStamp = 0;
    }

    /// <summary>当前会话立即应用 Preload：逐个 LoadKeyboardLayout，最后一个作为激活输入法。</summary>
    public static void Apply()
    {
        try
        {
            IReadOnlyList<string> order = GetActivePreloadOrder();
            foreach (var klid in order)
            {
                var hkl = KlidToHkl(klid);
                if (hkl == IntPtr.Zero) continue;
                // 加载并（对最后一个）激活；对前序仅加载，保持"最后一个为当前输入法"的习惯。
                LoadKeyboardLayout(klid, KLF_ACTIVATE | KLF_SUBSTITUTE_OK | KLF_NOTELLSHELL);
            }
        }
        catch
        {
            // 应用失败不抛出，避免 UI 崩溃；用户在系统输入指示器里仍可手动切换。
        }
    }

    // ======== 内部工具 ========

    /// <summary>把完整 8 位十六进制 KLID 转成 HKL IntPtr。非法输入返回 IntPtr.Zero。</summary>
    private static IntPtr KlidToHkl(string klidHex)
    {
        var key = NormalizeKlid(klidHex);
        if (key is null) return IntPtr.Zero;
        try
        {
            long value = System.Convert.ToInt64(key, 16);
            return new IntPtr((long)(uint)value);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>归一化 KLID：从 4 位或 8 位十六进制得到大写 8 位十六进制；失败返回 null。</summary>
    private static string? NormalizeKlid(string? klidHex)
    {
        if (string.IsNullOrWhiteSpace(klidHex)) return null;
        var raw = klidHex.Trim();
        if (raw.Length > 8) { raw = raw.Substring(raw.Length - 8); }
        if (!uint.TryParse(raw, System.Globalization.NumberStyles.HexNumber, null, out uint v)) return null;
        return v.ToString("X8");
    }

    /// <summary>读取 Preload 一个值对应的完整 KLID。</summary>
    private static bool TryReadKlid(RegistryKey key, string valueName, out string? klid)
    {
        klid = null;
        var raw = key.GetValue(valueName);
        if (raw is int i) { klid = ((uint)i).ToString("X8"); return true; }
        if (raw is long l) { klid = ((uint)l).ToString("X8"); return true; }
        if (raw is string s && s.Length >= 4)
        {
            klid = NormalizeKlid(s);
            return klid is not null;
        }
        return false;
    }

    // ======== 注册表读取 ========

    /// <summary>按 TTL 惰性刷新注册表缓存；命中缓存时零注册表访问。</summary>
    private static void RefreshRegistryCacheIfStale()
    {
        long now = Environment.TickCount64;
        if (_registryCache is null || _preloadCache is null ||
            now - _registryCacheStamp > RegistryCacheTtlMs)
        {
            _registryCache = ReadAllRegisteredLayouts();
            _preloadCache = ReadPreloadOrder();
            _registryCacheStamp = now;
        }
    }

    /// <summary>
    /// HKCU\Keyboard Layout\Preload 列出用户实际启用的布局顺序。值名 "1","2","3"...，
    /// 值是完整 8 位十六进制字符串（如 "e0200804"）或 DWORD（低 16 位常见）。
    /// </summary>
    private static List<string> ReadPreloadOrder()
    {
        var list = new List<string>(capacity: 8);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Preload");
            if (key is null) return list;
            var valueNames = key.GetValueNames();
            Array.Sort(valueNames, (a, b) =>
            {
                if (int.TryParse(a, out var ai) && int.TryParse(b, out var bi)) return ai.CompareTo(bi);
                return string.CompareOrdinal(a, b);
            });
            foreach (var name in valueNames)
            {
                if (TryReadKlid(key, name, out var klid) && klid is not null)
                {
                    list.Add(klid);
                }
            }
        }
        catch
        {
            // 忽略注册表读失败
        }
        return list;
    }

    private readonly struct LayoutInfo(string layoutName, string layoutFile, bool isIme)
    {
        public readonly string LayoutName = layoutName;
        public readonly string LayoutFile = layoutFile;
        public readonly bool IsIme = isIme;
    }

    /// <summary>
    /// HKLM\SYSTEM\CurrentControlSet\Control\Keyboard Layouts\<KLIDHex>
    /// 每个子键有 Layout Text（用户可读名）和 Layout File（.kbd / .ime / .dll）。
    /// 子键名就是完整 8 位十六进制 KLID，直接作为键名，与 Preload 中的完整值精确匹配。
    /// </summary>
    private static Dictionary<string, LayoutInfo> ReadAllRegisteredLayouts()
    {
        var map = new Dictionary<string, LayoutInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts");
            if (root is null) return map;
            foreach (var sub in root.GetSubKeyNames())
            {
                var klid = NormalizeKlid(sub);
                if (klid is null) continue;
                using var k = root.OpenSubKey(sub);
                if (k is null) continue;
                var layoutText = k.GetValue("Layout Text") as string ?? string.Empty;
                var layoutFile = k.GetValue("Layout File") as string ?? string.Empty;
                var isIme = layoutFile.EndsWith(".ime", StringComparison.OrdinalIgnoreCase);
                map[klid] = new LayoutInfo(layoutText, layoutFile, isIme);
            }
        }
        catch
        {
            // 忽略注册表读失败
        }
        return map;
    }

    // ======== API 层读取 ========

    private static List<string> ReadLoadedKlidsFromApi()
    {
        var list = new List<string>(capacity: 8);
        try
        {
            int needed = GetKeyboardLayoutList(0, null);
            if (needed <= 0) return list;
            var buffers = new IntPtr[needed];
            int got = GetKeyboardLayoutList(needed, buffers);
            if (got <= 0) return list;
            for (int i = 0; i < got; i++)
            {
                uint id = unchecked((uint)buffers[i].ToInt64());
                list.Add(id.ToString("X8"));
            }
        }
        catch
        {
            // 忽略
        }
        return list;
    }

    private static string? ReadActiveKlid()
    {
        try
        {
            // 用"前台应用"的线程读键盘布局，而不是本进程的线程——
            // 否则弹窗打开/抢焦点后读到的是弹窗自己的布局，造成"当前输入法识别不准"。
            var foreground = NativeMethods.GetForegroundWindow();
            uint threadId = 0;
            if (foreground != IntPtr.Zero)
            {
                // 返回值 = 前台窗口所在线程 ID；out 参数 = 进程 ID（不用，忽略）
                threadId = NativeMethods.GetWindowThreadProcessId(foreground, out _);
            }
            var hkl = GetKeyboardLayout(threadId); // 0 = 当前线程（无可判断前台时）
            uint id = unchecked((uint)hkl.ToInt64());
            return id == 0 ? null : id.ToString("X8");
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// 输入法 / 键盘布局的统一缩写规则。菜单栏按钮与弹窗列表共用同一套逻辑，
/// 避免出现"按钮显示 '微'、列表显示 '拼'"的割裂。
/// </summary>
public static class ImeNaming
{
    /// <summary>从真实布局名抠出一个代表字符：输入法取品牌首字（搜/微/拼/必/百/讯/五/极），
    /// 键盘布局按语言特征取单字（美/中/繁/日/韩/法/德/俄）。</summary>
    public static string ToLetter(string layoutName, bool isIme)
    {
        if (string.IsNullOrEmpty(layoutName)) return isIme ? "拼" : "K";

        // 输入法：优先匹配已知品牌 / 类型首字
        if (isIme)
        {
            if (layoutName.Contains("搜狗")) return "搜";
            // 微软拼音官方图标就是"拼"字（用户明确期望），必须先于"微软"匹配，
            // 否则"微软拼音"会命中"微软"显示成"微"。
            if (layoutName.Contains("微软拼音")) return "拼";
            if (layoutName.Contains("微软")) return "微";
            if (layoutName.Contains("拼音")) return "拼";
            if (layoutName.Contains("必应")) return "必";
            if (layoutName.Contains("百度")) return "百";
            if (layoutName.Contains("讯飞")) return "讯";
            if (layoutName.Contains("五笔")) return "五";
            if (layoutName.Contains("极点")) return "极";
            if (layoutName.Contains("仓颉")) return "仓";
            if (layoutName.Contains("自然")) return "自";
            // 通用：取第一个 CJK 字符
            foreach (var c in layoutName)
            {
                if (c >= 0x4E00 && c <= 0x9FFF) return c.ToString();
            }
            return "拼"; // 兜底 IME 标记
        }

        // 键盘布局：按语言特征取单字
        if (layoutName.Contains("美式") || layoutName.Contains("US") ||
            layoutName.Contains("English", StringComparison.OrdinalIgnoreCase)) return "美";
        if (layoutName.Contains("简体") || layoutName.Contains("PRC") || layoutName.Contains("China")) return "中";
        if (layoutName.Contains("繁体") || layoutName.Contains("Taiwan")) return "繁";
        if (layoutName.Contains("日本") || layoutName.Contains("Japanese")) return "日";
        if (layoutName.Contains("韩国") || layoutName.Contains("Korean")) return "韩";
        if (layoutName.Contains("French") || layoutName.Contains("法国")) return "法";
        if (layoutName.Contains("German") || layoutName.Contains("德国")) return "德";
        if (layoutName.Contains("Russian") || layoutName.Contains("俄国")) return "俄";

        // 通用兜底：取第一个字母
        foreach (var c in layoutName)
        {
            if (char.IsLetter(c)) return c.ToString().ToUpperInvariant();
        }
        return "K";
    }

    /// <summary>菜单栏按钮用：返回 1 个字符的"当前输入状态"标识。
    /// 语义对齐 Windows 原生输入指示器 / MyDockFinder：键盘布局显示 A（英文），
    /// 输入法按中/英文模式显示"中"或"A"——用户要看的是"现在打出来是中文还是英文"，不是输入法品牌。
    /// 模式确实读不到时（输入法不响应），退回语言推断；仍不确定才用品牌首字。</summary>
    public static string ForMenuBar(string layoutName, bool isIme, bool? chineseMode, int langId = 0)
    {
        // 纯键盘布局：只能输入英文
        if (!isIme) return "A";

        if (chineseMode == true) return "中";
        if (chineseMode == false) return "A";

        // 模式读不到：中文语言的输入法按中文模式展示，避免退化成"搜"这类品牌字而丢掉中/英语义。
        return IsChineseLangId(langId) ? "中" : ToLetter(layoutName, isIme);
    }

    /// <summary>语言 ID 是否中文（主语言 ID = 0x04，含简体 / 繁体）。</summary>
    public static bool IsChineseLangId(int langId) => langId != 0 && (langId & 0x3FF) == 0x04;
}
