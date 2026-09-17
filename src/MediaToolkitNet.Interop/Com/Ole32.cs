using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MediaToolkitNet.Interop.Com;

/// <summary>Apartment model requested from <c>CoInitializeEx</c>.</summary>
[Flags]
public enum ComApartment : uint
{
    /// <summary>Single-threaded apartment (COINIT_APARTMENTTHREADED).</summary>
    SingleThreaded = 0x2,

    /// <summary>Multi-threaded apartment (COINIT_MULTITHREADED).</summary>
    MultiThreaded = 0x0,

    /// <summary>COINIT_DISABLE_OLE1DDE.</summary>
    DisableOle1Dde = 0x4,
}

/// <summary>Minimal ole32 bindings needed to bootstrap the Windows backends.</summary>
[SupportedOSPlatform("windows")]
public static unsafe partial class Ole32
{
    private const string Library = "ole32.dll";

    /// <summary>CLSCTX_INPROC_SERVER.</summary>
    public const uint ClsCtxInprocServer = 0x1;

    /// <summary>CLSCTX_INPROC_HANDLER | CLSCTX_LOCAL_SERVER | CLSCTX_INPROC_SERVER.</summary>
    public const uint ClsCtxAll = 0x17;

    /// <summary>IID_IUnknown.</summary>
    public static readonly Guid IUnknown = new("00000000-0000-0000-C000-000000000046");

    [LibraryImport(Library)]
    private static partial int CoInitializeEx(void* reserved, uint flags);

    [LibraryImport(Library)]
    private static partial void CoUninitialize();

    [LibraryImport(Library)]
    private static partial int CoCreateInstance(Guid* rclsid, void* outer, uint clsContext, Guid* riid, void** result);

    /// <summary>Frees memory allocated by the COM task allocator.</summary>
    [LibraryImport(Library)]
    public static partial void CoTaskMemFree(void* pointer);

    /// <summary>Releases the contents of a PROPVARIANT. Exported by ole32, not oleaut32.</summary>
    [LibraryImport(Library)]
    public static partial int PropVariantClear(void* propVariant);

    /// <summary>
    /// Initialises COM on the calling thread.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when this call performed the initialisation and the
    /// caller owns the matching <see cref="Uninitialize"/>; <see langword="false"/>
    /// when COM was already initialised on the thread.
    /// </returns>
    public static bool Initialize(ComApartment apartment = ComApartment.MultiThreaded)
    {
        var hr = CoInitializeEx(null, (uint)apartment);
        if (hr == HResult.ChangedMode || hr == HResult.False)
        {
            // Already initialised, possibly with a different apartment model.
            return false;
        }

        HResult.ThrowIfFailed(hr, "CoInitializeEx");
        return true;
    }

    /// <summary>Balances a successful <see cref="Initialize"/>.</summary>
    public static void Uninitialize() => CoUninitialize();

    /// <summary>Creates an in-process COM object and returns an owning handle to it.</summary>
    public static ComPtr CreateInstance(in Guid clsid, in Guid iid, uint clsContext = ClsCtxInprocServer)
    {
        void* result;
        int hr;
        fixed (Guid* pClsid = &clsid)
        fixed (Guid* pIid = &iid)
        {
            hr = CoCreateInstance(pClsid, null, clsContext, pIid, &result);
        }

        HResult.ThrowIfFailed(hr, $"CoCreateInstance({clsid:B})");
        return new ComPtr(result);
    }
}
