#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Interop.Com;

namespace MediaToolkitNet.Windows.Native;

/// <summary>
/// Windows.Graphics.Capture reached without a WinRT projection: the runtime classes come from
/// <c>RoGetActivationFactory</c> and every interface is called through its vtable. The slot numbers count the three
/// IUnknown methods and the three IInspectable ones (<c>GetIids</c>, <c>GetRuntimeClassName</c>, <c>GetTrustLevel</c>)
/// before the interface's own, from the vtbl structs of <c>windows.graphics.capture.h</c> in the Windows SDK; the
/// IIDs are from <c>windows.graphics.capture.idl</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public static unsafe partial class Wgc
{
    /// <summary><c>DirectXPixelFormat::B8G8R8A8UIntNormalized</c>.</summary>
    public const int PixelFormatB8G8R8A8 = 87;

    // IGraphicsCaptureItem: get_DisplayName 6, get_Size 7.
    internal const int ItemGetDisplayName = 6;

    internal const int ItemGetSize = 7;

    // IGraphicsCaptureItemInterop : IUnknown (Windows.Graphics.Capture.Interop.h): CreateForWindow 3, CreateForMonitor 4.
    internal const int InteropCreateForWindow = 3;

    internal const int InteropCreateForMonitor = 4;

    // IDirect3D11CaptureFramePoolStatics2: CreateFreeThreaded 6.
    internal const int StaticsCreateFreeThreaded = 6;

    // IDirect3D11CaptureFramePool: TryGetNextFrame 7, CreateCaptureSession 10 (Recreate is 6; it is not used, see GraphicsCapture).
    internal const int PoolTryGetNextFrame = 7;

    internal const int PoolCreateCaptureSession = 10;

    // IGraphicsCaptureSession: StartCapture 6. IGraphicsCaptureSession2: put_IsCursorCaptureEnabled 7.
    // IGraphicsCaptureSession3: put_IsBorderRequired 7.
    internal const int SessionStartCapture = 6;

    internal const int Session2PutCursor = 7;

    internal const int Session3PutBorder = 7;

    // IDirect3D11CaptureFrame: get_Surface 6, get_SystemRelativeTime 7, get_ContentSize 8.
    internal const int FrameGetSurface = 6;

    internal const int FrameGetSystemRelativeTime = 7;

    internal const int FrameGetContentSize = 8;

    // IDirect3DDxgiInterfaceAccess : IUnknown: GetInterface 3. IClosable: Close 6.
    internal const int DxgiAccessGetInterface = 3;

    internal const int ClosableClose = 6;

    /// <summary>Runtime class <c>Windows.Graphics.Capture.GraphicsCaptureItem</c>, which serves the interop interface.</summary>
    public const string ItemClass = "Windows.Graphics.Capture.GraphicsCaptureItem";

    /// <summary>Runtime class <c>Windows.Graphics.Capture.Direct3D11CaptureFramePool</c>.</summary>
    public const string FramePoolClass = "Windows.Graphics.Capture.Direct3D11CaptureFramePool";

    /// <summary><c>IGraphicsCaptureItemInterop</c></summary>
    public static readonly Guid IidItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    /// <summary><c>IGraphicsCaptureItem</c></summary>
    public static readonly Guid IidItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary><c>IDirect3D11CaptureFramePoolStatics2</c></summary>
    public static readonly Guid IidFramePoolStatics2 = new("589B103F-6BBC-5DF5-A991-02E28B3B66D5");

    /// <summary><c>IDirect3D11CaptureFramePool</c></summary>
    public static readonly Guid IidFramePool = new("24EB6D22-1975-422E-82E7-780DBD8DDF24");

    /// <summary><c>IGraphicsCaptureSession</c></summary>
    public static readonly Guid IidSession = new("814E42A9-F70F-4AD7-939B-FDDCC6EB880D");

    /// <summary><c>IGraphicsCaptureSession2</c>, which has the cursor switch.</summary>
    public static readonly Guid IidSession2 = new("2C39AE40-7D2E-5044-804E-8B6799D4CF9E");

    /// <summary><c>IGraphicsCaptureSession3</c>, which has the border switch.</summary>
    public static readonly Guid IidSession3 = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");

    /// <summary><c>IDirect3D11CaptureFrame</c></summary>
    public static readonly Guid IidFrame = new("FA50C623-38DA-4B32-ACF3-FA9734AD800E");

    /// <summary><c>IDirect3DDxgiInterfaceAccess</c></summary>
    public static readonly Guid IidDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    /// <summary><c>IClosable</c></summary>
    public static readonly Guid IidClosable = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");

    /// <summary><c>IDXGIDevice</c></summary>
    public static readonly Guid IidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    /// <summary>The activation factory of a runtime class, asked for the interface <paramref name="iid"/>.</summary>
    public static ComPtr GetActivationFactory(string className, in Guid iid)
    {
        nint hstring;
        fixed (char* name = className)
        {
            HResult.ThrowIfFailed(WindowsCreateString(name, (uint)className.Length, &hstring), "WindowsCreateString");
        }

        try
        {
            void* factory;
            fixed (Guid* pIid = &iid)
            {
                HResult.ThrowIfFailed(RoGetActivationFactory(hstring, pIid, &factory), $"RoGetActivationFactory({className})");
            }

            return new ComPtr(factory);
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    /// <summary>Copies an HSTRING out and frees it.</summary>
    public static string TakeString(nint hstring)
    {
        if (hstring == 0)
        {
            return string.Empty;
        }

        try
        {
            uint length;
            var buffer = WindowsGetStringRawBuffer(hstring, &length);
            return new string(buffer, 0, (int)length);
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    /// <summary>IClosable::Close on an object that has it, which releases what it holds at once.</summary>
    public static void Close(void* inspectable)
    {
        if (inspectable is null || HResult.Failed(Com.QueryInterface(inspectable, IidClosable, out var closable)))
        {
            return;
        }

        ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(closable, ClosableClose))(closable);
        Com.Release(closable);
    }

    [LibraryImport("combase.dll")]
    private static partial int WindowsCreateString(char* source, uint length, nint* hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    private static partial char* WindowsGetStringRawBuffer(nint hstring, uint* length);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint classId, Guid* iid, void** factory);

    /// <summary>Wraps an <c>IDXGIDevice</c> as the <c>IDirect3DDevice</c> the frame pool takes (an IInspectable).</summary>
    [LibraryImport("d3d11.dll")]
    public static partial int CreateDirect3D11DeviceFromDXGIDevice(void* dxgiDevice, void** direct3DDevice);

    /// <summary><c>BOOL IsWindow(HWND)</c></summary>
    [LibraryImport("user32.dll")]
    public static partial int IsWindow(nint hwnd);

    /// <summary><c>BOOL IsWindowVisible(HWND)</c></summary>
    [LibraryImport("user32.dll")]
    public static partial int IsWindowVisible(nint hwnd);

    /// <summary><c>BOOL IsIconic(HWND)</c>, true for a minimised window.</summary>
    [LibraryImport("user32.dll")]
    public static partial int IsIconic(nint hwnd);

    /// <summary><c>BOOL EnumWindows(WNDENUMPROC, LPARAM)</c></summary>
    [LibraryImport("user32.dll")]
    public static partial int EnumWindows(delegate* unmanaged[Stdcall]<nint, nint, int> callback, nint lParam);

    /// <summary><c>int GetWindowTextW(HWND, LPWSTR, int)</c></summary>
    [LibraryImport("user32.dll")]
    public static partial int GetWindowTextW(nint hwnd, char* text, int max);

    /// <summary><c>DWORD GetWindowThreadProcessId(HWND, LPDWORD)</c></summary>
    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, uint* processId);

    /// <summary><c>LONG GetWindowLongW(HWND, int)</c></summary>
    [LibraryImport("user32.dll")]
    public static partial int GetWindowLongW(nint hwnd, int index);

    /// <summary><c>BOOL GetWindowRect(HWND, RECT*)</c></summary>
    [LibraryImport("user32.dll")]
    public static partial int GetWindowRect(nint hwnd, int* rect);

    /// <summary><c>HRESULT DwmGetWindowAttribute(HWND, DWORD, PVOID, DWORD)</c>; attribute 14 is <c>DWMWA_CLOAKED</c>.</summary>
    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint hwnd, uint attribute, int* value, uint size);
}
