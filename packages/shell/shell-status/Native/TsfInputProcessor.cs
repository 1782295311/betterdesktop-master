// BetterDesktop.Shell.Status — TSF 文本服务框架：读取当前"真正激活"的输入法 profile
// 为什么需要：现代输入法（微软拼音 / 搜狗）都是 TSF，注册表里以 CLSID 标识，
// 与键盘布局的 KLID 不是同一套编码，因此无法用前台 HKL 直接比对判定"当前激活的是哪一项"
// （表现：所有 TSF 项的 IsActive 恒为 false，菜单栏只能兜底显示列表第一项）。
// 权威答案只有 TSF 的 ITfInputProcessorProfileMgr::GetActiveProfile。
// 本模块任何失败都静默返回 false，调用方降级到"语言 ID 匹配"（不精确但可用）。

using System;
using System.Runtime.InteropServices;
using BetterDesktop.Shell.Core.Native;

namespace BetterDesktop.Shell.Status.Native;

/// <summary>当前激活的 TSF 输入法 profile（CLSID + 语言 ID）。</summary>
public sealed record TsfActiveProfile(Guid Clsid, ushort LangId);

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct TF_INPUTPROCESSORPROFILE
{
    public Guid clsid;
    public ushort langid;
    public Guid guidProfile;
    public Guid catid;
    public IntPtr hkl;
    public uint dwFlags;
    public IntPtr hklSubstitute;
    public uint dwPreferredLayout;
    public int bEnabled;
    public int bEnabledByDefault;
}

