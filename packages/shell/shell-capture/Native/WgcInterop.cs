using System;
using System.Runtime.InteropServices;

namespace BetterDesktop.Shell.Capture.Native;

/// <summary>
/// Windows.Graphics.Capture 互操作：把原生 D3D11 设备转成 WinRT IDirect3DDevice、
/// 把 WGC 帧表面转成原生 IDXGISurface 读像素。仅用两个标准入口点，零额外依赖。
/// </summary>
internal static class WgcInterop
{
    /// <summary>CreateDirect3D11DeviceFromDXGIDevice（windows.graphics.directx.direct3d11.interop.dll）。</summary>
    [DllImport("windows.graphics.directx.direct3d11.interop.dll", PreserveSig = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    /// <summary>IDirect3DDxgiInterfaceAccess（从 WGC 帧表面 QI，取原生 IDXGISurface）。</summary>
    [ComImport, Guid("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig] int GetInterface(ref Guid iid, out IntPtr p);
    }

    /// <summary>
    /// 由原生 ID3D11Device 指针创建 WinRT IDirect3DDevice（WGC 帧池需要）。
    /// 返回的 IInspectable 指针由调用方持有；C#/WinRT 包装（FromAbi）接管其生命周期。
    /// </summary>
    public static IntPtr CreateDirect3D11DeviceFromDxgiDevice(IntPtr dxgiDevicePtr, out int hresult)
    {
        hresult = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevicePtr, out IntPtr graphicsDevice);
        return hresult >= 0 ? graphicsDevice : IntPtr.Zero;
    }
}
