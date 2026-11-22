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
/// IIDs and vtable slots of the audio control interfaces, counted from the vtbl structs in <c>endpointvolume.h</c>,
/// <c>audiopolicy.h</c>, <c>audioclient.h</c> and <c>mmdeviceapi.h</c>, and of the DirectShow camera controls from
/// <c>strmif.h</c>.
/// </summary>
internal static class AudioIids
{
    public static readonly Guid EndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    public static readonly Guid MeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");

    public static readonly Guid EndpointVolumeCallback = new("657804FA-D6AD-4496-8A60-352752AF4F89");

    public static readonly Guid SessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    public static readonly Guid SessionControl2 = new("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d");

    public static readonly Guid SimpleAudioVolume = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");

    public static readonly Guid NotificationClient = new("7991EEC9-7E89-4D85-8390-6C703CEC60C0");

    public static readonly Guid CameraControl = new("C6E13370-30AC-11d0-A18C-00A0C9118956");

    public static readonly Guid VideoProcAmp = new("C6E13360-30AC-11d0-A18C-00A0C9118956");
}

/// <summary>Vtable slots, each counting every method of every base interface.</summary>
internal static class AudioSlots
{
    // IAudioEndpointVolume
    public const int VolumeRegister = 3;
    public const int VolumeUnregister = 4;
    public const int VolumeChannelCount = 5;
    public const int VolumeSetScalar = 7;
    public const int VolumeGetScalar = 9;
    public const int VolumeSetMute = 14;
    public const int VolumeGetMute = 15;

    // IAudioMeterInformation
    public const int MeterGetPeak = 3;

    // IAudioSessionManager2
    public const int ManagerGetSessionEnumerator = 5;

    // IAudioSessionEnumerator
    public const int EnumeratorGetCount = 3;
    public const int EnumeratorGetSession = 4;

    // IAudioSessionControl2 (IAudioSessionControl's 3-11 first)
    public const int SessionGetState = 3;
    public const int SessionGetDisplayName = 4;
    public const int SessionGetProcessId = 14;
    public const int SessionIsSystemSounds = 15;

    // ISimpleAudioVolume
    public const int SimpleSetVolume = 3;
    public const int SimpleGetVolume = 4;
    public const int SimpleSetMute = 5;
    public const int SimpleGetMute = 6;

    // IMMDeviceEnumerator
    public const int EnumeratorRegisterNotification = 6;
    public const int EnumeratorUnregisterNotification = 7;

    // IAMCameraControl and IAMVideoProcAmp
    public const int CameraGetRange = 3;
    public const int CameraSet = 4;
    public const int CameraGet = 5;
}

