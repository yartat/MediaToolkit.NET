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
/// The Media Foundation Media Engine as far as the player needs it. Slots are counted from the vtbl structs of
/// <c>mfmediaengine.h</c>, <c>mfobjects.h</c> and <c>d3d10.h</c>, the GUIDs are those headers' own.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class MediaEngine
{
    public static readonly Guid ClassFactoryClsid = new("b44392da-499b-446b-a4cb-005fead0e6d5");
    public static readonly Guid IidClassFactory = new("4D645ACE-26AA-4688-9BE1-DF3516990B93");
    public static readonly Guid IidNotify = new("fee7c112-e776-42b5-9bbf-0048524e2bd5");
    public static readonly Guid IidMultithread = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");

    public static readonly Guid AttributeCallback = new("c60381b8-83a4-41f8-a3d0-de05076849a9");
    public static readonly Guid AttributeDxgiManager = new("065702da-1094-486d-8617-ee7cc4ee4648");
    public static readonly Guid AttributeVideoOutputFormat = new("5066893c-8cf9-42bc-8b8a-472212e52726");

    // IMFMediaEngineClassFactory: CreateInstance 3.
    public const int FactoryCreateInstance = 3;

    // IMFMediaEngine (IUnknown 0-2).
    public const int SetSource = 6;
    public const int Load = 12;
    public const int GetCurrentTime = 16;
    public const int SetCurrentTime = 17;
    public const int GetDuration = 19;
    public const int Play = 32;
    public const int Pause = 33;
    public const int SetVolume = 37;
    public const int HasVideo = 38;
    public const int GetNativeVideoSize = 40;
    public const int Shutdown = 42;
    public const int TransferVideoFrame = 43;
    public const int OnVideoStreamTick = 44;

    // IMFDXGIDeviceManager: ResetDevice 7. ID3D10Multithread: SetMultithreadProtected 5.
    public const int ManagerResetDevice = 7;
    public const int MultithreadSetProtected = 5;

    // MF_MEDIA_ENGINE_EVENT.
    public const uint EventError = 5;
    public const uint EventLoadedMetadata = 10;
    public const uint EventSeeked = 17;
    public const uint EventEnded = 19;

    // D3D_DRIVER_TYPE_HARDWARE and _WARP; D3D11_CREATE_DEVICE_VIDEO_SUPPORT; D3D11_BIND_SHADER_RESOURCE | RENDER_TARGET.
    public const int DriverTypeHardware = 1;
    public const int DriverTypeWarp = 5;
    public const uint CreateDeviceVideoSupport = 0x800;
    public const uint BindShaderResourceAndRenderTarget = 0x8 | 0x20;

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateDXGIDeviceManager(uint* resetToken, void** manager);

    /// <summary>
    /// The engine's callback, a COM object this library implements: a struct whose first field is a vtable of four
    /// <c>[UnmanagedCallersOnly]</c> functions, as in the process loopback activation handler. The handle to the managed
    /// receiver goes when the last reference does.
    /// </summary>
    public static class Notify
    {
        private static readonly void** Vtable = BuildVtable();

        [StructLayout(LayoutKind.Sequential)]
        private struct Instance
        {
            public void** Vtable;

            public nint Receiver;

            public int RefCount;
        }

        /// <summary>A new callback object with one reference, which <see cref="Com.Release"/> gives up.</summary>
        public static void* Create(Action<uint, nuint, uint> receiver)
        {
            var self = (Instance*)NativeMemory.Alloc((nuint)sizeof(Instance));
            self->Vtable = Vtable;
            self->Receiver = GCHandle.ToIntPtr(GCHandle.Alloc(receiver));
            self->RefCount = 1;
            return self;
        }

        private static void** BuildVtable()
        {
            var table = (void**)NativeMemory.Alloc(4, (nuint)sizeof(void*));
            table[0] = (delegate* unmanaged[Stdcall]<Instance*, Guid*, void**, int>)&QueryInterface;
            table[1] = (delegate* unmanaged[Stdcall]<Instance*, uint>)&AddRef;
            table[2] = (delegate* unmanaged[Stdcall]<Instance*, uint>)&Release;
            table[3] = (delegate* unmanaged[Stdcall]<Instance*, uint, nuint, uint, int>)&EventNotify;
            return table;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int QueryInterface(Instance* self, Guid* iid, void** result)
        {
            if (result is null)
            {
                return unchecked((int)0x80004003);
            }

            *result = null;
            if (iid is null || (*iid != Ole32.IUnknown && *iid != IidNotify))
            {
                return HResult.NoInterface;
            }

            Interlocked.Increment(ref self->RefCount);
            *result = self;
            return HResult.Ok;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint AddRef(Instance* self) => (uint)Interlocked.Increment(ref self->RefCount);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint Release(Instance* self)
        {
            var remaining = Interlocked.Decrement(ref self->RefCount);
            if (remaining == 0)
            {
                GCHandle.FromIntPtr(self->Receiver).Free();
                NativeMemory.Free(self);
            }

            return (uint)Math.Max(remaining, 0);
        }

        // Nothing may throw into Media Foundation.
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int EventNotify(Instance* self, uint mediaEvent, nuint param1, uint param2)
        {
            try
            {
                ((Action<uint, nuint, uint>)GCHandle.FromIntPtr(self->Receiver).Target!)(mediaEvent, param1, param2);
                return HResult.Ok;
            }
            catch (Exception)
            {
                return unchecked((int)0x80004005);
            }
        }
    }
}
