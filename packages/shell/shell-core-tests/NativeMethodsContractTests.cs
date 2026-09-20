// BetterDesktop.Shell.Core.Tests — NativeMethods 声明契约测试（7434 纪律）
// 断言结构体布局与 Windows SDK 定义严格一致（Marshal.SizeOf 硬校验），
// 防止收口过程中出现布局漂移（如 SYSTEM_POWER_STATUS 曾被裁成 8 字节导致栈越界崩溃）。

using System;
using System.Runtime.InteropServices;
using BetterDesktop.Shell.Core.Native;
using Xunit;

namespace BetterDesktop.Shell.Core.Tests;

public class NativeMethodsContractTests
{
    [Fact]
    public void Point_Layout_8Bytes()
    {
        Assert.Equal(8, Marshal.SizeOf<NativeMethods.POINT>());
    }

    [Fact]
    public void Msg_Layout_48Bytes_OnX64()
    {
        // hwnd(8)+message(4)+pad(4)+wParam(8)+lParam(8)+time(4)+pad(4)+pt(8) = 48（x64）
        Assert.Equal(48, Marshal.SizeOf<NativeMethods.MSG>());
    }

    [Fact]
    public void MsllHookStruct_Layout_32Bytes_OnX64()
    {
        // pt(8)+mouseData(4)+flags(4)+time(4)+pad(4)+dwExtraInfo(8) = 32（x64）
        Assert.Equal(32, Marshal.SizeOf<NativeMethods.MSLLHOOKSTRUCT>());
    }

    [Fact]
    public void Rect_Layout_16Bytes()
    {
        Assert.Equal(16, Marshal.SizeOf<NativeMethods.RECT>());
        Assert.Equal(16, Marshal.SizeOf<NativeMethods.RECT32>());
    }

    [Fact]
    public void LastInputInfo_Layout_8Bytes()
    {
        Assert.Equal(8, Marshal.SizeOf<NativeMethods.LASTINPUTINFO>());
    }

    [Fact]
    public void SystemPowerStatus_Layout_16Bytes()
    {
        // 4×BYTE + 3×DWORD = 16。历史教训：曾被裁成 8 字节 → GetSystemPowerStatus 越界写栈（AccessViolation）。
        Assert.Equal(16, Marshal.SizeOf<NativeMethods.SYSTEM_POWER_STATUS>());
    }

    [Fact]
    public void MonitorInfo_Layout_40Bytes()
    {
        // cbSize(4)+rcMonitor(16)+rcWork(16)+dwFlags(4) = 40
        Assert.Equal(40, Marshal.SizeOf<NativeMethods.MONITORINFO>());
    }

    [Fact]
    public void GetClassName_Signature_IsUnicode()
    {
        // 声明须为 CharSet.Unicode（GetClassNameW），与 StringBuilder 重载配套；
        // 若误用 ANSI（GetClassNameA）在多字节类名场景会截断。
        var method = typeof(NativeMethods).GetMethod(nameof(NativeMethods.GetClassName));
        Assert.NotNull(method);
        var attr = method!.GetCustomAttributes(typeof(DllImportAttribute), false)[0] as DllImportAttribute;
        Assert.NotNull(attr);
        Assert.Equal(CharSet.Unicode, attr!.CharSet);
    }

    [Fact]
    public void SetWindowCompositionAttribute_ReturnType_IsBool_NotHresult()
    {
        // 【2026-09-14 回归守卫】该 API 返回 BOOL：**非零 = 成功**。
        // 曾按 HRESULT 判（`hr != 0 → 失败`），结果每次成功都记一条失败日志并无条件叠加降级材质
        //（日志实证：1000+ 条 "失败 hr=0x00000001"，连 Disable 也"失败"）。
        // 本断言钉住声明本身：改成 int 就会红。
        var method = typeof(NativeMethods).GetMethod(nameof(NativeMethods.SetWindowCompositionAttribute));
        Assert.NotNull(method);
        Assert.Equal(typeof(bool), method!.ReturnType);

        var marshal = method.ReturnParameter.GetCustomAttributes(typeof(MarshalAsAttribute), false)
            .Cast<MarshalAsAttribute>().FirstOrDefault();
        Assert.NotNull(marshal);
        Assert.Equal(UnmanagedType.Bool, marshal!.Value);
    }

    [Fact]
    public void DwmSetWindowAttribute_ReturnType_IsHresult()
    {
        // 对照组：DWM 系列返回 HRESULT（0 = 成功），与上面那条 BOOL 语义**相反** ——
        // 两族 API 用同一套判断逻辑必然错一边，本测试把两种语义一起钉住。
        var method = typeof(NativeMethods).GetMethod(nameof(NativeMethods.DwmSetWindowAttribute));
        Assert.NotNull(method);
        Assert.Equal(typeof(int), method!.ReturnType);
    }
}
