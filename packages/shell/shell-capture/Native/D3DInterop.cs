using System;
using System.Runtime.InteropServices;

namespace BetterDesktop.Shell.Capture.Native;

/// <summary>
/// DXGI / D3D11 原生互操作（DXGI Desktop Duplication 与 WGC 的像素读取共用）。
/// 全部手工 COM vtable 声明，零新增 NuGet 依赖（对齐依赖治理纪律）。
/// 符号与 Windows SDK d3d11.h / dxgi.h 对齐；vtable 顺序错误会导致内存破坏，改动必须对照头文件。
/// </summary>
internal static class D3DInterop
{
    // ---------------- 常量 ----------------

    public const uint DXGI_FORMAT_R16G16B16A16_FLOAT = 10;
    public const uint DXGI_FORMAT_R8G8B8A8_UNORM = 28;
    public const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;

    public const uint D3D11_USAGE_STAGING = 3;
    public const uint D3D11_CPU_ACCESS_READ = 0x20000;
    public const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;

    public const int D3D_DRIVER_TYPE_HARDWARE = 1;
    public const int D3D_DRIVER_TYPE_UNKNOWN = 0;
    public const uint D3D11_SDK_VERSION = 7;

    /// <summary>DXGI_ERROR_NOT_CURRENTLY_AVAILABLE（远程桌面/安全桌面下 DuplicateOutput 常见失败）。</summary>
    public const int DXGI_ERROR_NOT_CURRENTLY_AVAILABLE = unchecked((int)0x887A0022);

