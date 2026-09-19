using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Interop.Com;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows.DirectShow;

/// <summary>
/// The native object passed to <c>ISampleGrabber::SetCallback</c>.
/// </summary>
/// <remarks>
/// This is the one place in the repository that implements a COM interface
/// rather than calling one. The layout is what a COM object looks like in
/// memory: a pointer to the vtable first, then whatever state the object keeps.
/// The vtable is shared by every instance and the state is one
/// <see cref="GCHandle"/> back to the managed grabber.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SampleGrabberCallback
{
    /// <summary>Pointer to the shared vtable. Must stay the first field.</summary>
    public void** Vtable;

    /// <summary>A <see cref="GCHandle"/> to the owning <see cref="DirectShowSampleGrabber"/>.</summary>
    public nint Owner;

    /// <summary>The COM reference count.</summary>
    public int RefCount;
}

/// <summary>
/// Builds <see cref="SampleGrabberCallback"/> instances and holds the vtable
/// entry points.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class SampleGrabberCallbackFactory
{
    /// <summary>E_NOTIMPL.</summary>
    private const int NotImplemented = unchecked((int)0x80004001);

    /// <summary>E_POINTER.</summary>
    private const int NullPointer = unchecked((int)0x80004003);

    /// <summary>E_FAIL.</summary>
    private const int Fail = unchecked((int)0x80004005);

    /// <summary>
    /// ISampleGrabberCB derives straight from IUnknown, so its own two methods
    /// take slots 3 and 4.
    /// </summary>
    private const int SlotCount = 5;

    // One allocation for the process: every callback object shares it.
    private static readonly void** Vtable = BuildVtable();

    /// <summary>
    /// Creates a callback object holding a reference to <paramref name="owner"/>.
    /// It starts with one reference, which the caller owns.
    /// </summary>
    public static SampleGrabberCallback* Create(DirectShowSampleGrabber owner)
    {
        var self = (SampleGrabberCallback*)NativeMemory.Alloc((nuint)sizeof(SampleGrabberCallback));
        self->Vtable = Vtable;
        self->Owner = GCHandle.ToIntPtr(GCHandle.Alloc(owner));
        self->RefCount = 1;
        return self;
    }

    private static void** BuildVtable()
    {
        var table = (void**)NativeMemory.Alloc(SlotCount, (nuint)sizeof(void*));
        table[0] = (delegate* unmanaged[Stdcall]<SampleGrabberCallback*, Guid*, void**, int>)&QueryInterface;
        table[1] = (delegate* unmanaged[Stdcall]<SampleGrabberCallback*, uint>)&AddRef;
        table[2] = (delegate* unmanaged[Stdcall]<SampleGrabberCallback*, uint>)&Release;
        table[3] = (delegate* unmanaged[Stdcall]<SampleGrabberCallback*, double, void*, int>)&SampleCb;
        table[4] = (delegate* unmanaged[Stdcall]<SampleGrabberCallback*, double, byte*, int, int>)&BufferCb;
        return table;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(SampleGrabberCallback* self, Guid* iid, void** result)
    {
        if (result is null)
        {
            return NullPointer;
        }

        *result = null;
        if (iid is null)
        {
            return NullPointer;
        }

        if (*iid != Ole32.IUnknown && *iid != DsGuids.ISampleGrabberCB)
        {
            return HResult.NoInterface;
        }

        Interlocked.Increment(ref self->RefCount);
        *result = self;
        return HResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(SampleGrabberCallback* self) =>
        (uint)Interlocked.Increment(ref self->RefCount);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(SampleGrabberCallback* self)
    {
        var remaining = Interlocked.Decrement(ref self->RefCount);
        if (remaining > 0)
        {
            return (uint)remaining;
        }

        GCHandle.FromIntPtr(self->Owner).Free();
        NativeMemory.Free(self);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SampleCb(SampleGrabberCallback* self, double sampleTime, void* sample)
    {
        // Nothing may throw across this boundary: the caller is the DirectShow
        // streaming thread, and an escaping managed exception ends the process.
        try
        {
            if (sample is not null && Target(self) is { } owner)
            {
                owner.Deliver(sampleTime, sample);
            }

            return HResult.Ok;
        }
        catch
        {
            return Fail;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int BufferCb(SampleGrabberCallback* self, double sampleTime, byte* buffer, int length)
    {
        // The grabber is always registered for SampleCB, which carries the
        // sample itself and therefore its timestamps and any format change.
        _ = self;
        _ = sampleTime;
        _ = buffer;
        _ = length;
        return NotImplemented;
    }

    private static DirectShowSampleGrabber? Target(SampleGrabberCallback* self) =>
        self is null || self->Owner == 0
            ? null
            : GCHandle.FromIntPtr(self->Owner).Target as DirectShowSampleGrabber;
}
