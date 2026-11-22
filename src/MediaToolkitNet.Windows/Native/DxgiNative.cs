#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MediaToolkitNet.Windows.Native;

/// <summary>
/// Entry points and vtable slots of DXGI and Direct3D 11 as far as Desktop Duplication needs them. The slot numbers
/// count every method of every base interface, from the vtbl structs in <c>dxgi.h</c>, <c>dxgi1_2.h</c> and
/// <c>d3d11.h</c> of the Windows SDK.
/// </summary>
[SupportedOSPlatform("windows")]
public static unsafe partial class Dxgi
{
    /// <summary><c>DXGI_ERROR_WAIT_TIMEOUT</c>: no new frame within the time given.</summary>
    public const int ErrorWaitTimeout = unchecked((int)0x887A0027);

    /// <summary><c>DXGI_ERROR_ACCESS_LOST</c>: the duplication is no longer valid (a mode change, the secure desktop).</summary>
    public const int ErrorAccessLost = unchecked((int)0x887A0026);

    /// <summary><c>DXGI_ERROR_NOT_FOUND</c>: the end of an enumeration.</summary>
    public const int ErrorNotFound = unchecked((int)0x887A0002);

    /// <summary><c>DXGI_ERROR_NOT_CURRENTLY_AVAILABLE</c>: too many duplications of the output, or no support for it.</summary>
    public const int ErrorNotCurrentlyAvailable = unchecked((int)0x887A0022);

    /// <summary><c>DXGI_FORMAT_B8G8R8A8_UNORM</c>.</summary>
    public const uint FormatB8G8R8A8 = 87;

    /// <summary><c>D3D11_USAGE_STAGING</c>.</summary>
    public const uint UsageStaging = 3;

    /// <summary><c>D3D11_CPU_ACCESS_READ</c>.</summary>
    public const uint CpuAccessRead = 0x20000;

    /// <summary><c>D3D11_MAP_READ</c>.</summary>
    public const uint MapRead = 1;

    /// <summary><c>D3D11_CREATE_DEVICE_BGRA_SUPPORT</c>.</summary>
    public const uint CreateDeviceBgraSupport = 0x20;

    /// <summary><c>D3D11_SDK_VERSION</c>.</summary>
    public const uint SdkVersion = 7;

    /// <summary><c>D3D_DRIVER_TYPE_UNKNOWN</c>: the adapter is given.</summary>
    public const int DriverTypeUnknown = 0;

    // IDXGIObject: 3-6. IDXGIFactory: EnumAdapters 7 ... CreateSoftwareAdapter 11. IDXGIFactory1: EnumAdapters1 12.
    internal const int FactoryEnumAdapters1 = 12;

    // IDXGIAdapter (and IDXGIAdapter1, which only adds to it): EnumOutputs 7.
    internal const int AdapterEnumOutputs = 7;

    // IDXGIObject 3-6, IDXGIDeviceSubObject GetDevice 7, IDXGIResource: GetSharedHandle 8 ... (not used).
    // IDXGIOutput: GetDesc 7. IDXGIOutput1 adds to the end of IDXGIOutput: DuplicateOutput 22.
    internal const int OutputGetDesc = 7;

    internal const int Output1DuplicateOutput = 22;

    // IDXGIOutputDuplication: GetDesc 7, AcquireNextFrame 8, ReleaseFrame 14.
    internal const int DuplicationGetDesc = 7;

    internal const int DuplicationAcquireNextFrame = 8;

    internal const int DuplicationReleaseFrame = 14;

    // ID3D11Device: CreateTexture2D 5. ID3D11DeviceContext: Map 14, Unmap 15, CopyResource 47.
    internal const int DeviceCreateTexture2D = 5;

    internal const int ContextMap = 14;

    internal const int ContextUnmap = 15;

    internal const int ContextCopyResource = 47;

    // ID3D11Texture2D: ID3D11Resource GetType 7 ... GetEvictionPriority 9, GetDesc 10.
    internal const int Texture2DGetDesc = 10;

    /// <summary><c>IDXGIFactory1</c></summary>
    public static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    /// <summary><c>IDXGIOutput1</c></summary>
    public static readonly Guid IidOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");

    /// <summary><c>ID3D11Texture2D</c></summary>
    public static readonly Guid IidTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    /// <summary><c>BOOL EnumDisplaySettingsW(LPCWSTR deviceName, DWORD modeNum, DEVMODEW *)</c>; <c>modeNum</c> -1 is the current mode.</summary>
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int EnumDisplaySettingsW(string? deviceName, int modeNum, byte* devMode);

    [LibraryImport("dxgi.dll")]
    public static partial int CreateDXGIFactory1(Guid* riid, void** factory);

    [LibraryImport("d3d11.dll")]
    public static partial int D3D11CreateDevice(
        void* adapter,
        int driverType,
        nint software,
        uint flags,
        int* featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        void** device,
        int* featureLevel,
        void** immediateContext);
}