/// <summary>
/// The COM objects this library hands to Windows to be called back on: a struct whose first field points to a vtable of
/// <c>[UnmanagedCallersOnly]</c> functions, plus a <see cref="GCHandle"/> back to the managed owner. Nothing may throw
/// into Windows, and the last <c>Release</c> frees the handle and the memory.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class AudioCallbacks
{
    private static readonly void** VolumeVtable = BuildVolumeVtable();

    private static readonly void** NotificationVtable = BuildNotificationVtable();

    [StructLayout(LayoutKind.Sequential)]
    internal struct CallbackObject
    {
        public void** Vtable;

        public nint Owner;

        public int RefCount;

        // The IID this object answers besides IUnknown.
        public Guid Iid;
    }

    public static CallbackObject* NewVolumeCallback(AudioEndpointVolume owner) =>
        New(VolumeVtable, owner, AudioIids.EndpointVolumeCallback);

    public static CallbackObject* NewNotificationClient(AudioDeviceNotifications owner) =>
        New(NotificationVtable, owner, AudioIids.NotificationClient);

    /// <summary>Drops the caller's reference; Windows frees the object when it lets go of its own.</summary>
    public static void ReleaseOwn(CallbackObject* self) => ReleaseCore(self);

    private static CallbackObject* New(void** vtable, object owner, Guid iid)
    {
        var self = (CallbackObject*)NativeMemory.Alloc((nuint)sizeof(CallbackObject));
        self->Vtable = vtable;
        self->Owner = GCHandle.ToIntPtr(GCHandle.Alloc(owner));
        self->RefCount = 1;
        self->Iid = iid;
        return self;
    }

    private static void** BuildVolumeVtable()
    {
        var table = (void**)NativeMemory.Alloc(4, (nuint)sizeof(void*));
        table[0] = (delegate* unmanaged[Stdcall]<CallbackObject*, Guid*, void**, int>)&QueryInterface;
        table[1] = (delegate* unmanaged[Stdcall]<CallbackObject*, uint>)&AddRef;
        table[2] = (delegate* unmanaged[Stdcall]<CallbackObject*, uint>)&Release;
        table[3] = (delegate* unmanaged[Stdcall]<CallbackObject*, byte*, int>)&OnNotify;
        return table;
    }

    private static void** BuildNotificationVtable()
    {
        var table = (void**)NativeMemory.Alloc(8, (nuint)sizeof(void*));
        table[0] = (delegate* unmanaged[Stdcall]<CallbackObject*, Guid*, void**, int>)&QueryInterface;
        table[1] = (delegate* unmanaged[Stdcall]<CallbackObject*, uint>)&AddRef;
        table[2] = (delegate* unmanaged[Stdcall]<CallbackObject*, uint>)&Release;
        table[3] = (delegate* unmanaged[Stdcall]<CallbackObject*, char*, uint, int>)&OnDeviceStateChanged;
        table[4] = (delegate* unmanaged[Stdcall]<CallbackObject*, char*, int>)&OnDeviceAdded;
        table[5] = (delegate* unmanaged[Stdcall]<CallbackObject*, char*, int>)&OnDeviceRemoved;
        table[6] = (delegate* unmanaged[Stdcall]<CallbackObject*, int, int, char*, int>)&OnDefaultDeviceChanged;
        table[7] = (delegate* unmanaged[Stdcall]<CallbackObject*, char*, void*, int>)&OnPropertyValueChanged;
        return table;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(CallbackObject* self, Guid* iid, void** result)
    {
        if (result is null)
        {
            return unchecked((int)0x80004003);
        }

        *result = null;
        if (iid is null || (*iid != Ole32.IUnknown && *iid != self->Iid))
        {
            return HResult.NoInterface;
        }

        Interlocked.Increment(ref self->RefCount);
        *result = self;
        return HResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(CallbackObject* self) => (uint)Interlocked.Increment(ref self->RefCount);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(CallbackObject* self) => ReleaseCore(self);

    private static uint ReleaseCore(CallbackObject* self)
    {
        var remaining = Interlocked.Decrement(ref self->RefCount);
        if (remaining <= 0)
        {
            GCHandle.FromIntPtr(self->Owner).Free();
            NativeMemory.Free(self);
        }

        return (uint)Math.Max(remaining, 0);
    }

    private static T OwnerOf<T>(CallbackObject* self) where T : class => (T)GCHandle.FromIntPtr(self->Owner).Target!;

    // AUDIO_VOLUME_NOTIFICATION_DATA: GUID guidEventContext 0, BOOL bMuted 16, float fMasterVolume 20, UINT nChannels 24.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnNotify(CallbackObject* self, byte* data)
    {
        try
        {
            if (data is not null)
            {
                OwnerOf<AudioEndpointVolume>(self).Notified(*(int*)(data + 16) != 0, *(float*)(data + 20));
            }
        }
        catch (Exception)
        {
            // Nothing may be thrown into Windows.
        }

        return HResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDeviceStateChanged(CallbackObject* self, char* id, uint state) =>
        Notify(self, n => n.StateChanged(new string(id), state));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDeviceAdded(CallbackObject* self, char* id) => Notify(self, n => n.Added(new string(id)));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDeviceRemoved(CallbackObject* self, char* id) => Notify(self, n => n.Removed(new string(id)));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDefaultDeviceChanged(CallbackObject* self, int flow, int role, char* id) =>
        Notify(self, n => n.DefaultChanged((DataFlow)flow, (DeviceRole)role, id is null ? null : new string(id)));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnPropertyValueChanged(CallbackObject* self, char* id, void* key) => HResult.Ok;

    private static int Notify(CallbackObject* self, Action<AudioDeviceNotifications> call)
    {
        try
        {
            call(OwnerOf<AudioDeviceNotifications>(self));
        }
        catch (Exception)
        {
            // Nothing may be thrown into Windows.
        }

        return HResult.Ok;
    }
}