/// <summary>仅用于取 ITfInputProcessorProfileMgr：TSF 的 profile 管理器须经 ThreadMgr 的 QueryInterface 获得。</summary>
[ComImport]
[Guid("aa80e801-2021-11d2-93e0-0060b067b86e")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfThreadMgr
{
}

[ComImport]
[Guid("71c6e74c-0f28-11d8-a82a-00065b84435c")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfInputProcessorProfileMgr
{
    // vtable 前 7 个槽必须占位声明：漏一个都会让 GetActiveProfile 错位调用到错误的方法（崩溃级别）。
    // 这些占位方法在本模块不被调用，签名只需保证槽位数量正确。
    [PreserveSig] int ActivateProfile(uint dwProfileType, ushort langid, ref Guid clsid, ref Guid guidProfile, IntPtr hkl, uint dwFlags);
    [PreserveSig] int DeactivateProfile(uint dwProfileType, ushort langid, ref Guid clsid, ref Guid guidProfile, IntPtr hkl, uint dwFlags);
    [PreserveSig] int GetProfile(uint dwProfileType, ushort langid, ref Guid clsid, ref Guid guidProfile, IntPtr hkl, out TF_INPUTPROCESSORPROFILE pProfile);
    [PreserveSig] int EnumProfiles(ushort langid, out IntPtr ppEnum);
    [PreserveSig] int ReleaseInputProcessor(ref Guid rclsid, uint dwFlags);
    [PreserveSig] int RegisterProfile(ref Guid rclsid, ushort langid, ref Guid guidProfile, IntPtr pchDesc, uint cchDesc, IntPtr pchIconFile, uint cchIconFile, IntPtr pchIconIndex, uint cchIconIndex, IntPtr hkl, uint dwFlags, int bEnabledByDefault);
    [PreserveSig] int UnregisterProfile(ref Guid rclsid, ushort langid, ref Guid guidProfile, uint dwFlags);

    /// <summary>取当前激活的 profile（按 catid 过滤）。</summary>
    [PreserveSig] int GetActiveProfile(ref Guid catid, out TF_INPUTPROCESSORPROFILE pProfile);
}

/// <summary>TSF 输入法激活状态查询。</summary>
public static class TsfInputProcessor
{
    // CLSID_TF_ThreadMgr
    private static readonly Guid ClsidTfThreadMgr = new("529a9e6b-6587-4f23-ab9e-9c7d683ff3c9");

    // GUID_TFCAT_TIP_KEYBOARD：只关心键盘输入类 profile，排除语音 / 手写等。
    private static readonly Guid CatTfTipKeyboard = new("34745c63-b2f0-4784-8b67-5e12c8701a31");

    private const int CLSCTX_INPROC_SERVER = 1;

    /// <summary>
    /// 尝试读取当前激活的 TSF 输入法。
    /// 成功返回 true；TSF 不可用、当前是纯键盘布局、或任何 COM 失败均返回 false（调用方应降级）。
    /// </summary>
    public static bool TryGetActive(out TsfActiveProfile? profile)
    {
        profile = null;
        IntPtr pThreadMgr = IntPtr.Zero;
        object? comObj = null;
        try
        {
            // static readonly Guid 不能作 ref 实参（CS0199），必须取局部副本。
            var clsid = ClsidTfThreadMgr;
            var iid = typeof(ITfThreadMgr).GUID;

            var hr = NativeMethods.CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out pThreadMgr);
            if (hr != 0 || pThreadMgr == IntPtr.Zero)
            {
                return false;
            }

            // RCW 接管 CoCreateInstance 得到的那一个引用（不额外 AddRef），收尾由 ReleaseComObject 释放。
            comObj = Marshal.GetObjectForIUnknown(pThreadMgr);
            if (comObj is not ITfThreadMgr)
            {
                return false;
            }

            // 强制转换即对 COM 对象做 QueryInterface。
            var mgr = (ITfInputProcessorProfileMgr)comObj;
            var catid = CatTfTipKeyboard;
            var hrActive = mgr.GetActiveProfile(ref catid, out var prof);
            if (hrActive != 0 || prof.clsid == Guid.Empty)
            {
                return false;
            }

            profile = new TsfActiveProfile(prof.clsid, prof.langid);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (comObj is not null)
            {
                try { Marshal.ReleaseComObject(comObj); } catch { /* 释放失败不冒泡 */ }
            }
            else if (pThreadMgr != IntPtr.Zero)
            {
                try { Marshal.Release(pThreadMgr); } catch { /* 释放失败不冒泡 */ }
            }
        }
    }

    /// <summary>诊断：逐步输出 TSF 查询链路的 HRESULT，用于定位"TSF 不可用"究竟卡在哪一步。</summary>
    public static string Diagnose()
    {
        var sb = new System.Text.StringBuilder();
        IntPtr pThreadMgr = IntPtr.Zero;
        object? comObj = null;
        try
        {
            var clsid = ClsidTfThreadMgr;
            var iid = typeof(ITfThreadMgr).GUID;
            var hr = NativeMethods.CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out pThreadMgr);
            sb.Append($"NativeMethods.CoCreateInstance(hr=0x{hr:X8}, ptr=0x{pThreadMgr.ToInt64():X})");
            if (hr != 0 || pThreadMgr == IntPtr.Zero) return sb.ToString();

            comObj = Marshal.GetObjectForIUnknown(pThreadMgr);
            sb.Append($" -> RCW({comObj.GetType().Name})");

            var mgr = (ITfInputProcessorProfileMgr)comObj;
            sb.Append(" -> QI(ProfileMgr) OK");

            var catid = CatTfTipKeyboard;
            var hrActive = mgr.GetActiveProfile(ref catid, out var prof);
            sb.Append($" -> GetActiveProfile(hr=0x{hrActive:X8})");
            if (hrActive == 0)
            {
                sb.Append($" [clsid={prof.clsid}, langid=0x{prof.langid:X4}, hkl=0x{prof.hkl.ToInt64():X}]");
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            sb.Append($" -> EX {ex.GetType().Name}: {ex.Message}");
            return sb.ToString();
        }
        finally
        {
            if (comObj is not null)
            {
                try { Marshal.ReleaseComObject(comObj); } catch { /* 释放失败不冒泡 */ }
            }
            else if (pThreadMgr != IntPtr.Zero)
            {
                try { Marshal.Release(pThreadMgr); } catch { /* 释放失败不冒泡 */ }
            }
        }
    }

    /// <summary>取当前激活 TSF 输入法的 CLSID 十六进制串（与 KeyboardLayoutItem.KlidHex 同格式，便于直接比对）；失败返回 null。</summary>
    public static string? TryGetActiveClsidHex()
    {
        if (!TryGetActive(out var prof) || prof is null)
        {
            return null;
        }

        // 与 TsClsidToHex 的生成规则保持一致：取 CLSID 第一段（Data1）的 8 位十六进制。
        return prof.Clsid.ToString("N", null).Substring(0, 8).ToUpperInvariant();
    }
}
