#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Devices;
using MediaToolkitNet.Interop.Com;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows;

/// <summary>A change of an endpoint's volume or mute state.</summary>
/// <param name="Volume">The master volume, 0 to 1.</param>
/// <param name="IsMuted">Whether the endpoint is muted.</param>
public readonly record struct AudioVolumeChange(float Volume, bool IsMuted);

/// <summary>
/// The master volume, mute switch and peak meter of an audio endpoint: the speakers' volume as Windows' own mixer
/// shows it, or a microphone's gain. Changes made anywhere, by this class, the mixer or the keyboard, are reported.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class AudioEndpointVolume : IDisposable
{
    private ComPtr _device;
    private ComPtr _volume;
    private ComPtr _meter;
    private AudioCallbacks.CallbackObject* _callback;
    private bool _disposed;

    /// <summary>Opens the controls of an endpoint.</summary>
    /// <param name="device">The endpoint, or <see langword="null"/> for the default one.</param>
    /// <param name="capture">Whether it is a recording endpoint (a microphone) rather than a playback one.</param>
    public AudioEndpointVolume(MediaDevice? device = null, bool capture = false)
    {
        Ole32.Initialize();
        try
        {
            _device = WasapiDeviceEnumerator.OpenDevice(device, capture ? DataFlow.Capture : DataFlow.Render);
            HResult.ThrowIfFailed(Wasapi.Activate(_device.Pointer, AudioIids.EndpointVolume, out var volume), "IMMDevice::Activate(IAudioEndpointVolume)");
            _volume = new ComPtr(volume);
            HResult.ThrowIfFailed(Wasapi.Activate(_device.Pointer, AudioIids.MeterInformation, out var meter), "IMMDevice::Activate(IAudioMeterInformation)");
            _meter = new ComPtr(meter);

            _callback = AudioCallbacks.NewVolumeCallback(this);
            HResult.ThrowIfFailed(
                ((delegate* unmanaged[Stdcall]<void*, void*, int>)Com.Slot(_volume.Pointer, AudioSlots.VolumeRegister))(_volume.Pointer, _callback),
                "IAudioEndpointVolume::RegisterControlChangeNotify");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Raised when the volume or the mute switch changes, from whichever thread Windows calls on.</summary>
    public event EventHandler<AudioVolumeChange>? Changed;

    /// <summary>Raised when a handler of <see cref="Changed"/> throws, which must not end the process.</summary>
    public event EventHandler<MediaToolkitNetException>? Failed;

    /// <summary>The master volume, 0 (silent) to 1 (full), on the scale the Windows mixer uses.</summary>
    public float Volume
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            float value;
            HResult.ThrowIfFailed(
                ((delegate* unmanaged[Stdcall]<void*, float*, int>)Com.Slot(_volume.Pointer, AudioSlots.VolumeGetScalar))(_volume.Pointer, &value),
                "IAudioEndpointVolume::GetMasterVolumeLevelScalar");
            return value;
        }

        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 0f);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1f);
            HResult.ThrowIfFailed(
                ((delegate* unmanaged[Stdcall]<void*, float, void*, int>)Com.Slot(_volume.Pointer, AudioSlots.VolumeSetScalar))(_volume.Pointer, value, null),
                "IAudioEndpointVolume::SetMasterVolumeLevelScalar");
        }
    }

    /// <summary>Whether the endpoint is muted.</summary>
    public bool IsMuted
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int muted;
            HResult.ThrowIfFailed(
                ((delegate* unmanaged[Stdcall]<void*, int*, int>)Com.Slot(_volume.Pointer, AudioSlots.VolumeGetMute))(_volume.Pointer, &muted),
                "IAudioEndpointVolume::GetMute");
            return muted != 0;
        }

        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            HResult.ThrowIfFailed(
                ((delegate* unmanaged[Stdcall]<void*, int, void*, int>)Com.Slot(_volume.Pointer, AudioSlots.VolumeSetMute))(_volume.Pointer, value ? 1 : 0, null),
                "IAudioEndpointVolume::SetMute");
        }
    }

    /// <summary>The number of channels the endpoint has.</summary>
    public int ChannelCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            uint count;
            HResult.ThrowIfFailed(
                ((delegate* unmanaged[Stdcall]<void*, uint*, int>)Com.Slot(_volume.Pointer, AudioSlots.VolumeChannelCount))(_volume.Pointer, &count),
                "IAudioEndpointVolume::GetChannelCount");
            return (int)count;
        }
    }

    /// <summary>
    /// The highest level of the sound the endpoint is playing or recording just now, 0 to 1, after the volume has been
    /// applied: what a level meter shows.
    /// </summary>
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
        if (_callback is not null)
        {
            // After this returns Windows starts no more calls; then our reference to the callback can go.
            ((delegate* unmanaged[Stdcall]<void*, void*, int>)Com.Slot(_volume.Pointer, AudioSlots.VolumeUnregister))(_volume.Pointer, _callback);
            AudioCallbacks.ReleaseOwn(_callback);
            _callback = null;
        }

        _meter.Dispose();
        _volume.Dispose();
        _device.Dispose();
    }

    internal void Notified(bool muted, float volume)
    {
        try
        {
            Changed?.Invoke(this, new AudioVolumeChange(volume, muted));
        }
        catch (Exception ex)
        {
            SafeEvents.RaiseFailed(Failed, this, new MediaToolkitNetException(WasapiDeviceEnumerator.BackendName, $"a volume change handler threw: {ex.Message}", 0));
        }
    }
}
