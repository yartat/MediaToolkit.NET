#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Interop.Com;

namespace MediaToolkitNet.Windows.Native;

/// <summary>
/// <c>ActivateAudioInterfaceAsync</c> and the completion handler it needs, which is a COM object this library
/// implements: a struct whose first field is a pointer to a vtable of four <c>[UnmanagedCallersOnly]</c> functions,
/// the same shape as the DirectShow Sample Grabber callback. The handler must answer <c>IAgileObject</c>, or Windows
/// refuses to call it from its own thread.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class ActivateAudio
{
    // IActivateAudioInterfaceCompletionHandler: IUnknown 0-2, ActivateCompleted 3.
    // IActivateAudioInterfaceAsyncOperation: IUnknown 0-2, GetActivateResult 3.
    private const int OperationGetActivateResult = 3;

    private const int SlotCount = 4;

    private static readonly Guid IidCompletionHandler = new("41D949AB-9862-444A-80F6-C261334DA5EB");

    private static readonly Guid IidAgileObject = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");

    // One allocation for the process: every handler object shares it.
    private static readonly void** Vtable = BuildVtable();

    [StructLayout(LayoutKind.Sequential)]
    private struct Handler
    {
        public void** Vtable;

        public nint Done;

        public int RefCount;
    }

    [LibraryImport("mmdevapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ActivateAudioInterfaceAsync(
        string deviceInterfacePath, Guid* iid, void* activationParams, void* completionHandler, void** operation);

    /// <summary>
    /// Activates an interface of a virtual audio device and waits for Windows to finish, which it does on another
    /// thread. Returns the interface, or throws with the HRESULT the activation ended with.
    /// </summary>
    /// <param name="devicePath">For example <c>VAD\Process_Loopback</c>.</param>
    /// <param name="iid">The interface wanted.</param>
    /// <param name="propVariant">A <c>PROPVARIANT</c> holding the activation parameters.</param>
    /// <param name="timeout">How long to wait.</param>
    public static ComPtr Activate(string devicePath, in Guid iid, void* propVariant, TimeSpan timeout)
    {
        // Not disposed: Windows may still be inside Set when the wait returns. The handle that holds it goes with the handler.
        var done = new ManualResetEventSlim();
        var self = (Handler*)NativeMemory.Alloc((nuint)sizeof(Handler));
        var doneHandle = GCHandle.Alloc(done);
        self->Vtable = Vtable;
        self->Done = GCHandle.ToIntPtr(doneHandle);
        self->RefCount = 1;

        void* operation = null;
        try
        {
            fixed (Guid* pIid = &iid)
            {
                HResult.ThrowIfFailed(
                    ActivateAudioInterfaceAsync(devicePath, pIid, propVariant, self, &operation), "ActivateAudioInterfaceAsync");
            }

            if (!done.Wait(timeout))
            {
                throw new TimeoutException($"Windows did not finish activating {devicePath} within {timeout}.");
            }

            int activation;
            void* result;
            HResult.ThrowIfFailed(
                ((delegate* unmanaged[Stdcall]<void*, int*, void**, int>)Com.Slot(operation, OperationGetActivateResult))(
                    operation, &activation, &result),
                "IActivateAudioInterfaceAsyncOperation::GetActivateResult");
            HResult.ThrowIfFailed(activation, $"the activation of {devicePath}");
            return new ComPtr(result);
        }
        finally
        {
            if (operation is not null)
            {
                Com.Release(operation);
            }

            // Windows may still hold a reference for a moment; the last Release frees the object.
            ReleaseHandler(self);
        }
    }

    private static void** BuildVtable()
    {
        var table = (void**)NativeMemory.Alloc(SlotCount, (nuint)sizeof(void*));
        table[0] = (delegate* unmanaged[Stdcall]<Handler*, Guid*, void**, int>)&QueryInterface;
        table[1] = (delegate* unmanaged[Stdcall]<Handler*, uint>)&AddRef;
        table[2] = (delegate* unmanaged[Stdcall]<Handler*, uint>)&Release;
        table[3] = (delegate* unmanaged[Stdcall]<Handler*, void*, int>)&ActivateCompleted;
        return table;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(Handler* self, Guid* iid, void** result)
    {
        if (result is null)
        {
            return unchecked((int)0x80004003);
        }

        *result = null;
        if (iid is null || (*iid != Ole32.IUnknown && *iid != IidCompletionHandler && *iid != IidAgileObject))
        {
            return HResult.NoInterface;
        }

        Interlocked.Increment(ref self->RefCount);
        *result = self;
        return HResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(Handler* self) => (uint)Interlocked.Increment(ref self->RefCount);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(Handler* self) => ReleaseHandler(self);

    private static uint ReleaseHandler(Handler* self)
    {
        var remaining = Interlocked.Decrement(ref self->RefCount);
        if (remaining <= 0)
        {
            GCHandle.FromIntPtr(self->Done).Free();
            NativeMemory.Free(self);
        }

        return (uint)Math.Max(remaining, 0);
    }

    // Nothing may throw into Windows. The result is read afterwards, from the operation, by the waiting thread.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ActivateCompleted(Handler* self, void* operation)
    {
        try
        {
            ((ManualResetEventSlim)GCHandle.FromIntPtr(self->Done).Target!).Set();
            return HResult.Ok;
        }
        catch (Exception)
        {
            return unchecked((int)0x80004005);
        }
    }
}
