using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Core;

namespace MediaToolkitNet.Interop.Com;

/// <summary>
/// Raw COM access through vtable slots.
/// </summary>
/// <remarks>
/// The Windows backend deliberately avoids <c>[ComImport]</c> and the built-in
/// COM marshaller: every call goes through a function pointer read out of the
/// object vtable. That keeps the interop AOT- and trim-safe and makes the exact
/// calling convention visible at the call site.
/// </remarks>
[SupportedOSPlatform("windows")]
public static unsafe class Com
{
    /// <summary>Vtable slot of <c>IUnknown::QueryInterface</c>.</summary>
    public const int SlotQueryInterface = 0;

    /// <summary>Vtable slot of <c>IUnknown::AddRef</c>.</summary>
    public const int SlotAddRef = 1;

    /// <summary>Vtable slot of <c>IUnknown::Release</c>.</summary>
    public const int SlotRelease = 2;

    /// <summary>
    /// Returns the function pointer stored in vtable slot <paramref name="index"/>
    /// of the object pointed to by <paramref name="self"/>.
    /// </summary>
    public static void* Slot(void* self, int index) => (*(void***)self)[index];

    /// <summary>Calls <c>IUnknown::AddRef</c>.</summary>
    public static uint AddRef(void* self) =>
        ((delegate* unmanaged[Stdcall]<void*, uint>)Slot(self, SlotAddRef))(self);

    /// <summary>Calls <c>IUnknown::Release</c>.</summary>
    public static uint Release(void* self) =>
        ((delegate* unmanaged[Stdcall]<void*, uint>)Slot(self, SlotRelease))(self);

    /// <summary>Calls <c>IUnknown::QueryInterface</c>.</summary>
    public static int QueryInterface(void* self, in Guid iid, out void* result)
    {
        fixed (Guid* pIid = &iid)
        fixed (void** pResult = &result)
        {
            return ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Slot(self, SlotQueryInterface))(
                self, pIid, pResult);
        }
    }

    /// <summary>Releases the pointer and sets it to null. Safe to call on null.</summary>
    public static void SafeRelease(ref void* self)
    {
        if (self is not null)
        {
            Release(self);
            self = null;
        }
    }
}

/// <summary>
/// Owning handle to a COM object. Disposing it calls <c>IUnknown::Release</c> once.
/// </summary>
[SupportedOSPlatform("windows")]
public unsafe struct ComPtr : IDisposable
{
    private void* _pointer;

    /// <summary>Takes ownership of an already-referenced pointer.</summary>
    public ComPtr(void* pointer) => _pointer = pointer;

    /// <summary>The raw interface pointer.</summary>
    public readonly void* Pointer => _pointer;

    /// <summary>True when the handle holds no object.</summary>
    public readonly bool IsNull => _pointer is null;

    /// <summary>Address of the internal pointer, for passing as an out parameter to a native call.</summary>
    [System.Diagnostics.CodeAnalysis.UnscopedRef]
    public ref void* Ref => ref _pointer;

    /// <summary>Queries for another interface and returns an owning handle to it.</summary>
    /// <exception cref="ComException">The object does not implement the interface.</exception>
    public readonly ComPtr QueryInterface(in Guid iid)
    {
        var hr = Com.QueryInterface(_pointer, iid, out var result);
        HResult.ThrowIfFailed(hr, $"QueryInterface({iid:B})");
        return new ComPtr(result);
    }

    /// <summary>Releases the object and clears the handle.</summary>
    public void Dispose() => Com.SafeRelease(ref _pointer);
}

/// <summary>HRESULT helpers.</summary>
[SupportedOSPlatform("windows")]
public static class HResult
{
    /// <summary>S_OK.</summary>
    public const int Ok = 0;

    /// <summary>S_FALSE.</summary>
    public const int False = 1;

    /// <summary>E_NOINTERFACE.</summary>
    public const int NoInterface = unchecked((int)0x80004002);

    /// <summary>RPC_E_CHANGED_MODE, returned when COM was already initialised with another apartment model.</summary>
    public const int ChangedMode = unchecked((int)0x80010106);

    /// <summary>MF_E_NO_MORE_TYPES, returned when a media type enumeration is exhausted.</summary>
    public const int NoMoreTypes = unchecked((int)0xC00D36B9);

    /// <summary>True when the HRESULT indicates success.</summary>
    public static bool Succeeded(int hr) => hr >= 0;

    /// <summary>True when the HRESULT indicates failure.</summary>
    public static bool Failed(int hr) => hr < 0;

    /// <summary>Throws a <see cref="ComException"/> when <paramref name="hr"/> indicates failure.</summary>
    public static void ThrowIfFailed(int hr, string what)
    {
        if (Failed(hr))
        {
            throw new ComException(what, hr);
        }
    }
}

/// <summary>A failed HRESULT, with the Windows description attached when one exists.</summary>
[SupportedOSPlatform("windows")]
public sealed class ComException : MediaToolkitNetException
{
    /// <summary>Creates the exception for a failed call.</summary>
    public ComException(string what, int hr)
        : base("windows", $"{what} failed: {Describe(hr)}", hr)
    {
        HResultCode = hr;
    }

    /// <summary>The raw HRESULT.</summary>
    public int HResultCode { get; }

    private static string Describe(int hr)
    {
        var text = Marshal.GetPInvokeErrorMessage(hr);
        return string.IsNullOrWhiteSpace(text) ? $"0x{hr:X8}" : $"{text.TrimEnd()} (0x{hr:X8})";
    }
}
