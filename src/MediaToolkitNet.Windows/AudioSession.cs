#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions.Devices;
using MediaToolkitNet.Interop.Com;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows;

/// <summary>Whether a session is playing.</summary>
public enum AudioSessionState
{
    /// <summary>Open but silent.</summary>
    Inactive = 0,

    /// <summary>Playing.</summary>
    Active = 1,

    /// <summary>Closed.</summary>
    Expired = 2,
}

/// <summary>
/// One program's audio session on an endpoint, the entry the Windows volume mixer shows: its own volume and mute
/// switch, which move only that program, and its own meter.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class AudioSession : IDisposable
{
    private ComPtr _control;
    private ComPtr _volume;
    private ComPtr _meter;
    private bool _disposed;

    private AudioSession(ComPtr control, ComPtr volume, ComPtr meter)
    {
        _control = control;
        _volume = volume;
        _meter = meter;
    }

    /// <summary>The sessions of an endpoint as they are now; dispose each when done.</summary>
    /// <param name="device">The playback endpoint, or <see langword="null"/> for the default one.</param>
    public static IReadOnlyList<AudioSession> GetAll(MediaDevice? device = null)
    {
        Ole32.Initialize();
        using var endpoint = WasapiDeviceEnumerator.OpenDevice(device, DataFlow.Render);
        HResult.ThrowIfFailed(Wasapi.Activate(endpoint.Pointer, AudioIids.SessionManager2, out var managerPointer), "IMMDevice::Activate(IAudioSessionManager2)");
        using var manager = new ComPtr(managerPointer);

        void* enumeratorPointer;
        HResult.ThrowIfFailed(
            ((delegate* unmanaged[Stdcall]<void*, void**, int>)Com.Slot(manager.Pointer, AudioSlots.ManagerGetSessionEnumerator))(manager.Pointer, &enumeratorPointer),
            "IAudioSessionManager2::GetSessionEnumerator");
        using var enumerator = new ComPtr(enumeratorPointer);

        int count;
        HResult.ThrowIfFailed(
            ((delegate* unmanaged[Stdcall]<void*, int*, int>)Com.Slot(enumerator.Pointer, AudioSlots.EnumeratorGetCount))(enumerator.Pointer, &count),
            "IAudioSessionEnumerator::GetCount");

        var sessions = new List<AudioSession>(count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                void* controlPointer;
                HResult.ThrowIfFailed(
                    ((delegate* unmanaged[Stdcall]<void*, int, void**, int>)Com.Slot(enumerator.Pointer, AudioSlots.EnumeratorGetSession))(
                        enumerator.Pointer, i, &controlPointer),
                    "IAudioSessionEnumerator::GetSession");
                var control = new ComPtr(controlPointer);

                // IAudioSessionControl2 is what GetSession's pointer already implements; the others are asked for.
                var control2 = control.QueryInterface(AudioIids.SessionControl2);
                control.Dispose();
                var volume = control2.QueryInterface(AudioIids.SimpleAudioVolume);
                var meter = control2.QueryInterface(AudioIids.MeterInformation);
                sessions.Add(new AudioSession(control2, volume, meter));
            }
        }
        catch
        {
            foreach (var session in sessions)
            {
                session.Dispose();
            }

            throw;
        }

        return sessions;
    }

    /// <summary>The id of the program's process; 0 for the system sounds.</summary>
    public int ProcessId
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            uint id;
            HResult.ThrowIfFailed(
                ((delegate* unmanaged[Stdcall]<void*, uint*, int>)Com.Slot(_control.Pointer, AudioSlots.SessionGetProcessId))(_control.Pointer, &id),
                "IAudioSessionControl2::GetProcessId");
            return (int)id;
        }
    }

    /// <summary>True for the session the system's own sounds use.</summary>
    public bool IsSystemSounds
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var hr = ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(_control.Pointer, AudioSlots.SessionIsSystemSounds))(_control.Pointer);
            HResult.ThrowIfFailed(hr, "IAudioSessionControl2::IsSystemSoundsSession");
            return hr == HResult.Ok;
        }
    }

    /// <summary>The name the program gave the session, often empty; the mixer then shows the program's own name.</summary>
    public string DisplayName
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            char* name;
            HResult.ThrowIfFailed(
                ((delegate* unmanaged[Stdcall]<void*, char**, int>)Com.Slot(_control.Pointer, AudioSlots.SessionGetDisplayName))(_control.Pointer, &name),
                "IAudioSessionControl::GetDisplayName");
            try
            {
                return name is null ? string.Empty : new string(name);
            }
            finally
            {
                Ole32.CoTaskMemFree(name);
            }
        }
    }

    /// <summary>Whether the session is playing now.</summary>
    public AudioSessionState State
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int state;
            HResult.ThrowIfFailed(
                ((delegate* unmanaged[Stdcall]<void*, int*, int>)Com.Slot(_control.Pointer, AudioSlots.SessionGetState))(_control.Pointer, &state),
                "IAudioSessionControl::GetState");
            return (AudioSessionState)state;
        }
    }

    /// <summary>This program's volume, 0 to 1, on top of the endpoint's.</summary>
    public float Volume
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            float value;
            HResult.ThrowIfFailed(
                ((delegate* unmanaged[Stdcall]<void*, float*, int>)Com.Slot(_volume.Pointer, AudioSlots.SimpleGetVolume))(_volume.Pointer, &value),
                "ISimpleAudioVolume::GetMasterVolume");
            return value;
        }

        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 0f);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1f);
            HResult.ThrowIfFailed(
                ((delegate* unmanaged[Stdcall]<void*, float, void*, int>)Com.Slot(_volume.Pointer, AudioSlots.SimpleSetVolume))(_volume.Pointer, value, null),
                "ISimpleAudioVolume::SetMasterVolume");
        }
    }

    /// <summary>Whether this program is muted.</summary>
    public bool IsMuted
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int muted;
            HResult.ThrowIfFailed(
                ((delegate* unmanaged[Stdcall]<void*, int*, int>)Com.Slot(_volume.Pointer, AudioSlots.SimpleGetMute))(_volume.Pointer, &muted),
                "ISimpleAudioVolume::GetMute");
            return muted != 0;
        }

        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            HResult.ThrowIfFailed(
                ((delegate* unmanaged[Stdcall]<void*, int, void*, int>)Com.Slot(_volume.Pointer, AudioSlots.SimpleSetMute))(_volume.Pointer, value ? 1 : 0, null),
                "ISimpleAudioVolume::SetMute");
        }
    }

    /// <summary>The highest level of this program's sound just now, 0 to 1.</summary>
    public float PeakLevel
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            float peak;
            HResult.ThrowIfFailed(
                ((delegate* unmanaged[Stdcall]<void*, float*, int>)Com.Slot(_meter.Pointer, AudioSlots.MeterGetPeak))(_meter.Pointer, &peak),
                "IAudioMeterInformation::GetPeakValue");
            return peak;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _meter.Dispose();
        _volume.Dispose();
        _control.Dispose();
    }
}