    /// <summary>DXGI_ERROR_ACCESS_LOST（桌面切换/分辨率变化后 duplication 失效，需重建）。</summary>
    public const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);

    public static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    public static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    public static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid IID_IDXGISurface = new("cafcb53c-85c7-4f46-9be4-9a3cae8a2254");
    public static readonly Guid IID_IDXGIResource = new("0359dc30-95e3-4568-9b20-cb0f9b2ee3c7");

    // ---------------- 入口点 ----------------

    [DllImport("dxgi.dll", PreserveSig = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    [DllImport("d3d11.dll", PreserveSig = true)]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr pFeatureLevels,
        uint featureLevels,
        uint sdkVersion,
        out IntPtr ppDevice,
        out int pFeatureLevel,
        out IntPtr ppImmediateContext);

    /// <summary>创建硬件 D3D11 设备（BGRA 支持供 DXGI 表面互操作）。失败返回 IntPtr.Zero 并给出 HRESULT。</summary>
    public static IntPtr CreateHardwareDevice(out int hresult)
    {
        hresult = D3D11CreateDevice(
            IntPtr.Zero,
            D3D_DRIVER_TYPE_HARDWARE,
            IntPtr.Zero,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            IntPtr.Zero,
            0,
            D3D11_SDK_VERSION,
            out IntPtr device,
            out _,
            out _);
        return hresult >= 0 ? device : IntPtr.Zero;
    }

    /// <summary>创建 DXGI 工厂（用于枚举适配器/输出）。失败返回 IntPtr.Zero 并给出 HRESULT。</summary>
    public static IntPtr CreateFactory1(out int hresult)
    {
        Guid iid = IID_IDXGIFactory1; // 静态只读 Guid 不能作 ref 实参，本地副本
        hresult = CreateDXGIFactory1(ref iid, out IntPtr factory);
        return hresult >= 0 ? factory : IntPtr.Zero;
    }

    // ---------------- 结构体 ----------------

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_SAMPLE_DESC
    {
        public uint Count;
        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_OUTPUT_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public int DesktopLeft;
        public int DesktopTop;
        public int DesktopRight;
        public int DesktopBottom;
        public int AttachedToDesktop;
        public uint Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_SURFACE_DESC
    {
        public uint Width;
        public uint Height;
        public uint Format;
        public DXGI_SAMPLE_DESC SampleDesc;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_MAPPED_RECT
    {
        public int Pitch;
        public IntPtr PBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_OUTDUPL_FRAME_INFO
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public long PointerPosition_Position_X;
        public long PointerPosition_Position_Y;
        public int PointerPosition_Visible;
        public uint PointerShapeBufferSize;
        public uint PointerShapeBufferSizeRequired;
        public uint MetadataBufferSize;
        public uint MetadataBufferSizeRequired;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public DXGI_SAMPLE_DESC SampleDesc;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr PData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_BOX
    {
        public uint Left;
        public uint Top;
        public uint Front;
        public uint Right;
        public uint Bottom;
        public uint Back;
    }

    // ---------------- COM 接口 ----------------

    /// <summary>IDXGIFactory1（仅用 EnumAdapters1 / IsCurrent）。</summary>
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGIFactory1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int EnumAdapters(uint adapter, out IntPtr ppAdapter);
        [PreserveSig] int MakeWindowAssociation(IntPtr windowHandle, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr windowHandle);
        [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr ppSwapChain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr ppAdapter);
        [PreserveSig] int EnumAdapters1(uint adapter, out IntPtr ppAdapter);
        [PreserveSig] int IsCurrent();
    }

    /// <summary>IDXGIAdapter1（用 EnumOutputs / GetDesc1）。</summary>
    [ComImport, Guid("29038f61-3839-4626-91fd-425879ab0111"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGIAdapter1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int EnumOutputs(uint output, out IntPtr ppOutput);
        [PreserveSig] int GetDesc(out DXGI_ADAPTER_DESC pDesc);
        [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long pUMDVersion);
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 pDesc);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_ADAPTER_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long AdapterLuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    /// <summary>IDXGIOutput（用 GetDesc / DuplicateOutput）。</summary>
    [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGIOutput
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC pDesc);
        [PreserveSig] int GetDisplaySurfaceData(IntPtr pSurface);
        [PreserveSig] int GetFrameStatistics(out long pStats);
        [PreserveSig] int SetDisplaySurface(IntPtr pScanoutSurface);
        [PreserveSig] int GetDisplaySurfaceData1(IntPtr pSurface);
        [PreserveSig] int DuplicateOutput(IntPtr pDevice, out IntPtr ppOutputDuplication);
    }

    /// <summary>IDXGIOutputDuplication（用 AcquireNextFrame / ReleaseFrame）。</summary>
    [ComImport, Guid("191cfac3-a341-470d-b26e-a864f428319c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGIOutputDuplication
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int GetDesc(out DXGI_OUTDUPL_DESC pDesc);
        [PreserveSig] int AcquireNextFrame(
            uint timeoutInMilliseconds,
            out DXGI_OUTDUPL_FRAME_INFO pFrameInfo,
            out IntPtr ppDesktopResource);
        [PreserveSig] int GetFrameDirtyRects(uint dirtyRectsBufferSize, IntPtr pDirtyRectsBuffer, out uint pDirtyRectsBufferSizeRequired);
        [PreserveSig] int GetFrameMoveRects(uint moveRectsBufferSize, IntPtr pMoveRectBuffer, out uint pMoveRectsBufferSizeRequired);
        [PreserveSig] int GetFramePointerShape(uint pointerShapeBufferSize, IntPtr pPointerShapeBuffer, out uint pPointerShapeBufferSizeRequired, out int pPointerShapeInfo);
        [PreserveSig] int MapDesktopSurface(out DXGI_MAPPED_RECT pLockedRect);
        [PreserveSig] int UnMapDesktopSurface();
        [PreserveSig] int ReleaseFrame();
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_OUTDUPL_DESC
    {
        public DXGI_MODE_DESC ModeDesc;
        public int Rotation;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_MODE_DESC
    {
        public uint Width;
        public uint Height;
        public DXGI_RATIONAL RefreshRate;
        public uint Format;
        public uint ScanlineOrdering;
        public uint Scaling;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    /// <summary>IDXGIResource（仅作传递，QI 目标 ID3D11Texture2D）。</summary>
    [ComImport, Guid("0359dc30-95e3-4568-9b20-cb0f9b2ee3c7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGIResource
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int GetSharedHandle(out IntPtr pSharedHandle);
        [PreserveSig] int GetUsage(out uint pUsage);
        [PreserveSig] int SetEvictionPriority(uint evictionPriority);
        [PreserveSig] int GetEvictionPriority(out uint pEvictionPriority);
    }

    /// <summary>IDXGIDevice（仅作指针传递，供 CreateDirect3D11DeviceFromDXGIDevice）。</summary>
    [ComImport, Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGIDevice
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int GetAdapter(out IntPtr pAdapter);
        [PreserveSig] int CreateSurface(ref DXGI_SURFACE_DESC pDesc, uint numSurfaces, uint usage, IntPtr pSharedResource, out IntPtr ppSurface);
        [PreserveSig] int QueryResourceResidency(IntPtr[] ppResources, out int pResidencyStatus, uint numResources);
        [PreserveSig] int SetGPUThreadPriority(int priority);
        [PreserveSig] int GetGPUThreadPriority(out int pPriority);
    }

    /// <summary>ID3D11Device（vtable 顺序对照 d3d11.h；GetImmediateContext = 槽 41）。</summary>
    [ComImport, Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ID3D11Device
    {
        [PreserveSig] void GetDevice(IntPtr ppDevice);
        [PreserveSig] int GetPrivateData(ref Guid guid, ref uint pDataSize, IntPtr pData);
        [PreserveSig] int SetPrivateData(ref Guid guid, uint dataSize, IntPtr pData);
        [PreserveSig] int SetPrivateDataInterface(ref Guid guid, IntPtr pData);
        [PreserveSig] int CreateBuffer(IntPtr pDesc, IntPtr pInitialData, out IntPtr ppBuffer);
        [PreserveSig] int CreateTexture1D(IntPtr pDesc, IntPtr pInitialData, out IntPtr ppTexture1D);
        [PreserveSig] int CreateTexture2D(ref D3D11_TEXTURE2D_DESC pDesc, IntPtr pInitialData, out IntPtr ppTexture2D);
        [PreserveSig] int CreateTexture3D(IntPtr pDesc, IntPtr pInitialData, out IntPtr ppTexture3D);
        [PreserveSig] int CreateShaderResourceView(IntPtr pResource, IntPtr pDesc, out IntPtr ppSRView);
        [PreserveSig] int CreateUnorderedAccessView(IntPtr pResource, IntPtr pDesc, out IntPtr ppUAView);
        [PreserveSig] int CreateRenderTargetView(IntPtr pResource, IntPtr pDesc, out IntPtr ppRTView);
        [PreserveSig] int CreateDepthStencilView(IntPtr pResource, IntPtr pDesc, out IntPtr ppDepthStencilView);
        [PreserveSig] int CreateInputLayout(IntPtr pInputElementDescs, uint numElements, IntPtr pShaderBytecodeWithInputSignature, UIntPtr bytecodeLength, out IntPtr ppInputLayout);
        [PreserveSig] int CreateVertexShader(IntPtr pShaderBytecode, UIntPtr bytecodeLength, IntPtr pClassLinkage, out IntPtr ppVertexShader);
        [PreserveSig] int CreateGeometryShader(IntPtr pShaderBytecode, UIntPtr bytecodeLength, IntPtr pClassLinkage, out IntPtr ppGeometryShader);
        [PreserveSig] int CreateGeometryShaderWithStreamOutput(IntPtr pShaderBytecode, UIntPtr bytecodeLength, IntPtr pSODeclaration, uint numEntries, IntPtr pBufferStrides, uint numStrides, uint rasterizedStream, IntPtr pClassLinkage, out IntPtr ppGeometryShader);
        [PreserveSig] int CreatePixelShader(IntPtr pShaderBytecode, UIntPtr bytecodeLength, IntPtr pClassLinkage, out IntPtr ppPixelShader);
        [PreserveSig] int CreateHullShader(IntPtr pShaderBytecode, UIntPtr bytecodeLength, IntPtr pClassLinkage, out IntPtr ppHullShader);
        [PreserveSig] int CreateDomainShader(IntPtr pShaderBytecode, UIntPtr bytecodeLength, IntPtr pClassLinkage, out IntPtr ppDomainShader);
        [PreserveSig] int CreateComputeShader(IntPtr pShaderBytecode, UIntPtr bytecodeLength, IntPtr pClassLinkage, out IntPtr ppComputeShader);
        [PreserveSig] int CreateClassLinkage(out IntPtr ppLinkage);
        [PreserveSig] int CreateBlendState(IntPtr pBlendStateDesc, out IntPtr ppBlendState);
        [PreserveSig] int CreateDepthStencilState(IntPtr pDepthStencilDesc, out IntPtr ppDepthStencilState);
        [PreserveSig] int CreateRasterizerState(IntPtr pRasterizerDesc, out IntPtr ppRasterizerState);
        [PreserveSig] int CreateSamplerState(IntPtr pSamplerDesc, out IntPtr ppSamplerState);
        [PreserveSig] int CreateQuery(IntPtr pQueryDesc, out IntPtr ppQuery);
        [PreserveSig] int CreatePredicate(IntPtr pPredicateDesc, out IntPtr ppPredicate);
        [PreserveSig] int CreateCounter(IntPtr pCounterDesc, out IntPtr ppCounter);
        [PreserveSig] int CreateDeferredContext(uint contextFlags, out IntPtr ppDeferredContext);
        [PreserveSig] int OpenSharedResource(IntPtr hResource, ref Guid returnedInterface, out IntPtr ppResource);
        [PreserveSig] int CheckFormatSupport(uint format, out uint pFormatSupport);
        [PreserveSig] int CheckMultisampleQualityLevels(uint format, uint sampleCount, out uint pNumQualityLevels);
        [PreserveSig] int CheckCounterInfo(out long pCounterInfo);
        [PreserveSig] int CheckCounter(IntPtr pDesc, IntPtr pName, ref uint pNameLength, IntPtr pUnits, ref uint pUnitsLength, IntPtr pDescription, ref uint pDescriptionLength);
        [PreserveSig] int CheckFeatureSupport(int feature, IntPtr pFeatureSupportData, uint featureSupportDataSize);
        [PreserveSig] int GetFeatureLevel(out int pFeatureLevel);
        [PreserveSig] uint GetCreationFlags();
        [PreserveSig] int GetDeviceRemovedReason();
        [PreserveSig] void GetImmediateContext(out IntPtr ppImmediateContext);
        [PreserveSig] int SetExceptionMode(uint raiseFlags);
        [PreserveSig] uint GetExceptionMode();
    }

    /// <summary>ID3D11DeviceContext（仅用 Map/Unmap/CopyResource/CopySubresourceRegion；vtable 顺序对照 d3d11.h）。</summary>
    [ComImport, Guid("c0bfa96c-e089-44fb-8eaf-26f8796190da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ID3D11DeviceContext
    {
        [PreserveSig] void GetDevice(out IntPtr ppDevice);
        [PreserveSig] int GetPrivateData(ref Guid guid, ref uint pDataSize, IntPtr pData);
        [PreserveSig] int SetPrivateData(ref Guid guid, uint dataSize, IntPtr pData);
        [PreserveSig] int SetPrivateDataInterface(ref Guid guid, IntPtr pData);
        [PreserveSig] void VSSetConstantBuffers(uint startSlot, uint numBuffers, IntPtr ppConstantBuffers);
        [PreserveSig] void PSSetShaderResources(uint startSlot, uint numViews, IntPtr ppShaderResourceViews);
        [PreserveSig] void PSSetShader(IntPtr pPixelShader, IntPtr ppClassInstances, uint numClassInstances);
        [PreserveSig] void PSSetSamplers(uint startSlot, uint numSamplers, IntPtr ppSamplers);
        [PreserveSig] void VSSetShader(IntPtr pVertexShader, IntPtr ppClassInstances, uint numClassInstances);
        [PreserveSig] void DrawIndexed(uint indexCount, uint startIndexLocation, int baseVertexLocation);
        [PreserveSig] void Draw(uint vertexCount, uint startVertexLocation);
        [PreserveSig] int Map(IntPtr pResource, uint subresource, int mapType, uint mapFlags, out D3D11_MAPPED_SUBRESOURCE pMappedResource);
        [PreserveSig] void Unmap(IntPtr pResource, uint subresource);
        [PreserveSig] void PSSetConstantBuffers(uint startSlot, uint numBuffers, IntPtr ppConstantBuffers);
        [PreserveSig] void IASetInputLayout(IntPtr pInputLayout);
        [PreserveSig] void IASetVertexBuffers(uint startSlot, uint numBuffers, IntPtr ppVertexBuffers, IntPtr pStrides, IntPtr pOffsets);
        [PreserveSig] void IASetIndexBuffer(IntPtr pIndexBuffer, uint format, uint offset);
        [PreserveSig] void IASetPrimitiveTopology(int topology);
        [PreserveSig] void VSSetShaderResources(uint startSlot, uint numViews, IntPtr ppShaderResourceViews);
        [PreserveSig] void VSSetSamplers(uint startSlot, uint numSamplers, IntPtr ppSamplers);
        [PreserveSig] void Begin(IntPtr pAsync);
        [PreserveSig] void End(IntPtr pAsync);
        [PreserveSig] int GetData(IntPtr pAsync, IntPtr pData, uint dataSize, uint flags);
        [PreserveSig] void SetPredication(IntPtr pPredicate, int predicateValue);
        [PreserveSig] void GSSetConstantBuffers(uint startSlot, uint numBuffers, IntPtr ppConstantBuffers);
        [PreserveSig] void GSSetShader(IntPtr pShader, IntPtr ppClassInstances, uint numClassInstances);
        [PreserveSig] void GSSetShaderResources(uint startSlot, uint numViews, IntPtr ppShaderResourceViews);
        [PreserveSig] void GSSetSamplers(uint startSlot, uint numSamplers, IntPtr ppSamplers);
        [PreserveSig] void OMSetRenderTargets(uint numViews, IntPtr ppRenderTargetViews, IntPtr pDepthStencilView);
        [PreserveSig] void OMSetRenderTargetsAndUnorderedAccessViews(uint numRTVs, IntPtr ppRenderTargetViews, IntPtr pDepthStencilView, uint uavStartSlot, uint numUAVs, IntPtr ppUnorderedAccessViews, IntPtr pUAVInitialCounts);
        [PreserveSig] void OMSetBlendState(IntPtr pBlendState, IntPtr pBlendFactor, uint sampleMask);
        [PreserveSig] void OMSetDepthStencilState(IntPtr pDepthStencilState, uint stencilRef);
        [PreserveSig] void SOSetTargets(uint numBuffers, IntPtr ppSOTargets, IntPtr pOffsets);
        [PreserveSig] void DrawAuto();
        [PreserveSig] void DrawIndexedInstanced(uint indexCountPerInstance, uint instanceCount, uint startIndexLocation, int baseVertexLocation, uint startInstanceLocation);
        [PreserveSig] void DrawInstanced(uint vertexCountPerInstance, uint instanceCount, uint startVertexLocation, uint startInstanceLocation);
        [PreserveSig] void RSSetState(IntPtr pRasterizerState);
        [PreserveSig] void RSSetViewports(uint numViewports, IntPtr pViewports);
        [PreserveSig] void RSSetScissorRects(uint numRects, IntPtr pRects);
        [PreserveSig] void CopySubresourceRegion(IntPtr pDstResource, uint dstSubresource, uint dstX, uint dstY, uint dstZ, IntPtr pSrcResource, uint srcSubresource, IntPtr pSrcBox);
        [PreserveSig] void CopyResource(IntPtr pDstResource, IntPtr pSrcResource);
    }

    /// <summary>ID3D11Texture2D（仅用 GetDesc；从 IDXGIResource QI 得到）。</summary>
    [ComImport, Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ID3D11Texture2D
    {
        [PreserveSig] void GetDevice(out IntPtr ppDevice);
        [PreserveSig] int GetPrivateData(ref Guid guid, ref uint pDataSize, IntPtr pData);
        [PreserveSig] int SetPrivateData(ref Guid guid, uint dataSize, IntPtr pData);
        [PreserveSig] int SetPrivateDataInterface(ref Guid guid, IntPtr pData);
        [PreserveSig] void GetType(out int pResourceDimension);
        [PreserveSig] void SetEvictionPriority(uint evictionPriority);
        [PreserveSig] uint GetEvictionPriority();
        [PreserveSig] void GetDesc(out D3D11_TEXTURE2D_DESC pDesc);
    }

    /// <summary>IDXGISurface（从 WGC 帧表面 QI 得到；用 GetDesc / Map / Unmap）。</summary>
    [ComImport, Guid("cafcb53c-85c7-4f46-9be4-9a3cae8a2254"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGISurface
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int GetDevice(ref Guid riid, out IntPtr ppDevice);
        [PreserveSig] int GetDesc(out DXGI_SURFACE_DESC pDesc);
        [PreserveSig] int Map(int mapType, out DXGI_MAPPED_RECT pLockedRect);
        [PreserveSig] int Unmap();
    }

    // ---------------- 工具 ----------------

    public const int DXGI_MAP_READ = 1;
    public const int D3D11_MAP_READ = 1;

    /// <summary>对 COM 指针 QueryInterface 指定接口。</summary>
    public static IntPtr QueryInterface(IntPtr unknown, Guid iid)
    {
        Marshal.QueryInterface(unknown, ref iid, out IntPtr result);
        return result;
    }
}
